using System.Runtime.InteropServices;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The tooltip's one display funnel, <c>CGuiMan::ShowTooltipText</c>, chained always: it names the scene the
/// tooltip bubble landed in (<see cref="Scene"/>), because that bubble shares the <c>sceneGUI_PNL_BUBBLE*</c> prefix
/// DbGui refuses for the in-world chat bubbles under the nameplate suppression. <c>alloy.hitcheck=1</c> also chains
/// the delay (<c>CGuiMan::HandleGuiTimer</c>) and traces it, budgeted. Both hooks are cold.</summary>
internal static unsafe class TooltipProbe
{
    private const int Budget = 30;
    private static ExtenderLog? _log;
    private static GuiManHandleGuiTimerFn* _timer;
    private static GuiManShowTooltipTextFn* _show;
    private static int _fired, _shown;

    /// <summary>The scene the tooltip bubble's model is in since the last show (its handle, generation-checked by
    /// the reader), written on the game thread and read by the GUI draw.</summary>
    internal static volatile uint Scene;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _show = hooks.ChainGuiManShowTooltipText(&OnShow, 0);
        if (_show == null) log.Always("TooltipProbe: CGuiMan::ShowTooltipText did not chain -- in DB mode no tooltip draws "
            + "while the nameplate suppression refuses the chat bubbles");
        if (!Renderer.HitCheck.Boot) return;
        _timer = hooks.ChainGuiManHandleGuiTimer(&OnTimer, 0);
        log.Info($"TooltipProbe: timer {(_timer != null ? "chained" : "UNCHAINED")}, "
            + $"show {(_show != null ? "chained" : "UNCHAINED")} -- [tooltip] lines");
    }

    [UnmanagedCallersOnly]
    private static void OnTimer(nint gm, int dt)
    {
        var man = new CGuiMan(gm);
        int before = man.TooltipTimer;
        if (_timer != null && *_timer != null) (*_timer)(gm, dt);
        if (before <= 0 || before - dt >= 1 || _fired >= Budget) return;
        _fired++;
        nint hit = man.TooltipObject;
        _log?.Info($"[tooltip] delay fired (dt={dt}) at mouse ({man.MouseX},"
            + $"{man.MouseY}): hit {(hit == 0 ? "NOTHING" : Obj(new CGuiObject(hit).Model))}"
            + $", timer now {man.TooltipTimer}");
    }

    [UnmanagedCallersOnly]
    private static void OnShow(nint gm, nint text, int instant)
    {
        if (_show != null && *_show != null) (*_show)(gm, text, instant);
        nint bubble = new CGuiMan(gm).TooltipBubble;
        nint model = bubble == 0 ? 0 : new CGuiObject(bubble).Model;
        DbId mid = model == 0 ? default : Facade.ObjectId(model);
        Scene = !mid.IsNull && SceneDb.Objects.Valid(mid) ? SceneDb.ObjScene[mid.Index].Value : 0;
        if (!Renderer.HitCheck.Boot || _shown >= Budget) return;
        _shown++;
        nint chars = text == 0 ? 0 : *(nint*)text;
        nint str = bubble == 0 ? 0 : new CGuiTextBubble(bubble).AurString.Ptr;
        DbId sid = str == 0 ? default : Facade.StringId(str);
        TextDoc? d = sid.IsNull ? null : TextObjects.Of(sid);
        _log?.Info($"[tooltip] show \"{(chars == 0 ? "" : Marshal.PtrToStringUTF8(chars))}\" instant={instant} "
            + $"bubble={bubble:x} model {Obj(model)} | text {Obj(str)} lines={d?.LineCount ?? -1} "
            + $"box={d?.MaxWidth ?? -1:F2}x{d?.MaxHeight ?? -1:F2} font={(d?.Font != null ? "yes" : "NONE")}");
    }

    /// <summary>An engine object as the database knows it: record, model, scene, visibility, world position.</summary>
    private static string Obj(nint shell)
    {
        if (shell == 0) return "null";
        DbId id = Facade.ObjectId(shell);
        if (id.IsNull || !SceneDb.Objects.Valid(id)) return $"{shell:x}=NO RECORD";
        int i = id.Index;
        DbId s = SceneDb.ObjScene[i];
        string scene = s.IsNull ? "no scene" : $"s{s.Index}\"{SceneDb.SceneName[s.Index]}\"";
        var w = SceneDb.ObjWorld[i];
        return $"o{i}\"{SceneDb.ObjModel[i]?.Name ?? SceneDb.ObjSeamModelName[i] ?? "-"}\" {scene} "
            + $"visible={SceneDb.ObjVisible[i]} parts={SceneDb.ObjPartCount[i]} "
            + $"world=({w.M41:F2},{w.M42:F2},{w.M43:F2}) attachTo={SceneDb.ObjAttachTo[i].Index}";
    }
}
