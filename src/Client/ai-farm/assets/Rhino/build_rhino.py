"""Builds the KayKit-style pigs (🐖 🐷): Pig_Female (a sow, with teats) and Pig_Male (a boar:
bigger, with a heavier shoulder and little tusks). Each gets its model, palette texture, rig and
animations, using the shared animal kit (assets/AnimalKit/animal_kit.py; see it for how to run
this and for the rig conventions)."""

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
    "pink": ((143, 151, 156), (94, 101, 105)),
    "snout": ((135, 141, 144), (86, 91, 93)),
    "nostril": ((68, 67, 67), (38, 37, 38)),
    "hoof": ((99, 101, 101), (58, 60, 61)),
    "eye": ((36, 32, 38), (16, 14, 18)),
    "ear_inner": ((151, 113, 100), (103, 71, 64)),
    "mouth": ((120, 40, 54), (70, 20, 32)),
    "tusk": ((171, 164, 151), (111, 105, 96)),
    "teat": ((240, 150, 162), (214, 120, 136)),
}

wave, ramp, envelope, bump = kit.wave, kit.ramp, kit.envelope, kit.bump

# --------------------------------------------------------------------------------------
# Proportions: a round barrel on short legs
# --------------------------------------------------------------------------------------

HIP_Z, KNEE_Z = 0.32, 0.17
BODY_PIVOT_Z = 0.48
LEGS = {"leg.fl": (0.16, -0.3), "leg.fr": (-0.16, -0.3), "leg.bl": (0.16, 0.3), "leg.br": (-0.16, 0.3)}
HOOF_RADIUS = 0.07
SHADE = (0.0, 0.85)
BODY_CENTER, BODY_SIZE = (0, 0, 0.52), (0.64, 0.92, 0.56)
EYE_Z = 0.64
MOUTH_Z = 0.47
MALE_SCALE = 1.12


