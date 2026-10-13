//::///////////////////////////////////////////////
//:: inc_theme -- the small drawing helpers the themed showcases share
//:: A themed window is a column of widgets with a draw list behind it: the chrome (fills, frames, lines) is the draw list,
//:: the widgets (slots, buttons, dolls) sit on top and carry the ids a card is keyed by. Nothing here is a look; every
//:: colour, size and position is an argument. Colours are "#RRGGBB" or "#RRGGBBAA".
//:://////////////////////////////////////////////
#include "inc_show"

int ThHex(string s) { return FindSubString("0123456789abcdef", GetStringLowerCase(GetSubString(s, 0, 1))) * 16 + FindSubString("0123456789abcdef", GetStringLowerCase(GetSubString(s, 1, 1))); }

json ThC(string sHex)
{
    int nA = 255;
    if (GetStringLength(sHex) > 7) nA = ThHex(GetSubString(sHex, 7, 2));
    return NuiColor(ThHex(GetSubString(sHex, 1, 2)), ThHex(GetSubString(sHex, 3, 2)), ThHex(GetSubString(sHex, 5, 2)), nA);
}

json ThAdd(json jList, json jItem) { return JsonArrayInsert(jList, jItem); }

// A rectangle behind the widgets: filled, or a frame fT px thick.
json ThBox(float fX, float fY, float fW, float fH, string sC, int bFill = TRUE, float fT = 1.0)
{
    return NuiDrawListRect(JsonBool(TRUE), ThC(sC), JsonBool(bFill), JsonFloat(fT), NuiRect(fX, fY, fW, fH), NUI_DRAW_LIST_ITEM_ORDER_BEFORE);
}

json ThLine(float fX1, float fY1, float fX2, float fY2, string sC, float fT = 1.0)
{
    return NuiDrawListLine(JsonBool(TRUE), ThC(sC), JsonFloat(fT), NuiVec(fX1, fY1), NuiVec(fX2, fY2), NUI_DRAW_LIST_ITEM_ORDER_BEFORE);
}

json ThXY(json jP, float fX, float fY) { return JsonArrayInsert(JsonArrayInsert(jP, JsonFloat(fX)), JsonFloat(fY)); }

// A polygon behind the widgets from a flat x,y,x,y list (repeat the first point to close it).
json ThPoly(string sC, int bFill, float fT, json jXY)
{
    return NuiDrawListPolyLine(JsonBool(TRUE), ThC(sC), JsonBool(bFill), JsonFloat(fT), jXY, NUI_DRAW_LIST_ITEM_ORDER_BEFORE);
}

// A rectangle with its four corners cut off fCut px, behind the widgets: filled, or an outline fT thick.
json ThCut(float fX, float fY, float fW, float fH, string sC, float fCut, int bFill = TRUE, float fT = 1.0)
{
    json jP = JsonArray();
    jP = ThXY(jP, fX + fCut, fY);
    jP = ThXY(jP, fX + fW - fCut, fY);
    jP = ThXY(jP, fX + fW, fY + fCut);
    jP = ThXY(jP, fX + fW, fY + fH - fCut);
    jP = ThXY(jP, fX + fW - fCut, fY + fH);
    jP = ThXY(jP, fX + fCut, fY + fH);
    jP = ThXY(jP, fX, fY + fH - fCut);
    jP = ThXY(jP, fX, fY + fCut);
    jP = ThXY(jP, fX + fCut, fY);
    return ThPoly(sC, bFill, fT, jP);
}

// A circle or ellipse in the box, behind the widgets.
json ThRing(float fX, float fY, float fW, float fH, string sC, int bFill = FALSE, float fT = 2.0)
{
    return NuiDrawListCircle(JsonBool(TRUE), ThC(sC), JsonBool(bFill), JsonFloat(fT), NuiRect(fX, fY, fW, fH), NUI_DRAW_LIST_ITEM_ORDER_BEFORE);
}

// One line of text above the widgets.
json ThText(float fX, float fY, string sText, string sC, float fW = 300.0)
{
    return NuiDrawListText(JsonBool(TRUE), ThC(sC), NuiRect(fX, fY, fW, 18.0), JsonString(sText));
}

// A texture above the widgets, stretched into the box.
json ThPic(string sRes, float fX, float fY, float fW, float fH)
{
    return NuiDrawListImage(JsonBool(TRUE), JsonString(sRes), NuiRect(fX, fY, fW, fH), JsonInt(NUI_ASPECT_STRETCH),
        JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE));
}

// A texture behind the widgets.
json ThPicBack(string sRes, float fX, float fY, float fW, float fH, int nAlpha = 255)
{
    return NuiDrawListImage(JsonBool(TRUE), JsonString(sRes), NuiRect(fX, fY, fW, fH), JsonInt(NUI_ASPECT_STRETCH),
        JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE), NUI_DRAW_LIST_ITEM_ORDER_BEFORE);
}

// Draw list jList on element jE.
json ThDecor(json jE, json jList) { return NuiDrawList(jE, JsonBool(FALSE), jList); }

// A sized, id'd element: the thing a card is keyed by.
json ThSize(json jE, string sId, float fW, float fH)
{
    return NuiId(NuiWidth(NuiHeight(jE, fH), fW), sId);
}

