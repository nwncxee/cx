// A creature the player character hears but does not see: the record's slot mask says how far into its silhouette it
// is (Unseen.SlotMask), and the lit stage mixes everything it shaded toward one flat colour by that much.
const int CX_SLOT_SILHOUETTE = 8192;
/// The silhouette's one colour, linear: a dark grey that still reads against an unlit wall.
const vec3 CX_SILHOUETTE_RGB = vec3(0.012);
/// How far into its silhouette this record is: 0 its own look, 1 the silhouette.
float CxSilhouetteAmount(int slots) { return float((slots / CX_SLOT_SILHOUETTE) & 255) / 255.0; }
/// The colour a lit fragment leaves the stage with: its own, darkened toward the silhouette's.
vec3 CxSilhouette(vec3 lit, int slots)
{
    float amount = CxSilhouetteAmount(slots);
    // a whole silhouette is the colour itself: the mix leaves each channel its own last bit of the lit one
    return amount >= 1.0 ? CX_SILHOUETTE_RGB : mix(lit, CX_SILHOUETTE_RGB, amount);
}
