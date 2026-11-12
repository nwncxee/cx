using System;
using System.Collections.Generic;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class EmitterSim
{
    /// <summary>The texture names a missing-texture line has been said for this session.</summary>
    internal sealed class NoTexSaidState
    {
        public readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);
    }

    private static NoTexSaidState _noTexSaid = new();

    /// <summary>An in-view emitter the pass will decline for want of a texture: said once per texture when the registry
    /// has given up on it (or the emitter names none), and nothing while it is still loading.</summary>
    private static void NoteNoTexture(ref Slot slot)
    {
        var id = new TexId(slot.Tex);
        if (!TexRegistry.Final(id)) return;
        SayNoTexture(_log, _noTexSaid, id.IsNull ? "" : TexRegistry.ResrefOf(id), slot.Part);
    }

    /// <summary>The line for texture <paramref name="tex"/> ("" for an emitter that names none), naming the model of
    /// <paramref name="part"/>; false when it was said already.</summary>
    internal static bool SayNoTexture(ExtenderLog? log, NoTexSaidState said, string tex, int part)
    {
        if (log == null || !said.Names.Add(tex)) return false;
        string model = (uint)part < (uint)SceneDb.PartModel.Length ? SceneDb.PartModel[part]?.Name ?? "?" : "?";
        log.Always(tex.Length == 0
            ? $"EmitterSim: an emitter of model \"{model}\" names NO texture -- it draws nothing (said once)"
            : $"EmitterSim: emitter texture \"{tex}\" did NOT load -- every emitter on it draws nothing, first seen on model "
              + $"\"{model}\" (said once per texture)");
        return true;
    }

    /// <summary>The missing-texture line. Oracle: the texture names handed in, and how many lines a tapped log hears.</summary>
    private static void NoTextureSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "EmitterSim.NoTextureSelfCheck");
        var said = new NoTexSaidState();
        int heard = 0, named = 0;
        Action<string>? tap = log.Tap;
        log.Tap = line => { heard++; if (line.Contains("\"fx_nosuch\"")) named++; };
        bool first, again, cased, none;
        try
        {
            first = SayNoTexture(log, said, "fx_nosuch", -1);
            again = SayNoTexture(log, said, "fx_nosuch", -1);
            cased = SayNoTexture(log, said, "FX_NOSUCH", -1);
            none = SayNoTexture(log, said, "", -1);
        }
        finally { log.Tap = tap; }
        check.Expect(first && named == 1, "an emitter texture that never loads is named in the log");
        check.Expect(!again && !cased, "...once per texture, whatever case the model spells it in");
        check.Expect(none && heard == 2, $"an emitter that names no texture is said too, and nothing else is (heard {heard})");
        log.Info(check.Ok ? "EmitterSim.NoTextureSelfCheck: passed" : "EmitterSim.NoTextureSelfCheck: FAILED");
    }
}
