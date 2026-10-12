using System;
using CX.Alloy.Db;
using CX.Alloy.Mdl;

namespace CX.Alloy.Seam;

/// <summary>The per-object texture overrides behind <c>Gob::ReplaceTexture</c> / <c>RestoreTexture</c>, and the
/// instance environment map behind <c>SetEnvironmentMap</c>. An override lives in <see cref="SceneDb.PartTexId"/>,
/// which is per PART rather than on a material every instance of the model shares; the feed's stage uses it when
/// non-zero and the recipe's texture otherwise, so it is one column write plus <see cref="Dirty.Texture"/>.</summary>
internal static class ObjectTex
{
    /// <summary>Point every part of <paramref name="obj"/> whose current texture in slot <paramref name="slot"/> is
    /// named <paramref name="from"/> at <paramref name="to"/>; a null or empty <paramref name="from"/> matches every
    /// part. Returns how many parts changed -- the engine's non-zero "it worked". <paramref name="rows"/> are PLT
    /// layer rows and go on the MATCHED parts, per <c>CNWCAnimBaseParts::ReplaceTexture</c> @0xad8b10.</summary>
    internal static int Replace(DbId obj, string? from, string? to, int slot = 0, bool subtree = false,
                                ReadOnlySpan<int> rows = default)
    {
        if (string.IsNullOrEmpty(to) || (uint)slot >= 4) return 0;
        return ReplaceIn(obj, from, to, slot, 0, rows);
    }

    /// <summary>How deep a composition may be grafted before the walk gives up. A creature is one hop; the
    /// bound is a cycle guard, not a budget.</summary>
    private const int MaxGraft = 8;

    /// <summary>The object's own parts, then every piece <c>Gob::Append</c> grafted onto it. A creature's torso,
    /// arms and cloak are each a child object, so the base range alone is not the whole wardrobe.</summary>
    private static int ReplaceIn(DbId obj, string? from, string? to, int slot, int depth, ReadOnlySpan<int> rows)
    {
        if (depth > MaxGraft || !SceneDb.Objects.Valid(obj)) return 0;
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        int hits = 0;
        for (int p = first; p < first + count; p++)
        {
            // `subtree` was the engine's "only this part's descendants" form. CX's range IS the subtree of
            // the object's root, and the only caller that passes it is the whole-object case, so the flag
            // changes nothing here; it is accepted rather than ignored silently.
            if (ShowsOverride(p, slot, to!)) Refresh(p, slot);
            if (!Matches(p, slot, from) && !GraftNamed(p, slot, from)) continue;
            int at = p * 4 + slot;
            var old = new TexId((uint)SceneDb.PartTexId[at]);
            SceneDb.PartTexId[at] = (int)TexRegistry.Swap(old, to!).Value;
            if (!rows.IsEmpty) PltColors.SetPartRows(p, rows);
            hits++;
        }
        if (hits > 0) SceneDb.Touch(obj, Dirty.Texture);
        for (DbId c = SceneDb.ObjSeamAppendHead[obj.Index]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (!SceneDb.Objects.Valid(c)) break;
            hits += ReplaceIn(c, from, to, slot, depth + 1, rows);
        }
        return hits;
    }

    /// <summary>The step <c>Gob::ReplaceTexture</c> @0x43eb30 runs on a part that ALREADY shows the texture being
    /// asked for: drop back to the model's own name, then match <paramref name="slot"/> again. That is what lets
    /// the same <c>(from, to)</c> pair mean something twice -- the load panel re-issues one pair per save while
    /// swapping the file behind <c>to</c>. The texture is also FORGOTTEN, or the registry re-serves its pixels.</summary>
    private static void Refresh(int p, int slot)
    {
        int at = p * 4 + slot;
        if (SceneDb.PartTexId[at] == 0) return;              // already the model's own texture
        var had = new TexId((uint)SceneDb.PartTexId[at]);
        string resref = TexRegistry.ResrefOf(had);
        TexRegistry.Release(had);
        SceneDb.PartTexId[at] = 0;
        TexRegistry.Forget(resref);
    }

