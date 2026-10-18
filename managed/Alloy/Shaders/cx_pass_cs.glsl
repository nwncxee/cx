// A native stage's pass, as an Alloy compute program: the whole-stage environment, then -- between the markers -- the
// pass a `#pragma cx_pass` line names, its main() renamed. The pass declares its own work-group size, the images it
// reads and writes by the names its stage's `#pragma cx_image` lines give them, and the cx frame uniforms it reads;
// the extender binds them by name. The stub is no pass at all, so a program built from it is refused
// (docs/shader-overrides.md, "Native stages, images and passes").
#version 460 core
#include "cx_featdecl.glsl"
#include "cx_fogdecl.glsl"
#include "cx_stageenv.glsl"

// $VANILLA-MAIN $PASSCS$
layout(local_size_x = 1) in;
void CxVanMain() { }
// $VANILLA-END

void main()
{
    CxVanMain();
}
