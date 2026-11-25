//::///////////////////////////////////////////////
//:: stepguard -- the step guard's live examples. The script finds a straight wall near the PC and a doorway corner
//:: (Sc -> C -> T round it, Sc -> T blocked), then runs timed trials: open (an ordinary move short of the wall), corner
//:: (a click past the corner), wall (W held into the wall and let go), ram (W held diagonally into the corner post for
//:: 4 s and let go, the drive that pinned the player) and along (the same drive, then the camera turns along Sc -> C with
//:: W still held), then door0 (a walk through the nearest door, which shuts and locks in the player's face) and door1 (W held
//:: through it, the door shuts the same way, and a second later the camera turns along the door with W still held), then
//:: door2 (a walk into the doorway, the door shut on the player there, then a walk on to the far side). The server's position is
//:: logged four times a second as "CXSHOW: sg srv", for step_guard_grade.py.
//:://////////////////////////////////////////////
#include "inc_show"

// The click trials run first: a pin outlives a jump, so a drive that pins must not run before them.
const float OPEN_AT = 8.0;
const float CORNER_AT = 23.0;
const float WALL_AT = 41.0;
const float RAM_AT = 57.0;
const int RAM_TRIALS = 6;
const float ALONG_AT = 147.0;
const int ALONG_TRIALS = 4;
const float TRIAL = 15.0;
const float DOOR_AREA_AT = 207.0;
const float DOOR_AT = 227.0;
const int DOOR_TRIALS = 3;
const float SHUT_AT = 1.2;
const float SHUT_IN = 0.4;
const float TURN_AFTER_SHUT = 1.0;
const float DONE_AT = 272.0;

string P(vector v)
{
    return FloatToString(v.x, 0, 2) + " " + FloatToString(v.y, 0, 2) + " " + FloatToString(v.z, 0, 2);
}

vector Along(vector vAt, float fAngle, float fDist)
{
    return Vector(vAt.x + cos(fAngle) * fDist, vAt.y + sin(fAngle) * fDist, vAt.z);
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
    float fLen = VectorMagnitude(Vector(b.x - a.x, b.y - a.y, 0.0));
    int nSteps = FloatToInt(fLen / 0.25) + 1;
    int i;
    for (i = 1; i <= nSteps; i++)
    {
        float f = IntToFloat(i) / IntToFloat(nSteps);
        if (!Walkable(oArea, Vector(a.x + (b.x - a.x) * f, a.y + (b.y - a.y) * f, a.z))) return FALSE;
    }
    return TRUE;
}

// Every point of a->b, sampled every 0.25 m, is off the walkmesh.
int Solid(object oArea, vector a, vector b)
{
    float fLen = VectorMagnitude(Vector(b.x - a.x, b.y - a.y, 0.0));
    int nSteps = FloatToInt(fLen / 0.25) + 1;
    int i;
    for (i = 0; i <= nSteps; i++)
    {
        float f = IntToFloat(i) / IntToFloat(nSteps);
        if (Walkable(oArea, Vector(a.x + (b.x - a.x) * f, a.y + (b.y - a.y) * f, a.z))) return FALSE;
    }
    return TRUE;
}

// A straight wall: from S the ground runs L metres along D (1.5 to 4 m) and stops; the metre past it is solid; the
// line 0.25 m short of the wall stays walkable for 4 m along A, and the line 0.5 m past it stays solid.
int FindWall(object oPC)
{
    object oArea = GetArea(oPC);
    vector vPC = GetPosition(oPC);
    float fR;
    for (fR = 0.0; fR <= 12.0; fR += 2.0)
    {
        float fA;
        for (fA = 0.0; fA < 360.0; fA += 45.0)
        {
            vector vS = Along(vPC, fA, fR);
            if (!Walkable(oArea, vS)) continue;
            vS = Ground(oArea, vS);
            float fD;
            for (fD = 0.0; fD < 360.0; fD += 15.0)
            {
                float fL = 0.0;
                int k;
                for (k = 1; k <= 17 && Walkable(oArea, Along(vS, fD, 0.25 * IntToFloat(k))); k++) fL = 0.25 * IntToFloat(k);
                if (fL < 1.5 || fL > 4.0) continue;
                if (!Solid(oArea, Along(vS, fD, fL + 0.5), Along(vS, fD, fL + 1.25))) continue;
                float fTurn;
                for (fTurn = -90.0; fTurn <= 90.0; fTurn += 180.0)
                {
                    float fSide = fD + fTurn;
                    vector vNear = Along(vS, fD, fL - 0.25), vFar = Along(vS, fD, fL + 0.5);
                    if (!Sees(oArea, vNear, Along(vNear, fSide, 4.0))) continue;
                    if (!Solid(oArea, vFar, Along(vFar, fSide, 4.0))) continue;
                    SetLocalLocation(oPC, "SG_S", Location(oArea, vS, fD));
                    SetLocalLocation(oPC, "SG_O", Location(oArea, Ground(oArea, Along(vS, fD, fL - 1.0)), fD));
                    ShowMark("sg wall S " + P(vS) + " D " + FloatToString(fD, 0, 1) + " L " + FloatToString(fL, 0, 2)
                        + " A " + FloatToString(fSide, 0, 1));
                    return TRUE;
                }
            }
        }
    }
    return FALSE;
}