    /// <summary>Does part <paramref name="p"/>'s slot already carry an override named <paramref name="name"/>?</summary>
    private static bool ShowsOverride(int p, int slot, string name)
    {
        int ov = SceneDb.PartTexId[p * 4 + slot];
        return ov != 0 && string.Equals(TexRegistry.ResrefOf(new TexId((uint)ov)), name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drop every override on <paramref name="obj"/> so each part falls back to its recipe's own
    /// texture -- <c>Gob::RestoreTexture</c>.</summary>
    internal static int Restore(DbId obj) => RestoreIn(obj, 0);

    /// <inheritdoc cref="ReplaceIn"/>
    private static int RestoreIn(DbId obj, int depth)
    {
        if (depth > MaxGraft || !SceneDb.Objects.Valid(obj)) return 0;
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        int hits = 0;
        for (int p = first; p < first + count; p++)
            for (int slot = 0; slot < 4; slot++)
            {
                int at = p * 4 + slot;
                if (SceneDb.PartTexId[at] == 0) continue;
                if (slot == 0) PltColors.ClearPart(p);
                TexRegistry.Release(new TexId((uint)SceneDb.PartTexId[at]));
                SceneDb.PartTexId[at] = 0;
                hits++;
            }
        if (hits > 0) SceneDb.Touch(obj, Dirty.Texture);
        for (DbId c = SceneDb.ObjSeamAppendHead[obj.Index]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (!SceneDb.Objects.Valid(c)) break;
            hits += RestoreIn(c, depth + 1);
        }
        return hits;
    }

    /// <summary>Release the overrides a dying record holds; nothing else owns those texture references.</summary>
    internal static void Free(DbId obj) => Restore(obj);

    // ---- the instance environment map ----

    /// <summary><c>Gob::SetEnvironmentMap</c> @0x53f530: the map the whole GOB reflects. The engine kept ONE
    /// texture pointer for it (<c>gob+0x480</c>) and never a material texture slot, so it goes in
    /// <see cref="SceneDb.ObjEnvMap"/> and slot 3 stays the material's roughness map. A null, empty or
    /// <c>"NULL"</c> name clears it, as the engine's own compare does; 1 is the engine's answer, always.</summary>
    internal static int SetEnvMap(DbId obj, string? name)
    {
        bool clear = string.IsNullOrEmpty(name)
                  || string.Equals(name, "NULL", StringComparison.OrdinalIgnoreCase);
        SetEnvMapIn(obj, clear ? null : name, 0);
        return 1;
    }

    /// <summary>Give a freshly grafted piece the env map of the object it was grafted onto: one pointer per
    /// gob covered every part in vanilla, the body pieces appended after the call included.</summary>
    internal static void InheritEnvMap(DbId child, DbId from)
    {
        if ((uint)from.Index < SceneDb.MaxObjects) SetEnvMapIn(child, SceneDb.ObjEnvMap[from.Index], 0);
    }

    /// <summary>The object, then every piece <c>Gob::Append</c> grafted onto it: one gob's env map covered a
    /// creature's torso, arms and cloak, each of which is a child object here.</summary>
    private static void SetEnvMapIn(DbId obj, string? name, int depth)
    {
        if (depth > MaxGraft || !SceneDb.Objects.Valid(obj)) return;
        int i = obj.Index;
        if (!string.Equals(SceneDb.ObjEnvMap[i], name, StringComparison.Ordinal))
        {
            SceneDb.ObjEnvMap[i] = name;
            // The stage memoises the env bit per record, so the records that read it have to be told.
            SceneDb.Touch(obj, Dirty.Texture);
            Facade.Log?.Info($"seam: env map {(name == null ? "cleared" : $"\"{name}\"")} on o={i} -- kept in "
                             + "its own column; material slot 3 stays the roughness map");
        }
        for (DbId c = SceneDb.ObjSeamAppendHead[i]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (!SceneDb.Objects.Valid(c)) break;
            SetEnvMapIn(c, name, depth + 1);
        }
    }

    /// <summary>Point every part of <paramref name="obj"/> whose RESOLVED texture name in slot
    /// <paramref name="slot"/> is <paramref name="from"/> at <paramref name="to"/>, or -- <paramref name="to"/> null
    /// or empty -- back at its own texture; returns how many parts changed. The tile swap needs the resolved
    /// <see cref="SceneDb.PartTexName"/>, not <see cref="Matches"/>, whose model-node mesh a tile merges away.</summary>
    internal static int ReplaceByTexName(DbId obj, string from, string? to, int slot = 0)
    {
        if (string.IsNullOrEmpty(from) || (uint)slot >= 4 || !SceneDb.Objects.Valid(obj)) return 0;
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        int hits = 0;
        for (int p = first; p < first + count; p++)
        {
            if (!string.Equals(SceneDb.PartTexName(p, slot), from, StringComparison.OrdinalIgnoreCase)) continue;
            int at = p * 4 + slot;
            var old = new TexId((uint)SceneDb.PartTexId[at]);
            if (string.IsNullOrEmpty(to))
            {
                if (SceneDb.PartTexId[at] == 0) continue;   // already its own texture
                TexRegistry.Release(old);
                SceneDb.PartTexId[at] = 0;
            }
            else SceneDb.PartTexId[at] = (int)TexRegistry.Swap(old, to).Value;
            hits++;
        }
        if (hits > 0) SceneDb.Touch(obj, Dirty.Texture);
        return hits;
    }

    /// <summary>Is part <paramref name="p"/> a mesh of the graft MODEL named <paramref name="from"/>?
    /// <c>CreateBodyParts</c> @0x834440 issues <c>ReplaceTexture(from = the part's model resref, to = its PLT)</c>,
    /// and many body models declare a different bitmap (<c>pfh0_chest003</c>'s mesh says <c>pfh0_chest001</c>), so
    /// the model name has to match too or those torsos and pelvises stay on the default body.</summary>
    private static bool GraftNamed(int p, int slot, string? from)
    {
        if (slot != 0 || string.IsNullOrEmpty(from)) return false;
        ModelDef? m = SceneDb.PartModel[p];
        int n = SceneDb.PartNode[p];
        if (m == null || (uint)n >= (uint)m.Nodes.Length || m.Nodes[n].Mesh == null) return false;
        return string.Equals(m.Name, from, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Is part <paramref name="p"/>'s slot <paramref name="slot"/> the model's texture named
    /// <paramref name="from"/>? Matched on the MODEL's name whatever override is in place, as the engine's
    /// <c>AurTextureReplaceSingleReference</c> @0x48d960 does: <c>CGuiInvbox::SetObjectHilite</c> @0x691d50
    /// un-highlights with ("gui_boxes_ingame", "gui_boxes_ingame") over a part showing "gui_inv_hilite".</summary>
    private static bool Matches(int p, int slot, string? from)
    {
        if (string.IsNullOrEmpty(from)) return true;
        ModelDef? m = SceneDb.PartModel[p];
        int n = SceneDb.PartNode[p];
        if (m == null || (uint)n >= (uint)m.Nodes.Length) return false;
        // The texture names are the MESH's, not the node's: a node with no mesh has none to match.
        MeshDef? mesh = m.Nodes[n].Mesh;
        if (mesh == null || (uint)slot >= (uint)mesh.Textures.Length) return false;
        return string.Equals(mesh.Textures[slot], from, StringComparison.OrdinalIgnoreCase);
    }
}
