
// 1 = open sky above this point along the fall vector, 0 = covered. No map = open.
float CxCovered(vec3 p)
{
    if (uCoverVp[3][3] == 0.0) return 1.0;
    vec4 c = uCoverVp * vec4(p, 1.0);
    vec3 n = c.xyz / c.w * 0.5 + 0.5;
    if (any(lessThan(n.xy, vec2(0.0))) || any(greaterThan(n.xy, vec2(1.0)))) return 1.0;
    return texture(uCover, vec4(n.xy, uCoverLayer, n.z - 0.002));
}

// 1 = under a lip: covered here, open somewhere within the feather distance. Mid-roof and open sky are 0.
float CxLip(vec3 P, vec3 R, vec3 U)
{
    float e = max(uCoverSoft, 0.15);
    if (CxCovered(P) >= 0.5) return 0.0;
    return max(max(CxCovered(P + R * e), CxCovered(P - R * e)), max(CxCovered(P + U * e), CxCovered(P - U * e)));
}

void main()
{
    uint id = gl_GlobalInvocationID.x;
    bool keep = false;
    vec4 at = vec4(0.0), lit = vec4(0.0), sky = vec4(0.0);
    float speed = 0.75 + float(id & 3u) * 0.125;    // eighths: continuous at the phase wrap
    if (id < uint(uCount))
    {
        vec3 h = CxHash3(id * 2654435761u + 1u);
        vec3 F = uFall;
        vec3 R = normalize(cross(F, abs(F.z) < 0.99 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0)));
        vec3 U = cross(R, F);
        vec3 size = vec3(uBox.x, uBox.x, uBox.y);
        vec3 home = h * size;
        home.z = mod(home.z + uPhase * speed, size.z);
        vec3 c = vec3(dot(uCentre, R), dot(uCentre, U), dot(uCentre, F));
        vec3 q = home + size * floor((c - home) / size + 0.5);
        if (uKind == 2) q.xy += vec2(sin(uTime + h.y * 6.2832), cos(uTime * 1.5 + h.z * 6.2832)) * uDrift;
        vec3 P = R * q.x + U * q.y + F * q.z;
        float len = uKind != 2 ? uQuad.x * (0.5 + 0.8 * h.y) : uQuad.x;
        // no edge: the field thins RADIALLY from 40% of the half-width out, by drop COUNT (a hashed keep)
        // and not by a brightness ramp -- a ramp on a square box read as a band with a corner in it,
        // fewer drops reads as distance
        vec2 rel = (q.xy - c.xy) / (size.xy * 0.5);
        float edge = 1.0 - smoothstep(0.4, 1.0, length(rel));
        vec4 cv = uView * vec4(P, 1.0);
        vec4 cc = uProj * cv;
        float r = len + uQuad.y;
        bool seen = cc.w > -r && abs(cc.x) - r * abs(uProj[0][0]) <= cc.w && abs(cc.y) - r * abs(uProj[1][1]) <= cc.w;
        // the cover test at a hashed offset in the fall-normal plane: wind and splash feather a roof's edge
        vec3 Pc = P + (R * (h.y - 0.5) + U * (h.z - 0.5)) * uCoverSoft * 2.0;
        keep = seen && fract(h.z * 91.7) <= edge && (uKind == 3 ? CxLip(P, R, U) : CxCovered(Pc)) >= 0.5;
        if (keep)
        {
            // no surface normal: the sun reaches a drop through a forward-scattering lobe, a lamp from wherever it stands
            vec3 V = normalize(uEye - P);
            vec3 up = normalize(vec3(0.0, 0.0, 1.0) - V);
            sky = vec4(uAreaAmbient * (uProbeMips > 0.0 ? CxAmbientShape(up) : vec3(1.0)), uFallStep * speed);
            vec3 light = uAreaAmbient + uAreaDiffuse * 0.5;
            if (uLit != 0)
            {
                cxClusterXY = (cc.xy / max(cc.w, 1e-3) * 0.5 + 0.5) * vec2(uGrid.xy);
                float g = uKind == 2 ? clamp(uBack * 0.5, 0.0, 0.8) : CX_WX_RAIN_G;
                vec3 sun = uAreaDiffuse * (CxWxPhase(-uAreaDir, V, g) * CxSunShadow(P, uAreaDir, 1.0, 1.0));
                light = uAreaAmbient + sun + CxWxLamps(P, max(-cv.z, 1e-3));
            }
            at = vec4(P, len);
            // brightness: hashed 0.3..1.0
            lit = vec4(light, 0.3 + 0.7 * fract(h.x * 7.31));
        }
    }
    // compaction: one atomic per subgroup, each survivor at its rank among the subgroup's
    uvec4 ballot = subgroupBallot(keep);
    uint n = subgroupBallotBitCount(ballot), first = 0u;
    if (subgroupElect() && n > 0u) first = atomicAdd(uCmd[uSlot * 4 + 1], n);
    first = subgroupBroadcastFirst(first);
    if (!keep) return;
    uint at3 = (uint(uBase) + first + subgroupBallotExclusiveBitCount(ballot)) * 3u;
    uDropsOut[at3] = at;
    uDropsOut[at3 + 1u] = lit;
    uDropsOut[at3 + 2u] = sky;
}
