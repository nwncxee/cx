#define CX_FEAT_WINDGRASS 1
#define CX_FEAT_WINDLEAF  2
#define CX_FEAT_WINDPLANT 3
#define CX_FEAT_ROOFKN    4
#define CX_FEAT_ROOFKNM   5
#define CX_FEAT_FLOWMAP   7
#define CX_FEAT_SKIN      8
#define CX_FEAT_NOIRWET   9
#define CX_FEAT_NOIRGLOSS 10
#define CX_FEAT_GROUNDWEATHER 11
#define CX_FEAT_PALETTETINT 12
// CX-223: is this record POSED -- does feat.y carry a bone base? feat.x alone cannot say so: the skin class
// takes that lane from a content feature, but a CHARACTER shader (the palette tint) is skinned AND keeps its
// own feature there, so the override displaces only a feature that never meets a bone. A -1 in feat.y is the
// unposed sentinel, which is what a palette material's recipe seeds it with.
bool CxSkinned(vec4 feat)
{
    int fx = int(feat.x);
    return (fx == CX_FEAT_SKIN || fx == CX_FEAT_PALETTETINT) && feat.y >= 0.0;
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
