using CX.Engine;
using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary><b>The light rig, fed from the scene database:</b> the area-light terms, the local-light set and
/// the bake's static set, as <c>FrameInputs</c> calls, with the engine's lighting arithmetic ported
/// explicitly (<see cref="Adjust"/>) and pinned by the self-check. A light's part index is the id
/// <c>RecordAreaLight</c> stores and compares: stable for its life, unique, and never recycled.</summary>
internal static class DbLights
{
    /// <summary>A light at or past this range is the engine's DIRECTIONAL pair (the area's sun and ambient),
    /// not a local light: the same test the engine walk makes, through the same layout constant.</summary>
    private static float DirectionalRange => EngineLayout.DirectionalRange;

    private static int _walked, _admitted, _skipped;

    /// <summary>Gather this scene's lights into <c>FrameInputs</c>; once per world frame, before the draw.</summary>
    /// <param name="h">The frame's hoisted engine terms -- the area's VALUES, not a live <c>CNWCArea</c>, so
    /// this walk reads no engine memory and can run on <c>RenderThread</c>.</param>
    internal static void Walk(DbId scene, in FrameHoist h)
    {
        float radiusMult = h.RadiusMult;
        // A reused rig still owes the sun its motion: the glide runs on the clock, not on the walk, and the
        // signature does not move while the engine's own direction holds still between the server's steps.
        if (Unchanged(scene, radiusMult, in h)) { _reused++; if (WorldScene(scene)) CoastSun(); return; }
        int parts = DbInputs.LightCount, lits = SceneDb.Lights.High;
        FrameInputs.BeginAreaLights(parts + lits);
        FrameInputs.ResetLocalLights(parts + lits);
        _walked = 0; _admitted = 0; _skipped = 0; _static = 0; _areaAmb = 0; _areaDif = 0; _tile = 0; _deduped = 0;
        _skipOff = 0; _skipReach = 0; _skipMul = 0; _skipBlack = 0; _skipAnimated = 0;
        // Cleared per walk: an area pair that is no longer in the scene must stop lighting it. The SUN is
        // deliberately NOT cleared -- see AreaTerm.
        FrameInputs.HasAreaAmbient = false; FrameInputs.HasAreaDiffuse = false;
        DbId only = SceneFeed.BlindLights(scene);
        FrameInputs.Blind = !only.IsNull;
        // The bake's own walk opens with ours: it closes a STATIC set over the lights it is told about, and a
        // walk that never began leaves every light dynamic and every tile unbaked.
        LightmapBake.BeginWalkDb(h.Area, h.PlaceableLights);

        // The TILE lights, from SceneDb.Lights -- the authoritative set. A tile's light node is not a part in
        // most tile trees, so the part-registered enumeration below saw only the few that happened to have
        // one (4 of 22 live) and the rest of the area simply never lit.
        for (int lit = 1; lit < lits; lit++)
        {
            if (!DbInputs.LitLight(lit, out DbLight l)) continue;
            // Same scene filter the object lights get: a GUI scene's 3D view must not be lit by the world's
            // tile lamps, and the table is one table for every scene.
            if (!SceneDb.Objects.Valid(l.Obj) || SceneDb.ObjScene[l.Obj.Index].Value != scene.Value) continue;
            Admit(in l, scene, radiusMult, in h, tile: true, only);
        }

        // ...and the OBJECT lights, from the part registry. A tile light that DID become a part is in both
        // enumerations; it carries its Lit index, so it is dropped here rather than counted twice.
        for (int k = 0; k < parts; k++)
        {
            if (!DbInputs.Light(k, out DbLight l)) continue;
            if (l.Lit >= 0) { _deduped++; continue; }
            if (SceneDb.PartObj[l.Part].IsNull) { _skipped++; continue; }
            if (SceneDb.ObjScene[SceneDb.PartObj[l.Part].Index].Value != scene.Value) continue;
            Admit(in l, scene, radiusMult, in h, tile: false, only);
        }

        // Blind lights fade the area pair out as well, so the grey stand-in an unread ambient gets is wrong here.
        if (!only.IsNull && !FrameInputs.HasAreaAmbient) { FrameInputs.AreaAmbient = Vector3.Zero; FrameInputs.HasAreaAmbient = true; }

        // The same tail the engine walk closes with: a light that left the rig has to fade rather than vanish,
        // and the bake's static set is only a set once it is closed.
        SceneInputs.FinishWalk();
    }

