// The additive layer laid on the display-encoded frame as the stock client's 8-bit target takes an additive draw:
// added, clamped at 1, and never darker than the pixel it lands on.
vec3 CxGlowOver(vec3 display, vec3 glow)
{
    return max(display, min(display + glow, vec3(1.0)));
}
