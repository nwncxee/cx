void main()
{
    // the cell's own colours, root to tip, off the art (GrassAtlas)
    vec3 ramp = vRoot < 0.5 ? mix(vColRoot, vColMid, vRoot * 2.0) : mix(vColMid, vColTip, vRoot * 2.0 - 1.0);
    vec3 albedo = CxLinear(ramp);
    if (uSnow.y > 0.0)
    {
        float lum = dot(albedo, vec3(0.3, 0.59, 0.11));
        albedo = mix(albedo, lum * vec3(1.15, 0.95, 0.6), uSnow.y * 0.8);
        albedo = mix(albedo, vec3(0.85), uSnow.y * smoothstep(0.45, 0.9, vRoot) * 0.85);
    }
    // R5: rain darkens the blade as it darkens the ground under it, off the same wetness map and the same
    // cover-map shelter test the cards and the lit stage read -- a blade under a roof is dry now. The soak
    // is bounded (cx_wx_soak): the linear scale this line used to be went NEGATIVE past a darkening knob
    // of 1 at full wetness, which is the black grass.
    vec4 bWx = CxWetMap(vWorld.xy);
    bool bMapped = CxWetMapped(bWx);
    float bWet = clamp((bMapped ? bWx.x * uWxMapXf.w : uLook.z)
                     * CxWetOpenFast(vWorld, vec3(0.0, 0.0, 1.0), bMapped ? CxWetShelter(bWx, vWorld.z) : 1.0), 0.0, 1.0);
    albedo = CxWetSoak(albedo, uLook.w * bWet * CX_WX_LEAF_SOAK);
    vec3 N = normalize(vNrm);
    vec3 V = normalize(uEyePos - vWorld);
    float ndlRaw = dot(N, uAreaDir);
    float ndl = clamp(ndlRaw, 0.0, 1.0);
    bool near = vDist <= uShade;
    // a blade is two-sided: shadow it on its sun-facing side, never skip it as back-facing (lit)
    float sh = near ? CxSunShadow(vWorld, faceforward(N, -uAreaDir, N), 1.0, abs(dot(N, uAreaDir))) : 1.0;
    if (uViz == 3) { frag = vec4(vec3(sh), 1.0); return; }
    // a little wrap: a blade is thin, light reaches past its terminator
    vec3 sun = uAreaDiffuse * clamp((ndlRaw + 0.3) / 1.3, 0.0, 1.0) * sh;
    vec3 point = uVLit != 0 ? vLight : ((uLit != 0 && near) ? CxClusteredDiffuse(vWorld, N, vDepth) : vec3(0.0));
    // backlit: the sun behind the field shines THROUGH it, tinted by the blade, strongest toward the tip
    float back = pow(clamp(dot(-V, uAreaDir), 0.0, 1.0), 4.0) * uLook.x * (0.35 + 0.65 * vRoot);
    float sheen = pow(clamp(dot(N, normalize(uAreaDir + V)), 0.0, 1.0), 40.0) * uLook.y * vRoot;
    // self-occlusion inside the clump: the root sits in its neighbours' shade
    float ao = mix(1.0 - uRootAo, 1.0, sqrt(vRoot));
    vec3 lit = albedo * ((uAreaAmbient * vMatAmb * CxSkyView(vWorld, CX_SKY_UP) + point * vMatDif) * ao
                        + (sun * ao + uAreaDiffuse * sh * back) * vMatDif)
             + uAreaDiffuse * sh * sheen;
    // CX-51: a blade reflects as the ground around it does -- the probe, under the split-sum
    // weight at the roughness the wet splice gives that ground (the legacy 0.6, wetted toward 0.35). Without
    // it a lit or reflective patch of ground shows around blades that ignore it, as if glued on.
    if (uProbeMips > 0.0)
    {
        float gRough = mix(0.6, 0.35, bWet);
        vec3 Nv = faceforward(N, -V, N);   // two-sided: the face toward the eye
        vec3 R = reflect(-V, Nv);
        vec2 gAb = CxEnvBrdf(max(dot(Nv, V), 1e-3), sqrt(gRough));
        float gW = min((CX_DIELECTRIC_F0 * gAb.x + gAb.y) * uProbeGain, 1.0);
        vec3 gEnv = CxProbeFetch(uProbe, vWorld, R, sqrt(gRough) * (uProbeMips - 1.0));
        lit += gEnv * (gW * ao);
    }
    CxReflNone();   // the trace leaves a blade alone: the record under it is the ground it covers
    vec3 col = CxFog(lit, vWorld, vDepth);
    if (uGroundOn != 0)
    {
        vec3 g = texelFetch(uGround, ivec2(gl_FragCoord.xy), 0).rgb;
        if (uGroundTint > 0.0) col = mix(g, col, max(smoothstep(0.0, uGroundTint, vRoot), 1.0 - vLookDown));
        // the ladder's thinning edge and the far cut go out INTO the ground, not out of the frame: a blade on
        // its way out takes the colour of what is behind it, so no edge line and no pop
        col = mix(g, col, vFade);
    }
    frag = vec4(CxDisplayOverlays(col), 1.0);
}
