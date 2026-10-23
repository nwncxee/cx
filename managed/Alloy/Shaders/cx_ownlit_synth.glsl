// The coverage discards: the cutout's, an OIT fragment's at zero coverage, a split wash's interior (uSolid).
// One test, so main's early ask of a plain diffuse can never discard a texel the full one keeps.
bool CxCoverCut(float coverage, float alpha, vec2 du1, vec2 du2, vec2 cutSize, float cutMip)
{
    float s = coverage * alpha;
    return uAlphaCut >= 0.0 && CxAlphaMipAt(coverage, du1, du2, cutSize, uLodBias, cutMip) <= uAlphaCut
        || uOit != 0 && s <= 0.0
        || uSolid != 0 && s < CX_SOLID_ALPHA;
}

#ifdef CX_DECAL_WRITE
// the layer's writer draws no views, so every lit input only a view reads folds out of it
#define uViz 0
#endif

void main()
{
    if (uCapture == 2 && vWorld.z < uMirrorZ) discard;
    vec3 N = normalize(vNrm);
    // /alloy viz: attribute a dim frame to a TERM instead of guessing from the composite.
    // 1 = point-light diffuse alone (torch bright here but the frame dark => the torch is fine and
    //     something else lost intensity); 2 = cluster occupancy, green empty -> red full (a black
    //     froxel where the torch stands means the binning, not the light, is at fault).
    // 4 = coverage: every Alloy draw flat, so what STAYS textured is what the engine still draws.
    //     Cyan lit / magenta fullbright, so the two Alloy variants are told apart too.
    if (uViz == 4) { frag = vec4(0.0, 1.0, 1.0, 1.0); return; }
    // 5 = the LOCAL cube term alone: white lit, black occluded, flat white where no cube-bearing
    //     light reaches. A pillar that casts nothing here is not writing depth; one that is black
    //     here but bright in the composite is being washed out by a second light.
    if (uViz == 5) { frag = vec4(vec3(CxLocalShadowViz(vWorld, N, vDepth)), 1.0); return; }
    // CX-188: the one viz here that calls the walk, and the twin is never bound under /alloy viz -- left in,
    // it would keep CxClusteredLightLm's registers alive in exactly the program built to be rid of them.
#ifndef CX_DEFERRED
    if (uViz == 1) { frag = vec4(CxClusteredDiffuse(vWorld, N, vDepth), 1.0); return; }
#endif
    if (uViz == 2)
    {
        float n = float(CxClusterN(CxCluster(vDepth))) / float(CX_CLUSTER_CAP);
        frag = vec4(n, 1.0 - n, 0.0, 1.0);
        return;
    }
#ifdef CX_BLEND_FLAT
    // /alloy bench's blended ladder, rung 1: the raster and the OIT write with no shading at all
    frag = CxOitOut(vec4(vec3(0.0), 0.5)); return;
#endif
    // Every explicit derivative BEFORE the first discard: a killed lane leaves its quad neighbours' dFdx
    // undefined, and the anchor and keyhole discards below are per-fragment.
    float aoSlope = fwidth(gl_FragCoord.z);
    float aaKernel = CxSpecAAKernel(normalize(vNrm));
    vec3 dp1 = dFdx(vWorld), dp2 = dFdy(vWorld);
    vec2 du1 = dFdx(vUV), du2 = dFdy(vUV);
    CxDraw d = uDraw[vDraw];
    int feat = int(d.feat.x);
    // #330: the keyhole verdict FIRST. It used to sit at the very end, so every fragment inside the
    // aperture paid the whole lit stage -- the cluster loop and the cube taps -- and then discarded;
    // under a keyhole through stacked roofs that was every layer at every pixel of the hole. The
    // probe capture never dissolves (a hole baked into the cube is permanent in every reflection),
    // and nor does a content pair's shadow caster, which bakes from the light, not the camera.
#ifdef CX_CAST
    float kh = 1.0;
#else
    float kh = uCapture != 0 ? 1.0 : CxKeyhole(d.rot.z, vWorld, vDepth);
#endif
    if (kh < 0.0) discard;
    // CX-273: a decal fragment the layer already folded into the surface under it draws nothing here
    if (uDecal == 2 && CxDecalOn(imageLoad(uDecalSurf, ivec2(gl_FragCoord.xy)).r, vDepth)) discard;
    int slots = int(d.pbr.w);
    // 22 = normal-map coverage: magenta where the material's map REACHED the shader, grey where it
    // did not. The one thing viz 7 cannot say, because a mesh normal and a mapped one paint the same
    // way there -- and a map that dropped out at the per-slot re-fetch looks exactly like content
    // that never declared one.
    if (uViz == 22) { frag = vec4((slots & CX_SLOT_NORMAL) != 0 ? vec3(1.0, 0.0, 1.0) : vec3(0.25), 1.0); return; }
    // A pass that discards on coverage asks it of a plain diffuse (no env, no PLT) here,
    // ahead of the maps, SSAO and the sun's taps. Uniform-gated: opaque skips it.
    // CX-303: not in a content stage's program -- its setup stand-in makes the stock stage's own cut (cx_constd_f).
    // The full test below reuses this alpha: a sample taken after the discard picks its mip from undefined quad
    // derivatives, drops edge texels the depth prepass kept, and leaves the clear colour in their holes.
    float earlyA = -1.0;
#ifndef CX_CONTENT_PROGRAM
    if ((uAlphaCut >= 0.0 || uOit != 0 || uSolid != 0) && (slots & CX_SLOT_ENV) == 0 && !CxPltOn(vDraw))
    {
        earlyA = texture(uTex, vUV, uLodBias).a;
        if (CxCoverCut(earlyA, d.rot.w, du1, du2, vec2(textureSize(uTex, 0)), uAlphaMip)) discard;
    }
#endif
    vec2 uv = vUV;
    vec3 V = normalize(uEyePos - vWorld);
    float occl = 1.0;
    // #146: outside the normal/height branch below -- a material may declare an AO map and nothing
    // else, and the engine's screen-space SSAO (an FB effect, downstream of this pass entirely) does
    // not see the contact detail a baked map carries.
    // ADR 0026: ...and on a CxOrm material the same texel is glTF's packed occlusion/roughness/metallic,
    // R still the occlusion, so the one fetch serves both readings.
    vec3 orm = (slots & CX_SLOT_AO) != 0 ? texture(uAoTex, uv, uLodBias).rgb : vec3(1.0);
    occl *= orm.r;
    float ormOn = d.ext2.y > 0.5 && (slots & CX_SLOT_AO) != 0 ? 1.0 : 0.0;
    // #379: ...and the screen-space term beside it, a lighting input like the map (ADR 0021)
    // ...and only where the visibility at this pixel was computed from THIS surface. The buffer comes
    // from the depth prepass, so a fragment the prepass never drew -- a blended door, anything the
    // sweep skipped, anything drawn over a hole -- would otherwise wear the occlusion of whatever
    // stands behind it. #119 makes the two passes agree on gl_Position character-for-character, so
    // for a prepassed fragment this equality is exact and the epsilon is only for the depth encode.
    float aoDepth = texelFetch(uSceneDepth, ivec2(gl_FragCoord.xy), 0).r;
    float aoDz = abs(aoDepth - gl_FragCoord.z);
    bool aoMine = uSsaoOn > 0 && aoDz < CX_AO_DEPTH_EPS;
    if (aoMine) occl *= mix(1.0, CxSsaoAt(ivec2(gl_FragCoord.xy), aoDepth, aoSlope), uAlphaCut >= 0.0 ? uAoCutout : 1.0);
    if ((slots & (CX_SLOT_NORMAL | CX_SLOT_HEIGHT)) != 0)
    {
        // CX-221: the frame the MESH supplied, from the arena's packed tangent word, or -- for a mesh
        // whose word is zero, which is how "no tangent pool" arrives -- a cotangent frame from
        // screen-space derivatives (Mikkelsen). The supplied one is per VERTEX and interpolated, so a
        // curved surface's highlight stops stepping per triangle; the derivative one needs no stream and
        // gets mirrored UVs right for free.
        vec3 T, B;
        float inv;
        if (vTan.w != 0.0)
        {
            // Gram-Schmidt against the shading normal (Alloy's: the current SetupTSB, inc_transform.shd:206-217,
            // only normalises the tangent), then the handedness on the BITANGENT alone -- which is what SetupTSB
            // spends it on, and it works out to +dP/dv, the same axis the derivative frame below builds.
            T = vTan.xyz - N * dot(vTan.xyz, N);
            B = cross(N, T) * vTan.w;
            inv = inversesqrt(max(dot(T, T), 1e-12));
        }
        else
        {
            vec3 p2 = cross(dp2, N), p1 = cross(N, dp1);
            T = p2 * du1.x + p1 * du2.x;
            B = p2 * du1.y + p1 * du2.y;
            // The Mikkelsen frame comes out as det * (dP/du, dP/dv), where det is the screen-space UV
            // determinant -- so on a mirrored UV island BOTH axes come back negated, and normalizing by
            // a magnitude alone keeps that 180-degree rotation. The engine never has it: its tangent is
            // dP/du unconditionally (MdlNodeTriMesh::CalculateTangentSpaceBasis, nm 0x457710, divides the
            // edge combination BY the determinant) and its handedness is sign(det), which SetupTSB spends
            // on the bitangent alone. Carry the sign here and the two frames agree on every face.
            float uvDet = du1.x * du2.y - du1.y * du2.x;
            inv = inversesqrt(max(max(dot(T, T), dot(B, B)), 1e-12)) * (uvDet < 0.0 ? -1.0 : 1.0);
        }
        if ((slots & CX_SLOT_HEIGHT) != 0)
        {
            // inc_material.shd:307-311's SetupOcclusionFromTexture: the height against a blurrier tap of itself
            // IS the local self-occlusion, the blur shrinking with distance (OCCLUSION_VICINITT_RANGE 7.0
            // - log2 of the view depth), clamped to 0.9 and squared.
            // ponytail: no parallax displacement of the UVs -- that is the same map read again, per
            // fragment, in a loop. #122 owns what else the height map is worth.
            float far = max(0.0, CX_OCCL_VICINITY - log2(max(vDepth, 1e-4)));
            float o = texture(uHeightTex, uv, far + uLodBias).r - texture(uHeightTex, uv, uLodBias).r;
            float a = 1.0 - clamp(o, 0.0, 0.9);
            occl *= a * a;
        }
        if ((slots & CX_SLOT_NORMAL) != 0)
        {
            vec3 nm;
            nm.xy = texture(uNrmTex, uv, uLodBias).rg * 2.0 - 1.0;
            nm.z = sqrt(max(1.0 - dot(nm.xy, nm.xy), 0.0));
            // 23 = the TANGENT-SPACE normal as sampled, before the frame touches it. Flat lavender
            //      means the map carries no signal (or is not the map we think it is), which viz 7
            //      cannot tell apart from a frame throwing the perturbation away.
            if (uViz == 23) { frag = vec4(nm * 0.5 + 0.5, 1.0); return; }
            // Green is NOT inverted here, and that was measured, not assumed: a lit frame rendered
            // both ways against a vanilla frame of the same shot, correlated over the pixels the
            // choice actually moves, matches vanilla UNFLIPPED (+0.50 against +0.34, and the gap
            // widens the more the flip matters). Which is what the RE says too -- SetupTSB's
            // bitangent works out to +dP/dv, the same axis the frame below builds.
            N = normalize(mat3(T * inv, B * inv, N) * nm);
        }
    }
    // 7 = the SHADING NORMAL as colour, AFTER the normal map. Flat wedges with straight edges
    //     here are the mesh's own normals: no diffuse term, #143's wrap included, can hide a
    //     DISCONTINUITY in N, and nothing else tells that apart from a terminator crossing a facet.
    if (uViz == 7) { frag = vec4(N * 0.5 + 0.5, 1.0); return; }
    // 8 = the occlusion factor: white unoccluded. #146's map and the height term are the only two
    //     things that write it, so a frame that is white everywhere means no material in sight
    //     declared either -- which is the state shipped content is in today.
    if (uViz == 8) { frag = vec4(vec3(occl), 1.0); return; }
    if (uViz == 38)
    {   // lights per pixel, from the froxel this fragment reads: black 0, blue 8, green 16, yellow 24, red 32+
        float t = clamp(float(CxClusterN(CxCluster(vDepth))) / 32.0, 0.0, 1.0);
        vec3 h = t < 0.25 ? mix(vec3(0.0), vec3(0.0, 0.0, 1.0), t * 4.0)
               : t < 0.5 ? mix(vec3(0.0, 0.0, 1.0), vec3(0.0, 1.0, 0.0), (t - 0.25) * 4.0)
               : t < 0.75 ? mix(vec3(0.0, 1.0, 0.0), vec3(1.0, 1.0, 0.0), (t - 0.5) * 4.0)
               : mix(vec3(1.0, 1.0, 0.0), vec3(1.0, 0.0, 0.0), (t - 0.75) * 4.0);
        frag = vec4(h, 1.0); return;
    }
    // 35 = why this fragment has no screen-space AO. GREEN in the prepass depth and taking it;
    //      RED excluded as a cutout, BLUE by an out-of-prepass feature, YELLOW as a blended batch,
    //      MAGENTA as a keyhole batch the keyed depth sweep did not run for.
    //      Grey means the pass is not running at all.
    // 36 = the SCREEN-SPACE term alone, straight off SsaoPass, with neither the #146 map nor the
    //      height term in it. viz 8 cannot answer "does the AO buffer know this object is there",
    //      because a material that ships an AO map reads occluded in it either way.
    // 37 = whose occlusion is at this pixel. GREEN this surface's own (the prepass drew it here),
    //      RED another surface's -- the AO buffer holds geometry BEHIND this fragment, which is what
    //      "AO renders through doors" looks like from the inside. Grey where the pass is not running.
    if (uViz == 37)
    {
        frag = vec4(uSsaoOn <= 0 ? vec3(0.25) : aoMine ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), 1.0);
        return;
    }
    if (uViz == 36)
    {
        frag = vec4(vec3(aoMine ? CxSsaoAt(ivec2(gl_FragCoord.xy), aoDepth, aoSlope) : 1.0), 1.0);
        return;
    }
    if (uViz == 35)
    {
        vec3 c = uSsaoOn > 0 ? vec3(0.0, 1.0, 0.0)
               : uSsaoOn == -1 ? vec3(1.0, 0.0, 0.0)
               : uSsaoOn == -2 ? vec3(0.0, 0.0, 1.0)
               : uSsaoOn == -3 ? vec3(1.0, 1.0, 0.0)
               : uSsaoOn == -4 ? vec3(1.0, 0.0, 1.0) : vec3(0.25);
        frag = vec4(c, 1.0); return;
    }
    float sunRaw = clamp(dot(N, uAreaDir), 0.0, 1.0);
    float sunNdl = CxWrap(dot(N, uAreaDir), uWrapN);
    // the wrapped value, not the raw one -- a fragment the wrap has just lit must still be able to
    // be in shadow, or the band the wrap opens up glows through every caster
    float sunVis = (slots & CX_SLOT_NOSHADOW) != 0 ? 1.0 : CxSunShadow(vWorld, N, sunNdl, sunRaw);
    if ((slots & CX_SLOT_NOSHADOW) != 0) cxRecvShadow = 0.0;
    // 3 = sun visibility alone: white lit, black shadowed. A frame that looks unshadowed reads here
    //     as "the cascades are empty" or "nothing selected a cascade", which the composite cannot say.
    if (uViz == 3) { frag = vec4(vec3(sunVis), 1.0); return; }
    // Material colours go in RAW: inc_lighting.shd uses materialFrontAmbient (512), materialFrontDiffuse
    // (524) and materialFrontEmissive (603) with no ApplyColorSpace. Only the TEXTURE is linearised
    // (inc_standard.shd:256). Linearising the material terms too double-darkened every non-white material.
    vec3 matAmbient = d.amb.rgb;
    vec3 matDiffuse = d.dif.rgb;
    // CX-52: a diffuse that streamed in over the white stand-in fades in rather than cutting: from the
    // stand-in through its blurred top mips to full detail. Alpha stays sharp -- a cutout's shape does not blur.
    float arrive = d.ext2.z > 0.0 ? clamp((uNow - d.ext2.z) / CX_ARRIVE_SECONDS, 0.0, 1.0) : 1.0;
    // CX-117: a PLT part's diffuse is RESOLVED from the pair texture and the master palette here, per
    // instance, rather than composited per creature. Asked ONCE per fragment: a resolve is up to eight texel
    // fetches, so the three diffuse reads below take one resolve between them where the sampler path takes
    // three samples.
    bool plt = CxPltOn(vDraw);
    float arriveBias = uLodBias + (1.0 - arrive) * (1.0 - arrive) * CX_ARRIVE_MIPS;
    vec4 texel = plt ? CxPltResolve(uTex, uv, arriveBias, vDraw) : texture(uTex, uv, arriveBias);
    if (arrive < 1.0)
    {
        // A PLT's alpha comes out of the palette, not out of a mip of the pair texture, so the sharp-alpha
        // re-read the stand-in fade wants is already in `texel` -- one resolve, not two.
        if (!plt) texel.a = texture(uTex, uv, uLodBias).a;
        texel.rgb = mix(vec3(1.0), texel.rgb, smoothstep(0.0, 0.35, arrive));
    }
    if (earlyA >= 0.0) texel.a = earlyA;
    // CX-117 PLT forensics: 47 = the resolve forced to level 0 (mips out of the picture), 48 = the pair texel
    // itself (R intensity, G layer x 25), 49 = this texel's master-palette row over 1792, 50 = the PLT flag.
    // Not 40..46: those are the vis-ID, SSR and lightmap views, and this early return hid them.
    if (uViz == 47) { frag = vec4(plt ? CxPltBilinear(uTex, uv, 0, vDraw, CxPltClamps(vDraw)).rgb : vec3(0.0), 1.0); return; }
    if (uViz == 48) { vec2 rg = texture(uTex, uv).rg; frag = vec4(rg.r, rg.g * 25.5, 0.0, 1.0); return; }
    if (uViz == 49)
    {
        float row = plt ? float(CxPltRow(vDraw, min(int(texture(uTex, uv).g * 255.0 + 0.5), 9))) / 1792.0 : 0.0;
        frag = vec4(vec3(row), 1.0); return;
    }
    if (uViz == 50) { frag = vec4(plt ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), 1.0); return; }
    // #294: what the diffuse alpha MEANS here, asked once and spent once. On an env-mapped material
    // it is REFLECTIVITY, not coverage -- it becomes envLevel four lines down -- and the engine is
    // explicit about not spending it twice: inc_standard.shd:290 multiplies only `.rgb` through on
    // the ENVIRONMENT_MAP branch where :294 multiplies all four components on the other, and its
    // AlphaDiscard (:176) tests the MATERIAL alpha there, never the texel's. Alloy spent it as
    // reflectivity AND again as opacity, so the more reflective a surface was authored the more
    // invisible it drew -- the #294 crystal reads bright and solid in vanilla and as a ghost here.
    // CasterPass already knew this and lowered its own env threshold for the same reason; the
    // colour path's turn.
    bool alphaIsEnv = (slots & CX_SLOT_ENV) != 0;