// A corner: Sc walkable, C 4 m out with Sc->C clear, T 4 m to the side of C with C->T clear, and Sc->T blocked.
int FindCorner(object oPC)
{
    object oArea = GetArea(oPC);
    vector vPC = GetPosition(oPC);
    float fR;
    for (fR = 2.0; fR <= 20.0; fR += 2.0)
    {
        float fA;
        for (fA = 0.0; fA < 360.0; fA += 30.0)
        {
            vector vS = Along(vPC, fA, fR);
            if (!Walkable(oArea, vS)) continue;
            vS = Ground(oArea, vS);
            float fD;
            for (fD = 0.0; fD < 360.0; fD += 45.0)
            {
                vector vC = Along(vS, fD, 4.0);
                if (!Walkable(oArea, vC) || !Sees(oArea, vS, vC)) continue;
                vC = Ground(oArea, vC);
                float fTurn;
                for (fTurn = -90.0; fTurn <= 90.0; fTurn += 180.0)
                {
                    vector vT = Along(vC, fD + fTurn, 4.0);
                    if (!Walkable(oArea, vT)) continue;
                    vT = Ground(oArea, vT);
                    if (!Sees(oArea, vC, vT) || Sees(oArea, vS, vT)) continue;
                    float fFace = VectorToAngle(Vector(vT.x - vS.x, vT.y - vS.y, 0.0));
                    SetLocalLocation(oPC, "SG_SC", Location(oArea, vS, fFace));
                    SetLocalFloat(oPC, "SG_SCD", fD);
                    ShowMark("sg corner S " + P(vS) + " C " + P(vC) + " T " + P(vT));
                    return TRUE;
                }
            }
        }
    }
    return FALSE;
}

// A door between two rooms, in any area: walkable ground 1.5 to 3 m out on both sides and no transition. S is on
// one side and T on the other, facing through the door.
int DoorFits(object oPC, object oDoor)
{
    if (GetIsObjectValid(GetTransitionTarget(oDoor))) return FALSE;
    object oArea = GetArea(oDoor);
    vector vDoor = GetPosition(oDoor);
    float fF = GetFacing(oDoor);
    float fSide, fOut;
    for (fSide = 0.0; fSide < 360.0; fSide += 180.0)
    for (fOut = 3.0; fOut >= 1.5; fOut -= 0.5)
    {
        vector vS = Along(vDoor, fF + fSide, fOut), vT = Along(vDoor, fF + fSide + 180.0, fOut);
        if (!Walkable(oArea, vS) || !Walkable(oArea, vT)) continue;
        vS = Ground(oArea, vS);
        vT = Ground(oArea, vT);
        float fTo = VectorToAngle(Vector(vDoor.x - vS.x, vDoor.y - vS.y, 0.0));
        SetLocalObject(oPC, "SG_DOOR", oDoor);
        SetLocalLocation(oPC, "SG_DS", Location(oArea, vS, fTo));
        SetLocalLocation(oPC, "SG_DT", Location(oArea, vT, fTo));
        SetLocalLocation(oPC, "SG_DIN", Location(oArea, Ground(oArea, vDoor), fTo));
        ShowMark("sg door at " + P(vDoor) + " S " + P(vS) + " D " + FloatToString(fTo, 0, 1) + " in " + GetTag(oArea));
        return TRUE;
    }
    return FALSE;
}

int FindDoor(object oPC)
{
    object oArea = GetFirstArea();
    while (GetIsObjectValid(oArea))
    {
        object o = GetFirstObjectInArea(oArea);
        while (GetIsObjectValid(o))
        {
            if (GetObjectType(o) == OBJECT_TYPE_DOOR && DoorFits(oPC, o)) return TRUE;
            o = GetNextObjectInArea(oArea);
        }
        oArea = GetNextArea();
    }
    return FALSE;
}

void Open(object oPC)
{
    object oDoor = GetLocalObject(oPC, "SG_DOOR");
    SetLocked(oDoor, FALSE);
    AssignCommand(oDoor, ActionOpenDoor(oDoor));
}

void Camera(object oPC, float fFacing, string sWhy);

// Shuts and locks the door once the player comes within fWithin of it in the plane, polling for up to 6 s.
void Shut(object oPC, string sTrial, float fWithin, int nTry)
{
    object oDoor = GetLocalObject(oPC, "SG_DOOR");
    vector vP = GetPosition(oPC), vD = GetPosition(oDoor);
    if (VectorMagnitude(Vector(vP.x - vD.x, vP.y - vD.y, 0.0)) > fWithin && nTry < 60)
    {
        DelayCommand(0.1, Shut(oPC, sTrial, fWithin, nTry + 1));
        return;
    }
    AssignCommand(oDoor, ActionCloseDoor(oDoor));
    SetLocked(oDoor, TRUE);
    ShowMark("sg " + sTrial + " shut " + P(GetPosition(oPC)));
    if (sTrial == "door2")
        DelayCommand(0.5, AssignCommand(oPC, ActionMoveToLocation(GetLocalLocation(oPC, "SG_DT"))));
    if (sTrial == "door1")
    {
        float fAlong = GetFacingFromLocation(GetLocalLocation(oPC, "SG_DS")) + 90.0;
        DelayCommand(TURN_AFTER_SHUT, Camera(oPC, fAlong, "door1 turn"));
    }
}

