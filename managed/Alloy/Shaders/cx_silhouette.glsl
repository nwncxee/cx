// The silhouette's look. A server ships its own cx_silhouette.shd to replace it: this one function, which may read
// nothing but the surface it is handed. Two optional lines say what CX must know before it draws:
//   #pragma cx_silhouette opacity <0..100>   how solid a whole silhouette is; 100 when the line is absent
//   #pragma cx_silhouette glow               its glowing parts (a flame, an eye glow) draw; absent, they do not
/// The stock look's one colour, linear: a dark grey that still reads against an unlit wall.
const vec3 CX_SILHOUETTE_RGB = vec3(0.012);
CxSilhouetteLook CxSilhouetteShade(CxSilhouetteSurface s) { return CxSilhouetteLook(CX_SILHOUETTE_RGB, 0.0); }
