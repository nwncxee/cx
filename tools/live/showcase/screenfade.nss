//::///////////////////////////////////////////////
//:: screenfade -- what darkens the screen: the PC stands mid-area at noon, then takes in turn the darkness duration visual,
//:: a cutscene fade to black, blindness, the blind duration visual and a darkness area of effect -- the two that set the
//:: engine's blackout last, since the blackout outlives them -- with a plain shot first and last. Run it with Alloy and with alloy.enabled=0. No backdrop.
//:://////////////////////////////////////////////
#include "inc_perf"

void Blind(object oPC)
{
    ApplyEffectToObject(DURATION_TYPE_TEMPORARY, EffectBlindness(), oPC, 6.0);
    ShowMark("blindness");
}

void DarkVfx(object oPC)
{
    ApplyEffectToObject(DURATION_TYPE_TEMPORARY, EffectVisualEffect(VFX_DUR_DARKNESS), oPC, 6.0);
    ShowMark("vfx-dur-darkness");
}

void DarkAoe(object oPC)
{
    ApplyEffectAtLocation(DURATION_TYPE_TEMPORARY, EffectAreaOfEffect(AOE_PER_DARKNESS), GetLocation(oPC), 8.0);
    ShowMark("aoe-per-darkness");
}

void BlindVfx(object oPC)
{
    ApplyEffectToObject(DURATION_TYPE_TEMPORARY, EffectVisualEffect(VFX_DUR_BLIND), oPC, 6.0);
    ShowMark("vfx-dur-blind");
}

void Fade(object oPC)
{
    FadeToBlack(oPC, FADE_SPEED_FASTEST);
    ShowMark("fade-to-black");
}

void Unfade(object oPC)
{
    StopFade(oPC);
    ShowMark("stop-fade");
}

void Settle(object oPC)
{
    SetTime(12, 0, 0, 0);
    AssignCommand(oPC, SetCameraFacing(20.0, 12.0, 65.0, CAMERA_TRANSITION_TYPE_SNAP));
    ShowMark("settled in " + GetName(GetArea(oPC)));
    DelayCommand(5.0, DarkVfx(oPC));
    DelayCommand(13.0, Fade(oPC));
    DelayCommand(19.0, Unfade(oPC));
    DelayCommand(21.0, Blind(oPC));
    DelayCommand(29.0, BlindVfx(oPC));
    DelayCommand(37.0, DarkAoe(oPC));
}

void Show(object oPC)
{
    object oArea = GetFirstArea();
    while (GetIsObjectValid(oArea) && GetIsAreaInterior(oArea)) oArea = GetNextArea();
    if (!GetIsObjectValid(oArea)) { ShowMark("outdoor-FAILED: the module has no outdoor area"); return; }
    vector vMid = Vector(GetAreaSize(AREA_WIDTH, oArea) * 5.0, GetAreaSize(AREA_HEIGHT, oArea) * 5.0, 0.0);
    AssignCommand(oPC, JumpToLocation(Location(oArea, vMid, 0.0)));
    ShowMark("jump " + GetName(oArea));
    DelayCommand(8.0, Settle(oPC));
}
