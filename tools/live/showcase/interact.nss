//::///////////////////////////////////////////////
//:: interact -- the Interact key's live examples (CX-747). In the swamp the script finds clear spots, nothing else within
//:: 11 m of each, and stages one prop per station 3 m ahead of the player: nothing, a body bag left by a commoner it kills,
//:: an item on the ground, the bridge door (unlocked and shut), Talias, and an area-transition trigger into the ruins. In
//:: the ruins it stands the player 1.5 m from a shut transition door, for two presses: the first opens it, the second walks
//:: through it. Every station jumps the player in at its time and logs "CXSHOW: ia <station> ready"; a press is due 6 s
//:: later, and the station logs "ia <station> PASS" or "ia-FAILED: <station> ..." from what the server sees. "nothing"
//:: logs only its shot: the red line and the sound are the client's, and no server state follows from them.
//:://////////////////////////////////////////////
#include "inc_show"

const float FIRST = 8.0;
const float STATION = 15.0;
const float PRESS = 6.0;
const float WINDOW = 8.5;
const float AHEAD = 3.0;
const float CLEAR = 11.0;
const float APART = 24.0;
const string SWAMP = "swampofthedarkra";
const string RUINS = "thedarkrangersru";

string P(vector v)
{
    return FloatToString(v.x, 0, 2) + " " + FloatToString(v.y, 0, 2) + " " + FloatToString(v.z, 0, 2);
}

vector Along(vector vAt, float fAngle, float fDist)
{
    return Vector(vAt.x + cos(fAngle) * fDist, vAt.y + sin(fAngle) * fDist, vAt.z);
}

float Flat(vector a, vector b)
{
    return VectorMagnitude(Vector(a.x - b.x, a.y - b.y, 0.0));
}

int Walkable(object oArea, vector v)
{
    int nMat = GetSurfaceMaterial(Location(oArea, v, 0.0));
    return nMat > 0 && Get2DAString("surfacemat", "Walk", nMat) == "1";
}

vector Ground(object oArea, vector v)
{
    return Vector(v.x, v.y, GetGroundHeight(Location(oArea, v, 0.0)));
}

// The straight line a->b runs over walkable faces only, sampled every 0.25 m.
int Sees(object oArea, vector a, vector b)
{
    float fLen = Flat(a, b);
    int nSteps = FloatToInt(fLen / 0.25) + 1;
    int i;
    for (i = 1; i <= nSteps; i++)
    {
        float f = IntToFloat(i) / IntToFloat(nSteps);
        if (!Walkable(oArea, Vector(a.x + (b.x - a.x) * f, a.y + (b.y - a.y) * f, a.z))) return FALSE;
    }
    return TRUE;
}

object AreaBy(string sResRef)
{
    object oArea = GetFirstArea();
    while (GetIsObjectValid(oArea))
    {
        if (GetResRef(oArea) == sResRef) return oArea;
        oArea = GetNextArea();
    }
    return OBJECT_INVALID;
}

object DoorBy(object oArea, string sTag)
{
    object o = GetFirstObjectInArea(oArea);
    while (GetIsObjectValid(o))
    {
        if (GetObjectType(o) == OBJECT_TYPE_DOOR && GetTag(o) == sTag) return o;
        o = GetNextObjectInArea(oArea);
    }
    return OBJECT_INVALID;
}

// Nothing a press could act on stands within CLEAR of v, and v is APART from every spot taken so far.
int Clear(object oPC, object oArea, vector v)
{
    location l = Location(oArea, v, 0.0);
    int nType;
    for (nType = 0; nType < 4; nType++)
    {
        int nObj = nType == 0 ? OBJECT_TYPE_CREATURE : nType == 1 ? OBJECT_TYPE_DOOR : nType == 2 ? OBJECT_TYPE_PLACEABLE : OBJECT_TYPE_ITEM;
        object o = GetNearestObjectToLocation(nObj, l);
        if (GetIsObjectValid(o) && Flat(GetPosition(o), v) < CLEAR) return FALSE;
    }
    int n = GetLocalInt(oPC, "IA_N"), i;
    for (i = 0; i < n; i++)
        if (Flat(GetPositionFromLocation(GetLocalLocation(oPC, "IA_TAKEN" + IntToString(i))), v) < APART) return FALSE;
    return TRUE;
}