#ifdef CX_CONTENT_ENVMAP
    // a content stage's own ENVIRONMENT_MAP 1 is the define stock reads, whatever the material says
    alphaIsEnv = alphaIsEnv || feat >= CX_FEAT_CONTENT0;
#endif
    float coverage = alphaIsEnv ? 1.0 : texel.a;
    float cutMip = uAlphaMip;
    vec2 cutSize = vec2(textureSize(uTex, 0));
    // #126: the cutout, on the FINAL diffuse alpha and ahead of every other branch -- a discarded
    // fragment must cost neither the material synthesis nor a probe sample, and the probe capture
    // has to punch the same holes the screen does or the cube reflects solid foliage.
#ifndef CX_CONTENT_PROGRAM
    if (CxCoverCut(coverage, d.rot.w, du1, du2, cutSize, cutMip)) discard;
#endif
    // #121: the diffuse ALPHA is the reflectivity mask on env-mapped content (inc_standard.shd:248,
    // fEnvMapLevel = 1 - alpha). A diffuse with no alpha channel reads 1.0, i.e. level 0 -- which is
    // why this costs nothing on the parts that are not env-mapped.
    float envLevel = alphaIsEnv ? 1.0 - texel.a : 0.0;
    texel.rgb = mix(texel.rgb, CX_ENV_GREY, envLevel);
    vec3 albedo = CxLinear(texel.rgb);