    /// <summary>Is this frame's rig the one <see cref="FrameInputs"/> already holds? A SIGNATURE over the
    /// inputs the rig is a function of, not a dirty flag a writer could forget (a miss would freeze an area's
    /// lighting); one pass over the light records, no allocation, short-circuited by the registry's revision.
    /// <c>ponytail:</c> staleness is capped at <see cref="ResyncFrames"/>; narrow it by adding signature terms.</summary>
    private static bool Unchanged(DbId scene, float radiusMult, in FrameHoist h)
    {
        if (++_sinceResync >= ResyncFrames) { _sinceResync = 0; return false; }
        ulong sig = Signature(scene, radiusMult, in h);
        if (sig == _sig && _sigValid) return true;
        _sig = sig; _sigValid = true;
        return false;
    }

    /// <summary>Frames between unconditional rebuilds. Half a second at 60 fps.</summary>
    private const int ResyncFrames = 30;
    private static int _sinceResync;
    private static ulong _sig;
    private static bool _sigValid;
    /// <summary>Frames the rig was reused rather than rebuilt.</summary>
    internal static long Reused => _reused;
    private static long _reused;

    /// <summary>Everything the rig is a function of, folded into one value: the scene and the global radius
    /// multiplier, the area's palette pair (the two area terms come off it), and every light record's
    /// position, colour, reach, multiplier and enabled state.</summary>
    private static ulong Signature(DbId scene, float radiusMult, in FrameHoist t)
    {
        ulong h = 1469598103934665603UL;
        Mix(ref h, scene.Value);
        Mix(ref h, SceneFeed.BlindLights(scene).Value);
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(radiusMult));
        Mix(ref h, DbInputs.LightRev);
        if (t.HaveArea)
        {
            // CX-188: the palette PAIR used to be mixed and the day/night switch with it. The fold already
            // picked the side, so the resolved triple carries the same change: a day/night flip moves it, and
            // so does a scripted SetGlobalLight* on the side in use. The side NOT in use cannot change what
            // this frame lights with, so it is not a term of the rig.
            Mix(ref h, t.Night ? 1u : 0u);
            Mix(ref h, (uint)BitConverter.SingleToInt32Bits(t.Diffuse.X)); Mix(ref h, (uint)BitConverter.SingleToInt32Bits(t.Diffuse.Y)); Mix(ref h, (uint)BitConverter.SingleToInt32Bits(t.Diffuse.Z));
            Mix(ref h, (uint)BitConverter.SingleToInt32Bits(t.Ambient.X)); Mix(ref h, (uint)BitConverter.SingleToInt32Bits(t.Ambient.Y)); Mix(ref h, (uint)BitConverter.SingleToInt32Bits(t.Ambient.Z));
        }
        for (int k = 0; k < 3; k++)
        {
            Mix(ref h, (uint)BitConverter.SingleToInt32Bits(_sunAmb[k]));
            Mix(ref h, (uint)BitConverter.SingleToInt32Bits(_sunDif[k]));
        }
        int lits = SceneDb.Lights.High;
        for (int lit = 1; lit < lits; lit++)
            if (DbInputs.LitLight(lit, out DbLight l)) MixLight(ref h, in l);
        int parts = DbInputs.LightCount;
        for (int k = 0; k < parts; k++)
            if (DbInputs.Light(k, out DbLight l)) MixLight(ref h, in l);
        return h;
    }

    private static void MixLight(ref ulong h, in DbLight l)
    {
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Position.X));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Position.Y));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Position.Z));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Colour.X));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Colour.Y));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Colour.Z));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Radius));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Multiplier));
        Mix(ref h, l.Enabled ? 1u : 0u);
    }

    private static void Mix(ref ulong h, uint v) { h = (h ^ v) * 1099511628211UL; }

    /// <summary>One light into the frame's rig: the area-pair fork, the engine's admission tests, the
    /// adjusted radius/colour pair, and the bake's static verdict. Shared by both enumerations so a tile
    /// light and an object light cannot drift apart in how they are admitted. <paramref name="only"/> is the
    /// scene's blind-lights object (<see cref="SceneFeed.BlindLights"/>), null when there is none.</summary>
    private static void Admit(in DbLight l, DbId scene, float radiusMult, in FrameHoist h, bool tile, DbId only)
    {
        _walked++;
        if (tile) _tile++;
        if (!l.Enabled) { _skipped++; _skipOff++; return; }
        // Scene::SetBlindLights @0x45e010 -> m_pOnlyGob: PrioritizeLights @0x4c0940 and ControlLightsFade keep that
        // object's lights alone, area pair included. The bake still hears of every light: its static set is the area's.
        bool blinded = !only.IsNull && l.Obj != only;

        float range = l.Radius, inten = l.Multiplier;
        // The two index -1 lights, picked exactly as LightManager::UpdateLights @0x4c06c0 picks them: a light
        // reaching DirectionalRange or further is the AREA pair -- kind 0 the directional diffuse (and, when
        // it reaches like one, the sun), anything else the ambient -- and the last of each wins.
        if (range >= DirectionalRange) { if (!blinded) AreaTerm(in l, in h, scene); return; }
        Vector3 rgb = l.Colour;
        if (range <= 0f || inten <= 0f || (rgb.X == 0f && rgb.Y == 0f && rgb.Z == 0f))
        {
            _skipped++;
            if (range <= 0f) _skipReach++; else if (inten <= 0f) _skipMul++; else _skipBlack++;
            if (l.Part >= 0 && SceneDb.PartLitAnim[l.Part] != 0) _skipAnimated++;
            return;
        }

        bool ambient = DbInputs.AmbientOnly(in l);
        Adjust(range, inten, radiusMult, ref rgb, out float adjR);
        Vector3 pos = l.Position;
        // The bake's verdict, asked the way the engine walk asks it -- except that the database, not a walk of
        // engine objects, says whether the light holds still. A static light is what a tile is LIT BY: tiles
        // are lit from the baked pages and objects dynamically.
        bool isStatic = LightmapBake.RecordDb(l.Id, pos, adjR, rgb, ambient, l.Static, out int setIndex);
        if (blinded) return;
        FrameInputs.RecordAreaLight(l.Id, pos, adjR, rgb, ambient, isStatic, setIndex);
        _admitted++;
        if (isStatic) _static++;
        // A local (shadow-casting) light is a non-ambient one that asked to cast. A tile light says so in its
        // own record; an object light says so through its LightDef.
        bool casts = l.Lit >= 0 ? SceneDb.LitShadow[l.Lit] : l.Def is { Shadow: true };
        if (ambient || !casts) return;
        FrameInputs.RecordLocalLight(l.Id, pos, adjR, rgb);
    }

    /// <summary>Route one area light to the term its own kind drives. The colour goes through the receiver's
    /// own gamma here (<c>AlloyLights.SrgbGamma</c>) because every consumer of <c>FrameInputs.Area*</c>
    /// expects it pre-compensated; handing it linear is a base term that looks washed out.</summary>
    private static void AreaTerm(in DbLight l, in FrameHoist h, DbId scene)
    {
        bool isAmbient = DbInputs.AmbientOnly(in l);
        Vector3 c = Palette(in h, isAmbient, l.Colour);
        var col = new Vector3(MathF.Pow(MathF.Max(c.X, 0f), AlloyLights.SrgbGamma),
                              MathF.Pow(MathF.Max(c.Y, 0f), AlloyLights.SrgbGamma),
                              MathF.Pow(MathF.Max(c.Z, 0f), AlloyLights.SrgbGamma));
        if (isAmbient)
        {
            FrameInputs.AreaAmbient = col;
            FrameInputs.HasAreaAmbient = true;
            _areaAmb++;
            return;
        }
        Vector3 toLight = l.Position;
        if (toLight.LengthSquared() <= 1e-8f) return;
        FrameInputs.AreaDiffuse = col;
        FrameInputs.HasAreaDiffuse = true;
        _areaDif++;
        bool sun = l.Radius > DirectionalRange;
        // The WORLD's sun is the one a server steps; a GUI scene's own directional light is a lamp aimed at a
        // model and follows its scene exactly.
        PublishDir(WorldScene(scene) ? SunGlide.Follow(toLight, sun) : Vector3.Normalize(toLight), sun);
    }

    /// <summary>The directional term's direction, and the sun's travel derived from the SAME vector so the
    /// cascades and the shading cannot disagree. A light reaching past <see cref="DirectionalRange"/> IS the
    /// sun; the light travels the way the to-light vector came from, and that is set but never cleared --
    /// an interior has no sun and the caster pass gates on <c>FrameInputs.Interior</c> for exactly that.</summary>
    private static void PublishDir(Vector3 toLight, bool sun)
    {
        FrameInputs.AreaDiffuseDir = toLight;
        if (!sun) return;
        Vector3 travel = -toLight;
        if (travel.Z < FrameRead.DownwardZ) { FrameInputs.SunDir = travel; FrameInputs.HasSun = true; }
    }

    /// <summary>The glide's own frame on a walk that produced no light rig of its own: the shown direction
    /// moves, nothing else does.</summary>
    private static void CoastSun()
    {
        if (SunGlide.Coast(out Vector3 shown, out bool sun)) PublishDir(shown, sun);
    }

    /// <summary>Is this the world's scene -- a tile grid with an area behind it -- rather than a GUI scene's
    /// 3D view? The area's sun belongs to exactly one of them.</summary>
    private static bool WorldScene(DbId scene)
        => SceneDb.Scenes.Valid(scene) && SceneDb.SceneIsWorld[scene.Index];

    /// <summary>The area's own colour for the palette it is on and the term asked for, which a light record
    /// does not carry: the game writes both into the Aurora "sun" model by script
    /// (<c>CNWCArea::SetGlobalLightDiffuse</c> @0x802510, <c>SetGlobalLightAmbient</c> @0x8022d0, as
    /// <c>controlpart sun</c>), so the last one sent wins (<see cref="SunColour"/>), else the ARE pair.</summary>
    private static Vector3 Palette(in FrameHoist h, bool ambient, Vector3 fallback)
    {
        if (ambient ? _sunAmbSet : _sunDifSet) return ambient ? _sunAmb : _sunDif;
        if (!h.HaveArea) return fallback;
        Vector3 c = ambient ? h.Ambient : h.Diffuse;
        return c == Vector3.Zero ? fallback : c;
    }

    private static int _areaAmb, _areaDif, _tile, _deduped, _static;
    /// <summary>Why <see cref="Admit"/> skipped a light: hidden object, no reach, no multiplier, black, and how
    /// many of those took the zero from an animation rather than the record (2026-10-08: a city skipped 1434 of
    /// 1444 tile lights with no way to tell which).</summary>
    private static int _skipOff, _skipReach, _skipMul, _skipBlack, _skipAnimated;

    /// <summary>The colour the game last set on the sun's ambient or diffuse part (<c>SunFns</c>, the seam's
    /// <c>controlpart</c>): the day/night blend and any scripted palette, which the ARE pair is only the
    /// load-time default of. Kept across areas -- the game sets it again on every day/night update.</summary>
    internal static void SunColour(bool ambient, Vector3 c)
    {
        if (ambient) { _sunAmb = c; _sunAmbSet = true; }
        else { _sunDif = c; _sunDifSet = true; }
    }

    private static Vector3 _sunAmb, _sunDif;
    private static bool _sunAmbSet, _sunDifSet;

    /// <summary>The engine's own lit radius and colour pair (<c>GetLightAdjustedRadius</c> @0x4c0c10 and
    /// <c>GetLightAdjustedColor</c> @0x4c0b60), ported: the radius is range x intensity times the global
    /// radius multiplier, boosted by the max colour channel when that exceeds 1, and the colour divided back
    /// down by the same max -- an overbright light spends its surplus on REACH, not on hue.</summary>
    internal static void Adjust(float range, float inten, float radiusMult, ref Vector3 rgb, out float adjR)
    {
        float maxc = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
        adjR = range * inten * radiusMult;
        if (maxc > 1f)
        {
            adjR *= maxc;
            float k = AlloyLights.OverbrightK(maxc);
            rgb = new Vector3(rgb.X * k, rgb.Y * k, rgb.Z * k);
        }
    }

    internal static string Stats()
        => $"dblights: {_reused} frames reused, {_walked} walked ({_tile} tile, {_deduped} deduped), {_admitted} admitted "
         + $"({_static} static), {_skipped} skipped (hidden {_skipOff}, no reach {_skipReach}, no multiplier {_skipMul}, "
         + $"black {_skipBlack}; {_skipAnimated} of them posed by an animation), area amb/dif {_areaAmb}/{_areaDif}"
         + $", {SunGlide.Note()}"
         + (FrameInputs.Blind ? " BLIND LIGHTS (one object's lights alone, no area pair, no baked pages)" : "")
         + (FrameInputs.HasAreaAmbient ? "" : " NO AREA AMBIENT")
         + (FrameInputs.HasAreaDiffuse ? "" : " NO AREA DIFFUSE");

    /// <summary>Off-device: the arithmetic, the admission (blind lights included) and the area pair's published
    /// direction. A light dropped here is an unlit area, and a radius that drifted from the engine's is every
    /// lit sphere the wrong size. Oracle: hand-written expected values over a synthetic database.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbLights.SelfCheck");
        // The fixture's hoisted terms are "no area, unit radius multiplier": off-device there is no CNWCArea
        // to read, which is also the main-menu state.
        var noArea = new FrameHoist { RadiusMult = 1f };

        // The area light arrives as console text (CNWCArea::SetGlobalLightDiffuse's own sprintf, %f format).
        check.Expect(CX.Alloy.Seam.SunFns.Parse("sun gidy_sun_diff color 0.250000 0.500000 1.000000"u8, out bool amb, out Vector3 sc)
                     == CX.Alloy.Seam.SunFns.Verdict.Colour && !amb && sc == new Vector3(0.25f, 0.5f, 1f),
                     "controlpart's sun diffuse line parses to its colour");
        check.Expect(CX.Alloy.Seam.SunFns.Parse("SUN gidy_sun_amb color 0.1 0.2 0.3"u8, out amb, out _)
                     == CX.Alloy.Seam.SunFns.Verdict.Colour && amb, "...the ambient part, case-blind as FindGob is");
        check.Expect(CX.Alloy.Seam.SunFns.Parse("lamp01 light color 1 1 1"u8, out _, out _)
                     == CX.Alloy.Seam.SunFns.Verdict.NoObject, "...and any other object answers as the engine's would");

        // A plain light: radius is range x intensity x the global multiplier, hue untouched.
        var rgb = new Vector3(0.5f, 0.6f, 0.7f);
        Adjust(10f, 2f, 1.5f, ref rgb, out float r);
        check.Expect(MathF.Abs(r - 30f) < 1e-4f, $"a plain light's radius is range x intensity x mult ({r})");
        check.Expect(rgb == new Vector3(0.5f, 0.6f, 0.7f), "...and its colour is untouched");

        // An overbright torch: the surplus goes to REACH and the hue is normalised back down.
        var torch = new Vector3(1.75f, 1.50f, 1.00f);
        Vector3 before = torch;
        Adjust(10f, 1f, 1f, ref torch, out float tr);
        check.Expect(MathF.Abs(tr - 17.5f) < 1e-3f,
                     $"an overbright light spends its surplus on reach ({tr}, expected range x maxc)");
        check.Expect(torch.X <= before.X && torch.Y <= before.Y && torch.Z <= before.Z,
                     "...and its colour comes back DOWN rather than tinting every lit surface");
        check.Expect(MathF.Abs(torch.X / torch.Y - before.X / before.Y) < 1e-3f,
                     "...keeping its hue exactly (the ratio between channels is unchanged)");

        // The admission rules, over a synthetic database. The departure FADE is off for the duration: the walk
        // closes with it (so a light that leaves is still emitted, shrinking, for Fadeout frames), which
        // SceneInputs.FadeSelfCheck owns.
        int fadeWas = Lighting.Fadeout.V;
        Lighting.Fadeout.V = 0;
        DbInputs.Reset();
        ModelDef m = LightModel();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = SceneDb.Objects.Alloc();
        DbFeed.InitObject(obj.Index);
        SceneDb.ObjScene[obj.Index] = scene;
        SceneDb.ObjVisible[obj.Index] = true;
        DbId h = SceneDb.Parts.Alloc();
        int p = h.Index;
        DbFeed.InitPart(p);
        SceneDb.PartObj[p] = obj;
        SceneDb.PartModel[p] = m;
        SceneDb.PartNode[p] = 1;
        SceneDb.PartEnabled[p] = true;
        SceneDb.PartWorld[p] = Matrix4x4.CreateTranslation(3f, 4f, 5f);
        DbInputs.PartCreated(p);

        Walk(scene, in noArea);
        check.Expect(FrameInputs.LightCount == 1, $"a live light of this scene is admitted ({FrameInputs.LightCount})");
        check.Expect(FrameInputs.AreaLightId[0] == (IntPtr)(p + 1),
                     "...under its part index as an identity, which no allocator recycles");

        // ...and another scene's is not.
        DbId other = SceneDb.Scenes.Alloc();
        Walk(other, in noArea);
        check.Expect(FrameInputs.LightCount == 0, "a light of another scene is not in this scene's rig");

        // An invisible object's light is off, which is what a door closing on a lit room looks like.
        SceneDb.ObjVisible[obj.Index] = false;
        Walk(scene, in noArea);
        check.Expect(FrameInputs.LightCount == 0, "a light on an invisible object does not light");
        SceneDb.ObjVisible[obj.Index] = true;

        // Blind lights (a blindness effect's light): only the named object's lights light the scene.
        DbId blind = SceneDb.Objects.Alloc();
        SceneDb.SceneBlindLights[scene.Index] = blind;
        Walk(scene, in noArea);
        check.Expect(FrameInputs.Blind && FrameInputs.LightCount == 0,
                     $"under another object's blind lights this light is out of the rig ({FrameInputs.LightCount})");
        check.Expect(FrameInputs.HasAreaAmbient && FrameInputs.AreaAmbient == Vector3.Zero,
                     "...and the area ambient is black, not the grey stand-in");
        SceneDb.SceneBlindLights[scene.Index] = obj;
        Walk(scene, in noArea);
        check.Expect(FrameInputs.LightCount == 1, $"...while the blind object's own light still lights ({FrameInputs.LightCount})");
        SceneDb.SceneBlindLights[scene.Index] = default;
        SceneDb.Objects.Free(blind);

        // A TILE light, which is the case the part registry never saw: the Lit table is enumerated directly,
        // so a light node that never became a part still lights its area.
        DbId lid = SceneDb.Lights.Alloc();
        int lk = lid.Index;
        SceneDb.LitObj[lk] = obj;
        SceneDb.LitPart[lk] = -1;
        SceneDb.LitPos[lk] = new Vector3(9f, 8f, 7f);
        SceneDb.LitColour[lk] = new Vector3(1f, 1f, 1f);
        SceneDb.LitRange[lk] = 6f;
        SceneDb.LitMultiplier[lk] = 1f;
        SceneDb.LitDynamic[lk] = false;
        SceneDb.LitShadow[lk] = true;
        Walk(scene, in noArea);
        check.Expect(FrameInputs.LightCount == 2, $"a tile light with no part is admitted ({FrameInputs.LightCount})");
        int at = FrameInputs.AreaLightId[0] == DbInputs.LitId(lk) ? 0 : 1;
        check.Expect(FrameInputs.AreaLightId[at] == DbInputs.LitId(lk), "...under its own Lit record as an identity");
        // ...and LitDynamic=false is what the bake is TOLD. Whether it comes back static is the bake's answer,
        // and it is "no" until the arena ranges are charted -- a static light with no page to be lit
        // from is an unlit one (LightmapBake.Produces).
        check.Expect(FrameInputs.AreaLightStatic[at] == LightmapBake.Produces,
                     $"LitDynamic=false reaches the bake as static, and the bake answers for its own ability to "
                   + $"produce a page (Produces={LightmapBake.Produces}, static={FrameInputs.AreaLightStatic[at]})");

        // ...and the same light reached through a part is ONE light, not two.
        SceneDb.LitPart[lk] = p;
        DbInputs.RegisterTileLight(p, lk);
        Walk(scene, in noArea);
        check.Expect(FrameInputs.LightCount == 1, $"a tile light that also has a part is admitted once ({FrameInputs.LightCount})");
        SceneDb.Lights.Free(lid); SceneDb.Lights.TakeDirty();
        DbInputs.RegisterTileLight(p, -1);

        // The area PAIR's direction, end to end: the engine's own directional light reaches past
        // DirectionalRange, and what reaches the cascades and the shading is the glided direction. The edge
        // is announced first and last, so this fixture neither inherits a sun nor leaves one behind.
        SunGlide.AreaEdge();
        SceneDb.SceneIsWorld[scene.Index] = true;
        DbId sid = SceneDb.Lights.Alloc();
        int sk = sid.Index;
        SceneDb.LitObj[sk] = obj;
        SceneDb.LitPart[sk] = -1;
        SceneDb.LitPos[sk] = Overhead(0.0) * SunReach;
        SceneDb.LitColour[sk] = new Vector3(1f, 1f, 1f);
        SceneDb.LitRange[sk] = SunReach;
        SceneDb.LitMultiplier[sk] = 1f;
        SceneDb.LitAmbientOnly[sk] = false;
        Walk(scene, in noArea);
        check.Expect(FrameInputs.HasAreaDiffuse && (FrameInputs.AreaDiffuseDir - Overhead(0.0)).Length() < 1e-5f,
                     $"an area's first sun is shown, not glided to ({FrameInputs.AreaDiffuseDir})");
        check.Expect(FrameInputs.HasSun && FrameInputs.SunDir == -FrameInputs.AreaDiffuseDir,
                     $"...and the sun travels the way the to-light vector came from ({FrameInputs.SunDir})");
        // A server's unfaded step, 1.5 degrees: under the cut threshold, so the shown direction sets off
        // towards it rather than arriving on this frame.
        SceneDb.LitPos[sk] = Overhead(1.5) * SunReach;
        Walk(scene, in noArea);
        Vector3 at0 = Overhead(0.0), at15 = Overhead(1.5), live = FrameInputs.AreaDiffuseDir;
        check.Expect(Vector3.Dot(live, at0) > Vector3.Dot(live, at15),
                     $"a small step glides instead of landing on its own frame ({live})");
        check.Expect(FrameInputs.SunDir == -live, "...with one direction behind both the cascades and the shading");
        SceneDb.Lights.Free(sid); SceneDb.Lights.TakeDirty();
        SceneDb.SceneIsWorld[scene.Index] = false;
        SunGlide.AreaEdge();

        // A destroyed light leaves the rig in the same frame, with no sweep and no tombstone.
        DbInputs.PartDestroyed(p);
        Walk(scene, in noArea);
        check.Expect(FrameInputs.LightCount == 0, "a destroyed light is out of the rig at once");

        SceneDb.Parts.Free(h); SceneDb.Objects.Free(obj);
        SceneDb.Scenes.Free(scene); SceneDb.Scenes.Free(other);
        SceneDb.Parts.TakeDirty(); SceneDb.Objects.TakeDirty(); SceneDb.Scenes.TakeDirty();
        DbInputs.Reset();
        Lighting.Fadeout.V = fadeWas;
        FrameInputs.BeginAreaLights(0);
        FrameInputs.ResetLocalLights(0);

        log.Info(check.Ok ? "DbLights.SelfCheck: passed"
                          : "DbLights.SelfCheck: FAILED -- a DB-mode area would be lit wrongly or not at all");
        return check.Ok;
    }

    /// <summary>The fixture sun's reach: past <see cref="DirectionalRange"/>, so the walk takes the light for
    /// the area pair and for the sun rather than for a lamp.</summary>
    private const float SunReach = 10000f;

    /// <summary>A unit to-light direction <paramref name="deg"/> degrees off straight up, in the XZ plane:
    /// the fixture's sun, and the oracle its direction expectations are written against.</summary>
    private static Vector3 Overhead(double deg)
    {
        double r = deg * Math.PI / 180.0;
        return new Vector3((float)Math.Sin(r), 0f, (float)Math.Cos(r));
    }

    private static ModelDef LightModel()
    {
        var m = new ModelDef { Name = "cx_lightcheck", ContentHash = 0x11D11D };
        var root = new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy };
        var light = new NodeDef
        {
            Name = "lamp", Index = 1, Parent = 0, Kind = NodeKind.Light,
            Light = new LightDef { Dynamic = true, Shadow = true },
            Controllers = new[]
            {
                new ControllerTrack { Type = Ctl.LightColor, Columns = 3, Times = new[] { 0f }, Values = new[] { 1f, 0.9f, 0.8f } },
                new ControllerTrack { Type = Ctl.LightRadius, Columns = 1, Times = new[] { 0f }, Values = new[] { 12f } },
                new ControllerTrack { Type = Ctl.LightMultiplier, Columns = 1, Times = new[] { 0f }, Values = new[] { 1f } },
            },
        };
        m.Nodes = new[] { root, light };
        m.NodeByName["root"] = 0; m.NodeByName["lamp"] = 1;
        return m;
    }
}