void Take(object oPC, location l)
{
    int n = GetLocalInt(oPC, "IA_N");
    SetLocalLocation(oPC, "IA_TAKEN" + IntToString(n), l);
    SetLocalInt(oPC, "IA_N", n + 1);
}

// A clear walkable spot in the swamp facing a walkable, in-sight point AHEAD metres on: the station for sKey.
int FindSpot(object oPC, string sKey)
{
    object oArea = AreaBy(SWAMP);
    float fW = IntToFloat(GetAreaSize(AREA_WIDTH, oArea)) * 10.0, fH = IntToFloat(GetAreaSize(AREA_HEIGHT, oArea)) * 10.0;
    float fX, fY, fA;
    for (fX = 8.0; fX < fW - 8.0; fX += 4.0)
    for (fY = 8.0; fY < fH - 8.0; fY += 4.0)
    {
        vector v = Vector(fX, fY, 0.0);
        if (!Walkable(oArea, v) || !Clear(oPC, oArea, v)) continue;
        v = Ground(oArea, v);
        for (fA = 0.0; fA < 360.0; fA += 45.0)
        {
            vector vAhead = Along(v, fA, AHEAD), vPast = Along(v, fA, AHEAD + 1.5);
            if (!Walkable(oArea, vPast) || !Sees(oArea, v, vPast)) continue;
            location lAt = Location(oArea, v, fA);
            SetLocalLocation(oPC, "IA_" + sKey, lAt);
            SetLocalLocation(oPC, "IA_" + sKey + "_PROP", Location(oArea, Ground(oArea, vAhead), fA + 180.0));
            Take(oPC, lAt);
            ShowMark("ia " + sKey + " spot " + P(v) + " facing " + FloatToString(fA, 0, 0));
            return TRUE;
        }
    }
    ShowMark("ia-FAILED: " + sKey + " no clear spot in the swamp");
    return FALSE;
}

// The stand spot fDist in front of oDoor on whichever side is walkable and in sight of it, facing the door.
int DoorSpot(object oPC, string sKey, object oDoor, float fDist)
{
    object oArea = GetArea(oDoor);
    vector vDoor = GetPosition(oDoor);
    float fSide;
    for (fSide = 0.0; fSide < 360.0; fSide += 90.0)
    {
        float fOut = GetFacing(oDoor) + fSide;
        vector v = Along(vDoor, fOut, fDist);
        if (!Walkable(oArea, v) || !Sees(oArea, v, Along(vDoor, fOut, 0.5))) continue;
        location lAt = Location(oArea, Ground(oArea, v), fOut + 180.0);
        SetLocalLocation(oPC, "IA_" + sKey, lAt);
        Take(oPC, lAt);
        ShowMark("ia " + sKey + " spot " + P(GetPositionFromLocation(lAt)) + " door " + P(vDoor));
        return TRUE;
    }
    ShowMark("ia-FAILED: " + sKey + " no walkable side of " + GetTag(oDoor));
    return FALSE;
}

// Every creature but the player leaves the swamp and the ruins, their encounters stop and the swamp's scripted
// triggers go quiet, so the only things in reach of a station are its own props.
void Quiet(object oPC)
{
    int i;
    for (i = 0; i < 2; i++)
    {
        object oArea = AreaBy(i == 0 ? SWAMP : RUINS);
        object o = GetFirstObjectInArea(oArea);
        while (GetIsObjectValid(o))
        {
            int nType = GetObjectType(o);
            if (nType == OBJECT_TYPE_CREATURE && !GetIsPC(o)) { SetPlotFlag(o, FALSE); SetImmortal(o, FALSE); DestroyObject(o); }
            else if (nType == OBJECT_TYPE_ENCOUNTER) SetEncounterActive(FALSE, o);
            else if (nType == OBJECT_TYPE_TRIGGER)
            {
                SetEventScript(o, EVENT_SCRIPT_TRIGGER_ON_OBJECT_ENTER, "");
                SetEventScript(o, EVENT_SCRIPT_TRIGGER_ON_OBJECT_EXIT, "");
                SetEventScript(o, EVENT_SCRIPT_TRIGGER_ON_HEARTBEAT, "");
            }
            o = GetNextObjectInArea(oArea);
        }
    }
    ShowMark("ia quiet");
}

json Field(string sType, json jValue)
{
    return JsonObjectSet(JsonObjectSet(JsonObject(), "type", JsonString(sType)), "value", jValue);
}

