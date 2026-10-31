uniform mat4 uProj;          // this frame's projection, raw engine bytes (see CxViewDist)
uniform float uBands;        // luminance steps; at 1 (the default) the posterise is off
uniform float uInk;          // outline strength, 0..1; 0 when the option's Edge Hardness is 0
uniform float uEdge;         // outline half-width in pixels
uniform float uDepthThresh;  // Toon Depth Edge Threshold: the residual as a fraction of view distance
uniform float uColorThresh;  // Toon Color Edge Threshold: the residual as a luminance step
uniform float uHardness;     // Toon Edge Hardness: 0 = a wide soft ramp, 1 = a hard line

float CxLuma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }

// Positive view distance: z_clip = z_v*M22 + M32, w_clip = -z_v, so z_v = -M32 / (ndc + M22); the world's
// reversed depth under its folded projection is the NDC z itself.
float CxViewDist(float depth)
{
    return uProj[3].z / (depth + uProj[2].z);
}

vec4 CxStage(vec4 c)
{
    if (uBands >= 2.0)
    {
        float lum = CxLuma(c.rgb);
        // The half shift puts the levels at band CENTRES and the transitions at the boundaries: the
        // darkest band is 0.5/uBands, not 0, so the shadows land on a dark step instead of crushing
        // to black, and pure white still reaches 1.0.
        float t = lum * uBands - 0.5;
        // One fwidth wide: the band boundary is a drawn edge at any resolution, never a stair-step.
        float w = clamp(fwidth(t) * 0.5, 1e-3, 0.5);
        float q = (floor(t) + 0.5 + smoothstep(0.5 - w, 0.5 + w, fract(t))) / uBands;
        c.rgb = clamp(c.rgb * (q / max(lum, 1e-4)), 0.0, 1.0);
    }
    if (uInk > 0.0)
    {
        vec2 o = uEdge / vec2(textureSize(uDepth, 0));
        // IMPORTANT: the neighbours are averaged in RAW depth, which is the space a plane is
        // affine in -- window z is linear across a triangle, view distance is not. Averaging
        // linearised distances instead pens the ground at grazing angles, which is exactly the
        // stock stage's failure.
        float z0 = texture(uDepth, vUV).r;
        float zx = (texture(uDepth, vUV - vec2(o.x, 0.0)).r + texture(uDepth, vUV + vec2(o.x, 0.0)).r) * 0.5;
        float zy = (texture(uDepth, vUV - vec2(0.0, o.y)).r + texture(uDepth, vUV + vec2(0.0, o.y)).r) * 0.5;
        // The plane fit: what the two neighbours PREDICT this pixel's distance to be, against what it
        // is. Zero on a plane at any angle; a silhouette or a crease -- a break in the depth gradient
        // -- is the whole residual. Over the threshold as a FRACTION of view distance, so the slider
        // holds from the boots to the horizon.
        float d0 = CxViewDist(z0);
        float e = max(abs(CxViewDist(zx) - d0), abs(CxViewDist(zy) - d0)) / (d0 * uDepthThresh);
        // The colour edge is the same plane fit over display luminance, and it is the half that
        // inks a drawn detail on flat geometry. At the option's stock 1.0 a full-range luminance
        // step is needed, so it stays out of the way until the slider comes down.
        float l0 = CxLuma(texture(uSrc, vUV).rgb);
        float lx = (CxLuma(texture(uSrc, vUV - vec2(o.x, 0.0)).rgb) + CxLuma(texture(uSrc, vUV + vec2(o.x, 0.0)).rgb)) * 0.5;
        float ly = (CxLuma(texture(uSrc, vUV - vec2(0.0, o.y)).rgb) + CxLuma(texture(uSrc, vUV + vec2(0.0, o.y)).rgb)) * 0.5;
        e = max(e, max(abs(lx - l0), abs(ly - l0)) / uColorThresh);
        // Hardness is the ramp either side of the threshold: soft and wide at 0, a hard line at 1.
        float soft = mix(0.9, 0.02, uHardness);
        // AFTER the bands: ink on the banded colour is one uniform dark line, and it keeps the
        // step out of the fwidth() the band boundary measures itself with.
        c.rgb *= mix(1.0, 0.12, smoothstep(1.0 - soft, 1.0 + soft, e) * uInk);
    }
    return c;
}
