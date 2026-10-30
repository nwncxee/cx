#define GL_GLEXT_PROTOTYPES
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GL/gl.h>
#include <GL/glext.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
// Cluster-build check: runs the real cx_lightbuild over a plaza-shaped light set (256 lights, radii 4-60 m,
// dynamic, cold static and ambient lanes) on the real 32x18x24 grid under a tilted camera, then checks every
// froxel's list against what the walks in cx_lightfrag / cx_vanenv_f / cx_material read:
//   the layout -- live first, R4's picks the top-K cold statics by the curve at the froxel's nearest point
//   (culled ones included), the counts in the three header words;
//   light.relevance's bounds -- at sample points inside the froxel (out to the one-pixel jitter pad, and slice 0 down
//   to the eye), the brightest light over the floor candidates IS the brightest over the whole list, every
//   culled light sits at or under the cut, and CxVisionLight's max over the kept prefix is the whole list's.
// Each case runs with relevance at 0, 0.01 and 0.1, and once under /light bincap (no bounds). NVIDIA, then Mesa:
//   gcc -O1 tools/lightbench/buildcheck.c -o /tmp/buildcheck -lEGL -lGL -lm
//   __EGL_VENDOR_LIBRARY_FILENAMES=/usr/share/glvnd/egl_vendor.d/10_nvidia.json /tmp/buildcheck managed/Alloy/Shaders/cx_lightbuild.glsl
//   LB_MESA=1 LIBGL_ALWAYS_SOFTWARE=1 __EGL_VENDOR_LIBRARY_FILENAMES=/usr/share/glvnd/egl_vendor.d/50_mesa.json /tmp/buildcheck ...
enum { GX = 32, GY = 18, GZ = 24, NC = GX * GY * GZ, CAP = 128, NL = 256, CUBES = 64, DYNSPH = 16, VPW = 2544, VPH = 1379 };
static const float AT0 = 0.41f, AT1 = 136.33f, REACH = 0.8f, ZN = 0.5f, ZF = 400.f;
static float L[NL * 8];
static float V[16];          // view, column-major (GLSL reads it as uploaded)
static float P00, P11;

static uint32_t rng = 12345u;
static float rnd(void) { rng = rng * 1664525u + 1013904223u; return (rng >> 8) * (1.0f / 16777216.0f); }

static void toView(const float* w, float* e)
{
    for (int r = 0; r < 3; r++) e[r] = V[r] * w[0] + V[4 + r] * w[1] + V[8 + r] * w[2] + V[12 + r];
}
static float importance(int i, float dd, float relReach)
{
    const float* pr = L + i * 8;
    float f2 = dd / fmaxf(pr[3] * pr[3], 1e-8f);
    if (f2 >= 1.0f) return 0.0f;
    float fade = 1.0f - f2;
    if (relReach > 0.0f)
    {
        float r2 = relReach * relReach;
        if (f2 > r2) return 0.0f;
        fade *= fminf((r2 - f2) / (r2 * 0.3f), 1.0f);
    }
    return fade / (AT0 + AT1 * f2) * (pr[4] * 0.299f + pr[5] * 0.587f + pr[6] * 0.114f);
}
static float vision(int i, float dd)
{
    const float* pr = L + i * 8;
    float rr = pr[3] * pr[3];
    if (dd > rr) return 0.0f;
    float f2 = dd / rr;
    float lum = powf(fmaxf(pr[4], 0.f), 2.2f) * 0.299f + powf(fmaxf(pr[5], 0.f), 2.2f) * 0.587f + powf(fmaxf(pr[6], 0.f), 2.2f) * 0.114f;
    return (1.0f - f2) / (AT0 + AT1 * f2) * lum;
}
static float d2(const float* a, const float* b) { float x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2]; return x * x + y * y + z * z; }
static float movers[CUBES * DYNSPH * 4], dynPage[CUBES];
// cx_lightbuild's HotReach, world space, with the froxel sphere grown/shrunk by `g` to bracket float noise
static int hotReach(int slot, const float* l, const float* fc, float fr, float g)
{
    if (slot < 0 || slot >= CUBES || dynPage[slot] < 0.5f) return 0;
    for (int q = 0; q < DYNSPH; q++)
    {
        const float* ms = movers + (slot * DYNSPH + q) * 4;
        if (ms[3] < 0.f) break;
        float ax[3] = { ms[0] - l[0], ms[1] - l[1], ms[2] - l[2] };
        float rr = ms[3] + 0.3f, d = sqrtf(ax[0] * ax[0] + ax[1] * ax[1] + ax[2] * ax[2]);
        if (d * d <= 2.f * rr * rr) return 1;
        for (int a = 0; a < 3; a++) ax[a] /= d;
        float sn = rr / d, cs = sqrtf(1.f - sn * sn);
        float v[3] = { fc[0] - l[0], fc[1] - l[1], fc[2] - l[2] };
        float along = v[0] * ax[0] + v[1] * ax[1] + v[2] * ax[2];
        if (along < -fr * g) continue;
        float perp = sqrtf(fmaxf(v[0] * v[0] + v[1] * v[1] + v[2] * v[2] - along * along, 0.f));
        if (cs * perp - along * sn <= fr * g) return 1;
    }
    return 0;
}

