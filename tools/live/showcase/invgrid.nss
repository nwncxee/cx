//::///////////////////////////////////////////////
//:: invgrid -- a bag, two helmets, a potion and a ring on the PC, for the CX inventory's grid test
//:: (tools/live/inventory_grid_regression.sh). Standalone, as invsplit: the PC's backpack and worn items are emptied,
//:: one helmet is worn and a bag, a second helmet, a potion and a ring go in the backpack, in that order. Each tagged item's
//:: holder (the bag when it is in it, else the backpack) and worn slot are logged twice: before the keys act and once they are done.
//:://////////////////////////////////////////////

void Mark(string sStep)
{
    WriteTimestampedLogEntry("CXSHOW: " + sStep);
}

void Give(object oPC, string sResRef, string sTag)
{
    object oItem = CreateItemOnObject(sResRef, oPC, 1, sTag);
    SetIdentified(oItem, TRUE);
    Mark(GetIsObjectValid(oItem) ? "item " + sTag + " " + ObjectToString(oItem) : "item-FAILED: " + sResRef);
}

void Wear(object oPC)
{
    object oHelm = CreateItemOnObject("nw_arhe001", oPC, 1, "CXSHOW_worn");
    SetIdentified(oHelm, TRUE);
    AssignCommand(oPC, ActionEquipItem(oHelm, INVENTORY_SLOT_HEAD));
    Mark(GetIsObjectValid(oHelm) ? "item CXSHOW_worn " + ObjectToString(oHelm) : "item-FAILED: nw_arhe001");
}

int InBag(object oBag, object oItem)
{
    object oIn = GetFirstItemInInventory(oBag);
    while (GetIsObjectValid(oIn))
    {
        if (oIn == oItem) return TRUE;
        oIn = GetNextItemInInventory(oBag);
    }
    return FALSE;
}

string Where(object oPC, object oBag, object oItem)
{
    int i;
    for (i = 0; i < NUM_INVENTORY_SLOTS; i++)
        if (GetItemInSlot(i, oPC) == oItem) return "slot " + IntToString(i);
    return InBag(oBag, oItem) ? "in " + GetTag(oBag) : "backpack";
}

void Report(object oPC, string sWhen)
{
    object oBag = GetItemPossessedBy(oPC, "CXSHOW_bag");
    object oItem = GetFirstItemInInventory(oPC);
    while (GetIsObjectValid(oItem))
    {
        if (GetStringLeft(GetTag(oItem), 7) == "CXSHOW_") Mark(sWhen + " " + GetTag(oItem) + " " + Where(oPC, oBag, oItem));
        oItem = GetNextItemInInventory(oPC);
    }
    object oHead = GetItemInSlot(INVENTORY_SLOT_HEAD, oPC);
    Mark(sWhen + " head " + (GetIsObjectValid(oHead) ? GetTag(oHead) : "empty"));
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
    DelayCommand(1.5, Give(oPC, "nw_it_contain001", "CXSHOW_bag"));
    DelayCommand(2.0, Give(oPC, "nw_arhe002", "CXSHOW_helm"));
    DelayCommand(2.5, Give(oPC, "nw_it_mpotion001", "CXSHOW_potion"));
    DelayCommand(3.0, Give(oPC, "nw_it_mring001", "CXSHOW_ring"));
    DelayCommand(12.0, Report(oPC, "before"));
    DelayCommand(75.0, Report(oPC, "after"));
}

void main()
{
    object oPC = GetEnteringObject();
    if (!GetIsPC(oPC)) return;
    Arm(oPC, 0);
}
