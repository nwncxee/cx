// The `.txi proceduretype arturo` ripple, worked out per texel on the GPU. ArturoTextureController::Control
// @0x5d83d0 fills a height map from its two wave grids and fetches the source image through it
// (TextureController::Distort @0x5d61d0); this is the same height map and the same fetch, written straight
// into level 0 of the texture materials already sample. The wave grids themselves stay on the CPU: they are
// 15x15 by default and one cell of each step takes the controller's own rand() draw.
#version 460 core
layout(local_size_x = 8, local_size_y = 8) in;
layout(rgba8, binding = 0) writeonly uniform image2D uWarped;
layout(rgba8, binding = 1) readonly uniform image2D uSource;
layout(rg32f, binding = 2) readonly uniform image2D uWave;
layout(rg32f, binding = 3) readonly uniform image2D uTurn;
// The image, a power of two on both axes (the fetch wraps with a mask), and the arturowidth x arturoheight grid.
uniform ivec2 uSize;
uniform ivec2 uWaveSize;
uniform float uPhase;
uniform float uAmp;
uniform int uAngled;
// The axis-aligned warp's height scale: a byte's distance from the midpoint over 128.
const float CX_ARTURO_HEIGHT = 0.0078125;

// The height byte at one texel: the Cur and Prev grids (uWave's x and y) sampled bilinearly with a wrap,
// mixed by the phase and biased to the midpoint -- BuildArturoImage @0x5d67d0's second half.
int CxArturoHeight(ivec2 t)
{
    vec2 s = vec2(uWaveSize) / vec2(uSize);
    float fx = float(t.x) * s.x, fy = float(t.y) * s.y;
    // Truncation, not floor, as the controller's own (int) cast is: it reads row -0.5 as row 0, so the first
    // row and column of the image sample the first of the grid with no wrap behind them.
    int ix = int(fx - 0.5), iy = int(fy - 0.5);
    float tx = fx - float(ix), ty = fy - float(iy);
    int ix1 = (ix + 1) % uWaveSize.x, iy1 = (iy + 1) % uWaveSize.y;
    vec2 g00 = imageLoad(uWave, ivec2(ix, iy)).xy;
    vec2 g10 = imageLoad(uWave, ivec2(ix1, iy)).xy;
    vec2 g01 = imageLoad(uWave, ivec2(ix, iy1)).xy;
    vec2 g11 = imageLoad(uWave, ivec2(ix1, iy1)).xy;
    vec2 v = (g11 * tx + g01 * (1.0 - tx)) * ty + (g10 * tx + g00 * (1.0 - tx)) * (1.0 - ty);
    return int(uPhase * v.x + v.y * (1.0 - uPhase) + 127.0) & 0xFF;
}

void main()
{
    ivec2 t = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(t, uSize))) return;
    int b = CxArturoHeight(t);
    ivec2 src;
    if (uAngled != 0)
    {
        // distortangle reads the height as a direction (uTurn is sin, cos per byte) and steps the amplitude
        // along it; the size is added so the mask below sees a positive number, as the controller does.
        vec2 turn = imageLoad(uTurn, ivec2(b, 0)).xy;
        src = ivec2(uAmp * turn.x - 0.5, uAmp * turn.y - 0.5) + uSize + t;
    }
    else
    {
        src = t + int(float(b - 127) * CX_ARTURO_HEIGHT * uAmp - 0.5);
    }
    imageStore(uWarped, t, imageLoad(uSource, src & (uSize - 1)));
}
