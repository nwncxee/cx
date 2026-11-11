// A creature the player character hears but does not see: the record's slot mask says so (Unseen.SlotBit), and the
// lit stage paints it one flat colour in place of everything it shaded.
const int CX_SLOT_SILHOUETTE = 8192;
/// The silhouette's one colour, linear: a dark grey that still reads against an unlit wall.
const vec3 CX_SILHOUETTE_RGB = vec3(0.012);
/// Is this record part of a silhouette?
bool CxSilhouetted(int slots) { return (slots & CX_SLOT_SILHOUETTE) != 0; }
/// The colour a lit fragment leaves the stage with: its own, or the silhouette's.
vec3 CxSilhouette(vec3 lit, int slots) { return CxSilhouetted(slots) ? CX_SILHOUETTE_RGB : lit; }
