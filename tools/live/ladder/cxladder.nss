//::///////////////////////////////////////////////
//:: cxladder -- the CX-120 verification ladder
//:: Module OnClientEnter. Runs the session through the game states the live
//:: harness cannot click its way to: a walk, a conversation, a fight and two
//:: area transitions. Every rung writes "CXLADDER: <step>" to the engine's log
//:: (nwclientLog1.txt -- +TestNewModule hosts the module in the client's own
//:: process, so there is one log and it is the client's), which is what
//:: tools/live/run-live.sh grades.
//:: Timings are the constants below; nothing else schedules anything.
//:://////////////////////////////////////////////

// Seconds after the ladder ARMS -- which is not the same moment as OnClientEnter. The module's
// OnClientEnter runs while the PC is still between areas: GetArea(oPC) is OBJECT_INVALID there, so a
// location built from it is nowhere and CreateObject/ActionMoveToLocation against it do nothing (the
// first run of this ladder logged "walked 0.0m" and a talker that would not spawn). The engine also
// runs no delayed command during the load and then fires every expired one at once, which bunched
// three rungs into the same second. So main() only starts a poll; the clock below starts at the first
// tick where the PC actually has an area. Each rung is absolute from there, not cumulative.
const float T_WALK   =  4.0;
const float T_WALKED =  8.5;   // audit: how far the walk actually got, before the talker lands
const float T_TALK   =  9.0;
const float T_FIGHT  = 16.0;
const float T_KILLED = 25.0;   // audit: how many foes the PC got through
// CX-120 particles: a point-to-point BEAM and a detonation, on the live foe, inside the combat shot
// burst. The beam is the only rung that exercises SetEmitterTarget's edge (a p2p emitter's target is the
// instance of its reference child), and the fireball is the only one that detonates an explosion emitter.
const float T_BEAM   = 16.5;
const float T_BOOM   = 18.0;
const float T_BOLT   = 19.2;
/// How long the beam is held -- long enough to span the spell shots and the armour rungs.
const float BEAM_SECONDS = 14.0;
/// The beam's victim is its OWN creature, plot and immortal: the three ladder goblins are dead within a
/// second of the fight starting, and an effect applied to a corpse draws nothing at all.
const string MARK_TAG = "CXLADDER_MARK";
/// The equipped TORCH, early and worn for the rest of the session: an item model in the PC's off hand,
/// whose flame and smoke are emitter nodes of an ATTACHED model rather than of a scene member.
const float T_TORCH  =  1.0;
// The wear rungs sit AFTER the fight's audit: the client refuses to equip or unequip armour in combat,
// so every one of them was a no-op while the goblins were alive. LadderKilled clears the field.
const float T_EQUIP  = 26.0;   // CX-120: the armour swap, which rebuilds the PC's body-part models
const float T_STRIP  = 30.0;
const float T_REWEAR = 34.0;
const float T_AREA2  = 40.0;
// A NUI window over the second area, so the window-layer shots have something to sit above and below.
const float T_NUI    = 43.0;
const float T_BACK   = 55.0;
const float T_DONE   = 58.0;

/// How often, and for how many tries, main() asks whether the PC has landed in an area yet.
const float ARM_POLL = 0.5;
const int ARM_TRIES = 120;

/// The Inn of the Lance's main floor, inside PerinTrigger1's rectangle
/// (x 20.2..25.0, y 0.2..9.8). Stock trigger ground, so it is walkable without
/// guessing at the tile walkmesh; the PC joins at (17, 4) facing +X, 5.6 m short of it.
const float WALK_X = 22.5;
const float WALK_Y = 5.0;
/// Both spawns sit further along the same corridor, in front of the walked-to PC.
const float TALKER_DX = 2.5;
const float FOE_DX = 3.5;

