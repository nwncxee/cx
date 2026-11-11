// A creature the player character hears but does not see: the record's slot mask says how far into its silhouette it
// is (Unseen.SlotMask), and what the silhouette's look (cx_silhouette) is handed and hands back.
const int CX_SLOT_SILHOUETTE = 8192;
/// How far into its silhouette this record is: 0 its own look, 1 the silhouette.
float CxSilhouetteAmount(int slots) { return float((slots / CX_SLOT_SILHOUETTE) & 255) / 255.0; }
/// One fragment of a silhouette, as the lit stage shaded it. Directions are world space and unit length.
struct CxSilhouetteSurface
{
    vec3 lit;      // the colour the creature would leave the stage with, linear, its lighting on it
    vec3 albedo;   // the surface's own colour, linear, with no light on it
    vec3 normal;   // the way the surface faces
    vec3 view;     // from the surface toward the camera
    vec3 world;    // where the surface is
    float amount;  // how far into the silhouette, 0 to 1; the lit stage does the fade itself
    float time;    // seconds, wrapping every hour
};
/// What a look answers: the colour of a whole silhouette, linear, and the share of the creature's own reflections and
/// deferred light that still lands on it, 0 none to 1 all.
struct CxSilhouetteLook { vec3 rgb; float own; };
