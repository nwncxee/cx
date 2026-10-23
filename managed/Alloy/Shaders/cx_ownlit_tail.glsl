    // #121/CX-51: the reflection on EVERY lit surface, as the probe sees it. The material's env map is not
    // consulted: it was the vanilla renderer's stand-in for exactly this; it still marks legacy content shiny
    // (CxSynthPbr). Radiance under the split-sum weight -- DFG on the material's own F0 (SpecularColor x
    // Specularity, the scale vanilla's env term takes, so a matte or dark texel stays matte), specular
    // occlusion, capped at 1 so no bounce adds energy. /light probegain is the dial. The weight goes
    // out in the reflection record too, and Ssr swaps the probe for the screen wherever the screen shows R.
    // what a puddle's water reflects never reaches the stone under it: at grazing angles the mirror, not the
    // lit ground, is what the eye gets
    float wxTransmit = 1.0;
    vec3 reflW = vec3(0.0);
    // R4: the film's own split-sum weight, in the film's roughness -- what the screen march has to trace
    // where standing water is the mirror. cxCoat already carries the film's coverage, so this is taken at
    // its full strength and the BASE weight is what it displaces, never a second fade on top of it.
    vec3 reflFilm = vec3(0.0);
    // the share of the reflection that is lit legacy env content, not the probe: the screen march leaves it alone
    float envMix = 0.0;
#ifdef CX_DEFERRED
    // CX-188: the legacy env pattern, 0 wherever that branch does not run -- the rest of d(env)/d(diffuse)
    // (envMix, the grey level, reflW) is already in scope at the G-buffer write below
    float gEnvPat = 0.0;
