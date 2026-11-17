//::///////////////////////////////////////////////
//:: invstore -- a stock general store opened on the PC, for the CX inventory's store tests
//:: (tools/live/inventory_store_*.sh). Standalone, with its own main: no backdrop, since the test reads the
//:: inventory window and not a picture, and neverwinter.nim 2.3 skips a script whose main sits in an include.
//:: The store opens 25 s after the PC lands, after the tests' own key press at 15 s.
//:://////////////////////////////////////////////

void Mark(string sStep)
{
    WriteTimestampedLogEntry("CXSHOW: " + sStep);
}

void OpenTheStore(object oPC)
{
    object oStore = CreateObject(OBJECT_TYPE_STORE, "nw_storgenral001", GetLocation(oPC), FALSE, "CXSHOW_store");
    if (!GetIsObjectValid(oStore)) { Mark("store-FAILED: nw_storgenral001"); return; }
    OpenStore(oStore, oPC);
    Mark("store open " + ObjectToString(oStore));
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
    Mark("enter " + GetName(oPC) + " in " + GetName(GetArea(oPC)));
    DelayCommand(25.0, OpenTheStore(oPC));
}

void main()
{
    object oPC = GetEnteringObject();
    if (GetIsPC(oPC)) Arm(oPC, 0);
}