#ifdef CX_DECAL_WRITE
    // CX-273: the layer's writer. A fragment lying on the prepassed surface goes into the layer at its coverage
    // and is done -- the surface's own lit stage shades it; any other is left to the blended pass.
    {
        float z = texelFetch(uSceneDepth, ivec2(gl_FragCoord.xy), 0).r;
        float c = clamp(coverage * d.rot.w * kh, 0.0, 1.0);
        if (c <= 0.0 || !CxDecalOn(z, vDepth)) discard;
        frag = vec4(albedo * c, c);
        oDecalSurf = vec2(z, vDepth);
        return;
    }
#else
    // CX-273: ...and its reader -- any surface the nearest decal lies on (the prepassed ground, or a cutout or wash
    // interior drawn over it) takes the decals' albedo over its own, before anything is lit, so they shade as one
    float cxDecalCover = 0.0;
    if (uDecal == 1)
    {
        ivec2 dp = ivec2(gl_FragCoord.xy);
        vec4 dl = imageLoad(uDecalLayer, dp);
        if (dl.a < 1.0 && CxDecalOn(gl_FragCoord.z, imageLoad(uDecalSurf, dp).g))
        { albedo = albedo * dl.a + dl.rgb; cxDecalCover = 1.0 - dl.a; }
    }
    // 53 = the decal layer: how much of this surface's albedo the folded decals took, black where none did
    if (uViz == 53) { frag = vec4(vec3(cxDecalCover), 1.0); return; }