const string TALKER = "nw_innkeeper";      // stock blueprint, Commoner faction
const string TALKER_TAG = "CXLADDER_TALKER";
const string TALKER_DLG = "cxladder";      // the 2-node .dlg this module ships
const string FOE = "nw_goblina";           // stock blueprint, Hostile faction, 4 hp
const string FOE_TAG = "CXLADDER_FOE";
const int FOE_COUNT = 3;
const string WP_AREA2 = "WP_Death_Ruins";  // the stock respawn waypoint in The Dark Ranger's Ruins
/// Stock armour, heavy enough that wearing it swaps every body-part model the appearance is built from.
const string ARMOUR = "nw_aarcl007";
const string ARMOUR_TAG = "CXLADDER_ARMOUR";
/// A stock cloak. Its model is a gob of its OWN, attached to the creature's root rather than grafted
/// onto a rig node, so unequipping it DELETES that object and re-equipping makes a new one -- the edge
/// a recycled record slot is seen on (CX-120: the re-equipped cloak stopped drawing for good).
const string CLOAK = "x2_it_mcloak001";
const string CLOAK_TAG = "CXLADDER_CLOAK";
/// Stock torch, the one everyday item whose model carries fire and smoke emitters.
const string TORCH = "nw_it_torch001";
const string TORCH_TAG = "CXLADDER_TORCH";

/// One rung's marker. The only thing the harness greps.
void Mark(string sStep)
{
    WriteTimestampedLogEntry("CXLADDER: " + sStep);
}

/// Put the armour on, making it the first time. Equipping rebuilds the creature's body-part models, which
/// is the edge a cloak, a head and a torso stop riding the skeleton on.
void LadderWear(object oPC)
{
    object oArm = GetItemPossessedBy(oPC, ARMOUR_TAG);
    if (!GetIsObjectValid(oArm)) oArm = CreateItemOnObject(ARMOUR, oPC, 1, ARMOUR_TAG);
    if (!GetIsObjectValid(oArm)) { Mark("equip NO ITEM"); return; }
    AssignCommand(oPC, ActionEquipItem(oArm, INVENTORY_SLOT_CHEST));
    object oCloak = GetItemPossessedBy(oPC, CLOAK_TAG);
    if (!GetIsObjectValid(oCloak)) oCloak = CreateItemOnObject(CLOAK, oPC, 1, CLOAK_TAG);
    if (GetIsObjectValid(oCloak))
    {
        // A magical cloak arrives unidentified, and the client refuses to equip one.
        SetIdentified(oCloak, TRUE);
        AssignCommand(oPC, ActionEquipItem(oCloak, INVENTORY_SLOT_CLOAK));
    }
    else Mark("equip NO CLOAK");
    Mark("equip");
}

/// Say whether the cloak slot is filled right now -- the one fact the cloak rungs are about.
void LadderCloakAudit(object oPC, string sWhen)
{
    if (GetIsObjectValid(GetItemInSlot(INVENTORY_SLOT_CLOAK, oPC))) Mark("cloak " + sWhen + " ON");
    else Mark("cloak " + sWhen + " OFF");
}

/// Swap the torso body part outright, the way HotU's armour crafting does. Equipping armour is refused
/// mid-combat; this is not, and it is the exact call the user's chest-model cycling makes.
void LadderTorso(object oPC, int nModel)
{
    SetCreatureBodyPart(CREATURE_PART_TORSO, nModel, oPC);
    Mark("torso " + IntToString(nModel));
}

/// CX-120 armour parity: the chest models the game actually ships for a human body (verified against
/// the BIFs with tools/mdlcheck), paraded one per PARADE_STEP so a shot and a [db] census line land on
/// each. The parade starts AFTER the ladder's own last rung, so a default-length run never reaches it
/// and only a long soak (--seconds >= 115) pays for it.
const float T_PARADE    = 60.0;
const float PARADE_STEP =  2.0;

/// The nth chest model of the parade, or -1 past the end.
int ParadeModel(int n)
{
    switch (n)
    {
        case 0: return 1;
        case 1: return 2;
        case 2: return 3;
        case 3: return 4;
        case 4: return 6;
        case 5: return 9;
        case 6: return 10;
        case 7: return 11;
        case 8: return 14;
        case 9: return 20;
        case 10: return 21;
        case 11: return 22;
        case 12: return 23;
        case 13: return 24;
        case 14: return 25;
        case 15: return 26;
        case 16: return 28;
        case 17: return 29;
        case 18: return 30;
        case 19: return 33;
        case 20: return 37;
        case 21: return 38;
        case 22: return 40;
        case 23: return 42;
        case 24: return 43;
        case 25: return 45;
        case 26: return 46;
        case 27: return 47;
        case 28: return 48;
        case 29: return 51;
        case 30: return 52;
        case 31: return 53;
        case 32: return 63;
        case 33: return 186;
    }
    return -1;
}

