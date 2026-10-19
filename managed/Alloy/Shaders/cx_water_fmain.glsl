// #339's fragment stage, part two: stock fswater.shd, ported onto Alloy's plumbing.
// What is vanilla here. The wave field (SetupWind, GetMainWaveOffset,
// GetWavesOffset, GetSurfaceNormal), the parallax displacement search, the ray march
// (GetRayIntersectCoords/GetReflectionCoords), and the composite -- liquid body times the
// subsurface light, Beer-Lambert refraction through the copy, a screen-space reflection scaled by
// Schlick at water's own F0, and the lit surface standing in wherever the reflection ray misses. Line
// for line from the current fswater.shd (ovr.bif), with the engine's names swapped for this pass's.
// What is Alloy. Where stock calls ComputeLighting it gets the engine's forward
// light loop; here it gets the froxel walk, the sun cascades and the local cubes -- which is the whole
// point of drawing water in the owned pass at all. SubsurfaceLight is inc_lighting's wrapped
// diffuse accumulator (inc_lighting.shd:301, :322): the sun gets the wrap term exactly, and the
// clustered lights get plain Lambert, because the cluster walk has no wrap parameter and adding one
// would fork a lib four other programs share.
// What is dropped, and it is visible. SetupLocalWind and the
// windPointSources array -- the splash rings a creature or a spell leaves on the surface. The
// array is engine state with no Alloy feed; it needs its own RE and its own ticket.
// ponytail: no ripple sources. Add them when the engine's wind-source array has a reader (#340).

float CxSqr(float x) { return x * x; }

#include "cx_raymarch.glsl"

/// The copy's depth as a positive view distance (cx_raymarch), at this pass's own near/far.
float CxSceneZ(vec2 uv) { return CxMarchZ(uFbDepth, uNearFar, uv); }

/// The copy's colour. #132: linear already, so NO decode -- stock's ApplyColorSpace on this read is
/// the one line of it that must not survive the port.
vec3 CxFbColor(vec2 uv)
{
    return texture(uFbColor, uv).rgb;
}

// ---- the wave field: fswater.shd, unchanged except for where its inputs come from ---------------

float fTick;
float fAreaWindForce;
vec3 vAreaWindDirection;
vec3 vAreaWindDirectionDistorted;
float fMainWaveAmplitude;
float fMainWavePeriod;
float fMainWaveSpeed;
float fMainWaveNoiseModifier;
float fMainWaveNoiseFrequency;
float fMainWaveNoiseDisplacement;
float fArealVariation;
float fSurfaceNdotV;
vec3 vSurfaceNormalWorld;

/// vswater.shd's SetupVertexWind. Stock runs it per VERTEX and interpolates the results; every one
/// of them is a per-frame constant, so computing it here is the same value and one varying fewer.
void CxSetupVertexWind()
{
    fTick = CxClock() * (0.001 * CX_ANIMATION_SPEED);
    fAreaWindForce = dot(uAreaWind, uAreaWind);
    vAreaWindDirection = fAreaWindForce > 0.0 ? uAreaWind * inversesqrt(fAreaWindForce)
                                              : vec3(0.707, 0.707, 0.0);
    fAreaWindForce += 0.05;
}

