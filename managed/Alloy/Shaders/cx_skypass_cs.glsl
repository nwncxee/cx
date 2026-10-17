// A sky pass, as an Alloy compute program: the sky's shared environment, then -- between the markers -- the pass a
// sky twin names with `#pragma cx_sky_pass`, its main() renamed. The pass declares its own work-group size and the
// images it reads and writes, by the names the twin's `#pragma cx_sky_image` lines give them; the extender binds
// them by name. The stub is no pass at all, so a program built from it is refused (docs/shader-overrides.md).
#version 460 core
#include "cx_featdecl.glsl"
#include "cx_fogdecl.glsl"
#include "cx_skyenv.glsl"

// $VANILLA-MAIN $SKYCS$
layout(local_size_x = 1) in;
void CxVanMain() { }
// $VANILLA-END

void main()
{
    CxVanMain();
}
