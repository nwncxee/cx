using CX.Framework;

namespace CX.Services.Anim;

/// <summary>The <c>anim</c> group: the procedural pose layer's knobs, read hot by <see cref="PoseService"/>. The panel shows
/// only <see cref="Enabled"/>; every other knob is Chat-only (cx.cfg and <c>/anim</c>), and a correction reads on only
/// while both it and the master are (<see cref="Gated{T}"/>).</summary>
internal static class AnimKnobs
{
    internal static readonly KnobGroup G;
    /// <summary><c>anim.enabled</c>, the one panel row: off, every correction below reads off and the engine animates alone.</summary>
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<float> Distance;
    internal static readonly Gated<bool> FootIk;
    internal static readonly Knob<float> Max;
    internal static readonly Knob<float> Tilt;
    internal static readonly Knob<float> Lift;
    internal static readonly Gated<bool> Stride;
    internal static readonly Gated<bool> FootLock;
    internal static readonly Gated<bool> Settle;
    internal static readonly Gated<bool> Footsteps;
    internal static readonly Gated<bool> SyncAttach;
    internal static readonly Gated<bool> LookBody;
    internal static readonly Gated<bool> Diagonal;
    internal static readonly Gated<bool> Blend;
    internal static readonly Gated<bool> Crossfade;
    internal static readonly Gated<bool> Pivot;
    internal static readonly Gated<bool> Curves;
    internal static readonly Gated<bool> Follow;
    internal static readonly Gated<float> Lean;
    internal static readonly Gated<bool> Mass;
    internal static readonly Gated<bool> Drive;
    internal static readonly Gated<bool> Aim;
    internal static readonly Gated<float> Hit;