def build(sex):
    male = sex == "Male"
    pig = kit.Animal(f"Rhino_{sex}", OUT_DIR, PALETTE)

    # --- body ---------------------------------------------------------------------------
    size = (BODY_SIZE[0] + 0.04, BODY_SIZE[1], BODY_SIZE[2] + 0.03) if male else BODY_SIZE
    pig.part("body", "pink", kit.body_loft(BODY_CENTER, size, shoulder=0.09), SHADE)
    # --- head: a round head straight onto the shoulders, the snout out front ---------------
    pig.part("neck", "pink", kit.blob((0, -0.42, 0.55), (0.25, 0.2, 0.23), subdiv=3), SHADE)

    def head_shape():
        return kit.blob((0, -0.58, 0.61), (0.25, 0.22, 0.23), subdiv=3)

    head_target = kit.surface(head_shape())
    pig.part("head", "pink", head_shape(), SHADE)

    # Lower face cut at the mouth line (top on the head, chin on the jaw), and the flat snout disc.
    def lips(p):
        return "mouth" if abs(p.z - MOUTH_Z) < 0.005 else "pink"

    upper, chin = kit.split(kit.subdivided(kit.rounded_box((0, -0.75, 0.5), (0.22, 0.18, 0.15), 0.06,
                                                           segments=3), 1), MOUTH_Z)
    pig.part("head", lips, upper)
    pig.part("jaw", lips, chin)
    disc = kit.rounded_box((0,-.84,.53),(.25,.12,.16),.05,segments=3)
    pig.part("head", "snout", disc)
    for side in (1, -1):
        pig.part("head", "nostril", kit.blob((0.075 * side, -0.898, 0.55), (0.015, 0.008, 0.014)))
    # Both horns emerge from the snout and follow the head bone.
    pig.part("head", "tusk", kit.tapered((0,-.79,.57),(0,-.87,.83),.075,.003,segments=12))
    pig.part("head", "tusk", kit.tapered((0,-.66,.65),(0,-.69,.81),.052,.003,segments=10))

    for side in (1, -1):
        # Cast straight onto the forward cheek, above the snout. An angled
        # ray pushes the eyes back towards the ears instead.
        eye = kit.hit(head_target, (0.6 * side, -0.73, EYE_Z), (-side, 0, 0))
        assert eye is not None, "Pig eye ray missed the head"
        pig.part("eyes", "eye", kit.blob(eye, (0.012, 0.020, 0.025)))

    # Ears: big pointed flaps; the sow's flop forward over her brow, the boar's stand up.
    def ear(base, side, width, length, thickness, lift):
        """A pointed flap rising from its base, tipped forward by `lift` degrees and out a little."""
        bm = kit.blob((0, 0, length), (width, thickness, length), subdiv=3)
        for v in bm.verts:
            v.co.x *= 1 - 0.75 * max(0.0, v.co.z / (2 * length))  # narrowing to a point
        bm.transform(kit.transform(base, rot=(lift, 20 * side, 0)))
        return bm

    for side, bone in ((1, "ear.l"), (-1, "ear.r")):
        base = Vector((0.13 * side, -0.54, 0.79))
        lift = 25
        pig.part(bone, "pink", ear(base, side, 0.105, 0.115, 0.025, lift), SHADE)
        pig.part(bone, "ear_inner", ear(base + Vector((0, -0.013, 0.006)), side, 0.075, 0.09, 0.016, lift))

    # A corkscrew tail: out from the rump, then a loop (seen from behind).
    tail_root = Vector((0, size[1] / 2 - 0.03, 0.62))
    curl = [tail_root + Vector((0,.035*i,-.018*i)) for i in range(7)]
    links = ["tail", "tail.2", "tail.2", "tail.3", "tail.3", "tail.3"]
    for bone, a, b in zip(links, curl, curl[1:]):
        pig.part(bone, "pink", kit.capsule(a, b, 0.016, 0.015, 10, 3), SHADE)

    # --- legs ---------------------------------------------------------------------------
    for leg, (x, y) in LEGS.items():
        front = y < 0
        pig.part(leg, "pink", kit.capsule((x, y, KNEE_Z), (x, y, HIP_Z), 0.07, 0.085), SHADE)
        radii = (0.09, 0.1, 0.09) if front else (0.1, 0.12, 0.1)
        pig.part(leg, "pink", kit.blob((x, y, HIP_Z + 0.07), radii, subdiv=3), SHADE)
        pig.part(leg + ".lower", "pink", kit.capsule((x, y, 0.06), (x, y, KNEE_Z), 0.06, 0.066), SHADE)
        pig.part(leg + ".lower", "hoof", kit.cylinder((x, y, 0.0), HOOF_RADIUS, 0.065, segments=16))

    bones = [
        ("root", (0, 0, 0), None),
        ("body", (0, 0, BODY_PIVOT_Z), "root"),
        ("neck", (0, -0.36, 0.52), "body"),
        ("head", (0, -0.5, 0.56), "neck"),
        ("jaw", (0, -0.67, MOUTH_Z), "head"),
        ("eyes", (0, -0.7, EYE_Z), "head"),
        ("ear.l", (0.13, -0.54, 0.79), "head"),
        ("ear.r", (-0.13, -0.54, 0.79), "head"),
        ("tail", tuple(curl[0]), "body"),
        ("tail.2", tuple(curl[1]), "tail"),
        ("tail.3", tuple(curl[3]), "tail.2"),
        *[(leg, (x, y, HIP_Z), "body") for leg, (x, y) in LEGS.items()],
        *[(leg + ".lower", (x, y, KNEE_Z), leg) for leg, (x, y) in LEGS.items()],
    ]
    pig.build(bones, LEGS, HIP_Z, KNEE_Z, BODY_PIVOT_Z, HOOF_RADIUS, leg_radius=0.1,
              tail=("tail", "tail.2", "tail.3"), shade_range=SHADE)
    pig.rig.scale = ((1.75 if male else 1.60),) * 3

    # --- animations -----------------------------------------------------------------------

    def lying(p, front, back):
        """Lying down: forelegs tucked under the chest, hind legs folded alongside the belly."""
        for leg in ("leg.fl", "leg.fr"):
            p.fold(leg, -78, 158, 4, front)
        for leg in ("leg.bl", "leg.br"):
            p.fold(leg, -84, 166, 22, back)
        return p

    def wiggle(p, t, cycles=2, amount=20):
        """The curly tail twitching."""
        return p.tail(wag=amount * wave(t, cycles))

    @pig.animation("Idle", 90)
    def idle(t):
        p = pig.pose()
        p.add("neck", rot=(1.5 * wave(t), 0, 0))
        p.add("head", rot=(2 * wave(t, 2, 0.25), 0, 0))  # snuffling
        p.ears(droop=6 * bump(t, 0.4, 0.05))
        wiggle(p, t, cycles=3, amount=14)
        return p.blink(bump(t, 0.7, 0.04))

    @pig.animation("Idle_LookAround", 120)
    def idle_look_around(t):
        look = 30 * (ramp(t, 0.1, 0.25) - ramp(t, 0.4, 0.55)) - 30 * (ramp(t, 0.6, 0.72) - ramp(t, 0.85, 0.97))
        p = pig.pose()
        p.add("neck", rot=(0, look * 0.4, 0))
        p.add("head", rot=(-4 * abs(look) / 30, look * 0.6, 0))
        p.ears(back=-8 * abs(look) / 30)
        wiggle(p, t)
        return p.blink(bump(t, 0.3, 0.03) + bump(t, 0.8, 0.03))

    @pig.animation("Walking", 30)
    def walking(t):
        p = kit.gait(pig.pose(), t, kit.WALK, stance=0.62, stride=0.22, lift=0.07)
        p.add("body", rot=(0, 3 * wave(t), 2 * wave(t, 2)))
        p.add("head", rot=(3 * wave(t, 2), 0, 0))
        p.ears(droop=6 * wave(t, 2))
        return wiggle(p, t, cycles=1, amount=10)

    @pig.animation("Running", 16)
    def running(t):
        p = kit.gait(pig.pose(), t, kit.TROT, stance=0.45, stride=0.34, lift=0.1)
        p.add("body", rot=(4 * wave(t, 2), 0, 0), loc=(0, -0.015, 0))
        p.add("head", rot=(-5 * wave(t, 2), 0, 0))
        p.ears(back=25, droop=8 * wave(t, 2))
        return wiggle(p, t, cycles=2, amount=24)

    @pig.animation("Eating_Grass", 80)
    def eating_grass(t):
        # Rooting: snout down at the ground, nudging and snuffling forward.
        p = pig.pose()
        p.add("body", rot=(5, 0, 0), loc=(0, -0.03, 0))
        p.add("neck", rot=(28, 0, 0), loc=(0, -0.025, 0.05))
        p.add("head", rot=(22 + 4 * wave(t, 3), 6 * wave(t, 1), 0))
        p.chew(t, rate=6, amount=0.8)
        p.ears(droop=10)
        wiggle(p, t, cycles=3, amount=18)
        return p.blink(bump(t, 0.5, 0.04))

    @pig.animation("Snort", 40)
    def oink(t):
        w = envelope(t, 0.05, 0.2, 0.5, 0.7) + envelope(t, 0.55, 0.65, 0.85, 0.98) * 0.8
        p = pig.pose()
        p.add("neck", rot=(-6 * w, 0, 0))
        p.add("head", rot=(-10 * w, 0, 0))
        p.add("jaw", rot=(18 * w, 0, 0))
        p.ears(back=10 * w)
        wiggle(p, t, cycles=4, amount=16)
        return p.blink(0.3 * w)

    @pig.animation("Being_Fed", 90)
    def being_fed(t):
        reach = envelope(t, 0.0, 0.12, 0.85, 1.0)
        chewing = envelope(t, 0.15, 0.2, 0.8, 0.85)
        p = pig.pose()
        p.add("neck", rot=(-10 * reach, 0, 0))
        p.add("head", rot=(-14 * reach + 6 * bump(t, 0.16, 0.05), 0, 0))
        p.chew(t, rate=9, amount=1.4 * chewing)
        p.ears(back=-10 * reach)
        wiggle(p, t, cycles=6, amount=26 * chewing + 6)
        return p.blink(0.4 * envelope(t, 0.2, 0.3, 0.75, 0.85))

    @pig.animation("Getting_Love", 100)
    def getting_love(t):
        w = envelope(t, 0.05, 0.2, 0.8, 0.95)
        p = pig.pose()
        p.add("body", rot=(0, 0, 4 * w))  # leaning into the scratch
        p.add("neck", rot=(-4 * w, 8 * w, 0))
        p.add("head", rot=(-8 * w, 0, 14 * w))
        p.ears(droop=16 * w)
        wiggle(p, t, cycles=4, amount=18 * w + 6)
        return p.blink(0.95 * w)

    @pig.animation("Lie_Down", 40)
    def lie_down(t):
        front, back = ramp(t, 0.0, 0.55), ramp(t, 0.3, 0.95)
        return wiggle(lying(pig.pose(), front, back), t, cycles=1, amount=8)

    @pig.animation("Lie_Idle", 90)
    def lie_idle(t):
        p = lying(pig.pose(), 1, 1)
        p.add("head", rot=(2 * wave(t, 1, 0.25), 5 * wave(t), 0))
        p.ears(droop=8 * bump(t, 0.7, 0.05))
        wiggle(p, t, cycles=2, amount=8)
        return p.blink(bump(t, 0.4, 0.04))

    @pig.animation("Lie_StandUp", 40)
    def lie_stand_up(t):
        back, front = 1 - ramp(t, 0.0, 0.55), 1 - ramp(t, 0.3, 0.95)
        return wiggle(lying(pig.pose(), front, back), t, cycles=1, amount=8)

    @pig.animation("Sleeping", 120)
    def sleeping(t):
        p = lying(pig.pose(), 1, 1)
        p.add("neck", rot=(14, 0, 0))
        p.add("head", rot=(16 + 2 * wave(t), 0, 10))
        p.ears(droop=16)
        return p.blink(1.0)

    pig.finish()


for variant in ("Female", "Male"):
    build(variant)