void CxSetupWind(float fFragmentDepthOffset)
{
    fMainWaveAmplitude = 0.4 * fAreaWindForce;
    fMainWavePeriod = 1.5 / (fAreaWindForce + 1.0);
    fMainWaveSpeed = 0.6 + 0.05 / fAreaWindForce;
    fMainWaveNoiseModifier = 0.4 / (0.4 + fAreaWindForce);
    fMainWaveNoiseFrequency = 0.1;
    fMainWaveNoiseDisplacement = 0.25 + 0.5 * fAreaWindForce;

    fArealVariation = texture(uNoise, 0.003 * vWorld.xy).r * 2.0 - 1.0;
    vAreaWindDirectionDistorted = vAreaWindDirection;
    vAreaWindDirectionDistorted.xy += vec2(vAreaWindDirection.x, -vAreaWindDirection.y)
                                    * fArealVariation * 0.05;
    fArealVariation *= fArealVariation;
    fMainWaveAmplitude *= CxSqr(1.0 - fArealVariation);
    // Reduce at vertical.
    fMainWaveAmplitude *= max(CxSqr(vSurfaceNormalWorld.z) * 101.0 - 100.0, 0.0);

    float fAmplitudeNoise = texture(uNoise, fMainWavePeriod * 0.01 * vWorld.xy
                                          - 0.005 * fTick * fMainWaveSpeed * vAreaWindDirection.xy).r;
    fMainWaveAmplitude *= fAmplitudeNoise;
    // WATER_DYNAMIC_DEPTH 0: the amplitude is faded out where the surface is nearly edge-on or the
    // water is thin, because nothing displaces the DEPTH this fragment writes.
    fMainWaveAmplitude *= min(1.0, fSurfaceNdotV * fFragmentDepthOffset);
}

float CxMainWaveOffset(inout vec3 vPos)
{
    // ponytail: stock calls SetupLocalWind(vPos) here for the windPointSources splash rings. No feed
    // for that array yet (#340); the offset it would add is zero.
    float fWaveOffset = 0.0;

    float fMainWaveNoise = 1.0 - texture(uNoise, fMainWavePeriod * 0.04 * vPos.xy
                                               - 0.01 * fTick * fMainWaveSpeed * vAreaWindDirection.xy).r;
    vPos -= vAreaWindDirection * (fMainWaveNoise * fMainWaveAmplitude * 2.0 - 5.0 * fArealVariation);
    float fNoise = texture(uNoise, -vPos.xy * fMainWaveNoiseFrequency).r * fMainWaveNoiseModifier;

    float fWave = abs(mod(fTick * fMainWaveSpeed
                          - dot(vPos.xy - fNoise * vAreaWindDirectionDistorted.xy * fMainWaveNoiseDisplacement,
                                vAreaWindDirectionDistorted.xy) * fMainWavePeriod,
                          2.0) - 1.0);
    fWave *= fWave;
    fWave = 2.0 * fWave - CxSqr(fWave);
    fWave += CxSqr(fWave);
    fWaveOffset += fMainWaveAmplitude * CxSqr(fMainWaveNoise) * (fWave - 1.0);
    return fWaveOffset;
}

float CxWavesOffset(vec3 vPos)
{
    const vec2 vRotate = vec2(-0.5735764363, 0.819152044);
    const mat2 M = mat2(vRotate, vec2(-vRotate.y, vRotate.x));
    vec2 vDirection = -vAreaWindDirection.xy;

    float fWave = CxMainWaveOffset(vPos);

    float fNoiseFrequency = 0.25;
    float fAmplitude = 0.005;
    float fSpeed = 0.14;

    vPos.xy += vPos.z * 0.1;
    for (int i = 0; i < CX_WAVE_ITERATIONS; i++)
    {
        fWave -= fAmplitude
               * abs(texture(uNoise, vPos.xy * fNoiseFrequency + vDirection.xy * fTick * fSpeed).r * 2.0 - 1.0)
               * clamp(1.0 - 5.0 * dot(vDirection, vSurfaceNormalWorld.xy), 0.0, 1.0);
        vDirection = M * vDirection;
        fAmplitude *= 0.75;
        fNoiseFrequency *= 1.7;
    }
    return fWave;
}

