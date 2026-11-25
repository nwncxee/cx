//::///////////////////////////////////////////////
//:: inc_take -- the Take all showcases' module (invtakeall, invtakefull; tools/live/inventory_takeall_regression.sh). The
//:: PC's backpack is emptied, or filled to its last cell with rings (6 pages of 10x6) when nFill is set, a chest holding five small items stands 2 m ahead,
//:: and 8 s in the PC opens it. Every change of what the chest and the backpack hold is marked, so the grade reads the
//:: server's own record of each take; each count mark carries its tick, so a sorted marker file keeps their order.
//:://////////////////////////////////////////////

void Mark(string sStep)
{
    WriteTimestampedLogEntry("CXSHOW: " + sStep);
}

int Count(object oHolder)
{
    int n = 0;
    object oItem = GetFirstItemInInventory(oHolder);
    while (GetIsObjectValid(oItem)) { n++; oItem = GetNextItemInInventory(oHolder); }
    return n;
}

void Watch(object oPC, object oChest, int nChest, int nPack, int nTick)
{
    if (!GetIsObjectValid(oPC) || !GetIsObjectValid(oChest)) return;
    int c = Count(oChest), p = Count(oPC);
    if (c != nChest || p != nPack) Mark("count t=" + GetStringRight("000" + IntToString(nTick), 4) + " chest=" + IntToString(c) + " pack=" + IntToString(p));
    if (nTick >= 240) { Mark("final chest=" + IntToString(c) + " pack=" + IntToString(p)); return; }
    DelayCommand(0.25, Watch(oPC, oChest, c, p, nTick + 1));
}

void Chest(object oPC)
{
    object oArea = GetArea(oPC);
    float fFace = GetFacing(oPC);
    vector vAt = GetPosition(oPC) + AngleToVector(fFace) * 2.0;
    object oChest = CreateObject(OBJECT_TYPE_PLACEABLE, "plc_chest1", Location(oArea, vAt, fFace + 180.0), FALSE, "CXSHOW_chest");
    if (!GetIsObjectValid(oChest)) { Mark("chest-FAILED: plc_chest1"); return; }
    SetLocked(oChest, FALSE);
    CreateItemOnObject("nw_it_mpotion001", oChest, 1, "CXSHOW_loot");
    CreateItemOnObject("nw_it_mpotion002", oChest, 1, "CXSHOW_loot");
    CreateItemOnObject("nw_it_mring001", oChest, 1, "CXSHOW_loot");
    CreateItemOnObject("nw_it_mneck001", oChest, 1, "CXSHOW_loot");
    CreateItemOnObject("nw_it_gem001", oChest, 1, "CXSHOW_loot");
    Mark("chest " + ObjectToString(oChest) + " holds " + IntToString(Count(oChest)));
}

void Open(object oPC)
{
    object oChest = GetNearestObjectByTag("CXSHOW_chest", oPC);
    Mark("before chest=" + IntToString(Count(oChest)) + " pack=" + IntToString(Count(oPC)));
    AssignCommand(oPC, ActionInteractObject(oChest));
    Mark("open " + ObjectToString(oChest));
    Watch(oPC, oChest, Count(oChest), Count(oPC), 0);
}

void Fill(object oPC)
{
    int i;
    for (i = 0; i < 360; i++) CreateItemOnObject("nw_it_mring001", oPC, 1, "CXSHOW_fill");
    Mark("filled pack=" + IntToString(Count(oPC)));
}

void Arm(object oPC, int nFill, int nTry)
{
    if (!GetIsObjectValid(oPC)) return;
    if (!GetIsObjectValid(GetArea(oPC)))
    {
        if (nTry >= 120) { Mark("arm-FAILED: the PC never landed in an area"); return; }
        DelayCommand(0.5, Arm(oPC, nFill, nTry + 1));
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
    if (nFill) DelayCommand(1.0, Fill(oPC));
    DelayCommand(2.0, Chest(oPC));
    DelayCommand(8.0, Open(oPC));
}
