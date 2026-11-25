    // water.surfaces: painted water (DrawRecipe.WaterLook, code CX_WATER_LOOK) takes a water layer over its paint -- moving
    // ripples on the normal, the standing-water film the reflection passes already draw, and the rain's rings on it.
    // After the weather splice, so it rides that film rather than racing it.
    if (uWaterSurf.x > 0.0 && abs(CxLeafCode(d.occ) - CX_WATER_LOOK) < 0.5)
    {
        vec3 wlNg = normalize(vNrm);
        if (wlNg.z > 0.7)
        {
            vec2 wlP = vWorld.xy;
            // the noise bends the trains' crests, so no straight wave line repeats across the pond
            wlP += (CxWxNoise(vWorld.xy * 0.5, 0.0).xy - 0.5) * 1.5;
            float wlT = CxClock() * 0.001;
            // weather.ice: painted water has no depth to freeze in from its shore, so sheets grow in patches, frost on
            // each sheet's growing edge, until the whole surface is iced over
            float wlIce = 0.0, wlFrost = 0.0;
            if (uWaterSurf.w > 0.0)
            {
                float n = CxWxNoise(vWorld.xy * 2.0, 0.0).z;
                wlIce = uWaterSurf.w >= 0.98 ? 1.0 : smoothstep(n - 0.12, n + 0.12, uWaterSurf.w * 1.25 - 0.1);
                wlFrost = wlIce * (1.0 - wlIce) * 4.0;
            }
            vec2 wlTilt = vec2(0.0);
            for (int i = 0; i < 4; i++)
            {
                float a = 2.399963 * float(i) + 0.4;   // golden-angle spread of directions
                vec2 dir = vec2(cos(a), sin(a));
                float k = 2.3 * pow(1.6, float(i));    // wavenumber, rad per metre
                wlTilt -= dir * (cos(dot(wlP, dir) * k - wlT * (0.55 + 0.2 * float(i)) * 6.2831853) * (0.012 * k / (1.0 + float(i))));
            }
            wlTilt *= (0.6 + 1.8 * uWaterSurf.y) * (1.0 - wlIce);
            // near mirror: water's highlights are sharp glints, and a broad sheen is what reads as plastic
            float wlRing = 0.0, wlRough = 0.006;
            if (uCoverLayer.w > 0.0 && wlIce < 0.5)
            {
                wlTilt += CxWeatherRipple(vWorld.xy, uCoverLayer.w, CxWeatherImpactPx(vWorld, V), wlRing);
                wlRough = CxWeatherRippleRough(wlRough, wlRing, 1.0, uCoverLayer.w);
            }
            N = normalize(wlNg + vec3(wlTilt * uWaterSurf.x, 0.0));
            // the film's coverage is its reflection's floor: water.shine raises it toward a mirror head-on
            wxFilm = max(wxFilm, uWaterSurf.x);
            // ice keeps a dull sheen; frost takes the film off
            wxFilmRough = mix(wlRough, 0.04, wlIce);
            // water.shine: the head-on reflection raised toward a mirror, as on real water; ice and frost give it up
            cxFilmF0 = uWaterSurf.z * 0.5 * uWaterSurf.x * (1.0 - wlIce);
            wxFilm *= 1.0 - 0.8 * wlFrost;
            // the bed seen through water: darker and richer than the dry paint, since the water absorbs what it does not reflect
            vec3 wlBed = albedo;
            albedo = CxWetSoak(albedo, 0.5 * uWaterSurf.x) * mix(1.0, 0.45, uWaterSurf.x);
            albedo = mix(albedo, mix(wlBed * 0.5 + 0.12, vec3(0.72, 0.8, 0.86), 0.55), wlIce);
            albedo = mix(albedo, vec3(0.9, 0.93, 0.96), wlFrost * 0.8);
        }
    }
