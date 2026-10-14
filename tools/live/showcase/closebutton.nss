//::///////////////////////////////////////////////
//:: closebutton -- does a press where a hidden close button sits still close a background=0 window?
//:: Two closable windows of one size side by side: show_closeref draws stock, show_close has background=0 in the
//:: policy. A press at the reference's close button, shifted 380 px right, lands where the hidden one sits.
//:: Every 2 s the script logs whether show_close is still open.
//:://////////////////////////////////////////////
#include "inc_show"

const int POLL_TRIES = 30;

int OpenClosable(object oPC, string sId, float fX)
{
    json jRoot = NuiCol(JsonArrayInsert(JsonArray(), NuiLabel(JsonString(sId), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE))));
    json jWin = NuiWindow(jRoot, JsonString(sId), NuiRect(fX, 20.0, 300.0, 200.0),
                          JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE), JsonBool(FALSE), JsonBool(TRUE));
    int nToken = NuiCreate(oPC, jWin, sId);
    ShowMark("open " + sId + " token " + IntToString(nToken));
    return nToken;
}

void Poll(object oPC, int nTry)
{
    if (nTry >= POLL_TRIES) return;
    ShowMark(NuiFindWindow(oPC, "show_close") != 0 ? "show_close open" : "show_close gone");
    DelayCommand(2.0, Poll(oPC, nTry + 1));
}

void OpenBoth(object oPC)
{
    OpenClosable(oPC, "show_closeref", 20.0);
    OpenClosable(oPC, "show_close", 400.0);
    Poll(oPC, 0);
}

// A second after the backdrop: windows opened in the same frame as it draw under it.
void Show(object oPC)
{
    DelayCommand(1.0, OpenBoth(oPC));
}
