"""Builds the KayKit-style chickens (🐔 🐓 🐣 🐤 🐥): Chicken_Female (a brown hen) and Chicken_Male (a
rooster: taller, with a big comb and wattles, golden neck hackles, a dark breast and green sickle
tail feathers). Each gets its model, palette texture, rig and animations, using the shared animal
kit (assets/AnimalKit/animal_kit.py; see it for how to run this and for the rig conventions).

Bird legs bend backwards at the heel (knee_back), and each foot has a bone the kit keeps level so
the toes stay flat on the ground."""

import importlib
import math
import os
import sys

from mathutils import Vector

KIT_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "AnimalKit")
if KIT_DIR not in sys.path:
    sys.path.insert(0, KIT_DIR)
import animal_kit as kit  # noqa: E402

importlib.reload(kit)  # pick up kit edits when re-run in the same Blender session

if "OUT_DIR" not in globals():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    OUT_DIR = argv[0] if argv else os.path.dirname(os.path.abspath(__file__))

PALETTE = {
    "brown": ((196, 112, 62), (150, 74, 40)),
    "dark": ((96, 54, 36), (60, 32, 24)),
    "gold": ((255, 172, 56), (224, 118, 36)),
    "red_back": ((196, 62, 40), (150, 40, 30)),
    "green": ((44, 72, 66), (22, 38, 40)),
    "comb": ((236, 54, 54), (190, 30, 40)),
    "beak": ((252, 204, 88), (224, 160, 50)),
    "leg": ((252, 196, 78), (220, 150, 50)),
    "eye": ((36, 32, 38), (16, 14, 18)),
    "mouth": ((150, 60, 60), (100, 36, 40)),
}

wave, ramp, envelope, bump, smooth = kit.wave, kit.ramp, kit.envelope, kit.bump, kit.smooth

# --------------------------------------------------------------------------------------
# Proportions
# --------------------------------------------------------------------------------------

HIP_Z, KNEE_Z, FOOT_Z = 0.17, 0.095, 0.012
BODY_PIVOT_Z = 0.22
LEGS = {"leg.l": (0.05, 0.02), "leg.r": (-0.05, 0.02)}
SHADE = (0.0, 0.5)
MALE_SCALE = 1.18


