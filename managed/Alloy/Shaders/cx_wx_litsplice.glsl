            // #618/#619 + R2/R3: the weather on exposed surfaces. WHERE comes from the area wetness map
            // where it has reached (one fetch: the latched top-down surface's height, the basin's water
            // level and its relief) and from the cover map plus the CPU integrators everywhere else. Snow
            // keeps the older reads whole. Every surface takes it, a hosted weather body (td_ground_fs)
            // included.
            float wxWet = 0.0, wxSnow = 0.0, wxFilm = 0.0, wxPile = 0.0, wxFilmRough = 0.0, wxShore = 0.0, wxPoro = 0.0;
            vec2 wxDriftDir = vec2(0.0), wxP = vWorld.xy;
            vec3 wxTrack = vec3(0.0);
            if (uWeather.w > 3.5)
            {
                // /weather viz 4: the map. Red the damp wetness, green the basin's level, blue the metres of
                // standing water over this point; BLACK where the cover view has not latched this texel yet.
                vec4 vm = CxWetMap(vWorld.xy);
                frag = vec4(CxWetMapped(vm) ? vec3(vm.x, vm.y, CxWetDepth(vm) / max(uWxMapP.x, 1e-4)) : vec3(0.0), 1.0);
                return;
            }
            if (uWeather.w > 2.5) { frag = d.emi.w < -0.5 ? vec4(1.0, 0.0, 0.0, 1.0) : d.emi.w > 0.5 ? vec4(0.0, 0.0, 1.0, 1.0) : vec4(0.0, 1.0, 0.0, 1.0); return; }
            if (uWeather.x > 0.0 || uWeather.y > 0.0 || uWeather.w > 1.5)
            {
                vec3 Ng = normalize(vNrm);
                float up = clamp(Ng.z, 0.0, 1.0);
                // the area map's record first: where it has latched, rain needs no variance read at all
                vec4 wxM = CxWetMap(vWorld.xy);
                bool wxMapped = CxWetMapped(wxM);
                // the filtered (variance) exposure: snow's amount, and the fallback where the map has not latched
                float wxExp = uWeather.y > 0.0 || !wxMapped || uWeather.w > 1.5 ? CxWeatherExposure(vWorld, N) : 1.0;
                // /weather viz 2 paints the test that decides WETNESS -- the shelter cache where this is the
                // surface the rain lands on, the live disk (cx_wx_edge) everywhere else.
                if (uWeather.w > 1.5) { frag = vec4(vec3(CxWetOpenFast(vWorld, Ng, wxExp)), 1.0); return; }
                float wxPx = CxWeatherImpactPx(vWorld, V);
                vec4 wxNz = CxWxNoise(vWorld.xy, CxWxNoiseLod(wxPx));
                float n1 = wxNz.x, n2 = wxNz.y;
                // rain wets a face as much as the fall strikes it, relative to open ground: a wall parallel to the
                // fall stays dry, a windward one takes the lean. The fall is the cover view's axis (it is baked along it).
                vec3 wxFall = uCoverVp[3][3] == 0.0 ? vec3(0.0, 0.0, -1.0) : normalize(vec3(uCoverVp[0][2], uCoverVp[1][2], uCoverVp[2][2]));
                if (wxFall.z > 0.0) wxFall = -wxFall;
                // R2: the map's record drives HOW MUCH -- what has soaked in, what the basin holds, how far
                // this texel stands above its low point. It no longer draws the EDGE: at 1.6 texels a metre
                // on a large area its one-fetch shelter test stair-stepped the dry strip under an eave in
                // half-metre blocks. The edge comes off the cover map at its own resolution (cx_wx_edge),
                // and the map's coarse test is only what a point outside the cover window falls back to.
                float wxWetExp = CxWetOpenFast(vWorld, Ng, wxMapped ? CxWetShelter(wxM, vWorld.z) : wxExp);
                float wxWetAmt = clamp(wxMapped ? wxM.x * uWxMapXf.w : uWeather.x, 0.0, 1.0);
                wxWet = wxWetAmt * wxWetExp * clamp(dot(Ng, -wxFall) / max(-wxFall.z, 0.2), 0.0, 1.0);
                if (up < CX_WX_WALL_UP && wxWet > 0.0)
                {
                    // R5: rain does not wet a wall evenly -- it strikes it, then RUNS DOWN, so the wet is
                    // streaky, and the run-off collects in a dark band at the foot. The streak noise is
                    // stretched down z (tight across the wall, slack along it); the foot band needs the
                    // height of the GROUND beside the wall, which is the map's latched surface one step out
                    // along the face -- the texel under the wall itself is the wall's own top.
                    float wxStreak = CxWxEdgeNoise(vec2(dot(vWorld.xy, vec2(3.1, 2.7)), vWorld.z * CX_WX_STREAK_Z));
                    wxWet *= mix(1.0, CX_WX_STREAK_MIN + (1.0 - CX_WX_STREAK_MIN) * 2.0 * wxStreak, uWxRun.x);
                    vec4 wxFoot = CxWetMap(vWorld.xy + Ng.xy * CX_WX_FOOT_M);
                    if (CxWetMapped(wxFoot))
                    {
                        float wxBand = 1.0 - smoothstep(0.0, CX_WX_FOOT_M, vWorld.z - wxFoot.z);
                        wxWet = max(wxWet, wxBand * wxWetAmt * wxWetExp * uWxRun.x);
                    }
                    wxWet = min(wxWet, 1.0);
                }
                if (uWeather.y > 0.0 && d.emi.w >= -0.5 && !(uSnowPom.z > 0.5 && d.emi.w > 0.5))   // no snow on a creature (emi.w -1), nor on a listed tileset's tiles
                {
                    float wxSlope = smoothstep(uSnowLook.w - 0.15, uSnowLook.w + 0.15, up + (n1 - 0.5) * 0.4);
                    // drift: banked against whatever stands higher, out to the drift width
                    vec3 wxDrift = uSnowLook.z > 0.0 ? CxWxDriftAt(vWorld) : vec3(0.0);
                    wxPile = smoothstep(0.0, 0.6, wxDrift.x) * uSnowLook.z * up;
                    wxDriftDir = wxDrift.yz;
                    // ground variation: broad patches of thinner cover across open ground
                    float wxVary = 1.0 - uSnowLight.w * smoothstep(0.35, 0.7, wxNz.z) * 0.6;
                    wxSnow = clamp(uWeather.y * wxExp * wxSlope * (0.75 + 0.25 * n2) * wxVary * (1.0 + wxPile), 0.0, 1.0);
                    // parallax relief: the snow surface stands uSnowPom.x x height above the mesh. March the view
                    // ray from where it enters the relief's top down to the mesh, and shade the point where it
                    // meets the surface; the snow is one colour, so only the relief's own terms move, never the uv.
                    // The drift is taken at the entry and at the mesh point and interpolated along the ray.
                    if (uSnowPom.x > 0.0 && wxSnow > 0.4 && up > 0.7)
                    {
                        float amp = uSnowPom.x * wxSnow;
                        float vn = max(dot(V, Ng), 0.15);
                        vec2 top = vWorld.xy + V.xy * (amp / vn);
                        float pileTop = uSnowLook.z > 0.0 ? smoothstep(0.0, 0.6, CxWxDriftAt(vec3(top, vWorld.z)).x) * uSnowLook.z * up : 0.0;
                        float n = uSnowPom.y, prevH = 1.0, prevS = 1.0, s = 1.0, wxLod = CxWxNoiseLod(wxPx);
                        vec2 p = top;
                        for (int i = 1; i <= int(n); i++)
                        {
                            s = 1.0 - float(i) / n;
                            p = mix(vWorld.xy, top, s);
                            float hs = CxSnowHeight(CxWxNoise(p, wxLod), uSnowLight.w, mix(wxPile, pileTop, s));
                            if (s <= hs)
                            {
                                float t = (prevS - prevH) / max((prevS - prevH) - (s - hs), 1e-4);   // where the ray crossed
                                s = mix(prevS, s, t);
                                break;
                            }
                            prevH = hs; prevS = s;
                        }
                        wxP = mix(vWorld.xy, top, s);
                        wxPile = mix(wxPile, pileTop, s);
                    }
                    // footprints: a print packs the cover down, thinner over the sole
                    wxTrack = CxTrack(wxP);
                    wxSnow *= 1.0 - CX_WX_TRACK_BARE * wxTrack.x;
                }
                wxPoro = CxWxPoro(rough, spec, metal);
                if (up > 0.5)
                {
                    float wxFlat = smoothstep(0.9, 0.98, up);
                    if (wxMapped)
                    {
                        // R2: standing water is the basin's level less this texel's own relief, so a puddle
                        // has a real shoreline and drains from its edge inward as the level falls. The map
                        // cannot resolve relief finer than its own texel and no real ground is flat at the
                        // centimetre scale, so a noise micro-relief stands in for what it cannot see -- a
                        // flat plaza breaks into pools instead of sheeting over as one mirror. Porous
                        // ground drinks its shallow pockets, so the relief counts double there.
                        float wxMicro = uWxMapP.x * (CX_WX_MICRO_MEAN * 2.0 * (1.0 - uWxMapP.w) + CX_WX_MICRO_AMP * (0.35 * n1 + 0.65 * n2 - 0.5)) * (1.0 + wxPoro);
                        float wxD = CxWetDepth(wxM) - wxMicro;
                        wxFilm = wxWetExp * wxFlat * smoothstep(0.0, uWxMapP.y, wxD);
                        // R5: the SHORELINE. Ground the water has just left, or has not quite reached, is
                        // soaked right through -- darker and more saturated than the dry stone outside it
                        // and than the film over it, which is what gives a real puddle its rim.
                        wxShore = wxWetExp * wxFlat * (1.0 - wxFilm)
                                * smoothstep(-CX_WX_SHORE * uWxMapP.x, 0.0, wxD) * uWxRun.y;
                    }
                    else if (uWeather.z > 0.0)
                    {
                        // no map here yet: #619's estimate -- concave = the AO says low ground, or the rain
                        // view says the neighbours stand higher
                        float conc = clamp((1.0 - occl) * 1.5 + CxWeatherHollow(vWorld), 0.0, 1.0);
                        float m = conc * 0.5 + n1 * 0.6 + n2 * 0.15 - 0.2;
                        wxFilm = uWeather.z * wxFlat * wxExp * smoothstep(0.5 - uWeatherLook.z, 0.5 + uWeatherLook.z, m);
                    }
                }
                wxWet = clamp(max(wxWet, wxShore * wxWetAmt), 0.0, 1.0);
                if (wxSnow > 0.0)
                {
                    albedo = mix(albedo, vec3(uWeatherLook.y), wxSnow);
                    rough = mix(rough, 0.75, wxSnow); spec = mix(spec, 0.08, wxSnow);
                    wxPoro = CxWxPoro(rough, spec, metal);
                    // mounds: the normal tilts down the relief's gradient at the point the ray met it (or under
                    // the pixel with the relief off), more as the cover deepens
                    float wxGl = CxWxNoiseLod(wxPx);
                    vec2 wxG = vec2(CxSnowHeight(CxWxNoise(wxP + vec2(0.05, 0.0), wxGl), uSnowLight.w, wxPile) - CxSnowHeight(CxWxNoise(wxP - vec2(0.05, 0.0), wxGl), uSnowLight.w, wxPile),
                                    CxSnowHeight(CxWxNoise(wxP + vec2(0.0, 0.05), wxGl), uSnowLight.w, wxPile) - CxSnowHeight(CxWxNoise(wxP - vec2(0.0, 0.05), wxGl), uSnowLight.w, wxPile));
                    // a drift is a slope rising toward the wall: the normal leans away from it, most at its foot.
                    // The cover view's uv axes are its own right/up, not world xy; the tilt is applied in world
                    // xy through the view's inverse rotation -- the map is a top-down orthographic fit, so its
                    // uv is a rotation of world xy and the first two columns carry it.
                    vec2 wxLean = vec2(dot(uCoverVp[0].xy, wxDriftDir), dot(uCoverVp[1].xy, wxDriftDir));
                    wxLean = length(wxLean) > 1e-5 ? normalize(wxLean) : vec2(0.0);
                    N = normalize(mix(N, Ng, wxSnow * 0.7) - vec3(wxG * uSnowLook.y * 3.0 * wxSnow, 0.0) - vec3(wxLean * wxPile * 0.8, 0.0));
                    // ...and its walls lean into the dent (height = -depth, so the normal follows +grad), its floor greyer
                    N = normalize(N + vec3(wxTrack.yz * (CX_WX_TRACK_DENT * wxSnow), 0.0));
                    albedo *= 1.0 - CX_WX_TRACK_DARK * wxTrack.x * wxSnow;
                    // deeper cover brightens and rounds off: the mound reads thicker than the blanket
                    albedo = mix(albedo, vec3(uWeatherLook.y) * 1.05, clamp(wxPile, 0.0, 1.0) * 0.5);
                }
                if (wxWet > 0.0)
                {
                    // R3, Lagarde: a soaked surface darkens and SATURATES -- albedo raised to a power
                    // through CxWetSoak, which caps the exponent per fragment: a bare pow ran a dark
                    // albedo to black at the higher end of the darkening knob (cx_wx_soak)
                    albedo = CxWetSoak(albedo, uWeatherLook.x * wxPoro * wxWet);
                    // ...and the BASE roughness drops only moderately, and never under the ceiling a dry
                    // fragment was above: below uLightOcc.y every wet pixel joins the mirror path, which
                    // walks every static light in the cluster -- which was the frame, on a rainy plaza
                    // F0 stays what it was: damp ground is not latex; only a FILM (the coat) is a mirror
                    rough = max(mix(rough, rough * mix(0.6, CX_WX_PORO_ROUGH, wxPoro), wxWet), min(rough, uLightOcc.y));
                }
                if (wxFilm > 0.0)
                {
                    // standing water is a LAYER over the damp base, not a rewrite of the stone: a thin
                    // clear coat (cx_ownlit_light hands it to cxCoat / cxCoatRough) with the base normal
                    // flattened toward the water's own plane beneath it. The base keeps its roughness, and
                    // with it the cheap lighting path.
                    wxFilmRough = max(uWeatherLook.w, CX_ALPHA_MIN);
                    wxFilmRough = mix(wxFilmRough, max(wxFilmRough, CX_WX_FILM_FAR_A), smoothstep(CX_WX_FILM_FAR_M.x, CX_WX_FILM_FAR_M.y, vDepth));
                    // toward the GEOMETRIC normal, not toward the world's up. Water drowns a bed's
                    // normal-map detail but not its macro shape -- a sloped street under a film is still
                    // sloped -- and the old blend between the mapped normal and a world plane swept the
                    // near-mirror lobe's alignment smoothly across a whole surface, which is what drew
                    // the film's highlight as a bright ARC rather than a reflection. A film only exists
                    // where the face is near-flat (wxFlat), so the geometric normal IS the water plane.
                    N = normalize(mix(N, Ng, wxFilm));
                    albedo = CxWetSoak(albedo, uWeatherLook.x * wxFilm);
                }
                if (uCoverLayer.w > 0.0 && wxFilm > 0.0 && up > 0.9)
                {
                    // the rings a drop leaves are on the FILM, so they roughen the coat and not the bed
                    float wxRing, wxAmp = wxWetExp * wxFilm;
                    N = normalize(N + vec3(CxWeatherRipple(vWorld.xy, uCoverLayer.w, wxPx, wxRing) * wxAmp, 0.0));
                    wxFilmRough = CxWeatherRippleRough(wxFilmRough, wxRing, wxAmp, uCoverLayer.w);
                }
            }