#endif
    if (uCapture != 1)
    {
        vec3 R = reflect(-V, N);
        vec2 reflAb = CxEnvBrdf(NoV, sqrt(rough)) * vec2(1.0, cxFresnel);
        // a classic material rises as inc_lighting.shd:2849's fEnvironmentSpecularity does, damped by sqrt(roughness)
        vec3 reflF = cxModern > 0.5 ? cxF0 * reflAb.x + reflAb.y
                                    : specColor * mix(mix(spec, 1.0, CxFresnel(NoV) * cxFresnel), spec, sqrt(rough));
        reflW = min(reflF * msc * CxSpecOcclusion(NoV, occl, rough) * coatT * uProbeGain, vec3(1.0));
        if (uProbeMips > 0.0)
        {
            vec3 env = CxProbeFetch(uProbe, vWorld, R, sqrt(rough) * (uProbeMips - 1.0));
            // legacy shiny content reflects the LIGHT ARRIVING HERE through its env map's pattern
            // (inc_lighting.shd:543-582, EnvironmentColor = (Ambient + Diffuse) * envTexel): a torchlit
            // breastplate shines however dark the area probe is. The probe, normalised by its own mean,
            // is the pattern -- its LUMINANCE only: the stock env maps are grey, so the tint is the light's,
            // and the sky's blue carried into the pattern drew silver armour blue.
            if (!physical && envLevel > 0.0)
            {
                vec3 mean = CxProbeFetch(uProbe, vWorld, R, uProbeMips - 1.0);
                float m = dot(mean, vec3(0.2126, 0.7152, 0.0722));
                float pat = m > 1e-5 ? min(dot(env, vec3(0.2126, 0.7152, 0.0722)) / m, CX_ENV_PAT_MAX) : 1.0;
                envMix = min(envLevel * CX_ENV_SPEC_SCALE, 1.0);
                env = mix(env, (ambient + diffuse) * pat * CX_ENV_GREY_LIN, envMix);
#ifdef CX_DEFERRED
                gEnvPat = pat;
#endif
            }
            specLight += env * reflW;
        }
        if (wxFilm > 0.0 && cxCoat > 0.0)
        {
            vec2 fab = CxEnvBrdf(NoV, sqrt(cxCoatRough));
            reflFilm = vec3(min(cxCoat * (CX_DIELECTRIC_F0 * fab.x + fab.y)
                                * CxSpecOcclusion(NoV, occl, cxCoatRough) * uProbeGain, 1.0));
        }
        wxTransmit = 1.0 - max(max(reflW.r, max(reflW.g, reflW.b)), reflFilm.r) * wxFilm;
    }
    // 6 = the specular lobe alone: exactly what #120 adds. A part that reads flat in the composite is
    //     either black here (no lobe at all) or washed out by the diffuse, and nothing else tells the
    //     two apart.
    if (uViz == 6) { frag = vec4(specLight, 1.0); return; }
    // energy split: what the specular lobe reflects, the diffuse does not -- inc_lighting.shd:603-609's own
    // `(1.0 - fEnvironmentSpecularity) * (AmbientLight + DiffuseLight)`
    // ponytail: the sheen takes no ambient term (Filament's needs a DFG table of its own); cloth reads by
    // its direct rim, and a sheen that wants presence in shade is the table's cue.
    vec3 total = (ambient + diffuse * matDiffuse) * (kd * coatT * wxTransmit) + d.emi.rgb;
    // 9 = the LIGHT arriving here, with no albedo on it: vanilla's TotalLight, the value
    //     inc_lighting.shd multiplies ColorIO by. Paired with 31 it splits "the light is short"
    //     from "the surface is dark", which no composite frame can do.
    if (uViz == 9) { frag = vec4(total, 1.0); return; }
    // Specular and self-illumination land AFTER the albedo multiply: a highlight is light off the
    // surface, not light through it (inc_lighting.shd:653 `ColorIO.rgb += MATERIAL_SPECULAR_COLOR_VALUE *
    // SpecularLight`, and the texUnit5 add at :639).
    // inc_lighting.shd:647-651: "Specularity ignores material transparency" -- vanilla pre-divides the
    // specular by the alpha it is about to be blended by, so a ghosted creature keeps its highlights.
    // The PART's alpha, not the texture's: texture alpha is coverage -- where the sheet is not, nothing shines --
    // and dividing by it drew every cutout's and decal's graded edge as a rim of highlight up to ten times over.
    float partA = d.rot.w;
    if (partA < 1.0 && partA > 0.001) specLight /= max(partA, 0.1);
    vec3 lit = albedo * total + specLight;
    // ADR 0026: CxEmissiveStrength (KHR_materials_emissive_strength) scales the map into HDR, where the
    // bloom can see it; undeclared (-1) and the engine model take it at 1.
    if ((slots & CX_SLOT_ILLUM) != 0)
        lit += CxLinear(texture(uIllumTex, uv, uLodBias).rgb) * (uMaterial.x > 0.5 && d.ext2.x >= 0.0 ? d.ext2.x : 1.0);
    // #132: everything from here down stays LINEAR -- the screen and the probe want the same values
    // now, and PostChain's resolve does the frame's one encode. The probe never wanted anything else
    // (#121: glGenerateMipmap filters what is STORED, and filtering display-space values darkens
    // them), so what changed is the screen path catching up to it.
    // The dissolve and the fog are both linear multiplies/lerps here. #124 matched the fslit tail's
    // own order (ApplyKeyhole, RevertColorSpace, ApplyFog) and #132 knowingly drops the encode out of
    // the middle of it, which moves fog into linear -- a deliberate look change on foggy frames.
    // Both terms are camera-dependent, so the probe capture takes neither: a dissolve baked into the
    // cube is a permanent hole in every reflection the area ever shows.
    // CX-51: the capture keeps creatures out (a figure baked into the cube stands in every reflection until the
    // next capture) and stores 1 / distance in alpha, the parallax correction's depth (CxProbeFetch)
    if (uCapture != 0)
    {
        // both targets get mipped: one non-finite or blown texel becomes a whole coarse block of the reflection
        lit = any(isnan(lit)) || any(isinf(lit)) ? vec3(0.0) : min(lit, vec3(CX_SSR_MAX));
        // CX-51: the mirrored draw keeps everything, creatures included; alpha 1 = something is mirrored here
        if (uCapture == 2) { frag = vec4(lit, 1.0); return; }
        if (CxSkinned(d.feat) || d.feat.w < 0.0) discard;
        frag = vec4(lit, 1.0 / max(distance(vWorld, uEyePos), 1e-3));
        return;
    }
    // #126: rot.w is the per-draw alpha multiplier -- the part's own alpha times the tile fade
    // factor. It reaches the framebuffer only where the class blends; the opaque set draws with
    // GL_BLEND off, so its alpha channel is written and ignored, exactly as before.
    // #294: times COVERAGE, not the raw texel alpha -- on an env-mapped material that channel is
    // reflectivity and the engine leaves the fragment alpha to the material alone.
    // #132: linear out. No encode, no shoulder -- the resolve owns both for the whole frame.
    // #346/#347: the display-space overlays go on LAST, after the fog, exactly where
    // inc_standard.shd puts them -- noir, racial vision, then the targeting shape.
    // ponytail: the fog mixes in display space, so (1 - f) is the linear transmittance only approximately;
    // the display overlays never reach the swapped-in reflection. Exact = Ssr refogging, if a foggy puddle shows it.
    // R4: where the water film dominates, the record carries the FILM's roughness and the film's weight --
    // the screen march must trace the mirror the film is, not the matte bed the base roughness describes.
    CxReflOut(N, mix(rough, cxCoatRough, wxFilm), max(reflW * (1.0 - envMix), reflFilm) * (kh * (1.0 - CxFogFactor(vWorld, vDepth))));