def build(sex):
    male = sex == "Male"

    def coat(p):
        if male:
            if kit.inside(p, (0, 0.22, 0.32), (0.15, 0.12, 0.16)):
                return "green"  # tail end
            if kit.inside(p, (0, -0.11, 0.39), (0.14, 0.12, 0.13)):
                return "gold"  # neck hackles
            if kit.inside(p, (0, -0.09, 0.19), (0.17, 0.12, 0.12)):
                return "green"  # breast
            return "red_back"
        return "dark" if kit.inside(p, (0, 0.24, 0.36), (0.14, 0.12, 0.14)) else "brown"

    def wing_colour(p):
        if male:
            return "green" if p.z < 0.255 - 0.35 * (p.y - 0.03) else "red_back"
        return "dark" if kit.inside(p, (0, 0.2, 0.26), (0.1, 0.1, 0.12)) else "brown"

    hen = kit.Animal(f"Chicken_{sex}", OUT_DIR, PALETTE)

    # --- body: a plump egg, tipped up at the tail ------------------------------------------
    hen.part("body", coat, kit.blob((0, 0.01, 0.27), (0.13, 0.17, 0.13), subdiv=4, rot=(-12, 0, 0)))
    hen.part("body", coat, kit.blob((0, -0.07, 0.25), (0.11, 0.1, 0.11), subdiv=3))  # full breast

    # --- neck and head --------------------------------------------------------------------
    neck = kit.blob((0, -0.1, 0.34), (0.075, 0.07, 0.1) if not male else (0.085, 0.08, 0.11), subdiv=3)
    hen.part("neck", coat, neck)

    def head_shape():
        return kit.blob((0, -0.13, 0.44), (0.062, 0.07, 0.065), subdiv=3)

    head_target = kit.surface(head_shape())
    hen.part("head", coat, head_shape())
    # Beak: upper half on the head, lower half on the jaw; closed they make one pointed cone.
    upper, lower = kit.split(kit.tapered((0, -0.175, 0.43), (0, -0.245, 0.416), 0.027, 0.003, segments=12), 0.424)
    hen.part("head", "beak", upper)
    hen.part("jaw", "beak", lower)
    for side in (1, -1):
        # Cast straight across so the eye stays above the beak, rather than
        # drifting back onto the cheek as the ray travels towards the head.
        eye = kit.hit(head_target, (0.3 * side, -0.16, 0.465), (-side, 0, 0))
        assert eye is not None, "Chicken eye ray missed the head"
        hen.part("eyes", "eye", kit.blob(eye, (0.006, 0.010, 0.012)))
    # Comb: a row of red lobes along the top of the head (tall on the rooster); wattles below.
    lobes = 5 if male else 3
    for i in range(lobes):
        f = i / (lobes - 1)
        height = (0.05 if male else 0.025) * (1 - 0.5 * abs(f - 0.4))
        y = -0.17 + 0.09 * f if male else -0.16 + 0.06 * f
        hen.part("head", "comb", kit.blob((0, y, 0.5 + height * 0.5), (0.012, 0.018, height * 0.6 + 0.01)))
    wattle = (0.016, 0.014, 0.03) if male else (0.011, 0.01, 0.016)
    for side in (1, -1):
        hen.part("jaw", "comb", kit.blob((0.012 * side, -0.174, 0.397 - wattle[2] * 0.4), wattle))

    # Folded coverts and broad overlapping flight feathers. The feather vanes
    # make a readable fan when the wing bone lifts, not a rotating round paddle.
    for side, bone in ((1, "wing.l"), (-1, "wing.r")):
        hen.part(bone, wing_colour, kit.blob((0.12 * side, 0.00, 0.29), (0.035, 0.09, 0.07), subdiv=2,
                                             rot=(-14, 0, 6 * side)))
        for j in range(5):
            root = Vector((0.125 * side, -0.025 + j * 0.026, 0.30))
            end = root + Vector((0.018 * side, 0.075 + 0.012 * j, -0.115 + 0.008 * j))
            mid = root.lerp(end, 0.55)
            hen.part(bone, wing_colour, kit.sweep([root, mid, end],
                     [0.022, 0.031, 0.003], [0.009, 0.009, 0.003], width_axis=None))

    # --- tail -----------------------------------------------------------------------------
    if male:
        # Sickle feathers: broad, flattened plumes arching up and back, then drooping, in three sizes.
        for spread, reach, rise, width in ((0.0, 0.3, 0.2, 0.052), (0.045, 0.24, 0.15, 0.046),
                                           (-0.045, 0.2, 0.12, 0.041)):
            steps = 8
            points = [Vector((spread * k / steps, 0.13 + reach * k / steps,
                              0.33 + rise * math.sin(math.pi * 0.95 * k / steps))) for k in range(steps + 1)]
            widths = [width * (0.55 + 0.45 * math.sin(math.pi * k / steps))
                      * (1 - 0.95 * (k / steps) ** 3) for k in range(steps + 1)]
            hen.part("tail", "green", kit.sweep(points, widths,
                     [0.009 * (1 - 0.7 * k / steps) for k in range(steps + 1)], width_axis=None))
        hen.part("tail", "green", kit.blob((0, 0.17, 0.37), (0.06, 0.04, 0.1), rot=(-20, 0, 0)))
    else:
        # A short upright fan of tail feathers.
        for side in (-1, 0, 1):
            hen.part("tail", coat, kit.blob((0.03 * side, 0.18, 0.38), (0.025, 0.05, 0.1), subdiv=3,
                                            rot=(-25, 0, -12 * side)))

    # --- legs: feathered drumsticks, bare yellow shanks, three toes forward and one back -----
    for leg, (x, y) in LEGS.items():
        hen.part(leg, coat, kit.blob((x, y, HIP_Z + 0.02), (0.05, 0.06, 0.06), subdiv=3))
        hen.part(leg, "leg", kit.capsule((x, y, KNEE_Z), (x, y, HIP_Z - 0.02), 0.016, 0.018, 10, 3))
        hen.part(leg + ".lower", "leg", kit.capsule((x, y, FOOT_Z + 0.004), (x, y, KNEE_Z), 0.007, 0.014, 10, 3))
        side = 1 if x > 0 else -1
        for spread in (-28, 0, 28):
            a = math.radians(spread)
            tip = Vector((x + 0.055 * math.sin(a) * side, y - 0.055 * math.cos(a), 0.008))
            hen.part(leg + ".foot", "leg", kit.capsule((x, y, 0.009), tip, 0.009, 0.006, 8, 2))
        hen.part(leg + ".foot", "leg", kit.capsule((x, y, 0.009), (x, y + 0.03, 0.008), 0.008, 0.006, 8, 2))

    bones = [
        ("root", (0, 0, 0), None),
        ("body", (0, 0, BODY_PIVOT_Z), "root"),
        ("neck", (0, -0.09, 0.3), "body"),
        ("head", (0, -0.12, 0.41), "neck"),
        ("jaw", (0, -0.18, 0.424), "head"),
        ("eyes", (0, -0.15, 0.455), "head"),
        ("wing.l", (0.1, -0.05, 0.31), "body"),
        ("wing.r", (-0.1, -0.05, 0.31), "body"),
        ("tail", (0, 0.13, 0.32), "body"),
        *[(leg, (x, y, HIP_Z), "body") for leg, (x, y) in LEGS.items()],
        *[(leg + ".lower", (x, y, KNEE_Z), leg) for leg, (x, y) in LEGS.items()],
        *[(leg + ".foot", (x, y, FOOT_Z), leg + ".lower") for leg, (x, y) in LEGS.items()],
    ]
    hen.build(bones, LEGS, HIP_Z, KNEE_Z, BODY_PIVOT_Z, foot_tip=0.005, leg_radius=0.03, tail=("tail",),
              shade_range=SHADE, knee_back=True)
    if male:
        hen.rig.scale = (MALE_SCALE,) * 3

    # --- animations -----------------------------------------------------------------------

    def wings(p, out=0.0, flap=0.0):
        """out lifts the wings away from the body (degrees); flap adds to it."""
        p.add("wing.l", rot=(0, 0, out + flap))
        p.add("wing.r", rot=(0, 0, -(out + flap)))
        return p

    def peck(t, at, width=0.06):
        """0..1 for a quick jab down centred at time `at`."""
        return bump(t, at, width) ** 0.6

    def sitting(p, w):
        """Settled down on the ground (brooding): legs folded flat underneath."""
        for leg in LEGS:
            p.fold(leg, 80, -165, 0, w)
        return p

    @hen.animation("Idle", 90)
    def idle(t):
        # Chickens hold their head still and then jerk it to a new angle.
        turn = 18 * (ramp(t, 0.2, 0.23) - ramp(t, 0.55, 0.58)) - 14 * (ramp(t, 0.7, 0.73) - ramp(t, 0.9, 0.93))
        p = hen.pose()
        p.add("head", rot=(0, turn, 4 * (ramp(t, 0.2, 0.23) - ramp(t, 0.55, 0.58))))
        p.add("tail", rot=(3 * wave(t, 2), 0, 0))
        return p.blink(bump(t, 0.4, 0.03) + bump(t, 0.8, 0.03))

    @hen.animation("Idle_LookAround", 120)
    def idle_look_around(t):
        turn = 0.0
        for at, angle in ((0.1, 30), (0.3, -10), (0.45, -35), (0.65, 10), (0.85, 0)):
            turn += (angle - turn) * ramp(t, at, at + 0.03)
        p = hen.pose()
        p.add("neck", rot=(-8 * envelope(t, 0.05, 0.1, 0.85, 0.9), turn * 0.3, 0))
        p.add("head", rot=(0, turn * 0.7, 0))
        return p.blink(bump(t, 0.25, 0.02) + bump(t, 0.7, 0.02))

    @hen.animation("Walking", 30)
    def walking(t):
        p = kit.gait(hen.pose(), t, kit.BIPED, stance=0.6, stride=0.12, lift=0.05)
        p.add("body", rot=(0, 0, 5 * wave(t)))
        # The head bob: the head holds still in space while the body walks under it, then thrusts
        # forward to catch up, once per step.
        q = (t * 2) % 1.0
        thrust = smooth(q / 0.3) if q < 0.3 else 1 - (q - 0.3) / 0.7
        p.add("neck", loc=(0, 0, 0.035 * thrust - 0.015))
        p.add("tail", rot=(4 * wave(t, 2), 0, 0))
        return p

    @hen.animation("Running", 16)
    def running(t):
        p = kit.gait(hen.pose(), t, kit.BIPED, stance=0.45, stride=0.2, lift=0.07)
        p.add("body", rot=(14, 0, 6 * wave(t)), loc=(0, -0.01, 0))
        p.add("neck", rot=(-12, 0, 0), loc=(0, 0, 0.02))
        p.add("head", rot=(-4, 0, 0))
        return wings(p, out=25, flap=15 * wave(t, 2))

    @hen.animation("Eating_Grass", 60)
    def eating_grass(t):
        # Pecking at the ground: two quick jabs, a look, another jab.
        jab = max(peck(t, 0.15), peck(t, 0.35), peck(t, 0.75))
        p = hen.pose()
        # Beak pointing down at the ground, the neck reaching forward and down to it.
        p.add("body", rot=(14 + 18 * jab, 0, 0))
        p.add("neck", rot=(22 + 26 * jab, 0, 0), loc=(0, -0.04 - 0.025 * jab, 0.05 + 0.03 * jab))
        p.add("head", rot=(12 + 10 * jab, 0, 0))
        p.add("jaw", rot=(12 * bump(t, 0.2, 0.04) + 12 * bump(t, 0.8, 0.04), 0, 0))
        return p.add("tail", rot=(-6 * jab, 0, 0))

    @hen.animation("Crow" if male else "Cluck", 70 if male else 40)
    def call(t):
        if male:
            # Chest out, neck stretched up and back, beak wide for a long cock-a-doodle-doo.
            w = envelope(t, 0.05, 0.25, 0.75, 0.95)
            p = hen.pose()
            p.add("body", rot=(-14 * w, 0, 0))
            p.add("neck", rot=(-22 * w, 0, 0), loc=(0, 0.015 * w, 0))
            p.add("head", rot=(-26 * w, 0, 0))
            p.add("jaw", rot=(30 * envelope(t, 0.25, 0.32, 0.7, 0.78) * (0.85 + 0.15 * wave(t, 6)), 0, 0))
            wings(p, out=10 * w, flap=8 * bump(t, 0.2, 0.06))
            return p.blink(0.5 * w)
        # Short clucks: little beak opens with a head dip each.
        p = hen.pose()
        for at in (0.15, 0.4, 0.6):
            k = bump(t, at, 0.07)
            p.add("jaw", rot=(16 * k, 0, 0))
            p.add("head", rot=(-6 * k, 0, 0))
        return p.add("neck", rot=(4 * wave(t, 3), 0, 0))

    @hen.animation("Flap_Wings", 40)
    def flap_wings(t):
        w = envelope(t, 0.05, 0.15, 0.8, 0.95)
        p = hen.pose()
        p.add("body", rot=(-10 * w, 0, 0))
        p.add("neck", rot=(-8 * w, 0, 0))
        return wings(p, out=40 * w, flap=40 * w * wave(t, 5))

    @hen.animation("Being_Fed", 60)
    def being_fed(t):
        # Pecking up at food held out in front.
        jab = max(peck(t, 0.25, 0.07), peck(t, 0.5, 0.07), peck(t, 0.75, 0.07))
        reach = envelope(t, 0.0, 0.12, 0.88, 1.0)
        p = hen.pose()
        p.add("neck", rot=(-10 * reach + 18 * jab, 0, 0), loc=(0, 0, 0.02 * reach + 0.02 * jab))
        p.add("head", rot=(6 * jab, 0, 0))
        p.add("jaw", rot=(14 * jab, 0, 0))
        return p.add("tail", rot=(6 * wave(t, 3), 0, 0))

    @hen.animation("Getting_Love", 90)
    def getting_love(t):
        w = envelope(t, 0.05, 0.2, 0.8, 0.95)
        p = hen.pose()
        p.add("body", scale=(1 + 0.08 * w, 1 + 0.04 * w, 1 + 0.08 * w))  # feathers fluffed up
        p.add("neck", rot=(10 * w, 0, 0))
        p.add("head", rot=(6 * w, 0, 12 * w))
        wings(p, out=6 * w)
        return p.blink(0.95 * w)

    @hen.animation("Lie_Down", 40)
    def lie_down(t):
        w = ramp(t, 0.0, 1.0)
        p = sitting(hen.pose(), w)
        wings(p, out=12 * bump(t, 0.7, 0.2))  # a ruffle as it settles
        return p.add("body", scale=(1 + 0.05 * w, 1, 1))

    @hen.animation("Lie_Idle", 90)
    def lie_idle(t):
        p = sitting(hen.pose(), 1)
        p.add("body", scale=(1.05, 1, 1))
        p.add("head", rot=(0, 20 * (ramp(t, 0.3, 0.33) - ramp(t, 0.7, 0.73)), 0))
        return p.blink(bump(t, 0.5, 0.03))

    @hen.animation("Lie_StandUp", 30)
    def lie_stand_up(t):
        w = 1 - ramp(t, 0.0, 1.0)
        p = sitting(hen.pose(), w)
        wings(p, out=20 * bump(t, 0.4, 0.2))
        return p.add("body", scale=(1 + 0.05 * w, 1, 1))

    @hen.animation("Sleeping", 120)
    def sleeping(t):
        p = sitting(hen.pose(), 1)
        p.add("body", scale=(1.08, 1, 1 + 0.02 * wave(t)))
        p.add("neck", rot=(30, 0, 0), loc=(0, -0.04, 0.03))  # head sunk into the shoulders
        p.add("head", rot=(30, 0, 0))
        return p.blink(1.0)

    hen.finish()


for variant in ("Female", "Male"):
    build(variant)
