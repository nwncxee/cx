// The reactive mask, written where the effect is: the OIT resolve, beam, particle, halo and weather passes report the
// share of the pixel they took into a second R8 attachment, which accumulates over them. AMD's guidance is to
// draw a ghosting object into the reactive mask at a value close to 1 (FidelityFX SDK 1.1.4,
// ffx_fsr3upscaler.h), and the share an effect covers is what "close to 1" means for an effect that is all of
// the pixel and for one that is a tenth of it.
#ifndef CX_REACTIVE_LIB
#define CX_REACTIVE_LIB

layout(location = 1) out float fragReact;

/// How reactive FSR 3.1 and the TAA resolve treat a pixel an effect covered by `share`: four times over and
/// capped below 1. Four times because a streak over a tenth of a pixel still has no history worth
/// reprojecting; the cap because a fully reactive pixel is one frame of noise with nothing averaging it.
float CxReactive(float share)
{
    return min(max(share, 0.0) * 4.0, 0.9);
}

/// How reactive a pixel blended geometry covers at `alpha` is: its alpha, capped at 0.9, as AMD's FSR 3.1
/// guidance asks for alpha-blended objects. No boost: a pane a quarter opaque keeps most of its history.
float CxReactiveAlpha(float alpha)
{
    return clamp(alpha, 0.0, 0.9);
}

/// The share of a pixel a draw of `rgb` at `alpha` takes. An additive draw leaves the background whole under
/// it, so its share is what it ADDS; an alpha-over draw's share is its alpha. Same rule the velocity sweep
/// uses to decide whose vector a pixel keeps.
float CxReactiveShare(vec3 rgb, float alpha, bool additive)
{
    return additive ? max(max(rgb.r, rgb.g), rgb.b) * clamp(alpha, 0.0, 1.0) : clamp(alpha, 0.0, 1.0);
}

#endif
