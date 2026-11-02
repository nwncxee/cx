// The far rain veil the volumetric fog's froxels carry, where the near lattice has thinned out, and what a
// rain-only build of that volume scatters. Every function below is self-contained -- it reads no uniform and
// no file constant -- so the precipitation pass can include this for CxWxNearKeep alone and nothing here
// needs a second home. Needs CxVfHaze (cx_vfog_cs) ahead of it.
#ifndef CX_VFRAIN_LIB
#define CX_VFRAIN_LIB

/// The share of the near lattice's drops kept `dist` metres from the camera: whole close by, nothing at the
/// lattice's end and past it, and no step anywhere between.
float CxWxNearKeep(float dist)
{
    // where the lattice ends, and where it starts thinning: its own radial fade's 40% of that reach
    const float reach = 25.0, full = 10.0;
    return 1.0 - smoothstep(full, reach, dist);
}

/// The veil's share of one whole rain `dist` metres out under `open` sky (1 open, 0 roofed): exactly what
/// CxWxNearKeep stopped keeping, so drops and veil sum to one rain at every distance, thinned under a roof.
float CxVfRain(float dist, float open)
{
    // what still drifts in under a roof: a froxel is one cover sample, and wind carries rain past a lip
    const float roofed = 0.15;
    return (1.0 - CxWxNearKeep(dist)) * mix(roofed, 1.0, clamp(open, 0.0, 1.0));
}

/// The air's extinction per metre at `world`: the fog's own haze where `haze` says this build draws it, plus
/// `rain` of a whole rain's veil.
float CxVfDensity(vec3 world, float dist, float open, float rain, float haze)
{
    // a whole rain's extinction per metre: a quarter of a 30 m view taken by the veil alone
    const float fall = 0.01;
    return haze * CxVfHaze(world) + rain * fall * CxVfRain(dist, open);
}

/// The light the air scatters toward the eye: `sun` the ambient and the sun's beam, `lamps` the clustered
/// lamps' glow. A rain-only build (`haze` 0) carries none of it -- a veil is not the fog a lamp lights.
vec3 CxVfScatter(vec3 sun, vec3 lamps, float haze)
{
    return haze > 0.0 ? sun + lamps : sun;
}
#endif