/// Set one chest model and schedule the next. Marks the model number so the grader can line a shot and
/// a census line up with the part that was on the PC at that moment.
void LadderParade(object oPC, int n)
{
    int nModel = ParadeModel(n);
    if (nModel < 0) { Mark("parade end"); return; }
    // Worn armour REPLACES the creature's torso part and a cloak hangs over it, so either one makes
    // every rung of the parade a no-op -- in mode 0 as much as in DB mode. Both come off first.
    if (n == 0)
    {
        object oCloak = GetItemInSlot(INVENTORY_SLOT_CLOAK, oPC);
        object oChest = GetItemInSlot(INVENTORY_SLOT_CHEST, oPC);
        if (GetIsObjectValid(oCloak)) AssignCommand(oPC, ActionUnequipItem(oCloak));
        if (GetIsObjectValid(oChest)) AssignCommand(oPC, ActionUnequipItem(oChest));
        Mark("parade stripped");
    }
    SetCreatureBodyPart(CREATURE_PART_TORSO, nModel, oPC);
    Mark("parade " + IntToString(n) + " model " + IntToString(nModel));
    DelayCommand(PARADE_STEP, LadderParade(oPC, n + 1));
}

/// Light a torch in the off hand. Equipped items are attached models, so this is the only rung whose
/// particles hang off an attachment chain instead of a scene member.
void LadderTorch(object oPC)
{
    object oTorch = GetItemPossessedBy(oPC, TORCH_TAG);
    if (!GetIsObjectValid(oTorch)) oTorch = CreateItemOnObject(TORCH, oPC, 1, TORCH_TAG);
    if (!GetIsObjectValid(oTorch)) { Mark("torch NO ITEM"); return; }
    AssignCommand(oPC, ActionEquipItem(oTorch, INVENTORY_SLOT_LEFTHAND));
    Mark("torch");
}

/// ...and off again, so the next wear is a RE-equip.
void LadderStrip(object oPC)
{
    AssignCommand(oPC, ClearAllActions());
    object oCloak = GetItemInSlot(INVENTORY_SLOT_CLOAK, oPC);
    if (GetIsObjectValid(oCloak)) AssignCommand(oPC, ActionUnequipItem(oCloak));
    object oArm = GetItemInSlot(INVENTORY_SLOT_CHEST, oPC);
    if (!GetIsObjectValid(oArm)) { Mark("unequip NOTHING WORN"); return; }
    AssignCommand(oPC, ActionUnequipItem(oArm));
    Mark("unequip");
}


/// A point on the inn floor, at WALK_X + fDX. Facing 0.0 is +X, the direction the
/// corridor and the walk run in, so the PC arrives looking at where the spawns land
/// (and they, at 180.0, look back at it) -- which is what puts them in the screenshots.
location LadderSpot(object oArea, float fDX, float fFacing)
{
    return Location(oArea, Vector(WALK_X + fDX, WALK_Y, 0.0), fFacing);
}

void LadderWalk(object oPC, location lTo)
{
    Mark("walk");
    AssignCommand(oPC, ClearAllActions());
    AssignCommand(oPC, ActionMoveToLocation(lTo, FALSE));
}

void LadderWalked(object oPC, location lFrom)
{
    Mark("walked " + FloatToString(GetDistanceBetweenLocations(lFrom, GetLocation(oPC)), 0, 1) + "m");
}

void LadderTalk(object oPC)
{
    object oNpc = CreateObject(OBJECT_TYPE_CREATURE, TALKER,
                               LadderSpot(GetArea(oPC), TALKER_DX, 180.0), FALSE, TALKER_TAG);
    if (!GetIsObjectValid(oNpc))
    {
        Mark("talk-FAILED: no blueprint " + TALKER);
        return;
    }
    Mark("talk");
    AssignCommand(oNpc, ClearAllActions());
    AssignCommand(oNpc, ActionStartConversation(oPC, TALKER_DLG, FALSE, TRUE));
}

