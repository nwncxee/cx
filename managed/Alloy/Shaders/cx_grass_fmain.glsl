void main()
{
    vec4 texel = CxGrassTexel();
    // COVERAGE, not a binary test. fwidth is the alpha's rate of change across this pixel, so the
    // ramp is exactly one pixel wide wherever the blade edge lands -- which is what turns a stair-
    // stepped silhouette into a shaded one. It must be computed BEFORE any discard: derivatives are
    // only defined in uniform control flow.
    float aw = max(fwidth(texel.a), 1e-5);
    // Only the DITHER path hardens the far edge: there a one-pixel ramp on a one-pixel blade reads as
    // floating pixels. Blended (softedge) or A2C, the ramp is the anti-aliasing -- a hard step on a
    // one-pixel blade is sub-pixel noise SMAA never sees an edge run in. Both stages carry this.
    float edge = (uMsaa != 0 || uSoftEdge != 0) ? uEdge : mix(uEdge, 0.01, smoothstep(0.5 * uLod.x, uLod.x, vDist));
    float cov = clamp((texel.a - CX_GRASS_CUT) / (aw * edge) + 0.5, 0.0, 1.0);
    // Where the target is multisampled, alpha-to-coverage resolves the edge properly and the dither
    // is left to carry the distance fade alone. Where it is not, the one dither carries both -- still
    // strictly softer than the hard step, and still never a semi-transparent depth-writing fragment.
    float keep = (uMsaa != 0 || uSoftEdge != 0) ? vFade : cov * vFade;
    if (keep <= 0.0 || cov <= 0.0) discard;
    if (keep < 1.0 && CxGrassDither() > keep) discard;

    vec3 N = normalize(vNrm);
    vec3 albedo = CxLinear(texel.rgb);
    // R2: rain darkens the field as it darkens the ground under it, off the SAME area wetness map the lit
    // stage reads -- so a blade under a roof is dry, which the old hard-coded copy could not tell. Where
    // the map has not reached, uLook.z is the global integrator, as CX-51 shipped it.
    vec4 gWx = CxWetMap(vWorld.xy);
    bool gMapped = CxWetMapped(gWx);
    // R5: the shelter test is the lit stage's own -- the cover map at its own resolution, not the wetness
    // map's texel. The blade stands on the ground, so the test is made against the ground's up.
    float gOpen = CxWetOpenFast(vWorld, vec3(0.0, 0.0, 1.0), gMapped ? CxWetShelter(gWx, vWorld.z) : 1.0);
    float gWet = clamp((gMapped ? gWx.x * uWxMapXf.w : uLook.z) * gOpen, 0.0, 1.0);
    // R3: Lagarde's soak -- the albedo is raised to a power, so the green deepens instead of greying, and
    // CX_WX_LEAF_SOAK holds a waxy blade well short of the exponent porous stone takes. Bounded in
    // CxWetSoak: the bare pow here was the black grass at the top of the darkening knob.
    albedo = CxWetSoak(albedo, uLook.w * gWet * CX_WX_LEAF_SOAK);
    if (uSnow.y > 0.0)
    {
        // winter: the green drains to straw (luminance under a warm tint), and the upper blade takes
        // snow -- the same albedo the ground cover uses, so a field reads as one surface
        float lum = dot(albedo, vec3(0.3, 0.59, 0.11));
        albedo = mix(albedo, lum * vec3(1.15, 0.95, 0.6), uSnow.y * 0.8);
        albedo = mix(albedo, vec3(0.85), uSnow.y * smoothstep(0.45, 0.9, vRoot) * 0.85);
    }
    float ndl = clamp(dot(N, uAreaDir), 0.0, 1.0);
    // #355: a far blade is a few pixels; the cascade and cluster taps (the frame's costliest term)
    // cannot be seen on it and are not taken. Divergent, but blades cluster by distance.
    bool near = vDist <= uShade;
    // the one cascade tap: the direct sun, the transmitted sun and the glint all take it -- on the blade's
    // sun-facing side, since a back-facing normal would read as lit and the transmission would glow in shade
    float sh = near ? CxSunShadow(vWorld, faceforward(N, -uAreaDir, N), 1.0, abs(dot(N, uAreaDir))) : 1.0;
    if (uViz == 3) { frag = vec4(vec3(sh), 1.0); return; }
    vec3 sunpoint = uAreaDiffuse * ndl * sh
                  + (uVLit != 0 ? vLight : ((uLit != 0 && near) ? CxClusteredDiffuse(vWorld, N, vDepth) : vec3(0.0)));
    // vanilla (inc_lighting.shd:411/:520): the ground's vertex colour joins the DIFFUSE sum as
    // StaticLight, and the ambient term carries no NdotL factor in the forced-vertex-lighting path
    vec3 ambient = uAreaAmbient * uMatAmbient * CxSkyView(vWorld, CX_SKY_UP);
    // #478: no screen-space AO on a blade. Blades are not in the depth SsaoPass reads, so the pixel's
    // occlusion is whatever stands behind the blade, and it read as the scene showing through the
    // field; the ground tint is what grounds a blade now. A root ramp on everything.
    if (uViz == 9) { frag = vec4(ambient + sunpoint * uMatDiffuse, 1.0); return; }
    vec3 lit = albedo * (ambient + sunpoint * uMatDiffuse) * mix(1.0 - uRootAo, 1.0, vRoot);
    // CX-24: the sun behind the field shines THROUGH it, tinted by the blade and strongest toward the tips,
    // and a faint glint rides the rounded normal.
    vec3 V = normalize(uEyePos - vWorld);
    float back = pow(clamp(dot(-V, uAreaDir), 0.0, 1.0), 4.0) * uLook.x * (0.35 + 0.65 * vRoot);
    float sheen = pow(clamp(dot(N, normalize(uAreaDir + V)), 0.0, 1.0), 40.0) * uLook.y * vRoot;
    lit += uAreaDiffuse * sh * (albedo * uMatDiffuse * back + sheen);

    // CX-51: a blade reflects as the ground around it does -- the probe, under the split-sum
    // weight at the roughness the wet splice gives that ground (the legacy 0.6, wetted toward 0.35). Without
    // it a lit or reflective patch of ground shows around blades that ignore it, as if glued on.
    if (uProbeMips > 0.0)
    {
        float gRough = mix(0.6, 0.35, gWet);
        vec3 Nv = faceforward(N, -V, N);   // two-sided: the face toward the eye
        vec3 R = reflect(-V, Nv);
        vec2 gAb = CxEnvBrdf(max(dot(Nv, V), 1e-3), sqrt(gRough));
        float gW = min((CX_DIELECTRIC_F0 * gAb.x + gAb.y) * uProbeGain, 1.0);
        vec3 gEnv = CxProbeFetch(uProbe, vWorld, R, sqrt(gRough) * (uProbeMips - 1.0));
        lit += gEnv * (gW * mix(1.0 - uRootAo, 1.0, vRoot));
    }
    // vanilla grass runs fsgrass, which is the stock lit shader with KEYHOLING 1 and FOG 1 -- so the
    // camera dissolve and the scene fog both apply, and dropping them would fill a low camera shot
    // with undissolved grass. Grass always dissolves: it has no per-part CanDissolve to read.
    float kh = CxKeyhole(1.0, vWorld, vDepth);
    if (kh < 0.0) discard;
    CxReflNone();   // the trace leaves a blade alone: the record under it is the ground it covers
    vec3 col = CxFog(lit * kh, vWorld, vDepth);
    // Sprouting from the ground: the blade's base takes the colour the frame already has BEHIND
    // this fragment -- lit, shadowed and fogged terrain, pavement, whatever is there -- and fades to
    // its own colour over the lower uGroundTint of its height. Read at gl_FragCoord, as the water
    // stage reads the same copy: the pixel the blade is about to cover, no projection to get wrong.
    // A tuft on stone is grey at the base and green at the tip. /grass gtint 0 is the blade alone.
    if (uGroundViz != 0)
    {
        ivec2 gs = textureSize(uGround, 0);
        if (uGroundViz == 1) frag = vec4(texelFetch(uGround, ivec2(gl_FragCoord.xy), 0).rgb, 1.0);
        else if (uGroundViz == 2) frag = vec4(texelFetch(uGround, gs / 2, 0).rgb, 1.0);
        else if (uGroundViz == 3) frag = vec4(gl_FragCoord.xy / vec2(gs), 0.0, 1.0);
        else frag = vec4(texture(uGround, gl_FragCoord.xy / vec2(gs)).rgb, 1.0);
        return;
    }
    if (uGroundOn != 0 && uGroundTint > 0.0)
        col = mix(texelFetch(uGround, ivec2(gl_FragCoord.xy), 0).rgb, col, max(smoothstep(0.0, uGroundTint, vRoot), 1.0 - vLookDown));
    frag = vec4(CxDisplayOverlays(col), uSoftEdge != 0 ? min(cov * 2.0, 1.0) : (uMsaa != 0 ? cov : 1.0));
}
