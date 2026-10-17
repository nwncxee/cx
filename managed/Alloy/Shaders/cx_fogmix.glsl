// The fog's target colour and the lerp, with the FACTOR left to the caller. The sky stage
// takes this half alone: vanilla compiles its textured unlit stages FOG 0, so the fullbright stage must never
// fog by distance -- it fogs the dome by ELEVATION, which is the one term the distance ramp cannot
// reach (SceneFog.SkyBand).

/// The LINEAR colour a fogged fragment mixes toward along this ray. Three weights, all of them
/// direction or depth -- none touches the ramp:
///  - the sky, full at and above the horizon (where the seam against the skybox is) and falling off
///    looking steeply DOWN, where there is no sky to match and the author's colour is the honest one;
///  - the FOG ITSELF. #341's first cut pulled the target all the way to the sky at every depth, so a
///    lightly hazed building took a sky-coloured haze rather than the author's. Scaling by `f` keeps
///    the near and middle of the ramp on the colour the builder picked and lets the sky win only
///    where the fog is total, which is the one place the seam exists;
///  - the sun, added rather than mixed: looking into the sun through fog is BRIGHTER than the fog,
///    and a mix could only ever tint it.
vec3 CxFogColourLin(vec3 world, float f)
{
    vec3 dir = normalize(world - uEyePos);
    float w = uFogSky.x * smoothstep(-0.35, 0.0, dir.z) * f;
    vec3 c = mix(uFogColor.rgb, uFogSkyColor, w);
    return c + uFogSunColor * (f * pow(max(dot(dir, uFogSun.xyz), 0.0), uFogSun.w));
}

/// The lerp itself. The engine fogs in DISPLAY space (fslit's tail is ApplyKeyhole,
/// RevertColorSpace, ApplyFog): mixing in linear against the same endpoints is a different CURVE --
/// it builds up late and then slams, where the engine's ramp is even. #132 moved this across the
/// encode and #336 moved it back; #341 keeps it there and fixes what batch 2 left half-done, which
/// was a display-space source mixed toward a LINEAR target. Both ends are encoded here, once.
vec3 CxFogMix(vec3 lin, vec3 world, float f)
{
    if (f <= 0.0) return lin;
    vec3 g = vec3(1.0 / $SRGB$);
    vec3 disp = mix(pow(max(lin, vec3(0.0)), g),
                    pow(max(CxFogColourLin(world, f), vec3(0.0)), g), f);
    return pow(max(disp, vec3(0.0)), vec3($SRGB$));
}
