// CX-303: the stock helpers both content environments share (cx_content_f, cx_content_v) -- inc_common's colour,
// hash and noise functions, under the engine's names. Every one is guarded, so a stage that defines its own copy
// keeps it: the translator defines CX_CON_HAS_<name> for each function the stage defines. ApplyColorSpace's vec3
// pair comes from the stage's side (cx_vanenv_f for a fragment stage, cx_content_v for a vertex one).

// A translation by t as a matrix: the camera-centred frame's view matrix and its inverse.
mat4 CxConView(vec3 t) { return mat4(1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, t.x, t.y, t.z, 1.0); }
// The lit vertex stage turns the UVs by (cos, sin) = d.rot.xy; this is that turn as the engine's texture matrix.
mat4 CxConTexMat(vec2 cs) { return mat4(cs.x, cs.y, 0.0, 0.0, -cs.y, cs.x, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0); }

#ifndef CX_CON_HAS_ApplyColorSpace
float ApplyColorSpace(float c) { return ApplyColorSpace(vec3(c)).x; }
vec4 ApplyColorSpace(vec4 c) { return vec4(ApplyColorSpace(c.rgb), c.a); }
#endif
#ifndef CX_CON_HAS_RevertColorSpace
float RevertColorSpace(float c) { return RevertColorSpace(vec3(c)).x; }
vec4 RevertColorSpace(vec4 c) { return vec4(RevertColorSpace(c.rgb), c.a); }
#endif
#ifndef CX_CON_HAS_ColorToBrightness
float ColorToBrightness(vec3 c) { return max(c.r, max(c.g, c.b)); }
float ColorToBrightness(vec4 c) { return max(c.r, max(c.g, c.b)); }
#endif
#ifndef CX_CON_HAS_ColorToLuma
float ColorToLuma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
float ColorToLuma(vec4 c) { return dot(c.rgb, vec3(0.299, 0.587, 0.114)); }
#endif
#ifndef CX_CON_HAS_ColorClamp
vec3 ColorClamp(vec3 c) { return clamp(c, 0.0, 1.0); }
vec4 ColorClamp(vec4 c) { return clamp(c, 0.0, 1.0); }
#endif
#ifndef CX_CON_HAS_sqr
float sqr(float a) { return a * a; }
#endif
// Hash and noise helpers. The hashes are inc_common's own formulas (a sine hash, interleaved gradient noise); the
// noises are a gradient noise of Alloy's own that answers in the same [-1, 1] range under the stock names, so a
// stage calling them compiles and varies smoothly -- the exact pattern is not the engine's.
#ifndef CX_CON_HAS_Random
float Random(vec2 v) { return fract(sin(dot(v, vec2(12.9898, 78.233))) * 43758.5453); }
float Random(vec3 v) { return fract(sin(dot(v, vec3(12.9898, 78.233, 10.1523))) * 43758.5453); }
#endif
#ifndef CX_CON_HAS_InterleavedGradientNoise
float InterleavedGradientNoise(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }
#endif
vec2 CxConGrad2(vec2 c) { float a = 6.2831853 * fract(sin(dot(c, vec2(127.1, 311.7))) * 43758.5453); return vec2(cos(a), sin(a)); }
vec3 CxConGrad3(vec3 c)
{
    vec2 h = fract(sin(vec2(dot(c, vec3(127.1, 311.7, 74.7)), dot(c, vec3(269.5, 183.3, 246.1)))) * 43758.5453);
    float z = h.x * 2.0 - 1.0, a = 6.2831853 * h.y, r = sqrt(max(1.0 - z * z, 0.0));
    return vec3(r * cos(a), r * sin(a), z);
}
#ifndef CX_CON_HAS_NoiseSimplex2D
float NoiseSimplex2D(vec2 p)
{
    vec2 i = floor(p), f = fract(p), u = f * f * (3.0 - 2.0 * f);
    float a = dot(CxConGrad2(i), f), b = dot(CxConGrad2(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0));
    float c = dot(CxConGrad2(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0)), d = dot(CxConGrad2(i + 1.0), f - 1.0);
    return 1.4142 * mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}
#endif
#ifndef CX_CON_HAS_NoiseSimplex3D
float NoiseSimplex3D(vec3 p)
{
    vec3 i = floor(p), f = fract(p), u = f * f * (3.0 - 2.0 * f);
    float n000 = dot(CxConGrad3(i), f), n100 = dot(CxConGrad3(i + vec3(1, 0, 0)), f - vec3(1, 0, 0));
    float n010 = dot(CxConGrad3(i + vec3(0, 1, 0)), f - vec3(0, 1, 0)), n110 = dot(CxConGrad3(i + vec3(1, 1, 0)), f - vec3(1, 1, 0));
    float n001 = dot(CxConGrad3(i + vec3(0, 0, 1)), f - vec3(0, 0, 1)), n101 = dot(CxConGrad3(i + vec3(1, 0, 1)), f - vec3(1, 0, 1));
    float n011 = dot(CxConGrad3(i + vec3(0, 1, 1)), f - vec3(0, 1, 1)), n111 = dot(CxConGrad3(i + 1.0), f - 1.0);
    return 1.1547 * mix(mix(mix(n000, n100, u.x), mix(n010, n110, u.x), u.y),
                        mix(mix(n001, n101, u.x), mix(n011, n111, u.x), u.y), u.z);
}
#endif
