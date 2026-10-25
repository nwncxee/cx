// The movie quad, drawn in Alloy's context into the present image. The four vertices are MoviePlate's
// letterbox fit, already in normalised device coordinates with the frame's texture window on them, so this
// stage has no per-frame term of its own and needs no uniform.
#version 460 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUV;
out vec2 vUV;

void main()
{
    gl_Position = vec4(aPos, 0.0, 1.0);
    vUV = aUV;
}