void Track(object oPC)
{
    if (!GetIsObjectValid(oPC) || GetLocalInt(oPC, "SG_DONE")) return;
    ShowMark("sg srv " + P(GetPosition(oPC)));
    DelayCommand(0.25, Track(oPC));
}

void Camera(object oPC, float fFacing, string sWhy)
{
    AssignCommand(oPC, SetCameraFacing(fFacing, 8.0, 60.0, CAMERA_TRANSITION_TYPE_SNAP));
    ShowMark("sg " + sWhy + " camera " + FloatToString(fFacing, 0, 1));
}

void Jump(object oPC, string sTrial, location lAt)
{
    AssignCommand(oPC, ClearAllActions(TRUE));
    AssignCommand(oPC, JumpToLocation(lAt));
    ShowMark("sg " + sTrial + " jump " + P(GetPositionFromLocation(lAt)));
    DelayCommand(1.5, Camera(oPC, GetFacingFromLocation(lAt), sTrial));
}

// The click trial's framing: looking down Sc -> T from far and low, so a click high on the screen lands past the corner.
void Frame(object oPC, float fFacing, string sWhy)
{
    AssignCommand(oPC, SetCameraFacing(fFacing, 18.0, 1.0, CAMERA_TRANSITION_TYPE_SNAP));
    ShowMark("sg " + sWhy + " framed " + FloatToString(fFacing, 0, 1));
}

void Move(object oPC, string sTrial, location lTo)
{
    AssignCommand(oPC, ActionMoveToLocation(lTo, TRUE));
    ShowMark("sg " + sTrial + " move " + P(GetPositionFromLocation(lTo)));
}

void Done(object oPC)
{
    SetLocalInt(oPC, "SG_DONE", TRUE);
    ShowMark("sg done");
}

void Show(object oPC)
{
    NuiDestroy(oPC, NuiFindWindow(oPC, "show_backdrop"));
    SetTime(12, 0, 0, 0);
    if (!FindWall(oPC)) { ShowMark("sg-FAILED: no straight wall found near the PC"); return; }
    if (!FindCorner(oPC)) { ShowMark("sg-FAILED: no corner found near the PC"); return; }
    if (!FindDoor(oPC)) { ShowMark("sg-FAILED: no door between two walkable rooms in any area"); return; }
    location lS = GetLocalLocation(oPC, "SG_S");
    location lSc = GetLocalLocation(oPC, "SG_SC");
    DelayCommand(WALL_AT, Jump(oPC, "wall", lS));
    DelayCommand(CORNER_AT, Jump(oPC, "corner", lSc));
    DelayCommand(CORNER_AT + 1.6, Frame(oPC, GetFacingFromLocation(lSc), "corner"));
    DelayCommand(OPEN_AT, Jump(oPC, "open", lS));
    DelayCommand(OPEN_AT + 3.0, Move(oPC, "open", GetLocalLocation(oPC, "SG_O")));
    int i;
    for (i = 0; i < RAM_TRIALS; i++)
        DelayCommand(RAM_AT + TRIAL * IntToFloat(i), Jump(oPC, "ram" + IntToString(i), lSc));
    for (i = 0; i < ALONG_TRIALS; i++)
    {
        float fAt = ALONG_AT + TRIAL * IntToFloat(i);
        DelayCommand(fAt, Jump(oPC, "along" + IntToString(i), lSc));
        DelayCommand(fAt + 7.0, Camera(oPC, GetLocalFloat(oPC, "SG_SCD"), "along" + IntToString(i) + " turn"));
    }
    DelayCommand(DOOR_AREA_AT, Jump(oPC, "doorarea", GetLocalLocation(oPC, "SG_DS")));
    for (i = 0; i < DOOR_TRIALS; i++)
    {
        float fAt = DOOR_AT + TRIAL * IntToFloat(i);
        string sTrial = "door" + IntToString(i);
        DelayCommand(fAt - 1.0, Open(oPC));
        DelayCommand(fAt, Jump(oPC, sTrial, GetLocalLocation(oPC, "SG_DS")));
        DelayCommand(fAt + 2.0, Shut(oPC, sTrial, i == 2 ? SHUT_IN : SHUT_AT, 0));
        if (i == 0) DelayCommand(fAt + 3.0, AssignCommand(oPC, ActionMoveToLocation(GetLocalLocation(oPC, "SG_DT"))));
        if (i == 2) DelayCommand(fAt + 3.0, AssignCommand(oPC, ActionMoveToLocation(GetLocalLocation(oPC, "SG_DIN"))));
    }
    DelayCommand(DONE_AT, Done(oPC));
    DelayCommand(OPEN_AT - 1.0, Track(oPC));
}