json Point(float fX, float fY)
{
    json j = JsonObjectSet(JsonObject(), "__struct_id", JsonInt(3));
    j = JsonObjectSet(j, "PointX", Field("float", JsonFloat(fX)));
    j = JsonObjectSet(j, "PointY", Field("float", JsonFloat(fY)));
    return JsonObjectSet(j, "PointZ", Field("float", JsonFloat(0.0)));
}

// A 2 m square area-transition trigger at lAt into waypoint WP_Death_Ruins, made from one of the swamp's own
// triggers: the stock module has no transition trigger, and CreateObject makes none.
object MakeExit(location lAt)
{
    object oArea = GetAreaFromLocation(lAt), oSeed = GetNearestObjectToLocation(OBJECT_TYPE_TRIGGER, lAt);
    object oWp = GetWaypointByTag("WP_Death_Ruins");
    if (!GetIsObjectValid(oSeed) || !GetIsObjectValid(oWp)) { ShowMark("ia-FAILED: exit no seed trigger or no ruins waypoint"); return OBJECT_INVALID; }
    json j = ObjectToJson(oSeed, TRUE);
    json jGeo = JsonArray();
    jGeo = JsonArrayInsert(jGeo, Point(-1.0, -1.0));
    jGeo = JsonArrayInsert(jGeo, Point(1.0, -1.0));
    jGeo = JsonArrayInsert(jGeo, Point(1.0, 1.0));
    jGeo = JsonArrayInsert(jGeo, Point(-1.0, 1.0));
    j = JsonObjectSet(j, "Geometry", Field("list", jGeo));
    vector vAt = GetPositionFromLocation(lAt);   // JsonToObject keeps the points where XPosition puts them, the seed's own spot
    j = JsonObjectSet(j, "XPosition", Field("float", JsonFloat(vAt.x)));
    j = JsonObjectSet(j, "YPosition", Field("float", JsonFloat(vAt.y)));
    j = JsonObjectSet(j, "ZPosition", Field("float", JsonFloat(vAt.z)));
    j = JsonObjectSet(j, "Type", Field("int", JsonInt(1)));
    j = JsonObjectSet(j, "Cursor", Field("byte", JsonInt(1)));
    j = JsonObjectSet(j, "LinkedTo", Field("cexostring", JsonString("WP_Death_Ruins")));
    j = JsonObjectSet(j, "LinkedToFlags", Field("byte", JsonInt(2)));
    j = JsonObjectSet(j, "Tag", Field("cexostring", JsonString("CXIA_EXIT")));
    string sEvent;
    int i;
    for (i = 0; i < 6; i++)
    {
        sEvent = i == 0 ? "OnClick" : i == 1 ? "ScriptOnEnter" : i == 2 ? "ScriptOnExit" : i == 3 ? "ScriptHeartbeat" : i == 4 ? "OnDisarm" : "OnTrapTriggered";
        if (JsonGetType(JsonObjectGet(j, sEvent)) != JSON_TYPE_NULL) j = JsonObjectSet(j, sEvent, Field("resref", JsonString("")));
    }
    object oExit = JsonToObject(j, lAt, OBJECT_INVALID, TRUE);
    if (!GetIsObjectValid(oExit)) { ShowMark("ia-FAILED: exit JsonToObject made nothing"); return OBJECT_INVALID; }
    SetTransitionTarget(oExit, oWp);
    ShowMark("ia exit trigger " + ObjectToString(oExit) + " at " + P(GetPosition(oExit)) + " target " + GetTag(GetTransitionTarget(oExit)));
    return oExit;
}

// A commoner with a droppable sword, killed where the bag should lie, so the engine leaves its own body bag.
void Victim(object oPC)
{
    location lAt = GetLocalLocation(oPC, "IA_loot_PROP");
    object oCre = CreateObject(OBJECT_TYPE_CREATURE, "nw_commale", lAt, FALSE, "CXIA_VICTIM");
    if (!GetIsObjectValid(oCre)) { ShowMark("ia-FAILED: loot no commoner"); return; }
    object oItem = CreateItemOnObject("nw_wswls001", oCre);
    SetDroppableFlag(oItem, TRUE);
    SetPlotFlag(oCre, FALSE);
    SetLocalObject(oPC, "IA_LOOT_ITEM", oItem);
    DelayCommand(1.0, ApplyEffectToObject(DURATION_TYPE_INSTANT, EffectDeath(), oCre));
    ShowMark("ia loot victim " + ObjectToString(oCre));
}