int main(int argc, char** argv)
{
    PFNEGLQUERYDEVICESEXTPROC qd = (void*)eglGetProcAddress("eglQueryDevicesEXT");
    PFNEGLGETPLATFORMDISPLAYEXTPROC gpd = (void*)eglGetProcAddress("eglGetPlatformDisplayEXT");
    EGLDeviceEXT devs[8]; EGLint nd = 0; qd(8, devs, &nd);
    EGLDisplay d = getenv("LB_MESA") ? gpd(0x31DD, EGL_DEFAULT_DISPLAY, 0) : gpd(EGL_PLATFORM_DEVICE_EXT, devs[0], 0);
    eglInitialize(d, 0, 0); eglBindAPI(EGL_OPENGL_API);
    EGLint cx[] = { EGL_CONTEXT_MAJOR_VERSION, 4, EGL_CONTEXT_MINOR_VERSION, 6, EGL_CONTEXT_OPENGL_PROFILE_MASK, EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT, EGL_NONE };
    EGLContext c = eglCreateContext(d, EGL_NO_CONFIG_KHR, EGL_NO_CONTEXT, cx);
    if (!eglMakeCurrent(d, EGL_NO_SURFACE, EGL_NO_SURFACE, c)) { fprintf(stderr, "no ctx\n"); return 1; }
    FILE* f = fopen(argv[1], "rb"); fseek(f, 0, SEEK_END); long len = ftell(f); rewind(f);
    char* raw = malloc(len + 1); fread(raw, 1, len, f); raw[len] = 0;
    char* src = malloc(len + 256), *o = src;
    const char* tok[][2] = { { "$CAP$", "128" }, { "$SORT$", "32" }, { "$GROUP$", "64" }, { "$DYNSPH$", "16" }, { "$CUBES$", "64" }, { "$SRGB$", "2.2" } };
    for (char* r = raw; *r;)
    {
        int hit = 0;
        for (int t = 0; t < 6 && !hit; t++)
        {
            size_t l = strlen(tok[t][0]);
            if (!strncmp(r, tok[t][0], l)) { o += sprintf(o, "%s", tok[t][1]); r += l; hit = 1; }
        }
        if (!hit) *o++ = *r++;
    }
    *o = 0;
    GLuint p = glCreateShaderProgramv(GL_COMPUTE_SHADER, 1, (const char**)&src);
    GLint ok; glGetProgramiv(p, GL_LINK_STATUS, &ok);
    if (!ok) { char log[8192]; glGetProgramInfoLog(p, 8192, 0, log); puts(log); return 1; }
    glUseProgram(p);

    // camera 2 m up, pitched 15 deg down, yawed 20 deg: a rigid view so the uView convention is exercised
    float pitch = -0.26f, yaw = 0.35f, cp = cosf(pitch), sp = sinf(pitch), cy = cosf(yaw), sy = sinf(yaw);
    float R[9] = { cy, 0, -sy,  sy * sp, cp, cy * sp,  sy * cp, -sp, cy * cp };   // rows: right, up, back
    float eye[3] = { 3.f, 2.f, 10.f };
    for (int r = 0; r < 3; r++)
    {
        V[r] = R[r * 3]; V[4 + r] = R[r * 3 + 1]; V[8 + r] = R[r * 3 + 2];
        V[12 + r] = -(R[r * 3] * eye[0] + R[r * 3 + 1] * eye[1] + R[r * 3 + 2] * eye[2]);
    }
    V[3] = V[7] = V[11] = 0; V[15] = 1;
    P11 = 1.0f / tanf(0.5f * 45.f * 3.14159265f / 180.f); P00 = P11 * VPH / VPW;

    int fails = 0;
    for (int scene = 0; scene < 2; scene++)
    {
    // scene 0 spreads the lights over 140 x 170 m; scene 1 packs them into the 60 x 70 m in front of the eye,
    // the torch-dense plaza that reads 32+ lights a pixel
    float spread = scene == 0 ? 1.f : 0.42f;
    printf("%s:\n", scene == 0 ? "wide area" : "dense plaza");
    for (int i = 0; i < NL; i++)
    {
        float* r = L + i * 8;
        r[0] = (rnd() - 0.5f) * 140.f * spread; r[1] = rnd() * 7.f; r[2] = -rnd() * 170.f * spread + 15.f;
        r[3] = rnd() < 0.75f ? 4.f + rnd() * 18.f : 30.f + rnd() * 30.f;
        float g = 0.2f + 0.8f * rnd();
        r[4] = g; r[5] = g * (0.4f + 0.6f * rnd()); r[6] = g * rnd() * rnd();
        float k = rnd();
        int cube = (int)(rnd() * CUBES);
        r[7] = k < 0.15f ? -1.f : k < 0.2f ? -2.f : k < 0.25f ? (float)cube : (float)((rnd() < 0.3f ? cube : -1) + 256 + 512);
    }
    // scene 1 arms the hot path: every cube slot holds a dyn page and three movers by its own light
    for (int i = 0; i < CUBES * DYNSPH; i++) { movers[i * 4 + 3] = -1.f; }
    for (int c = 0; c < CUBES; c++) dynPage[c] = 0.f;
    if (scene == 1)
        for (int i = 0; i < NL; i++)
        {
            int w = (int)L[i * 8 + 7];
            if (w < 256) continue;
            int slot = w - 256 - 512;
            if (slot < 0 || slot >= CUBES || dynPage[slot] > 0.5f) continue;
            dynPage[slot] = 1.f;
            for (int q = 0; q < 3; q++)
            {
                float* m = movers + (slot * DYNSPH + q) * 4;
                m[0] = L[i * 8] + (rnd() - 0.5f) * 30.f; m[1] = rnd() * 2.f; m[2] = L[i * 8 + 2] + (rnd() - 0.5f) * 30.f; m[3] = 0.4f + rnd() * 1.5f;
            }
        }
    GLuint b[5]; glGenBuffers(5, b);
    glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[0]); glBufferData(GL_SHADER_STORAGE_BUFFER, sizeof L, L, GL_STATIC_DRAW);
    glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[1]); glBufferData(GL_SHADER_STORAGE_BUFFER, NC * 3 * 4, 0, GL_DYNAMIC_READ);
    glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[2]); glBufferData(GL_SHADER_STORAGE_BUFFER, (size_t)NC * CAP * 4, 0, GL_DYNAMIC_READ);
    glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[3]); glBufferData(GL_SHADER_STORAGE_BUFFER, 16, 0, GL_DYNAMIC_READ);
    glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[4]); glBufferData(GL_SHADER_STORAGE_BUFFER, sizeof movers, movers, GL_STATIC_DRAW);
    for (int i = 0; i < 4; i++) glBindBufferBase(GL_SHADER_STORAGE_BUFFER, i, b[i]);
    glBindBufferBase(GL_SHADER_STORAGE_BUFFER, 25, b[4]);
    glUniformMatrix4fv(glGetUniformLocation(p, "uView"), 1, 0, V);
    glUniform2f(glGetUniformLocation(p, "uProjXY"), P00, P11);
    glUniform2f(glGetUniformLocation(p, "uZ"), ZN, ZF);
    glUniform3i(glGetUniformLocation(p, "uGrid"), GX, GY, GZ);
    glUniform1i(glGetUniformLocation(p, "uLightCount"), NL);
    glUniform1f(glGetUniformLocation(p, "uReach"), REACH);
    glUniform2f(glGetUniformLocation(p, "uAtten"), AT0, AT1);
    glUniform1fv(glGetUniformLocation(p, "uDynPage"), CUBES, dynPage);
    glUniform2f(glGetUniformLocation(p, "uHot"), 1.f, 0.f);
    glUniform2f(glGetUniformLocation(p, "uPad"), 2.f / VPW, 2.f / VPH);
    glUniform1i(glGetUniformLocation(p, "uTally"), 1);

    glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[0]); glBufferSubData(GL_SHADER_STORAGE_BUFFER, 0, sizeof L, L);
    static uint32_t cnt[NC * 3], idx[(size_t)NC * CAP];
    struct { float rel; int cap; } cases[] = { { 0.f, 0 }, { 0.01f, 0 }, { 0.1f, 0 }, { 0.1f, 12 } };
    for (int ci = 0; ci < 4; ci++)
    {
        float rel = cases[ci].rel;
        glUniform2f(glGetUniformLocation(p, "uRel"), rel, REACH);
        glUniform1i(glGetUniformLocation(p, "uBinCap"), cases[ci].cap);
        uint32_t zero[4] = { 0 };
        glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[3]); glBufferSubData(GL_SHADER_STORAGE_BUFFER, 0, 16, zero);
        GLuint q; glGenQueries(1, &q);
        glBeginQuery(GL_TIME_ELAPSED, q);
        for (int rep = 0; rep < 20; rep++) glDispatchCompute((NC + 63) / 64, 1, 1);
        glEndQuery(GL_TIME_ELAPSED);
        GLuint64 ns; glGetQueryObjectui64v(q, GL_QUERY_RESULT, &ns);
        glMemoryBarrier(GL_ALL_BARRIER_BITS);
        printf("  build %.3f ms\n", ns * 1e-6 / 20);
        glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[3]); glBufferSubData(GL_SHADER_STORAGE_BUFFER, 0, 16, zero);
        glDispatchCompute((NC + 63) / 64, 1, 1); glMemoryBarrier(GL_ALL_BARRIER_BITS);
        glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[1]); glGetBufferSubData(GL_SHADER_STORAGE_BUFFER, 0, sizeof cnt, cnt);
        glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[2]); glGetBufferSubData(GL_SHADER_STORAGE_BUFFER, 0, sizeof idx, idx);
        uint32_t stats[4]; glBindBuffer(GL_SHADER_STORAGE_BUFFER, b[3]); glGetBufferSubData(GL_SHADER_STORAGE_BUFFER, 0, 16, stats);
        int caseFails = 0;
        double sumN = 0, sumKept = 0, sumCand = 0, sumLive = 0;
        long hotSeen = 0;
        for (int cl = 0; cl < NC; cl++)
        {
            uint32_t n = cnt[cl * 3] & 0xffff, live = cnt[cl * 3] >> 16;
            uint32_t liveCand = cnt[cl * 3 + 1] >> 16, coldCand = cnt[cl * 3 + 2] & 0xffff, kept = cnt[cl * 3 + 2] >> 16;
            const uint32_t* e = idx + (size_t)cl * CAP;
            sumN += n; sumKept += kept; sumLive += live; sumCand += liveCand + coldCand - live;
            int bad = 0;
            if (!(liveCand <= live && live <= coldCand && coldCand <= kept && kept <= n && n <= CAP)) bad |= 1;
            if (rel == 0.f || cases[ci].cap > 0) { if (kept != n || liveCand != live || coldCand != n) bad |= 2; }
            unsigned char seen[NL] = { 0 };
            int gx = cl % GX, gy = (cl / GX) % GY, gz = cl / (GX * GY);
            float lz = logf(ZF / ZN), dd0 = ZN * expf(lz * gz / GZ), dd1 = ZN * expf(lz * (gz + 1) / GZ);
            float x0 = -1.f + 2.f * gx / GX, x1 = -1.f + 2.f * (gx + 1) / GX, y0 = -1.f + 2.f * gy / GY, y1 = -1.f + 2.f * (gy + 1) / GY;
            // the froxel's bounding sphere, view space -> world through the rigid view's transpose
            float flo[3] = { 1e30f, 1e30f, 1e30f }, fhi[3] = { -1e30f, -1e30f, -1e30f };
            for (int cc = 0; cc < 8; cc++)
            {
                float dz = (cc & 4) ? dd1 : dd0, pt[3] = { ((cc & 1) ? x1 : x0) * dz / P00, ((cc & 2) ? y1 : y0) * dz / P11, -dz };
                for (int a = 0; a < 3; a++) { flo[a] = fminf(flo[a], pt[a]); fhi[a] = fmaxf(fhi[a], pt[a]); }
            }
            float fcv[3], fcw[3], fr = 0.f;
            for (int a = 0; a < 3; a++) { fcv[a] = 0.5f * (flo[a] + fhi[a]); fr += (fhi[a] - flo[a]) * (fhi[a] - flo[a]); }
            fr = 0.5f * sqrtf(fr);
            for (int a = 0; a < 3; a++) fcw[a] = V[a * 4] * (fcv[0] - V[12]) + V[a * 4 + 1] * (fcv[1] - V[13]) + V[a * 4 + 2] * (fcv[2] - V[14]);
            for (uint32_t k = 0; k < n && !bad; k++)
            {
                if (e[k] >= NL || seen[e[k]]) { bad |= 4; break; }
                seen[e[k]] = 1;
                int w = (int)L[e[k] * 8 + 7], stat = w >= 128, slot = w - 256 - 512;
                int hotLoose = stat && hotReach(slot, L + e[k] * 8, fcw, fr, 1.001f), hotTight = stat && hotReach(slot, L + e[k] * 8, fcw, fr, 0.999f);
                if (k < live && stat && !hotLoose) bad |= 8;
                if (k >= live && k < kept && (!stat || hotTight)) bad |= 8;
                if (k < live && hotLoose) hotSeen++;
            }
            // the bounds, at sample points inside the (jittered) froxel
            for (int sIdx = 0; sIdx < 48 && !bad && rel > 0.f && cases[ci].cap == 0; sIdx++)
            {
                // out to the whole pad (one pixel), twice the TAA jitter it stands for
                float jx = (rnd() - 0.5f) * 4.f / VPW, jy = (rnd() - 0.5f) * 4.f / VPH;
                float nx = x0 + (x1 - x0) * rnd() + jx, ny = y0 + (y1 - y0) * rnd() + jy;
                float dz = sIdx < 4 && gz == 0 ? 0.1f + rnd() * (dd0 - 0.1f) : dd0 + (dd1 - dd0) * (sIdx < 8 ? (float)(sIdx & 1) : rnd());
                float pv[3] = { nx * dz / P00, ny * dz / P11, -dz };
                float relFull = 0, relCand = 0, visFull = 0, visKept = 0;
                float imp[CAP], vis[CAP];
                for (uint32_t k = 0; k < n; k++)
                {
                    float ev[3]; toView(L + e[k] * 8, ev);
                    float dd = d2(ev, pv);
                    imp[k] = importance(e[k], dd, REACH); vis[k] = vision(e[k], dd);
                    relFull = fmaxf(relFull, imp[k]); visFull = fmaxf(visFull, vis[k]);
                    if (k < liveCand || (k >= live && k < coldCand)) relCand = fmaxf(relCand, imp[k]);
                    if (k < kept) visKept = fmaxf(visKept, vis[k]);
                }
                if (relCand < relFull * (1.f - 1e-5f)) bad |= 32;
                if (visKept < visFull * (1.f - 1e-5f)) bad |= 64;
                for (uint32_t k = kept; k < n; k++)
                    if (imp[k] > rel * relFull * (1.f + 1e-4f)) bad |= 128;
            }
            if (bad)
            {
                caseFails++;
                if (caseFails < 6) printf("  froxel %d (%d,%d,%d) bad=%x n=%u live=%u liveCand=%u coldCand=%u kept=%u\n",
                                          cl, gx, gy, gz, bad, n, live, liveCand, coldCand, kept);
            }
        }
        printf("relevance %.2f bincap %d: %s -- avg per froxel binned %.1f, live %.1f, kept %.1f, brightest-light search %.1f, hot statics live %ld; tally %u/%u/%u\n",
               rel, cases[ci].cap, caseFails ? "FAIL" : "ok", sumN / NC, sumLive / NC, sumKept / NC, sumCand / NC,
               hotSeen, stats[1], stats[2], stats[3]);
        if (stats[1] != (uint32_t)sumN || stats[2] != (uint32_t)sumKept || stats[3] != (uint32_t)sumCand) { printf("  tally disagrees with the lists\n"); caseFails++; }
        fails += caseFails;
    }
    }
    printf("%s (%s)\n", fails ? "FAIL" : "ok", glGetString(GL_RENDERER));
    return fails != 0;
}
