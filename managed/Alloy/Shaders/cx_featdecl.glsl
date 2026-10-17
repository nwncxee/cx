// The record's feat.x, one contract with ShaderFeature (ShaderContract holds the two in step): 0 = the plain lit path,
// the skinmesh class, and from CX_FEAT_CONTENT0 up one id per CONTENT pair -- a .mtr's customShaderVS/customShaderFS,
// run translated in a program of its own (ContentPrograms).
#define CX_FEAT_SKIN      1
#define CX_FEAT_CONTENT0  2
// Is this record POSED -- does feat.y carry a bone base? feat.x alone cannot say so: the skin class takes that lane,
// but a content pair is a CHARACTER shader as often as not, skinned AND keeping its own id, so feat.y is the bone base
// for both and -1 the unposed sentinel a content recipe seeds it with.
bool CxSkinned(vec4 feat)
{
    int fx = int(feat.x);
    return (fx == CX_FEAT_SKIN || fx >= CX_FEAT_CONTENT0) && feat.y >= 0.0;
}
uniform int uTimeOfDay;        // worldtimerTimeOfDay, the engine's own int
// The animation clock, wrapped to the hour in int. float(uTimeOfDay) steps 8 at the end of the day, which is
// half a frame's sway; the wrap keeps it at 0.25 at the cost of one phase jump an hour.
float CxClock() { return float(uTimeOfDay % 3600000); }
uniform float uLodBias;        // #391: log2(render scale), <= 0; every material sample takes it
uniform float uNow;            // CX-52: TexStream.Clock, the clock a streamed diffuse's arrival is stamped on
uniform vec3 uAreaWind;        // areaGlobalWind
uniform int uAreaFlags;        // areaFlags
uniform vec3 uCamPos;          // cameraPosition
uniform vec3 uPlayerPos;       // playerPosition
uniform vec3 uCamFocus;        // cameraFocus -- the camera PIVOT (runtime +0x90), not uCamPos