/// The surface normal, in WORLD space -- stock builds it here and converts to view afterwards; the
/// Alloy lighting libs want world, so the conversion is the one that goes away.
vec3 CxWaterNormal(vec3 vPos)
{
    vec3 vTangent = vec3(1.0, 0.0, 0.0);
    vec3 vBitangent = vec3(0.0, 1.0, 0.0);
    // If we are on a tilted plane, translate the normal into this plane.
    if (abs(vSurfaceNormalWorld.z) < 0.99)
    {
        vTangent = normalize(sign(vSurfaceNormalWorld.z) * vec3(-vSurfaceNormalWorld.y, vSurfaceNormalWorld.x, 0.0));
        vBitangent = cross(vSurfaceNormalWorld, vTangent);
    }

    float fOffset = 0.05;
    float w0 = CxWavesOffset(vPos);
    float wx = CxWavesOffset(vPos + vTangent * fOffset);
    float wy = CxWavesOffset(vPos + vBitangent * fOffset);

    vec3 vNormal = cross(vTangent * fOffset + vSurfaceNormalWorld * (wx - w0),
                         vBitangent * fOffset + vSurfaceNormalWorld * (wy - w0));
    return normalize(vNormal);
}

/// The copy's normal at uv (cx_raymarch), at this pass's own projection and pixel size.
vec3 CxCopyNormal(vec2 uv) { return CxMarchNormal(uFbDepth, uProj, uNearFar, uPixel.xy, uv); }

/// inc_common.shd's CalculateEnvironmentTexCoords: the sphere-map lookup stock reflects the
/// environment with, from the VIEW-space fragment normal and the view position. Verbatim -- the
/// half-offset and the 0.25 in the y term are the engine's own framing of its env textures, and a
/// "tidier" projection here is a different reflection.
vec2 CxEnvCoords(vec3 nView, vec3 posView)
{
    vec2 vEnvReflect = nView.xy / (1.0 + abs(nView.z));
    vEnvReflect += 0.5 * (posView.xy / (-posView.z + 0.2));
    return 0.5 * vEnvReflect + vec2(0.5, 0.25);
}

/// The fog AMOUNT alone -- stock scales the refraction's transparency by (1 - GetFogAlpha), so the
/// riverbed stops showing through where the fog has already swallowed it. Same expression CxFog
/// uses; only the mix is different.
float CxFogAlpha(float depth)
{
    return uFogParams.x == 0.0 ? 0.0 : clamp((depth - uFogParams.y) * uFogParams.w, 0.0, 1.0);
}