// The placeable within 3 m of the loot spot that holds the dead commoner's sword.
object FindBag(object oPC)
{
    object oItem = GetLocalObject(oPC, "IA_LOOT_ITEM");
    object oOwner = GetItemPossessor(oItem);
    if (GetIsObjectValid(oOwner) && GetObjectType(oOwner) == OBJECT_TYPE_PLACEABLE) return oOwner;
    return OBJECT_INVALID;
}

void Stage(object oPC)
{
    object oSwamp = AreaBy(SWAMP), oRuins = AreaBy(RUINS);
    object oBridge = DoorBy(oSwamp, "BridgeDoor"), oRuinDoor = DoorBy(oRuins, "RuinInteriorS");
    if (!GetIsObjectValid(oBridge) || !GetIsObjectValid(oRuinDoor)) { ShowMark("ia-FAILED: stage no BridgeDoor or RuinInteriorS"); return; }
    SetLocked(oBridge, FALSE);
    AssignCommand(oBridge, ActionCloseDoor(oBridge));
    SetLocked(oRuinDoor, FALSE);
    AssignCommand(oRuinDoor, ActionCloseDoor(oRuinDoor));
    SetLocalObject(oPC, "IA_BRIDGE", oBridge);
    SetLocalObject(oPC, "IA_RUINDOOR", oRuinDoor);
    if (!DoorSpot(oPC, "door", oBridge, AHEAD)) return;
    if (!DoorSpot(oPC, "q13", oRuinDoor, 1.5)) return;
    ShowMark("ia stage doors " + GetTag(oBridge) + " " + GetTag(oRuinDoor) + " transition " + GetTag(GetTransitionTarget(oRuinDoor)));
}

void Find(object oPC, int i)
{
    string sKey = i == 0 ? "nothing" : i == 1 ? "loot" : i == 2 ? "pickup" : i == 3 ? "talk" : "exit";
    if (!FindSpot(oPC, sKey)) return;
    location lProp = GetLocalLocation(oPC, "IA_" + sKey + "_PROP");
    if (i == 1) Victim(oPC);
    else if (i == 2)
    {
        object oItem = CreateObject(OBJECT_TYPE_ITEM, "nw_wswls001", lProp, FALSE, "CXIA_GROUND");
        SetLocalObject(oPC, "IA_GROUND", oItem);
        ShowMark("ia pickup item " + ObjectToString(oItem) + " valid " + IntToString(GetIsObjectValid(oItem)));
    }
    else if (i == 3)
    {
        object oTalker = CreateObject(OBJECT_TYPE_CREATURE, "talias", lProp, FALSE, "CXIA_TALKER");
        SetPlotFlag(oTalker, TRUE);
        SetLocalObject(oPC, "IA_TALKER", oTalker);
        ShowMark("ia talk talker " + ObjectToString(oTalker) + " valid " + IntToString(GetIsObjectValid(oTalker))
            + " reaction " + IntToString(GetReputation(oTalker, oPC)));
    }
    else if (i == 4)
    {
        location lExit = Location(GetAreaFromLocation(lProp), Along(GetPositionFromLocation(lProp), GetFacingFromLocation(lProp) + 180.0, 0.5),
                                  0.0);
        SetLocalObject(oPC, "IA_EXIT", MakeExit(lExit));
    }
    if (i < 4) DelayCommand(0.2, Find(oPC, i + 1));
    else ShowMark("ia staged");
}

void Camera(object oPC, float fFacing)
{
    AssignCommand(oPC, SetCameraFacing(fFacing, 7.0, 55.0, CAMERA_TRANSITION_TYPE_SNAP));
}

void Arrive(object oPC, string sKey, string sTell)
{
    location lAt = GetLocalLocation(oPC, "IA_" + sKey);
    AssignCommand(oPC, ClearAllActions(TRUE));
    AssignCommand(oPC, JumpToLocation(lAt));
    DelayCommand(1.0, AssignCommand(oPC, SetFacing(GetFacingFromLocation(lAt))));
    DelayCommand(1.5, Camera(oPC, GetFacingFromLocation(lAt)));
    DelayCommand(2.0, FloatingTextStringOnCreature(sTell, oPC, FALSE));
    ShowMark("ia " + sKey + " ready at " + P(GetPositionFromLocation(lAt)));
}