#endif
    // #122: the maps are sampled here and the synthesis itself is a pure function -- it has to fold
    // to a constant off-device (check-shaders.py) for the declared-beats-synthesised rule to be
    // checkable at all. A slot with nothing bound is not sampled, exactly as before.
    float specTexel = (slots & CX_SLOT_SPEC) != 0 ? texture(uSpecTex, uv, uLodBias).r : 0.0;
    float roughTexel = (slots & CX_SLOT_ROUGH) != 0 ? texture(uRoughTex, uv, uLodBias).r : 0.0;
    vec3 pbr = CxSynthPbr(d.pbr.xyz, slots, envLevel, specTexel, roughTexel);
    // #350/CX-303: a content pair's fragment half, AFTER synthesis -- the point its stock body runs, with
    // SetupStandardShaderInputs's outputs in the engine's own names. The material globals are filled first
    // because a body may READ them before it writes them (`min(MATERIAL_ROUGHNESS_VALUE, WET_ROUGHNESS)` is a
    // comparison); the specular colour is synthesised from the PRE-body albedo because SetupSpecularity runs
    // before the body in stock.
    CxHostTexel = vec4(albedo, coverage);
    CxHostUV = uv;
    CxHostDu = vec4(du1, du2);
    CxHostCutAt = vec3(cutSize, cutMip);
    FragmentColor = vec4(1.0);      // stock main's own opener, for a body that skips it
    vFragmentNormal = N;
    vSurfaceNormal = normalize(vNrm);
    vViewToSurface_n = normalize(vWorld - uEyePos);
    fSpecularity = pbr.x; fRoughness = pbr.y; fMetallicness = pbr.z;
    SpecularColor = CxSpecColor(d.spc, albedo * matDiffuse, pbr.z, slots);
    // ADR 0026: a material that declares the glTF vocabulary is shaded by it under the modern model -- the
    // stock parameters above are its vanilla view, these its Alloy one. Ahead of the hosted body, which
    // edits the result in the engine's units like any other material's.
    bool physical = uMaterial.x > 0.5 && CxPhysical(d.ext0, ormOn);
    if (physical)
    {
        vec3 sc;
        pbr = CxPhysicalPbr(d.ext0, ormOn, orm, pbr, albedo * matDiffuse, sc);
        fSpecularity = pbr.x; fRoughness = pbr.y; fMetallicness = pbr.z;
        SpecularColor = sc;
    }
    // 30 = the hosted body BYPASSED: the plain lit path for every hosted material, the A/B for
    //      "the look is wrong" vs "the handoff is wrong".
    if (uViz != 30 && CxHostSurface(feat))
    {
        albedo = FragmentColor.rgb;
        N = normalize(vFragmentNormal);
        pbr = vec3(fSpecularity, fRoughness, fMetallicness);
#ifdef CX_CONTENT_PROGRAM
        // CX-303: a content stage's alpha IS its coverage -- a stage that cuts on another map hands that map back --
        // and it made its own cut; what is left is the OIT pass's zero-coverage drop
        coverage = FragmentColor.a;
        if (uOit != 0 && coverage * d.rot.w <= 0.0) discard;
#endif
    }