void main()
{
    CxDraw d = uDraw[vDraw];
    // /alloy viz 4 is the coverage view: cyan is Alloy lit and magenta owned-fullbright, so
    // water takes BLUE.
    if (uViz == 4) { frag = vec4(0.0, 0.0, 1.0, 1.0); return; }

    vSurfaceNormalWorld = normalize(vNrm);
    CxSetupVertexWind();

    vec3 V = normalize(uEyePos - vWorld);
    vec2 vScreenCoords = gl_FragCoord.xy * uPixel.xy;
    vec3 vPosView = (uView * vec4(vWorld, 1.0)).xyz;
    vec3 vViewToSurface_n = normalize(vPosView);

    // The liquid's own colour is the diffuse's AVERAGE, not a texel -- stock reads it at LOD 20 for
    // exactly this reason: a water material's texture is a ripple pattern, and sampling it per
    // fragment paints that pattern onto a surface whose shape comes from the wave field instead.
    vec4 baseTexel = textureLod(uTex, vec2(0.5), 20.0);
    vec4 WaterBaseColor = vec4(CxLinear(baseTexel.rgb), baseTexel.a);

    float fScreenFBDepth = CxSceneZ(vScreenCoords);
    fSurfaceNdotV = dot(vSurfaceNormalWorld, V);
    CxSetupWind(fScreenFBDepth + vPosView.z);

    // The displacement search: where on the (raised) wave surface this pixel's ray actually lands.
    // Stock's iteration, unchanged -- it is what turns a fragment-side wave field into something with
    // parallax instead of a texture painted flat on the plane.
    vec3 vWaterSurfaceCoords = vWorld;
    vec3 vCameraToWorld_n = normalize(vWorld - uEyePos);
    float fDisplaceFactor = 1.0 / (vCameraToWorld_n.z > 0.0 ? vCameraToWorld_n.z + CX_VECTOR_LIMIT
                                                            : vCameraToWorld_n.z - CX_VECTOR_LIMIT);
    vec2 vWorldOffset = vCameraToWorld_n.xy * fDisplaceFactor;

    float fDistance = CxMainWaveOffset(vWaterSurfaceCoords);
    float fSegmentSize = 1.0;
    vWaterSurfaceCoords.xy = vWorld.xy + vWorldOffset * fDistance;
    for (int n = 1; n < CX_DISPLACEMENT_ITERATIONS; n++)
    {
        float fMainWaveOffset = CxMainWaveOffset(vWaterSurfaceCoords);
        fSegmentSize *= 0.5;
        fDistance += (fMainWaveOffset - fDistance) * fSegmentSize;
        vWaterSurfaceCoords.xy = vWorld.xy + vWorldOffset * fDistance;
    }

    vec3 vPosViewThis = (uView * vec4(vWorld + fDisplaceFactor * vCameraToWorld_n * fDistance, 1.0)).xyz;
    vViewToSurface_n = normalize(vPosViewThis);
    float fDepthOffset = max(fScreenFBDepth + vPosViewThis.z, 0.0);

    vec3 N = CxWaterNormal(vWaterSurfaceCoords);
    // rain rings the surface too (weather.splash), on a level one; the same world-sized rings the puddles carry
    float fSplashFade = 0.0;
    if (uSplash > 0.0 && vSurfaceNormalWorld.z > 0.9)
        N = normalize(N + vec3(CxWeatherRipple(vWaterSurfaceCoords.xy, uSplash, CxWeatherImpactPx(vWorld, V), fSplashFade), 0.0));
    vec3 vFragmentNormal = normalize(mat3(uView) * N);

    // ---- lighting: stock's ComputeLighting, answered by the froxel walk ----------------------
    // fRoughness = 0.00025 + 0.00025*-vPosView.z under SPECULAR_DISTRIBUTION_MODEL 1, and
    // fSpecularity = 1.0 -- stock sets it to one on purpose and factors water's real F0 in at the
    // composite, through the Fresnel term. Doing it in both places is the double-count to avoid.
    float fRoughness = 0.00025 + 0.00025 * -vPosView.z;
    if (uSplash > 0.0 && vSurfaceNormalWorld.z > 0.9) fRoughness = CxWeatherRippleRough(fRoughness, fSplashFade, 1.0, uSplash);
    float NdotV = dot(N, V);
    float sunRaw = clamp(dot(N, uAreaDir), 0.0, 1.0);
    float sunVis = CxSunShadow(vWorld, N, sunRaw, sunRaw);

    vec3 pointSpec;
    vec3 pointDiffuse = CxClusteredLight(vWorld, N, V, vDepth, 0.0, fRoughness, 1.0, pointSpec);
    // FragmentColor in stock: lighting on a BLACK albedo, so what survives is the specular alone.
    vec3 ShadedColor = (uAreaDiffuse * (sunRaw * sunVis) * CxSpecLobe(N, V, uAreaDir, fRoughness, 1.0)
                        + pointSpec) * CxSpecBase(NdotV, fRoughness);
    // ...plus the ENVIRONMENT term, which is the largest single thing the first cut of this port was
    // missing (inc_lighting.shd:542-596). Stock adds `(AmbientLight + DiffuseLight) * envTexel *
    // fEnvironmentSpecularity` into the specular light, and for water that last factor is exactly
    // ONE: the specularity is 1.0 on this path, and SPECULAR_FRESNEL 1 makes it (:549)
    // mix(GetFresnelSpecularity(1.0, NdotV), 1.0, sqrt(rough)), every term of which is 1. So the scene's
    // environment map arrives at full strength, sampled through the WAVE-perturbed normal -- which is
    // what makes a ripple legible at all. Without it the surface is body colour plus a 2%-Fresnel
    // reflection, i.e. flat paint, however correct the wave field underneath is.
    vec3 DiffuseLight = uAreaDiffuse * (sunRaw * sunVis) + pointDiffuse;
    if (uEnvOk != 0)
    {
        float envLod = clamp(fRoughness * CX_ENV_LOD_MOD + CX_ENV_LOD_OFF, 0.0, CX_ENV_LOD_MAX);
        vec3 envTexel = CxLinear(texture(uEnvMap, CxEnvCoords(vFragmentNormal, vPosViewThis), envLod).rgb);
        ShadedColor += (uAreaAmbient + DiffuseLight) * envTexel;
    }
    // inc_lighting's SubsurfaceLight (its assembly at :526-539): the wrapped per-light sum, plus
    // the area ambient OUTSIDE that sum, all scaled by the wrap's energy preservation. The sun gets
    // the wrap exactly -- off the flat SURFACE normal, max(0, N.L + wrap) (:300-301, LIGHT_SUBSURFACE_SCATTERING_WRAP
    // 1.0); the clustered lights get plain Lambert, because the cluster walk has no wrap parameter and four
    // other programs share it.
    // ponytail: no materialFrontDiffuse tint on the sum, which stock applies. The record carries that
    // colour in d.dif -- but only on a LIT batch, and an unlit water batch would read a stale slot of
    // any magnitude. Stock water materials are white there; tint it from d.dif if one ever is not.
    float sunWrap = max(dot(vSurfaceNormalWorld, uAreaDir) + CX_SSS_WRAP, 0.0) / (1.0 + CX_SSS_WRAP);
    vec3 SubsurfaceLight = (uAreaDiffuse * (sunWrap * sunVis) + pointDiffuse + uAreaAmbient)
                         * CX_SSS_ENERGY;

    // ---- refraction: the floor, seen through the surface -------------------------------------
    vec3 vRefraction = refract(vViewToSurface_n, vFragmentNormal, 1.0 / 1.33333);
    // Only the DELTA against the flat surface, which is stock's own note: it removes refraction's
    // flattening and keeps the distortion.
    vec3 vRefractSurface = refract(vViewToSurface_n, normalize(mat3(uView) * vSurfaceNormalWorld), 1.0 / 1.33333);
    vRefraction = vViewToSurface_n + (vRefraction - vRefractSurface);

    vec4 vRefractionCoords = CxRayIntersect(uFbDepth, uProj, uNearFar, vPosViewThis, vRefraction, fDepthOffset,
                                              CX_WATER_REFRACTION_MAX_RANGE,
                                              CX_WATER_REFRACTION_ITERATIONS);
    float fRefractionModifier = clamp(1.0 + 10.0 * vRefractionCoords.w, 0.0, 1.0);
    vec3 ColorRefraction = CxFbColor(mix(vScreenCoords, vRefractionCoords.xy, fRefractionModifier));

    float fRefractionTransparency = WaterBaseColor.a < 1.0
        ? exp2(-mix(fDepthOffset,
                    clamp(vPosViewThis.z - vRefractionCoords.z, 0.0, CX_WATER_REFRACTION_MAX_RANGE),
                    fRefractionModifier)
               * CX_WATER_TRANSPARENCY_FACTOR * WaterBaseColor.a / (1.0 - WaterBaseColor.a))
        : 0.0;
    ColorRefraction *= pow(fRefractionTransparency, fSurfaceNdotV);
    // under the volumetric fog the bed fades with the surface's own transmittance, as the colour does
    fRefractionTransparency *= 1.0 - (uVFog.x > 0.0 ? CxFogFactor(vWorld, vDepth) : CxFogAlpha(vDepth));

    // ---- reflection: the same march, out to the horizon --------------------------------------
    vec3 ColorReflection;
    vec3 ColorLiquid = WaterBaseColor.rgb * SubsurfaceLight;
    vec3 vReflect = reflect(vViewToSurface_n, vFragmentNormal);
    vec4 vReflectedCoords = CxReflectionCoords(uFbDepth, uProj, uNearFar, vPosViewThis, vReflect, fDepthOffset);
    float fReflectionMod = clamp(1.0 - vReflectedCoords.w, 0.0, 1.0);
    // stock's own test is on fReflectionMod >= 0 after its clamp, so the branch always runs: a ray that leaves
    // the screen samples the clamped edge, weighted down by the march's own miss measure (fswater.shd:472-482)
    ColorReflection = CxFbColor(clamp(vReflectedCoords.xy, vec2(0.001), vec2(0.999)));
    vec3 vReflectedNormal = CxCopyNormal(clamp(vReflectedCoords.xy, vec2(0.001), vec2(0.999)));
    fReflectionMod *= 1.0 - CxFogAlpha(-vReflectedCoords.z);
    ColorReflection = mix(vec3(0.0), ColorReflection,
                          clamp(0.1 - 4.0 * dot(vReflectedNormal, vReflect), 0.0, 1.0));
    // The miss and the reject both land on the SHADED surface, which is vanilla's flat-horizon
    // look. #129's probe fallback is exactly the custom shading this ticket removes.
    ColorReflection = mix(ShadedColor, ColorReflection, fReflectionMod);

    // ---- composite ---------------------------------------------------------------------------
    ColorLiquid = mix(ColorLiquid, ColorRefraction, fRefractionTransparency);
    float fFresnelSpecularity = mix(CX_WATER_SPECULARITY, 1.0, CxFresnel(NdotV));
    vec3 lit = ColorLiquid + ColorReflection * fFresnelSpecularity;

    // #129's term views, kept: water composites four things that can each be wrong on their own and
    // look identical in the sum. 9 = what is seen THROUGH the surface, 10 = the reflection,
    // 11 = fresnel as grey, 12 = the copy as sampled, 13 = the copy's linearised depth over 100m,
    // 20 = the wave field's normal.
    if (uViz == 9)  { frag = vec4(ColorLiquid, 1.0); return; }
    if (uViz == 10) { frag = vec4(ColorReflection, 1.0); return; }
    if (uViz == 11) { frag = vec4(vec3(fFresnelSpecularity), 1.0); return; }
    if (uViz == 12) { frag = vec4(CxFbColor(vScreenCoords), 1.0); return; }
    if (uViz == 13) { frag = vec4(vec3(clamp(fScreenFBDepth / 100.0, 0.0, 1.0)), 1.0); return; }
    if (uViz == 20) { frag = vec4(N * 0.5 + 0.5, 1.0); return; }
    // 28 = the environment term alone -- the scene env map through the wave normal, times the light
    // on the water. Black here with a live scene means uEnvOk is 0, i.e. Scene+0x1e8 held nothing.
    if (uViz == 28)
    {
        frag = vec4(uEnvOk != 0
                    ? CxLinear(texture(uEnvMap, CxEnvCoords(vFragmentNormal, vPosViewThis),
                                         clamp(fRoughness * CX_ENV_LOD_MOD + CX_ENV_LOD_OFF,
                                               0.0, CX_ENV_LOD_MAX)).rgb)
                    : vec3(0.0), 1.0);
        return;
    }
    // 21 = the noise texture, sampled the way the wave field samples it. A flat colour means it is
    // not bound (every read the same) or not REPEATing (one edge texel over the whole body) -- and
    // either one drives the wave amplitude to exactly zero, which reads as a mirror-flat sea with
    // working reflections. That is the shape of the first cut's bug, so it gets its own view.
    if (uViz == 21) { frag = vec4(vec3(texture(uNoise, vWorld.xy * 0.1).r), 1.0); return; }
    // 22 = the three terms that can each zero the sea on their own, in one view, because a flat
    // surface looks identical whichever it was. RED = the part's own normal .z, which the amplitude
    // is gated on through max(z^2*101-100, 0): anything below 0.995 kills every wave whatever the
    // wind is. GREEN = the wave amplitude over 2 units. BLUE = the water's thickness at this pixel
    // over 10 units, which the amplitude is faded by (stock's WATER_DYNAMIC_DEPTH 0 branch) -- black
    // blue means the depth copy says the floor is AT the surface and the fade takes the sea out.
    if (uViz == 22)
    {
        frag = vec4(clamp(abs(vSurfaceNormalWorld.z), 0.0, 1.0),
                    clamp(fMainWaveAmplitude * 0.5, 0.0, 1.0),
                    clamp((fScreenFBDepth + vPosView.z) * 0.1, 0.0, 1.0), 1.0);
        return;
    }
    // 23 = the wave OFFSET the parallax search converged on, grey around 0.5. Structure here with a
    // black green channel in 22 would mean the amplitude survived after all and the shading is what
    // is hiding the sea; flat grey means the field itself is dead.
    if (uViz == 23) { frag = vec4(vec3(clamp(fDistance * 0.5 + 0.5, 0.0, 1.0)), 1.0); return; }
    // 24/25 = the two halves of the wave FUNCTION, once 22 has shown the amplitude is healthy.
    // 24 is the phase the mod runs on -- banding at a few metres is correct, a flat field means the
    // fTick term has swamped the position term and float precision has eaten the variation. 25 is
    // the advected noise the amplitude is shaped by, whose UV carries the same fTick.
    if (uViz == 24)
    {
        vec3 p = vWorld;
        float fNoise = texture(uNoise, -p.xy * fMainWaveNoiseFrequency).r * fMainWaveNoiseModifier;
        float phase = fTick * fMainWaveSpeed
                    - dot(p.xy - fNoise * vAreaWindDirectionDistorted.xy * fMainWaveNoiseDisplacement,
                          vAreaWindDirectionDistorted.xy) * fMainWavePeriod;
        frag = vec4(vec3(mod(phase, 2.0) * 0.5), 1.0); return;
    }
    // 26 = the phase's INPUTS, after 24 showed the phase itself varying by a tenth of a cycle
    // where it should sweep several. RED and GREEN are the fragment's world x and y in 10-unit
    // bands (one NWN tile): a flat field means vWorld barely moves across a surface that plainly
    // spans tiles, and every world-space term in the shader is then meaningless. BLUE is the
    // spatial half of the phase alone, with the clock left out, so a banded red/green over a flat
    // blue puts the fault in fMainWavePeriod -- i.e. in the wind force the SHADER sees, whatever
    // the census printed on the CPU.
    if (uViz == 26)
    {
        frag = vec4(fract(vWorld.x * 0.1), fract(vWorld.y * 0.1),
                    fract(dot(vWorld.xy, vAreaWindDirectionDistorted.xy) * fMainWavePeriod * 0.5),
                    1.0);
        return;
    }
    // 27 = the two scalars the phase is built from, as a readable ramp rather than a number:
    // RED is fAreaWindForce over 8 (4,05 reads as half), GREEN is fMainWavePeriod over 1 (0,396
    // reads as a dark 0,4). Flat is expected -- what matters is WHICH value, and a shader that
    // disagrees with the census line is a uniform that never arrived.
    if (uViz == 27)
    {
        frag = vec4(clamp(fAreaWindForce * 0.125, 0.0, 1.0),
                    clamp(fMainWavePeriod, 0.0, 1.0),
                    clamp(length(vAreaWindDirectionDistorted.xy), 0.0, 1.0), 1.0);
        return;
    }
    if (uViz == 25)
    {
        frag = vec4(vec3(1.0 - texture(uNoise, fMainWavePeriod * 0.04 * vWorld.xy
                                     - 0.01 * fTick * fMainWaveSpeed * vAreaWindDirection.xy).r), 1.0);
        return;
    }
    if (uCapture != 0) { frag = vec4(lit, 1.0); return; }

    float kh = CxKeyhole(d.rot.z, vWorld, vDepth);
    if (kh < 0.0) discard;
    // Stock's own `FragmentColor.a = 1.0`: everything behind the surface has already been
    // composited in by the refraction term, so letting the framebuffer show through counts the
    // floor twice. rot.w is below 1 only on a fading tile.
    frag = vec4(CxDisplayContent(CxFog(lit * kh, vWorld, vDepth)), d.rot.w);
}
