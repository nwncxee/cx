// CX-95: the stock GUI colour pipeline (fst/fstc/fs over inc_standard): texture, vertex colour and globalColor are
// taken to linear, multiplied and re-encoded -- so display = globalColor * vcolor * texture, alpha multiplied straight.
// The records hold the engine's sRGB caches; its uniforms went through GetColorSpaceFromSRGB @0x55db30 (rgb ^ 2.2),
// hence the GuiLin on globalColor and the material. Lit draws (the model view, the compass) take a fixed key light.
#version 460 core
in vec2 vUv;
in vec4 vColor;
in vec3 vNrm;
flat in int vRec;
layout(std430, binding = 8) readonly buffer CxGuiBuf { vec4 uGui[]; };
layout(binding = 0) uniform sampler2D uTex;
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

void main()
{
    int r = vRec * 16;
    int flags = int(uGui[r + 13].x);
    vec4 t = (flags & 32) != 0 ? GuiPlt(r, vUv)
           : (flags & 1) != 0  ? texture(uTex, vUv) : vec4(1.0);
    // FlagSolidTexel: the alpha is reflectivity, folded into the colour as the engine's env-off bake does
    // (rgb = mix(env grey, rgb, a), then a = 1; cx_ownlit_head.glsl's CX_ENV_GREY), never spent as opacity.
    if ((flags & 64) != 0) { t.rgb = mix(vec3(0.666667), t.rgb, t.a); t.a = 1.0; }
    vec4 c;
    if ((flags & 4) != 0)
    {
        vec3 n = vNrm;
        float k = dot(n, n) > 1e-8 ? max(dot(normalize(n), normalize(vec3(0.35, 0.5, 0.8))), 0.0) : 1.0;
        vec3 light = GuiLin(uGui[r + 12].rgb) + GuiLin(uGui[r + 10].rgb) * 0.3 + GuiLin(uGui[r + 11].rgb) * (0.25 + 0.75 * k);
        c = vec4(GuiEnc(GuiLin(t.rgb) * light), t.a * uGui[r + 11].a);
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
