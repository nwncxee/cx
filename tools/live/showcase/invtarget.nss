//::///////////////////////////////////////////////
//:: invtarget -- an item target cursor on the PC, for the CX inventory's targeting test
//:: (tools/live/inventory_target_regression.sh). Standalone, with its own main, as invstore: the PC's backpack is
//:: emptied and given one tagged longsword, and 25 s after landing EnterTargetingMode raises the engine's own item
//:: target cursor. The module's player-target event runs this script too and logs what the click picked.
//:://////////////////////////////////////////////

const string TARGET_TAG = "CXSHOW_target";

void Mark(string sStep)
{
    WriteTimestampedLogEntry("CXSHOW: " + sStep);
}

void Give(object oPC)
{
    object oItem = CreateItemOnObject("nw_wswls001", oPC, 1, TARGET_TAG);
    Mark(GetIsObjectValid(oItem) ? "item " + ObjectToString(oItem) : "item-FAILED: nw_wswls001");
}

void Aim(object oPC)
{
    EnterTargetingMode(oPC, OBJECT_TYPE_ITEM);
    Mark("aiming");
}

void Arm(object oPC, int nTry)
{
    if (!GetIsObjectValid(oPC)) return;
    if (!GetIsObjectValid(GetArea(oPC)))
    {
        if (nTry >= 120) { Mark("arm-FAILED: the PC never landed in an area"); return; }
        DelayCommand(0.5, Arm(oPC, nTry + 1));
        return;
    }
    SetImmortal(oPC, TRUE);
    object oItem = GetFirstItemInInventory(oPC);
    while (GetIsObjectValid(oItem))
    {
        DestroyObject(oItem);
        oItem = GetNextItemInInventory(oPC);
    }
    Mark("enter " + GetName(oPC) + " in " + GetName(GetArea(oPC)));
    DelayCommand(1.0, Give(oPC));
    DelayCommand(25.0, Aim(oPC));
}

void main()
{
    if (GetCurrentlyRunningEvent() == EVENT_SCRIPT_MODULE_ON_PLAYER_TARGET)
    {
        object oPicked = GetTargetingModeSelectedObject();
        Mark("targeted " + (GetIsObjectValid(oPicked) ? GetTag(oPicked) : "nothing"));
        return;
    }
    object oPC = GetEnteringObject();
    if (!GetIsPC(oPC)) return;
    SetEventScript(GetModule(), EVENT_SCRIPT_MODULE_ON_PLAYER_TARGET, "cxshow");
    Arm(oPC, 0);
}
