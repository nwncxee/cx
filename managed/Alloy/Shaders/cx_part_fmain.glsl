// An additive sprite draws into the display-space glow layer, so its texel stays as authored, the way the stock
// stage writes it; every other particle draws into the linear scene, so its texel's encode comes off.
vec3 CxPartAlbedo(vec3 texel, int additive)
{
    return additive != 0 ? texel : CxLinear(texel);
}

void main()
{
    fragReact = 0.0;
    vec4 c = vColor * texture(uTex, vUv);
#ifdef CX_MOTION
    // CX-48: the velocity FIELD, not the colour. Written only where the sprite is most of the pixel: anywhere
    // else the pixel is mostly the background, which must keep its own vector or TAA drags it along with the
    // sprite. Over alpha-over that share is alpha; an additive sprite leaves the background whole under it,
    // so its share is what it ADDS -- alpha alone painted a black-backed glow's whole quad.
    float share = uAdditive != 0 ? max(max(c.r, c.g), c.b) * c.a : c.a;
    if (share < CX_MOTION_CUT) discard;
    frag = vec4(CxMotionVec(vCur, vPrev), 0.0, 0.0);
    return;
#endif
    if (uViz == 2)
    {
        // Flat and opaque-ish: the quads as QUADS. A soft smoke texture hides everything about the
        // geometry that could be wrong with it.
        frag = vec4(0.6, 0.6, 0.6, 0.35);
        return;
    }
    if (uViz == 1)
    {
        const vec3 tint[7] = vec3[7](vec3(1,0,0), vec3(0,1,0), vec3(0,0,1), vec3(1,1,0),
                                     vec3(1,0,1), vec3(0,1,1), vec3(1,1,1));
        frag = vec4(tint[clamp(vType, 0, 6)], c.a);
        return;
    }
    if (uAlphaCut >= 0.0 && c.a <= uAlphaCut) discard;

    vec3 albedo = CxPartAlbedo(c.rgb, uAdditive);
    // #336: lit per vertex; vLight is 1.0 on an unlit draw
    albedo *= vLight;
    c.rgb = albedo;

    // Soft particles, exactly where fsparticle puts them: after the discard, before fog. The fade is
    // squared because a linear ramp still shows its own leading edge as a line -- the square is what
    // the stock stage uses and what makes the contact read as a dissolve.
    if (uSoftOn != 0 && vSoft > 0.0)
    {
        float gap = CxSceneViewZ(gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0))) - vViewZ;
        float k = clamp(gap * vSoft, 0.0, 1.0);
        c.a *= k * k;
    }

    // #148: past 1.0 this leaves display range and lands in #139's headroom, where the bloom
    // threshold catches it because the effect IS bright -- not because a threshold happened to sit
    // where its texture did. Before fog: fog attenuates what is there, and emission is what is there.
    c.rgb *= uEmissive;

    // #341: the SAME fog the world pass runs -- ramp, height layer, sky-matched target and all.
    // An emitter that fogged toward a different colour than the terrain behind it is a smoke plume
    // that reads as a different weather.
    float f = CxFogFactor(vWorld, vViewZ);
    if (uAdditive != 0) c.a *= (1.0 - f) * CxHazeT(vWorld);
    else c.rgb = CxFog(c.rgb, vWorld, vViewZ);
    frag = c;
    fragReact = CxReactive(CxReactiveShare(c.rgb, c.a, uAdditive != 0));
}
