// CX-95: the stock GUI colour pipeline (fst/fstc/fs over inc_standard): texture, vertex colour and globalColor are
// taken to linear, multiplied and re-encoded -- so display = globalColor * vcolor * texture, alpha multiplied straight.
// The records hold the engine's sRGB caches; its uniforms went through GetColorSpaceFromSRGB @0x55db30 (rgb ^ 2.2),
// hence the GuiLin on globalColor and the material. Lit draws (the model view, the compass) take the engine's fixed key light, or a server's when the record says so.
#version 460 core
in vec2 vUv;
in vec4 vColor;
in vec3 vNrm;
in vec3 vPos;
flat in int vRec;
layout(std430, binding = 8) readonly buffer CxGuiBuf { vec4 uGui[]; };
layout(binding = 0) uniform sampler2D uTex;
layout(binding = 12) uniform sampler2D uEnvMap;   // the environment map a part reflects (TextureSlots.GuiEnv)
layout(binding = 19) uniform sampler2D uPltPal;
uniform int uGamma;   // GAMMA_CORRECTION: the engine's ApplyColorSpace/RevertColorSpace pair is live
out vec4 frag;

// CX-120: a PLT icon. An item's icon texture is a PAIR texture -- intensity in R, layer in G -- and its
// colour is master[row[layer]][intensity], the same lookup cx_plt.glsl performs for the world. Drawn without
// it the pair data IS the picture: the solid red cloak in the native Examine panel (user-shots/22.png).
// The rows live in this record rather than in CxPltBuf because a GUI record is not a world record slot, and
// the resolve is level 0 only: a GUI icon is drawn at its own size, never minified.
float GuiPltRow(int r, int layer)
{
    return layer < 4  ? uGui[r + 14][layer]
         : layer < 8  ? uGui[r + 15][layer - 4]
         : layer == 8 ? uGui[r + 13].z : uGui[r + 13].w;
}

vec4 GuiPltTexel(int r, ivec2 p, ivec2 dim)
{
    vec2 rg = texelFetch(uTex, clamp(p, ivec2(0), dim - 1), 0).rg;
    int layer = min(int(rg.g * 255.0 + 0.5), 9);
    return texelFetch(uPltPal, ivec2(int(rg.r * 255.0 + 0.5), int(GuiPltRow(r, layer))), 0);
}

// Bilinear over RESOLVED colours: the pair texture itself must never be filtered, because the mean of two
// layer indices is a third layer.
vec4 GuiPlt(int r, vec2 uv)
{
    ivec2 dim = max(textureSize(uTex, 0), ivec2(1));
    vec2 t = uv * vec2(dim) - 0.5;
    vec2 f = fract(t);
    ivec2 p = ivec2(floor(t));
    return mix(mix(GuiPltTexel(r, p + ivec2(0, 0), dim), GuiPltTexel(r, p + ivec2(1, 0), dim), f.x),
               mix(GuiPltTexel(r, p + ivec2(0, 1), dim), GuiPltTexel(r, p + ivec2(1, 1), dim), f.x), f.y);
}

vec3 GuiLin(vec3 c) { return uGamma != 0 ? sign(c) * pow(abs(c), vec3(2.2)) : c; }
vec3 GuiEnc(vec3 c) { return uGamma != 0 ? sign(c) * pow(abs(c), vec3(0.45454545)) : c; }

// inc_envmap.shd CalculateEnvironmentTexCoords, verbatim: the engine's own framing of its env textures.
vec2 GuiEnvCoords(vec3 n, vec3 pos)
{
    vec2 e = n.xy / (1.0 + abs(n.z));
    e += 0.5 * (pos.xy / (-pos.z + 0.2));
    return 0.5 * e + vec2(0.5, 0.25);
}

// An env-mapped part's texture alpha is its reflectivity (inc_standard.shd, the vertex-lit path a GUI scene takes): the
// colour is mix(texture, environment texel, 1 - alpha) before the light. A part whose object names no map (or one that does
// not load) takes the grey bake instead.
vec3 GuiEnvTexel(vec3 n)
{
    return GuiLin(texture(uEnvMap, GuiEnvCoords(n, vPos)).rgb);
}

void main()
{
    int r = vRec * 16;
    int flags = int(uGui[r + 13].x);
    vec4 t = (flags & 32) != 0 ? GuiPlt(r, vUv)
           : (flags & 1) != 0  ? texture(uTex, vUv) : vec4(1.0);
    // FlagSolidTexel: the alpha is never opacity. An env-mapped part with a map to reflect spends it as reflectivity below; any other
    // takes the engine's env-off bake (rgb = mix(env grey, rgb, a), then a = 1; cx_ownlit_head.glsl's CX_ENV_GREY).
    float envLevel = 1.0 - t.a;
    if ((flags & 64) != 0 && (flags & 768) != 768) { t.rgb = mix(vec3(0.666667), t.rgb, t.a); t.a = 1.0; }
    else if ((flags & 64) != 0) t.a = 1.0;
    vec4 c;
    if ((flags & 4) != 0)
    {
        vec3 n = vNrm;
        bool nrm = dot(n, n) > 1e-8;
        vec3 un = nrm ? normalize(n) : vec3(0.0, 0.0, 1.0);
        vec3 lit, emi = GuiLin(uGui[r + 12].rgb);
        if ((flags & 128) != 0)
        {
            float k = nrm ? max(dot(un, normalize(uGui[r + 9].xyz)), 0.0) : 1.0;
            lit = uGui[r + 10].rgb + uGui[r + 11].rgb * k;
        }
        else
        {
            float k = nrm ? max(dot(un, normalize(vec3(0.35, 0.5, 0.8))), 0.0) : 1.0;
            lit = GuiLin(uGui[r + 10].rgb) * 0.3 + GuiLin(uGui[r + 11].rgb) * (0.25 + 0.75 * k);
        }
        vec3 albedo = GuiLin(t.rgb);
        if ((flags & 768) == 768) albedo = mix(albedo, GuiEnvTexel(un), envLevel);
        vec3 shaded = (flags & 128) != 0 ? albedo * lit + emi : albedo * (lit + emi);
        c = vec4(GuiEnc(shaded), t.a * uGui[r + 11].a);
    }
    else
    {
        vec4 g = uGui[r + 9];
        vec4 vc = (flags & 2) != 0 ? vec4(GuiLin(vColor.rgb), vColor.a) : vec4(1.0);
        c = vec4(GuiEnc(vc.rgb * GuiLin(g.rgb) * GuiLin(t.rgb)), vc.a * g.a * t.a);
    }
    if ((flags & 8) != 0 && c.a <= 0.2) discard;   // SetUniforms: fAlphaDiscardValue = cap 1 ? 0.2 : -1
    frag = c;
}