// The station's outcome as the server sees it: 1 met, 0 not yet.
int Met(object oPC, string sKey)
{
    if (sKey == "loot") { object oBag = FindBag(oPC); return GetIsObjectValid(oBag) && GetIsOpen(oBag); }
    if (sKey == "pickup") return GetItemPossessor(GetLocalObject(oPC, "IA_GROUND")) == oPC;
    if (sKey == "door") return GetIsOpen(GetLocalObject(oPC, "IA_BRIDGE"));
    if (sKey == "talk") return IsInConversation(GetLocalObject(oPC, "IA_TALKER"));
    if (sKey == "exit") return GetResRef(GetArea(oPC)) == RUINS;
    if (sKey == "q13open") return GetIsOpen(GetLocalObject(oPC, "IA_RUINDOOR")) && GetResRef(GetArea(oPC)) == RUINS;
    if (sKey == "q13walk") return GetResRef(GetArea(oPC)) == SWAMP;
    return FALSE;
}

void Watch(object oPC, string sKey, float fLeft)
{
    if (Met(oPC, sKey)) { ShowMark("ia " + sKey + " PASS at " + P(GetPosition(oPC)) + " in " + GetResRef(GetArea(oPC))); return; }
    if (fLeft <= 0.0)
    {
        string sWhy = " player at " + P(GetPosition(oPC)) + " in " + GetResRef(GetArea(oPC));
        if (sKey == "loot") sWhy += " bag " + ObjectToString(FindBag(oPC));
        if (sKey == "q13walk" || sKey == "q13open") sWhy += " door open " + IntToString(GetIsOpen(GetLocalObject(oPC, "IA_RUINDOOR")));
        ShowMark("ia-FAILED: " + sKey + sWhy);
        return;
    }
    DelayCommand(0.5, Watch(oPC, sKey, fLeft - 0.5));
}

void Bag(object oPC)
{
    object oBag = FindBag(oPC);
    ShowMark("ia loot bag " + ObjectToString(oBag) + " tag " + GetTag(oBag) + " resref " + GetResRef(oBag));
}

void Done(object oPC)
{
    ShowMark("ia done in " + GetResRef(GetArea(oPC)));
}

void Show(object oPC)
{
    NuiDestroy(oPC, NuiFindWindow(oPC, "show_backdrop"));
    SetTime(12, 0, 0, 0);
    Quiet(oPC);
    Stage(oPC);
    DelayCommand(0.5, Find(oPC, 0));
    string sTell = "Press your Interact key now: ";
    float fAt = FIRST;
    DelayCommand(fAt, Arrive(oPC, "nothing", sTell + "nothing is in reach, so a red line and a sound"));
    DelayCommand(fAt - 1.0, Bag(oPC));
    fAt += STATION;
    DelayCommand(fAt, Arrive(oPC, "loot", sTell + "it opens the body bag"));
    DelayCommand(fAt + PRESS, Watch(oPC, "loot", WINDOW));
    fAt += STATION;
    DelayCommand(fAt, Arrive(oPC, "pickup", sTell + "it picks up the sword"));
    DelayCommand(fAt + PRESS, Watch(oPC, "pickup", WINDOW));
    fAt += STATION;
    DelayCommand(fAt, Arrive(oPC, "door", sTell + "it opens the door"));
    DelayCommand(fAt + PRESS, Watch(oPC, "door", WINDOW));
    fAt += STATION;
    DelayCommand(fAt, Arrive(oPC, "talk", sTell + "it starts a conversation with Talias"));
    DelayCommand(fAt + PRESS, Watch(oPC, "talk", WINDOW));
    fAt += STATION;
    DelayCommand(fAt, Arrive(oPC, "exit", sTell + "it walks you into the exit square, to the ruins"));
    DelayCommand(fAt + PRESS, Watch(oPC, "exit", WINDOW));
    fAt += STATION;
    DelayCommand(fAt, Arrive(oPC, "q13", sTell + "it opens the ruins door; press again 7 s later to walk through"));
    DelayCommand(fAt + PRESS + 2.0, Watch(oPC, "q13open", 6.0));
    DelayCommand(fAt + PRESS + 7.0, Watch(oPC, "q13walk", 16.0));
    DelayCommand(fAt + PRESS + 25.0, Done(oPC));
}
