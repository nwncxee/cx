//::///////////////////////////////////////////////
//:: colortokens -- colour tokens in every kind of NUI text, for the approved examples of the colour token work
//:: One window: labels for the colour, nesting, unclosed and stray-close cases, two centred titles, a wrapped
//:: text box beside its untokened twin, a button, a check box, a combo, a tooltip, an edit field filled with
//:: tokens, and a token in the window title. Tokens come from the stock x3_inc_string helper.
//:://////////////////////////////////////////////
#include "inc_show"
#include "x3_inc_string"

const string RED = "700";
const string GREEN = "070";
const string GOLD = "760";
const string PARA = "The quick brown fox jumps over the lazy dog while the hound sleeps beside the warm hearth of the old inn.";

json Label(string sText, int nAlign)
{
    return NuiHeight(NuiLabel(JsonString(sText), JsonInt(nAlign), JsonInt(NUI_VALIGN_MIDDLE)), 22.0);
}

json Add(json jList, json jItem)
{
    return JsonArrayInsert(jList, jItem);
}

void OpenTokens(object oPC)
{
    string sRedOpen = GetStringLeft(StringToRGBString("x", RED), 6);
    json jCol = JsonArray();
    jCol = Add(jCol, Label("E1 Gold: " + StringToRGBString("1500", GOLD) + " gp", NUI_HALIGN_LEFT));
    jCol = Add(jCol, Label("E2 Press <Enter> when x < y", NUI_HALIGN_LEFT));
    jCol = Add(jCol, Label("E3 <cool> stuff", NUI_HALIGN_LEFT));
    jCol = Add(jCol, Label("E4 " + StringToRGBString("A " + StringToRGBString("B", GREEN) + " C", RED) + " D", NUI_HALIGN_LEFT));
    jCol = Add(jCol, Label("E5 " + sRedOpen + "Warning", NUI_HALIGN_LEFT));
    jCol = Add(jCol, Label("E5 Plain", NUI_HALIGN_LEFT));
    jCol = Add(jCol, Label("E6 Text</c> more", NUI_HALIGN_LEFT));
    jCol = Add(jCol, Label(StringToRGBString("Title", RED), NUI_HALIGN_CENTER));
    jCol = Add(jCol, Label("Title", NUI_HALIGN_CENTER));
    json jTexts = JsonArray();
    jTexts = Add(jTexts, NuiText(JsonString("The quick " + StringToRGBString("brown fox jumps over the lazy dog while the", RED) + " hound sleeps beside the " + StringToRGBString("warm", GOLD) + " hearth of the old inn."), TRUE, NUI_SCROLLBAR_NONE));
    jTexts = Add(jTexts, NuiText(JsonString(PARA), TRUE, NUI_SCROLLBAR_NONE));
    jCol = Add(jCol, NuiHeight(NuiRow(jTexts), 110.0));
    json jRow = JsonArray();
    jRow = Add(jRow, NuiButton(JsonString(StringToRGBString("Button", RED))));
    jRow = Add(jRow, NuiCheck(JsonString(StringToRGBString("Check", GREEN)), JsonBool(TRUE)));
    json jEntries = JsonArray();
    jEntries = Add(jEntries, NuiComboEntry(StringToRGBString("Combo", GOLD), 0));
    jEntries = Add(jEntries, NuiComboEntry("Plain entry", 1));
    jRow = Add(jRow, NuiCombo(jEntries, JsonInt(0)));
    jRow = Add(jRow, NuiTooltip(NuiButton(JsonString("Tip")), JsonString(StringToRGBString("Tooltip", RED) + " plain")));
    jCol = Add(jCol, NuiHeight(NuiRow(jRow), 36.0));
    json jPicks = JsonArray();
    jPicks = Add(jPicks, JsonString(StringToRGBString("Option", GREEN)));
    jPicks = Add(jPicks, JsonString("Plain"));
    json jRow2 = JsonArray();
    jRow2 = Add(jRow2, NuiOptions(NUI_DIRECTION_HORIZONTAL, jPicks, JsonInt(0)));
    jRow2 = Add(jRow2, NuiToggles(NUI_DIRECTION_HORIZONTAL, jPicks));
    jCol = Add(jCol, NuiHeight(NuiRow(jRow2), 36.0));
    json jCell = NuiLabel(JsonString("Row " + StringToRGBString("red", RED) + " plain"), JsonInt(NUI_HALIGN_LEFT), JsonInt(NUI_VALIGN_MIDDLE));
    json jCells = JsonArrayInsert(JsonArray(), NuiListTemplateCell(jCell, 0.0, TRUE));
    jCol = Add(jCol, NuiHeight(NuiList(jCells, JsonInt(3), 22.0), 80.0));
    jCol = Add(jCol, NuiHeight(NuiTextEdit(JsonString(""), JsonString(StringToRGBString("Hi", RED)), 64, FALSE), 36.0));
    json jWin = NuiWindow(NuiCol(jCol), JsonString("E10 " + StringToRGBString("Tokens", GOLD)), NuiRect(40.0, 20.0, 560.0, 700.0),
                          JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE), JsonBool(FALSE), JsonBool(TRUE));
    ShowRegister(oPC, jWin, "show_tokens");
}

// A second after the backdrop: windows opened in the same frame as it draw under it.
void Show(object oPC)
{
    DelayCommand(1.0, OpenTokens(oPC));
}
