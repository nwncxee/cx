uniform mat4 uView;
uniform mat4 uProj;
uniform vec3 uEye;
uniform vec3 uCentre;    // the field's centre: the camera
uniform vec3 uFall;      // unit fall direction: gravity leaning into the area wind
uniform float uPhase;    // metres fallen, wrapped at 1024 box lengths
uniform float uTime;     // wobble clock, seconds
uniform vec2 uBox;       // (width across the fall axis, length along it)
uniform vec2 uQuad;      // (extent along the fall axis, width) of one quad
uniform int uKind;       // 1 rain, 2 snow, 3 roof drips
uniform float uDrift;    // snow wobble amplitude, metres
uniform sampler2DArrayShadow uCover;   // #617: the sun array; the cover map is layer uCoverLayer
uniform mat4 uCoverVp;   // world -> the cover view's clip space; zero matrix = no map
uniform float uCoverLayer;
uniform float uCoverSoft;    // metres of feather at a cover edge
uniform int uLit;        // 1 = take the light rig; 0 = flat area terms
uniform float uBack;     // snow: how strongly a flake scatters light forward, toward an eye looking at the light
uniform int uCount;      // the cull: how many lattice points this dispatch walks
uniform int uSlot;       // ...the indirect command its survivors count into, and so their records
uniform float uFallStep; // metres the field fell this frame at speed 1
uniform float uSpeed;    // the field's fall speed, m/s; each drop falls at its own share of it
uniform float uBlur;     // rain motion blur, 0 none to 1 a full shutter
uniform float uPxScale;  // pixels per metre at unit view depth
