#version 460 core
uniform sampler2D uTex;
uniform float uAlphaCut;
#include "cx_castdither.glsl"
in vec2 vUV;
flat in float vAlpha;