    static AnimKnobs()
    {
        G = KnobGroup.Declare("anim", "Graphics", "Motion", master: "Procedural animations",
            masterHelp: "Corrections on top of the game's own animation: feet that stand on the floor that is "
                      + "drawn, strides that match the pace, leaning, body weight and hit reactions. Off, every "
                      + "correction below reads off and characters animate exactly as the stock game plays them.",
            header: "Client-side only.", order: 350, title: "Animation");
        Enabled = G.Master!;
        Distance = G.Slider("distance", "Detail distance", 10f, 80f, 5f, 30f, "m", tier: Tier.ChatOnly,
            help: "Characters farther than this from yours keep the engine's own animation: at that range the corrections cannot be seen, and a crowded area pays for every character in it. Cloaks stay in step at any distance.");
        G.Section("Feet");
        FootIk = new(G.Toggle("footik", "Stand on uneven floors", true, tier: Tier.ChatOnly,
            help: "Characters stand on the floor that is drawn -- raised platforms, rugs, decks, stairs and sloped tiles -- instead of the flat walkmesh under it: the body is raised or lowered and each leg bends to its own foothold. Sitting, lying, knockdown and death are left alone."), false);
        Max = G.Slider("footik_max", "Largest step", 0.1f, 1.5f, 0.05f, 0.5f, "m", tier: Tier.ChatOnly,
            help: "How far above or below the walkmesh a floor may be and still count. A table the character walks past is higher than this and is ignored.");
        Tilt = G.Slider("footik_tilt", "Foot tilt", 0f, 45f, 5f, 30f, "°", tier: Tier.ChatOnly,
            help: "The steepest slope a foot turns to lie flat on, so a sole follows a ramp or a sloped tile instead of cutting into it. 0 keeps the foot as animated.");
        Lift = G.Slider("footik_lift", "Highest knee lift", 0.05f, 0.6f, 0.05f, 0.25f, "m", tier: Tier.ChatOnly,
            help: "How much higher one foot may be set than the other. A steep step past this leaves the foot in the step rather than folding the leg up to it.");
        Stride = new(G.Toggle("stride", "Match stride to speed", true, tier: Tier.ChatOnly,
            help: "The walk and run cycles play at the pace the character actually covers ground, measured from the animation's own footfalls. The engine paces them from a fixed stride length per appearance, which a custom walk cycle, haste or a slow effect does not match, so feet slide."), false);
        FootLock = new(G.Toggle("footlock", "Plant feet", true, tier: Tier.ChatOnly,
            help: "While walking or running, a foot on the ground stays where it landed until the animation lifts it, so what is left of the sliding is taken up by the leg."), false);
        Settle = new(G.Toggle("settle", "Step into and out of a stance", true, tier: Tier.ChatOnly,
            help: "Coming to a halt, a character keeps the foot it stopped on where it landed and steps the other one into its standing pose, adjusting the first after it only if it is left out of place; setting off, the cycle starts on the foot already in front, so the first stride comes from the back one. Without it both feet slide into the stance and out of it again. Needs Plant feet on."), false);
        Footsteps = new(G.Toggle("footsteps", "Footsteps on contact", true, tier: Tier.ChatOnly,
            help: "A walking or running character's footstep sounds play when each foot lands, instead of where the animation's author placed them. Off, or beyond the detail distance, the animation's own timing is used."), false);
        SyncAttach = new(G.Toggle("syncattach", "Keep cloaks in step", true, tier: Tier.ChatOnly,
            help: "Fixes a bug with cloak desynchronizing from the body in certain situations."), false);
        G.Section("Body");
        LookBody = new(G.Toggle("lookbody", "Turn the body with the head", true, tier: Tier.ChatOnly,
            help: "When a character looks at something -- a conversation partner, or your aim under the action camera -- the chest and neck share the turn instead of the head swivelling alone. Where the head points does not change."), false);
        Diagonal = new(G.Toggle("diagonal", "Turn the legs into diagonal moves", true, tier: Tier.ChatOnly,
            help: "A character moving diagonally forward plays its ordinary walk or run with the hips and legs turned toward where it is going, instead of the engine's sideways shuffle; the chest stays mostly facing forward. Moving diagonally backward, which has no animation of its own, it walks backward with the legs turned the same way. The turn eases in and out as the direction changes."), false);
        Pivot = new(G.Toggle("pivot", "Turn over a planted foot", true, tier: Tier.ChatOnly,
            help: "When a character turns on the spot, or turns round faster than it could walk the turn, the chest turns first and the hips and legs follow a moment later, over the foot on the ground, while the other foot steps round. A turn walked or run through is taken in stride by the legs. Without it the whole body turns at once. Works best with Plant feet on."), false);
        Blend = new(G.Toggle("blend", "Smooth movement changes", true, tier: Tier.ChatOnly,
            help: "When a character switches between standing, walking, running, stepping aside or walking backward, its limbs carry on with the old movement for a moment and flow into the new one, instead of snapping. Walking forward into walking backward slows the legs through the turn rather than reversing them in one frame."), false);
        Crossfade = new(G.Toggle("crossfade", "Ease animation changes", true, tier: Tier.ChatOnly,
            help: "When anything animated changes from one animation to another -- a character walking into standing, or standing into an attack -- the engine fades between the two at an even rate, so the change starts and stops with a jolt. This eases the fade in and out. Doors, placeables and creatures are included, at any distance."), false);
        Curves = new(G.Toggle("curves", "Smooth animation curves", true, tier: Tier.ChatOnly,
            help: "Every animation flows through its poses instead of changing speed at each one: the engine moves a limb in a straight line from one pose to the next, which is most of the stiffness in stock animations. A pose the animator holds or strikes still lands exactly where it was placed, and an animation whose author drew its own curves is left as it is. Affects everything animated, not only characters."), false);
        Lean = new(G.Slider("lean", "Lean", 0f, 200f, 10f, 100f, "%", tier: Tier.ChatOnly,
            help: "The upper body leans into turns, forward when speeding up, back when stopping, and into a climb. The head keeps half of it level. 0 turns it off."), 0f);
        Mass = new(G.Toggle("mass", "Body weight", true, tier: Tier.ChatOnly,
            help: "Starting, stopping and stepping down off a drop load the body: the hips dip and come back up with a little give, the knees bending so the feet stay where they are, and the chest swings a touch past upright before it settles at the end of a stop or a turn. Walking or running at a steady pace carries no load."), false);
        Drive = new(G.Toggle("drive", "Weighty keyboard movement", true, tier: Tier.ChatOnly,
            help: "Moving your character with the keyboard, it sets off at a walk's first step and picks up speed over a moment, and when you let go it slows to a halt over the ground it would have covered anyway, instead of starting and stopping at full speed. Changing between walking and running eases the same way. Only your own character, and only on your screen: the server moves it as before, and the two stay within the distance they already drift apart by."), false);
        Follow = new(G.Toggle("follow", "Arms and head follow through", true, tier: Tier.ChatOnly,
            help: "When a character turns, sets off or stops, its arms swing behind, then catch up and settle, instead of moving rigidly with the chest; the neck steadies the head, so the jolts of the chest reach it softened. Attacks, spells and other actions keep their arms and head exactly as animated."), false);
        Aim = new(G.Toggle("aim", "Aim at the target", true, tier: Tier.ChatOnly,
            help: "During an attack the upper body bends toward the target's height: a bow shot at a creature on a ledge or a swing at a giant tilts up, a strike at a rat tilts down."), false);
        Hit = new(G.Slider("hitreact", "Hit reactions", 0f, 200f, 10f, 100f, "%", tier: Tier.ChatOnly,
            help: "A landed hit knocks the upper body away from the blow, harder for more damage and for a critical hit, on top of the engine's own flinch. 0 turns it off."), 0f);
        G.Status = PoseService.Report;
    }

    /// <summary>A knob that counts only while <see cref="Enabled"/> is on: <see cref="V"/> is the knob's value then,
    /// and the off value otherwise.</summary>
    internal readonly struct Gated<T>
    {
        private readonly Knob<T> _knob;
        private readonly T _off;
        internal Gated(Knob<T> knob, T off) { _knob = knob; _off = off; }
        public T V => Enabled.V ? _knob.V : _off;
    }
}