void LadderFight(object oPC)
{
    // NWScript has no "end conversation": losing the partner is what makes the
    // client drop the dialog UI, so the talker is destroyed rather than dismissed.
    DestroyObject(GetObjectByTag(TALKER_TAG));
    AssignCommand(oPC, ClearAllActions(TRUE));

    object oArea = GetArea(oPC);
    object oFirst = OBJECT_INVALID;
    int i;
    for (i = 0; i < FOE_COUNT; i++)
    {
        object oFoe = CreateObject(OBJECT_TYPE_CREATURE, FOE,
                                   LadderSpot(oArea, FOE_DX + IntToFloat(i), 180.0), FALSE, FOE_TAG);
        if (!GetIsObjectValid(oFoe))
            continue;
        // The blueprint is already Hostile; saying so again costs nothing and
        // makes the fight independent of the blueprint's faction field.
        ChangeToStandardFaction(oFoe, STANDARD_FACTION_HOSTILE);
        SetIsTemporaryEnemy(oPC, oFoe);
        if (!GetIsObjectValid(oFirst))
            oFirst = oFoe;
    }
    if (!GetIsObjectValid(oFirst))
    {
        Mark("combat-FAILED: no blueprint " + FOE);
        return;
    }
    Mark("combat");
    AssignCommand(oPC, ActionAttack(oFirst));
}

/// A lightning BEAM from the PC's hand to the foe: CNWCVisualEffectOnObject::AttachBeam attaches the
/// beam object to the caster and re-points its reference nodes at the victim, which is what gives the
/// model's three p2p emitters a target. Without that edge the bolts draw nothing at all.
void LadderBeam(object oPC)
{
    object oMark = CreateObject(OBJECT_TYPE_CREATURE, FOE,
                                LadderSpot(GetArea(oPC), FOE_DX + 3.0, 180.0), FALSE, MARK_TAG);
    if (!GetIsObjectValid(oMark)) { Mark("beam NO TARGET"); return; }
    ChangeToStandardFaction(oMark, STANDARD_FACTION_COMMONER);
    SetImmortal(oMark, TRUE);
    SetPlotFlag(oMark, TRUE);
    ApplyEffectToObject(DURATION_TYPE_TEMPORARY, EffectBeam(VFX_BEAM_LIGHTNING, oPC, BODY_NODE_HAND),
                        oMark, BEAM_SECONDS);
    Mark("beam");
}

/// A detonation on the foe: an explosion emitter firing, and the one thing in the game that raises a
/// point-source wind gust from the particle step.
void LadderBoom(object oPC, int nVfx, string sStep)
{
    object oMark = GetObjectByTag(MARK_TAG);
    location lAt = GetIsObjectValid(oMark) ? GetLocation(oMark) : GetLocation(oPC);
    ApplyEffectAtLocation(DURATION_TYPE_INSTANT, EffectVisualEffect(nVfx), lAt);
    Mark(sStep);
}

void LadderKilled()
{
    int nLeft = 0;
    int i;
    for (i = 0; i < FOE_COUNT; i++)
    {
        object oFoe = GetObjectByTag(FOE_TAG, i);
        if (GetIsObjectValid(oFoe) && !GetIsDead(oFoe))
            nLeft++;
    }
    Mark("combat-result " + IntToString(FOE_COUNT - nLeft) + "/" + IntToString(FOE_COUNT) + " down");
    // ...and off the field, so the wear rungs that follow are not refused for being in combat.
    for (i = 0; i < FOE_COUNT; i++) DestroyObject(GetObjectByTag(FOE_TAG, i));
}

void LadderJump(string sStep, object oPC, location lTo)
{
    Mark(sStep);
    AssignCommand(oPC, ClearAllActions(TRUE));
    AssignCommand(oPC, JumpToLocation(lTo));
}

/// A plain NUI window at a fixed spot (400, 260, 300x200), the one tools/live's layer shots are placed around.
void LadderNui(object oPC)
{
    string sWin = "{\"type\":\"window\",\"version\":1,\"title\":\"cxladder nui\","
        + "\"geometry\":{\"x\":400.0,\"y\":260.0,\"w\":300.0,\"h\":200.0},"
        + "\"resizable\":false,\"collapsed\":false,\"closable\":false,\"transparent\":false,\"border\":true,"
        + "\"root\":{\"type\":\"col\",\"children\":[{\"type\":\"label\",\"value\":\"NUI window\"}]}}";
    json jWin = JsonParse(sWin);
    Mark("nui " + IntToString(NuiCreate(oPC, jWin, "cxladder_nui")));
}

