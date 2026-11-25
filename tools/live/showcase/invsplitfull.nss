//::///////////////////////////////////////////////
//:: invsplitfull -- invsplit with the first backpack page filled, for the CX inventory's split test
//:: (tools/live/inventory_split_regression.sh). Standalone, with its own main, as invtarget: the PC's backpack is
//:: emptied and given one tagged stack of 10 arrows (the first cell). invsplitfull is the same with the rest of the
//:: first page filled, so the split's new half must land on another page.
//:://////////////////////////////////////////////

const string SPLIT_TAG = "CXSHOW_split";
const int FILL_FIRST = TRUE;

void Mark(string sStep)
{
    WriteTimestampedLogEntry("CXSHOW: " + sStep);
}

void Give(object oPC)
{
    object oItem = CreateItemOnObject("nw_wamar001", oPC, 10, SPLIT_TAG);
    Mark(GetIsObjectValid(oItem) ? "item " + ObjectToString(oItem) + " stack " + IntToString(GetItemStackSize(oItem)) : "item-FAILED: nw_wamar001");
    if (!FILL_FIRST) return;
    int i;
    for (i = 0; i < 30; i++) CreateItemOnObject("nw_it_torch001", oPC, 1, "CXSHOW_fill");
    Mark("filled");
}

void Report(object oPC)
{
    object oItem = GetFirstItemInInventory(oPC);
    while (GetIsObjectValid(oItem))
    {
        if (GetTag(oItem) == SPLIT_TAG) Mark("stack " + ObjectToString(oItem) + " " + IntToString(GetItemStackSize(oItem)));
        oItem = GetNextItemInInventory(oPC);
    }
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
    DelayCommand(40.0, Report(oPC));
}

void main()
{
    object oPC = GetEnteringObject();
    if (!GetIsPC(oPC)) return;
    Arm(oPC, 0);
}
