//::///////////////////////////////////////////////
//:: invchest -- a worn helmet, a spare helmet and a chest the PC opens before the inventory ever opens, for the CX
//:: inventory's hand checks (tools/live/inventory_chest_regression.sh). Standalone, as invgrid: the PC's backpack and worn
//:: items are emptied, one helmet is worn, a second goes in the backpack, and a chest holding a dagger stands 2 m ahead.
//:: Eight seconds in the PC opens the chest; the head slot's holder is logged then and 60 s after login.
//:://////////////////////////////////////////////

void Mark(string sStep)
{
    WriteTimestampedLogEntry("CXSHOW: " + sStep);
}

void Head(object oPC, string sWhen)
{
    object oHead = GetItemInSlot(INVENTORY_SLOT_HEAD, oPC);
    Mark(sWhen + " head " + (GetIsObjectValid(oHead) ? GetTag(oHead) : "empty"));
}

void Wear(object oPC)
{
    object oHelm = CreateItemOnObject("nw_arhe001", oPC, 1, "CXSHOW_worn");
    SetIdentified(oHelm, TRUE);
    AssignCommand(oPC, ActionEquipItem(oHelm, INVENTORY_SLOT_HEAD));
    Mark(GetIsObjectValid(oHelm) ? "item CXSHOW_worn " + ObjectToString(oHelm) : "item-FAILED: nw_arhe001");
}

void Spare(object oPC)
{
    object oHelm = CreateItemOnObject("nw_arhe002", oPC, 1, "CXSHOW_helm");
    SetIdentified(oHelm, TRUE);
    Mark(GetIsObjectValid(oHelm) ? "item CXSHOW_helm " + ObjectToString(oHelm) : "item-FAILED: nw_arhe002");
}

void Chest(object oPC)
{
    object oArea = GetArea(oPC);
    float fFace = GetFacing(oPC);
    vector vAt = GetPosition(oPC) + AngleToVector(fFace) * 2.0;
    object oChest = CreateObject(OBJECT_TYPE_PLACEABLE, "plc_chest1", Location(oArea, vAt, fFace + 180.0), FALSE, "CXSHOW_chest");
    if (!GetIsObjectValid(oChest)) { Mark("chest-FAILED: plc_chest1"); return; }
    SetLocked(oChest, FALSE);
    CreateItemOnObject("nw_wswdg001", oChest, 1, "CXSHOW_dagger");
    Mark("chest " + ObjectToString(oChest));
}

void Open(object oPC)
{
    object oChest = GetNearestObjectByTag("CXSHOW_chest", oPC);
    Head(oPC, "before");
    AssignCommand(oPC, ActionInteractObject(oChest));
    Mark("open " + ObjectToString(oChest));
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
    int i;
    for (i = 0; i < NUM_INVENTORY_SLOTS; i++)
        if (i != INVENTORY_SLOT_CARMOUR && i != INVENTORY_SLOT_CWEAPON_L && i != INVENTORY_SLOT_CWEAPON_R
            && i != INVENTORY_SLOT_CWEAPON_B) DestroyObject(GetItemInSlot(i, oPC));
    Mark("enter " + GetName(oPC) + " in " + GetName(GetArea(oPC)));
    DelayCommand(1.0, Wear(oPC));
    DelayCommand(1.5, Spare(oPC));
    DelayCommand(2.0, Chest(oPC));
    DelayCommand(8.0, Open(oPC));
    DelayCommand(60.0, Head(oPC, "after"));
}

void main()
{
    object oPC = GetEnteringObject();
    if (!GetIsPC(oPC)) return;
    Arm(oPC, 0);
}
