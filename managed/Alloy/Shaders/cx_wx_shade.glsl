/// The share of a pixel a drop, flake or roof drip covers (kind 1 rain, 2 snow, 3 drips) at quad coordinate `uv`:
/// its shape, its contact fade `k` against the scene behind, and its hashed brightness `seed`. Colour and velocity
/// both read this one value, so the pixels that take a drop's motion are the ones that show it.
float CxWxCover(int kind, vec2 uv, float k, float seed)
{
    float a;
    if (kind == 2) { float r = length(uv); a = r >= 1.0 ? 0.0 : smoothstep(1.0, 0.5, r); seed = 0.5 + 0.5 * seed; }
    else { a = (1.0 - uv.x * uv.x) * (1.0 - uv.y * uv.y); a = a * a; }
    return a * k * k * seed;
}

/// A drop, flake or roof drip (kind 1 rain, 2 snow, 3 drips) as premultiplied colour over what is behind it: it
/// hides a fixed share of the scene whatever light is on it, shows `behind` (the sky bent through it) in that
/// share, and throws `scatter` toward the eye. `cov` is the share of the pixel it covers.
vec4 CxWxShade(int kind, float cov, vec3 scatter, vec3 behind)
{
    float ext = kind == 2 ? 0.18 : kind == 3 ? 0.1 : 0.06;
    float albedo = kind == 2 ? 0.8 : 0.25;
    return vec4((scatter * albedo + behind * ext) * cov, ext * cov);
}

#ifdef CX_WX_LAMPS
/// One lamp's light on a drop at P: the lamp's own curve and reach, from wherever it stands, since a drop has no
/// surface to turn away from it. Zero past the reach the builder gave the lamp.
vec3 CxWxLamp(vec4 pr, vec3 col, vec3 P)
{
    vec3 c, L; float att, f2, ndlRaw, ndl;
    vec3 N = normalize(pr.xyz - P + vec3(0.0, 0.0, 1e-4));
    if (!CxPointLight(pr, col, P, N, c, L, att, f2, ndlRaw, ndl)) return vec3(0.0);
    return c * att;
}

/// Every lamp binned into P's cluster on a drop, each cube-shadowed with one tap as the forward walk does, and
/// dimmed by day as the walk dims it. Ambient-class lights light it unshadowed, as vanilla lights them.
vec3 CxWxLamps(vec3 P, float viewDepth)
{
    cxLod = 1.0;
    uint cl = CxCluster(viewDepth);
    uint n = CxListWalk(cl);
    vec3 acc = vec3(0.0);
    for (uint k = 0u; k < n; k++)
    {
        uint li = CxListIdx(cl, k);
        vec4 pr = uLights[li * 2u];
        vec4 cs = uLights[li * 2u + 1u];
        vec3 l = CxWxLamp(pr, cs.rgb, P);
        if (dot(l, l) <= 0.0) continue;
        int slot = CxLightSlot(int(cs.w));
        if (slot >= 0)
            l *= mix(uLocalFloor, 1.0, CxLocalShadowQ(slot, P, normalize(pr.xyz - P), 1.0, 0.0, viewDepth, false));
        acc += l;
    }
    return acc * uDayDim;
}

/// Henyey-Greenstein over 4 pi: how much of a light travelling along `travel` a drop sends toward the eye at `V`;
/// averages 1 over the sphere, so the forward lobe is a redistribution and not extra light.
float CxWxPhase(vec3 travel, vec3 V, float g)
{
    float mu = dot(travel, V);
    return (1.0 - g * g) / pow(max(1.0 + g * g - 2.0 * g * mu, 1e-4), 1.5);
}
#endif
