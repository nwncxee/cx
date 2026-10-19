namespace CX.Ui;

/// <summary>What a consumer settings row (<see cref="KnobPanel.Row"/> without a knob) edits. The row carries the
/// extras each needs (bit, colour key, slider range); the panel never reads it, the consumer switches on it in
/// its row-press callback. Chat's per-window <c>SettingsWindow</c> is the one consumer.</summary>
public enum SetAct
{
    None, ToggleBit, InputTop, AutoHideHdr, AutoHideFrame, LockWin, TellsHere, AnimOn, SliderHold, SliderFade, SliderTau, SliderFont, Typeface, OutlineOn,
    CycleFmt, CycleSrc, CycleAlign, CycleTellOpen, CycleBgMode, SliderBgAngle, SliderBgStart, SliderBgEnd,
    TabRemove, TabLabel, TabChannel, CloseWindow,
    CopyFrom,
}