void LadderArea2(object oPC)
{
    object oWp = GetWaypointByTag(WP_AREA2);
    if (!GetIsObjectValid(oWp))
    {
        Mark("area2-FAILED: no waypoint " + WP_AREA2);
        return;
    }
    // The sky is a per-area ARE field and the stock areas this ladder borrows set none, so the DB and
    // engine sky paths were never exercised live. Asking for one here is what makes the rung's shot show a
    // dome instead of the void.
    SetSkyBox(SKYBOX_GRASS_CLEAR, GetArea(oWp));
    LadderJump("area2 " + GetName(GetArea(oWp)), oPC, GetLocation(oWp));
    // ...and look at the horizon rather than down at the floor: a top-down chase camera shows no sky at
    // all, so the rung's shot could not say whether one was drawn. NWN's pitch runs the other way from the
    // name -- 1 is overhead and 89 is level with the ground.
    DelayCommand(1.0, AssignCommand(oPC, SetCameraFacing(90.0, 12.0, 85.0, CAMERA_TRANSITION_TYPE_SNAP)));
}

void LadderDone(object oPC)
{
    Mark("done in " + GetName(GetArea(oPC)));
    // Belt to the immortality braces: a dead PC means the rungs after its death proved nothing.
    if (GetIsDead(oPC))
        Mark("done-FAILED: the PC died during the ladder");
}

/// Declared ahead of LadderArm so the poll can call itself.
void LadderArm(object oPC, int nTry);

/// Waits for the PC to actually be standing in an area, then starts the ladder's clock.
void LadderArm(object oPC, int nTry)
{
    if (!GetIsObjectValid(oPC))
        return;
    if (!GetIsObjectValid(GetArea(oPC)))
    {
        if (nTry >= ARM_TRIES)
        {
            Mark("enter-FAILED: the PC never landed in an area");
            return;
        }
        DelayCommand(ARM_POLL, LadderArm(oPC, nTry + 1));
        return;
    }

    // +TestNewModule does not take the local-vault character the harness drops: it joins with its own,
    // which three goblins chewed through by the end of the first passing run and left the last
    // screenshot on the death panel instead of the world. Immortal still takes the hits and still
    // fights -- it just cannot lose, which is what a test character is for.
    SetImmortal(oPC, TRUE);

    location lStart = GetLocation(oPC);
    location lWalk = LadderSpot(GetArea(oPC), 0.0, 0.0);
    Mark("enter " + GetName(oPC) + " in " + GetName(GetArea(oPC)));

    DelayCommand(T_TORCH,  LadderTorch(oPC));
    DelayCommand(T_WALK,   LadderWalk(oPC, lWalk));
    DelayCommand(T_WALKED, LadderWalked(oPC, lStart));
    DelayCommand(T_TALK,   LadderTalk(oPC));
    DelayCommand(T_FIGHT,  LadderFight(oPC));
    DelayCommand(T_KILLED, LadderKilled());
    DelayCommand(T_BEAM,   LadderBeam(oPC));
    DelayCommand(T_BOOM,   LadderBoom(oPC, VFX_FNF_FIREBALL, "fireball"));
    DelayCommand(T_BOLT,   LadderBoom(oPC, VFX_IMP_LIGHTNING_M, "boltimpact"));
    DelayCommand(T_EQUIP,  LadderWear(oPC));
    DelayCommand(T_EQUIP,  LadderTorso(oPC, 13));
    DelayCommand(T_STRIP,  LadderStrip(oPC));
    DelayCommand(T_STRIP,  LadderTorso(oPC, 29));
    DelayCommand(T_STRIP + 2.0, LadderCloakAudit(oPC, "stripped"));
    DelayCommand(T_REWEAR, LadderWear(oPC));
    DelayCommand(T_REWEAR, LadderTorso(oPC, 4));
    DelayCommand(T_REWEAR + 2.0, LadderCloakAudit(oPC, "reworn"));
    DelayCommand(T_AREA2,  LadderArea2(oPC));
    DelayCommand(T_NUI,    LadderNui(oPC));
    // Back to the location the PC joined at: a second transition, and a known-good spot.
    DelayCommand(T_BACK,   LadderJump("area1", oPC, lStart));
    DelayCommand(T_DONE,   LadderDone(oPC));
    DelayCommand(T_PARADE, LadderParade(oPC, 0));
}

void main()
{
    object oPC = GetEnteringObject();
    if (GetIsPC(oPC))
        LadderArm(oPC, 0);
}
