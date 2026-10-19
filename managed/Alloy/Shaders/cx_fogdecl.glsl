// #124: the uniforms behind the camera dissolve and scene fog, in both fragment stages. All
// four come straight out of the engine's own globals -- the same values GLRender::SetUniforms
// @0x4ccbf0 hands its own shaders -- so nothing here is a reconstruction of engine state.
// #341: the fog block on its own, so a stage can fog without declaring the keyhole's
// uniforms. uEyePos lives here rather than in each stage's own head because the fog target is
// direction-dependent now and every fogging stage needs the same view ray.
uniform vec4 uFogParams;       // (enabled, start, end, 1/(end-start)) -- the engine's fogParams
uniform vec4 uFogColor;        // #132: LINEAR -- the C# side applies the engine's own sRGB curve
                               // to the display-space value before uploading it
uniform vec3 uEyePos;          // #120: this pass's camera, for the view vector
uniform vec3 uFogSky;          // #341: (sky mix, height falloff per unit, layer floor world Z)
uniform vec3 uFogSkyColor;     // #341: LINEAR sky colour the fog converges toward
uniform vec4 uFogSun;          // #341: (world direction toward the sun, in-scatter exponent)
uniform vec3 uFogSunColor;     // #341: LINEAR sun colour, already scaled by the in-scatter strength
uniform vec4 uFogOrigin;       // fog origin: xyz = the player's feet, w = 1 radial from it, 0 = view depth
// The volumetric fog (FogVolume, vfog.enabled): x = 1 where this pass reads the volume, else the ramp above draws;
// y = depth slices; z = the volume's near distance; w = 1 / log(far / near), the slice mapping cx_vfog inverts.
uniform vec4 uVFog;
uniform mat4 uVFogVp;          // the main view's unjittered view-projection the volume was built on
layout(binding = 32) uniform sampler3D uVFogVol;   // in-scattered light (LINEAR rgb) and transmittance (a) from the eye