#ifdef CX_DEPTH_ONLY
    // CX-303: a content pair's depth and caster twins stop here, with every discard the colour pass makes made --
    // the stage's own, the stand-in's stock cut, the keyhole -- and nothing lit. A post body cuts too, on the
    // surface colour standing in for the lit one it reads in the colour pass: its alpha, the coverage, is the same.
#ifdef CX_CONTENT_POST
    CxHostPost(FragmentColor);
#endif
    oVisId = CxVisId(vDraw, gl_PrimitiveID);
#ifndef CX_CAST
    frag = vec4(uHlColor, 1.0);
#endif
    return;
#endif
    cxModern = physical ? 1.0 : 0.0;
    if (!physical && (uMaterial.w > 0.5 || CxBareMaterial(d.pbr.xyz, slots))) cxFresnel = 0.0;
    // 31/32/33 = the surface this fragment shades with -- albedo, shading normal,
    // (spec, rough, metal) -- against 7 (the normal before any body). Emitted AFTER the branch so
    // they describe EVERY material: inside it they answered only on a hosted one and read as
    // identical to viz 0 everywhere else, which is a viz that cannot be trusted to say "no
    // difference".
    if (uViz == 31) { frag = vec4(albedo, 1.0); return; }
    if (uViz == 32) { frag = vec4(N * 0.5 + 0.5, 1.0); return; }
    if (uViz == 33) { frag = vec4(pbr, 1.0); return; }
    float spec = pbr.x, rough = pbr.y, metal = pbr.z;

