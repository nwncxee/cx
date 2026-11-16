// inc_material's generated material -- specularity, roughness, metallicness and the specular colour -- as pure
// functions of what the material declares, its maps' texels and its reflectivity. It reads the slot bits
// (cx_slotbits) and CxLinear from its includer: the lit stage, and the GUI twin a content part draws through.
// Synthesis floors for a material that declares nothing, off the CURRENT inc_material.shd (ovr.bif, the set
// the client runs): fGeneratedSpecularityMin 0.04 (:68), fGeneratedRoughnessMin 0.125 under the GGX
// distribution (:46-50, the Alloy lobe's), fGeneratedRoughnessMax 0.55 in both branches (:40-44).
const float CX_SPEC_MIN = 0.04, CX_ROUGH_MAX = 0.55, CX_ROUGH_MIN = 0.125;
// metallicness, :216-227. With a specular map: m = specularity^2, then G*m / (1 + (G-1)*m) with G 25. Without
// one: fMetallicnessModifier 10 * specularity - 10 * fGeneratedSpecularityMin.
const float CX_METAL_GAIN_MAP = 25.0, CX_METAL_SCALE_BARE = 10.0, CX_METAL_BIAS_BARE = 0.4;
// ApplyColorSpace(0.5) at the engine's 2.2 gamma -- the mid-grey inc_material.shd divides the
// albedo's peak channel by when it synthesises a specular colour.
const float CX_MID_GREY = 0.2176;
// inc_material.shd:141-142 -- an env-mapped material with no specular map takes its specularity from
// the diffuse alpha (fEnvMapLevel = 1 - alpha), and its roughness down toward the floor with it.
// TWO modifiers, not one: fLegacyEnvMapSpecularityModifier 8.0 and fLegacyEnvMapRoughnessModifier
// 2.5 (:55-57). Sharing 8.0 would saturate the roughness curve at envLevel 0.125 and render every
// legacy shiny surface at the floor.
const float CX_ENV_SPEC_MAX = 0.98, CX_ENV_SPEC_SCALE = 8.0, CX_ENV_ROUGH_SCALE = 2.5;
/// A material that authors nothing -- no Specularity, no spec or roughness map, i.e. typically no .mtr.
/// Its lobe is stock's, but held at F0 at every angle: legacy content was never made for a grazing Fresnel rise.
bool CxBareMaterial(vec3 declared, int slots)
{
    return (slots & (CX_SLOT_SPEC | CX_SLOT_ROUGH)) == 0 && declared.x <= 0.0;
}

/// (Specularity, Roughness, Metallicness) -- declared wins, then the map, then the generated floor.
vec3 CxSynthPbr(vec3 declared, int slots, float envLevel, float specTexel, float roughTexel)
{
    float envShiny = min(envLevel * CX_ENV_SPEC_SCALE, 1.0);
    float envRough = min(envLevel * CX_ENV_ROUGH_SCALE, 1.0);
    bool sm = (slots & CX_SLOT_SPEC) != 0;
    float spec = declared.x;
    if (spec <= 0.0)
    {
        if (sm) spec = specTexel;
        // #121: env map + no specular map = legacy "shiny" content, and this is the engine's own
        // translation of it (inc_material.shd:141-142, "mainly for the sake of enabling specularity on
        // legacy content").
        else if (envShiny > 0.0) spec = mix(CX_SPEC_MIN, CX_ENV_SPEC_MAX, envShiny);
        else spec = CX_SPEC_MIN;
    }
    float rough = declared.y;
    if (rough <= 0.0)
    {
        // A ROUGHNESS MAP IS DECLARED CONTENT: it is taken whole, and the env curve below never
        // touches it -- stock applies that curve only where the roughness was generated
        // (inc_material.shd:186 puts the min() inside the specular-map branch, not after it).
        if ((slots & CX_SLOT_ROUGH) != 0) rough = roughTexel;
        else
        {
            // a specular map with no roughness map is pre-PBR content: translate rather than trust it
            rough = sm ? mix(CX_ROUGH_MIN, CX_ROUGH_MAX, (1.0 - spec) * (1.0 - spec))
                       : CX_ROUGH_MAX;
            // ...and the env map drives it toward the same floor, on TOP of that translation. With
            // no maps at all the min() is stock's bare assignment: the mix never exceeds the max.
            if (envLevel > 0.0)
                rough = min(rough, mix(CX_ROUGH_MAX, CX_ROUGH_MIN, envRough));
        }
    }
    float metal = declared.z;
    // stock keys this curve on whether a SPECULAR map is present (inc_material.shd:216-227). Collapsing the
    // two branches would generate visibly different metal for content that declares Specularity but ships no map.
    if (metal <= 0.0)
    {
        float m = spec * spec;
        metal = sm ? clamp(CX_METAL_GAIN_MAP * m / (1.0 + (CX_METAL_GAIN_MAP - 1.0) * m), 0.0, 1.0)
                   : clamp(CX_METAL_SCALE_BARE * spec - CX_METAL_BIAS_BARE, 0.0, 1.0);
    }
    return vec3(spec, rough, metal);
}

/// The material's specular colour: CustomSpecularColor where declared (w = the CPU-side "not black"
/// test, which is stock's own sentinel), else generated off the albedo.
vec3 CxSpecColor(vec4 declared, vec3 base, float metal, int slots)
{
    if (declared.w > 0.0) return CxLinear(declared.rgb);
    // stock takes plain white as the non-metal base once a height map is bound -- the peak-channel
    // rescue below is for flat content, and a height-mapped material already carries its crevices
    if ((slots & CX_SLOT_HEIGHT) != 0) return mix(vec3(1.0), base, metal);
    // non-metals should be white, but dark diffuse texels stand in for crevices, so the peak channel
    // scales it -- inc_material.shd:242-251's own reasoning, in its own words
    return mix(vec3(min(1.0, max(base.r, max(base.g, base.b)) / CX_MID_GREY)), base, metal);
}
