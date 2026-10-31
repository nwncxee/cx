// The screen-space march: inc_common's GetRayIntersectCoords, shared by the water stage (#339, on the
// frame copy) and the lit stage's reflections (CX-51, on last frame's copy). The copy to march is a
// parameter -- its depth, the projection it was drawn with, and that projection's near/far.
#ifndef CX_RAYMARCH
#define CX_RAYMARCH

/// Window-space depth from a copy, linearised to a POSITIVE view-space distance -- stock's GetDepth,
/// reconstructed from the copy's own near/far rather than from an engine uniform.
float CxMarchZ(sampler2D depth, vec2 nf, vec2 uv)
{
    float d = 1.0 - 2.0 * texture(depth, uv).x;   // GL's NDC z of the world's reversed depth
    return (2.0 * nf.x * nf.y) / (nf.y + nf.x - d * (nf.y - nf.x));
}

/// Stock's march, verbatim in method: step along the view-space ray, read the copy's depth at the
/// projected point, and feed the signed depth difference back as the next step so the iteration
/// converges on the crossing. Returns (uv, hit view distance, relative error).
vec4 CxRayIntersect(sampler2D depth, mat4 proj, vec2 nf, vec3 vPosViewOrigin, vec3 vRay, float fDepthOffset, float fMaxDistance, int nIterations)
{
    vec2 vRayIntersectionCoords = vec2(-1.0);

    float fAngleFactor = mix(1.0, abs(vRay.z), 0.9);
    fMaxDistance *= fAngleFactor;
    vRay /= fAngleFactor;

    float fDistanceOffsetMax = fMaxDistance / float(nIterations);
    float fDepthDeltaMin = 4096.0;
    float fRayIntersectionDepth = fMaxDistance + vPosViewOrigin.z;

    vec3 vRayMaxProj = mat3(proj) * (vPosViewOrigin + fMaxDistance * vRay);
    vec2 vRayCoordsMax = 0.5 * (vRayMaxProj.xy / vRayMaxProj.z) + 0.5;
    vec3 vPosOriginProj = mat3(proj) * vPosViewOrigin;
    vec2 vPosProjOriginCoords = 0.5 * (vPosOriginProj.xy / vPosOriginProj.z) + 0.5;

    float fInterpolationDenominatorInv =
        1.0 / (1.0 / (vPosViewOrigin.z + vRay.z * fMaxDistance) - 1.0 / vPosViewOrigin.z);
    float fInterpolationOffset = (1.0 / vPosViewOrigin.z) * fInterpolationDenominatorInv;

    float fDepthDelta = clamp(fDepthOffset, 0.0, fDistanceOffsetMax);
    float fDistance = 0.0;
    bool bFoundIntersection = false;

    for (int n = 1; n <= nIterations; n++)
    {
        fDistance += clamp(fDepthDelta, -fDistanceOffsetMax, fDistanceOffsetMax);
        fDistance = max(0.01, fDistance);
        vec3 vRayThis = fDistance * vRay;

        vec2 vRayCoordsThis = mix(vPosProjOriginCoords, vRayCoordsMax,
                                  (1.0 / (vPosViewOrigin.z + vRayThis.z)) * fInterpolationDenominatorInv
                                  - fInterpolationOffset);
        if (vRayCoordsThis.x > 1.0 || vRayCoordsThis.x < 0.0
            || vRayCoordsThis.y > 1.0 || vRayCoordsThis.y < 0.0) break;

        float fSampleDepth = CxMarchZ(depth, nf, vRayCoordsThis);
        fDepthDelta = fSampleDepth + (vPosViewOrigin.z + vRayThis.z);

        if (abs(fDepthDelta) / (fDistance + 1.0) <= abs(fDepthDeltaMin))
        {
            vRayIntersectionCoords = vRayCoordsThis;
            fDepthDeltaMin = fDepthDelta / (fDistance + 1.0);
            fRayIntersectionDepth = fSampleDepth;
        }
        if (fDepthDelta < 0.0) bFoundIntersection = true;
        if (bFoundIntersection) fDistanceOffsetMax *= 0.5;
    }

    return vec4(vRayIntersectionCoords.x, vRayIntersectionCoords.y, -fRayIntersectionDepth, fDepthDeltaMin);
}

vec4 CxReflectionCoords(sampler2D depth, mat4 proj, vec2 nf, vec3 vPosViewThis, vec3 vReflect, float fDepthOffset)
{
    const int nIterations = 10;
    float fDepthMax = mix(mix(nf.y, 0.0, 0.5 + 0.5 * vReflect.z), 100.0, 0.1);
    float fDepthDeltaMax = fDepthMax + vPosViewThis.z;
    float fMaxDistance = clamp(abs(fDepthDeltaMax), 1.0, 30.0);
    vec4 c = CxRayIntersect(depth, proj, nf, vPosViewThis, vReflect, fDepthOffset, fMaxDistance, nIterations);
    return vec4(c.xyz, clamp(abs(c.w / 0.2), 0.0, 1.0));
}
/// inc_common's GetNormalVector: the VIEW-space normal of whatever a copy's depth holds at these
/// coordinates, used only to reject a reflection off a surface facing away from the ray. Central
/// differences here rather than stock's nearest-neighbour pick -- the projection helpers its version
/// leans on are a bigger port than the term is worth.
vec3 CxMarchNormal(sampler2D depth, mat4 proj, vec2 nf, vec2 px, vec2 uv)
{
    // View position from a depth sample: the ray through this pixel, scaled to the sampled distance.
    vec3 dir = vec3((2.0 * uv.x - 1.0) / proj[0][0], (2.0 * uv.y - 1.0) / proj[1][1], -1.0);
    vec3 p  = dir * CxMarchZ(depth, nf, uv);
    vec3 dx = vec3((2.0 * (uv.x + px.x) - 1.0) / proj[0][0], dir.y, -1.0) * CxMarchZ(depth, nf, uv + vec2(px.x, 0.0)) - p;
    vec3 dy = vec3(dir.x, (2.0 * (uv.y + px.y) - 1.0) / proj[1][1], -1.0) * CxMarchZ(depth, nf, uv + vec2(0.0, px.y)) - p;
    return normalize(cross(dx, dy));
}

#endif