#ifdef CX_DEFERRED
    // CX-188 (ADR 0029): the record the deferred compute pass lights from, written on the uCapture == 0 path
    // alone (the capture and the mirror returned above). It goes LAST because the transfer weights carry the
    // whole rest of the composition -- everything the walk's two results would have been multiplied by on
    // their way to this pixel. docs/spec/deferred-lighting.md's Lane semantics table is the contract.
    {
        CxGbuf g;
        g.flags = CX_GB_DEFERRED
                | (baked ? CX_GB_BAKED : 0u)
                | (onMover > 0.0 ? CX_GB_ONMOVER : 0u)
                | (wxFilm > 0.0 && baked && cxCoat > 0.0 ? CX_GB_FILM : 0u)
                | (cxRecvShadow == 0.0 ? CX_GB_NOSHADOW : 0u)
                | (cxModern < 0.5 ? CX_GB_STOCKLOBE : 0u);
        g.N = N; g.rough = rough; g.spec = spec; g.fresnel = cxFresnel;
        g.coat = cxCoat; g.coatRough = cxCoatRough;
        // the reader rebuilds cxSpecScale and fetches the sheen by slot (cx_gbuf)
        g.specC = uMaterial.x > 0.5 ? cxF0 : specColor; g.slot = vDraw;
        // the ghost divide, applied to specLight just above: vanilla pre-divides the specular by the alpha it
        // is about to be blended by, so everything reaching the pixel THROUGH specLight carries it
        float ghost = partA < 1.0 && partA > 0.001 ? max(partA, 0.1) : 1.0;
        // the fog mixes in display space, so (1 - factor) is its linear transmittance only approximately
        // (the spec's Known deviations, and the approximation CxReflOut above already takes). The REVEAL
        // factor, not the plain one: on an unexplored tile that ramp is the one the pixel actually took.
        float thru = kh * (1.0 - CxFogRevealFactor(vWorld, vDepth, d.ext2.w));
        // P: pointSpec and the film statics both enter specLight times dirOcc, and specLight reaches the
        // pixel through the ghost divide and nothing else.
        g.specT = dirOcc * thru / ghost;
        // Wd: `point` enters `diffuse` times dirOcc, and `diffuse` reaches the pixel three ways -- through
        // the albedo multiply (`total`'s matDiffuse x kd x coatT x wxTransmit), and as ARRIVING light in the
        // two terms that read it so: the coat's probe reflection and the legacy env pattern, both of which
        // land in specLight and therefore take the ghost divide too.
        g.diffT = dirOcc * thru * (albedo * (matDiffuse * (kd * coatT * wxTransmit))
                                   + (gCoatProbe + reflW * (CX_ENV_GREY_LIN * (gEnvPat * envMix))) / ghost);
        g.lmLum = gLmLum;
        g.lmMover = baked ? CxLightmapMover(chart.rec, vChart) : vec4(0.0);
        g.viewDepth = 1.0 / gl_FragCoord.w;
        // L: the baked term's whole contribution -- its diffuse rode `point` into Wd, its lobe rode
        // `pointSpec` into P. The twin left `lm` undebited, so this is exactly what the debt scales back.
        g.lmShare = g.diffT * lm + g.specT * gLmSpec;
        CxGbufWrite(g);
    }
#endif
#ifdef CX_A2C_MASK
    gl_SampleMask[0] = CxA2cMask(coverage * d.rot.w);
#endif
#ifndef CX_DEFERRED
    if (uViz == 43) lit = cxLm;
    if (uViz == 52) lit = cxLightFrom * 0.5;   // through the blend/OIT tail, so a decal shows at its own coverage
#endif
    vec4 outC = vec4(CxDisplayOverlays(CxFogReveal(lit * kh, vWorld, vDepth, d.ext2.w)), coverage * d.rot.w);
#ifdef CX_CONTENT_POST
    // CX-303: what a content stage does after its ApplyStandardShader, on the colour that call hands back in the
    // engine: lit, dissolved and fogged, in display space (CxHostPost converts both ways)
    outC = CxHostPost(outC);
#endif
    frag = CxOitOut(outC);
}