// A slot: an icon in a fW x fH cell with a frame (sFill behind, sLine round it, sLine "" for none).
json ThSlot(string sId, string sIcon, float fW, float fH, string sFill, string sLine, float fT = 1.0)
{
    json jL = JsonArray();
    if (sFill != "") jL = ThAdd(jL, ThBox(0.0, 0.0, fW, fH, sFill));
    if (sLine != "") jL = ThAdd(jL, NuiDrawListRect(JsonBool(TRUE), ThC(sLine), JsonBool(FALSE), JsonFloat(fT),
        NuiRect(fT / 2.0, fT / 2.0, fW - fT, fH - fT)));
    json jI = NuiSpacer();
    if (sIcon != "") jI = NuiImage(JsonString(sIcon), JsonInt(NUI_ASPECT_FIT), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE));
    return ThDecor(ThSize(jI, sId, fW, fH), jL);
}

// A doll: the @model:sKey view in an element of its own shape (set the shape with model.<sKey>.size).
// With a colour in sFrame the doll gets a fT px frame round it.
json ThDoll(string sKey, string sId, float fW, float fH, string sFrame = "", float fT = 2.0)
{
    json jD = JsonArray();
    if (sFrame != "") jD = ThAdd(jD, ThBox(0.0, 0.0, fW, fH, sFrame, FALSE, fT));
    jD = ThAdd(jD, NuiDrawListImage(JsonBool(TRUE), JsonString("@model:" + sKey), NuiRect(0.0, 0.0, fW, fH),
        JsonInt(NUI_ASPECT_STRETCH), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE)));
    return NuiId(NuiDrawList(NuiHeight(NuiWidth(NuiSpacer(), fW), fH), JsonBool(FALSE), jD), sId);
}

json ThCol2(json jA, json jB) { return NuiCol(ThAdd(ThAdd(JsonArray(), jA), jB)); }
json ThRow2(json jA, json jB) { return NuiRow(ThAdd(ThAdd(JsonArray(), jA), jB)); }
json ThGap(float fW, float fH) { return NuiHeight(NuiWidth(NuiSpacer(), fW), fH); }

// A chromeless, transparent window at x,y of size w,h.
int ThOpen(object oPC, string sId, json jRoot, float fX, float fY, float fW, float fH)
{
    json jWin = NuiWindow(jRoot, JsonBool(FALSE), NuiRect(fX, fY, fW, fH), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE), JsonBool(FALSE));
    return ShowRegister(oPC, jWin, sId);
}

// A stock item icon for filler cells: the nth of 24, wrapping.
string ThIcon(int n)
{
    json j = JsonParse("[\"iit_ring_001\",\"iit_neck_002\",\"iit_potion_b_011\",\"iit_glove_003\",\"iit_scroll_001\",\"iit_gem_004\",\"iit_boots_m_021\",\"iit_bracer_002\","
        + "\"iit_potion_b_025\",\"iit_ring_004\",\"iit_book_003\",\"iit_cloak_005\",\"iit_gem_009\",\"iit_neck_004\",\"iit_potion_b_033\",\"iit_belt_003\","
        + "\"iit_key_001\",\"iit_torch_001\",\"iit_gem_013\",\"iit_ring_005\",\"iit_spscroll_001\",\"iit_potion_b_017\",\"iit_horn_001\",\"iit_midmisc_003\"]");
    return JsonGetString(JsonArrayGet(j, n % 24));
}

// A stock spell icon: the nth of 24, wrapping.
string ThSpell(int n)
{
    json j = JsonParse("[\"is_fireball\",\"is_magmiss\",\"is_haste\",\"is_sleep\",\"is_web\",\"is_slow\",\"is_stoneskn\",\"is_mirrimage\","
        + "\"is_fear\",\"is_acidarrow\",\"is_barkskin\",\"is_burnhands\",\"is_chlightn\",\"is_darkness\",\"is_daze\",\"is_enchant\","
        + "\"is_ethvis\",\"is_globeinv\",\"is_grease\",\"is_holdmon\",\"is_minglobe\",\"is_heal\",\"is_bless\",\"is_summon3\"]");
    return JsonGetString(JsonArrayGet(j, n % 24));
}

// A label in colour sC, w x h, text aligned by nHAlign (NUI_HALIGN_*), centred vertically.
json ThLabel(string sText, string sC, float fW, float fH, int nHAlign = NUI_HALIGN_LEFT)
{
    return NuiHeight(NuiWidth(NuiStyleForegroundColor(NuiLabel(JsonString(sText), JsonInt(nHAlign), JsonInt(NUI_VALIGN_MIDDLE)), ThC(sC)), fW), fH);
}

// A grid of nCols x nRows slots w x h with ids <sPrefix>0, <sPrefix>1 ..., filled by a pattern of icons (some blank, none
// when nBlank is 0), item icons or, with bSpells, spell icons.
json ThGrid(string sPrefix, int nCols, int nRows, float fW, float fH, string sFill, string sLine, int nBlank = 3, int bSpells = FALSE)
{
    json jCol = JsonArray();
    int r;
    for (r = 0; r < nRows; r++)
    {
        json jRow = JsonArray();
        int c;
        for (c = 0; c < nCols; c++)
        {
            int n = r * nCols + c;
            string sIcon = ThIcon(n * 5 + r);
            if (bSpells) sIcon = ThSpell(n);
            if (nBlank > 0 && (n * 7 + r) % nBlank == 0) sIcon = "";
            jRow = ThAdd(jRow, ThSlot(sPrefix + IntToString(n), sIcon, fW, fH, sFill, sLine));
        }
        jCol = ThAdd(jCol, NuiRow(jRow));
    }
    return NuiCol(jCol);
}
