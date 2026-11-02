/// The coverage of a streak or flake above which its pixel takes the drop's own motion. Below it the pixel keeps the
/// ground's vector, so a thin faint edge never drags the ground along the rain.
const float CX_WX_CORE = 0.35;

/// The motion a precipitation pixel of coverage `cov` holds: the drop's in its core, the ground's on its edge.
vec2 CxWxVelocity(float cov, vec2 drop, vec2 ground)
{
    return cov > CX_WX_CORE ? drop : ground;
}
