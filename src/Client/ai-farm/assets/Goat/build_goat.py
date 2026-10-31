"""Builds the KayKit-style goats (🐐): Goat_Female (a white nanny with small horns, a little beard
and an udder) and Goat_Male (a billy: bigger, with long swept-back horns, a long beard and a
darker saddle). Each gets its model, palette texture, rig and animations, using the shared animal
kit (assets/AnimalKit/animal_kit.py; see it for how to run this and for the rig conventions)."""

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
    "white": ((255, 252, 246), (222, 216, 206)),
    "saddle": ((186, 160, 132), (140, 116, 94)),
    "beard": ((220, 212, 200), (170, 160, 148)),
    "nose": ((236, 186, 180), (206, 150, 148)),
    "nostril": ((120, 80, 80), (80, 50, 50)),
    "hoof": ((96, 84, 80), (56, 48, 46)),
    "horn": ((206, 190, 164), (140, 124, 104)),
    "eye": ((62, 48, 30), (30, 22, 14)),
    "ear_inner": ((246, 196, 190), (220, 160, 160)),
    "mouth": ((120, 40, 54), (70, 20, 32)),
    "udder": ((248, 200, 196), (222, 160, 160)),
}

wave, ramp, envelope, bump = kit.wave, kit.ramp, kit.envelope, kit.bump

# --------------------------------------------------------------------------------------
# Proportions: a lean, deep-chested body on longer legs than the cow's
# --------------------------------------------------------------------------------------

HIP_Z, KNEE_Z = 0.46, 0.24
BODY_PIVOT_Z = 0.6
LEGS = {"leg.fl": (0.12, -0.29), "leg.fr": (-0.12, -0.29), "leg.bl": (0.12, 0.29), "leg.br": (-0.12, 0.29)}
HOOF_RADIUS = 0.05
SHADE = (0.0, 1.0)
BODY_CENTER, BODY_SIZE = (0, 0, 0.62), (0.4, 0.82, 0.38)
EYE_Z = 0.92
MOUTH_Z = 0.8
MALE_SCALE = 1.15


def build(sex):
    male = sex == "Male"

    def coat(p):
        # The billy has a darker saddle over his back and shoulders.
        if male and kit.inside(p, (0, -0.05, 0.86), (0.3, 0.42, 0.2)):
            return "saddle"
        return "white"

    goat = kit.Animal(f"Goat_{sex}", OUT_DIR, PALETTE)

    # --- body -----------------------------------------------------------------------------
    goat.part("body", coat, kit.subdivided(kit.body_loft(BODY_CENTER, (0.46, 0.82, 0.42)), 2))
    if not male:
        goat.part("udder", "udder", kit.blob((0, 0.22, 0.42), (0.07, 0.065, 0.05), subdiv=3))
        for dx in (-0.03, 0.03):
            goat.part("udder", "udder", kit.tapered((dx, 0.22, 0.38), (dx, 0.215, 0.34), 0.012, 0.008, 8))

    # --- neck and head: the neck rises steeply; a wedge-shaped head with a narrow muzzle ---
    goat.part("neck", coat, kit.capsule((0, -0.33, 0.66), (0, -0.44, 0.86), 0.12 if male else 0.1, 0.085, 14, 4))

    def head_shape():
        return kit.rounded_box((0, -0.52, 0.9), (0.20, 0.23, 0.19), 0.07, segments=2, rot=(-20, 0, 0))

    head_target = kit.surface(head_shape())
    goat.part("head", coat, kit.subdivided(head_shape(), 1))

    def lips(p):
        return "mouth" if abs(p.z - MOUTH_Z) < 0.004 else "white"

    muzzle = kit.subdivided(kit.rounded_box((0, -0.64, 0.83), (0.13, 0.13, 0.11), 0.05, segments=3,
                                            rot=(-20, 0, 0)), 1)
    upper, chin = kit.split(muzzle, MOUTH_Z)
    goat.part("head", lips, upper)
    goat.part("jaw", lips, chin)
    goat.part("head", "nose", kit.blob((0, -0.705, 0.845), (0.045, 0.02, 0.03)))
    for side in (1, -1):
        goat.part("head", "nostril", kit.blob((0.018 * side, -0.722, 0.847), (0.009, 0.006, 0.013)))
    for side in (1, -1):
        # Keep the ray at the eye's intended depth instead of drifting back
        # towards the ears while travelling across the side of the head.
        eye = kit.hit(head_target, (0.4 * side, -0.56, EYE_Z), (-side, 0, 0))
        assert eye is not None, "Goat eye ray missed the head"
        # Goats' slot pupils read best as a wide dark oval.
        goat.part("eyes", "eye", kit.blob(eye, (0.008, 0.020, 0.012)))

    # Beard hanging from the chin (long on the billy).
    beard_len = 0.13 if male else 0.05
    goat.part("jaw", "beard", kit.tapered((0, -0.66, 0.78), (0, -0.64, 0.78 - beard_len), 0.026, 0.006, 10))

    # Ears sticking out sideways and a little down.
    for side, bone in ((1, "ear.l"), (-1, "ear.r")):
        base = Vector((0.07 * side, -0.48, 0.95))
        goat.part(bone, coat, kit.blob(base + Vector((0.07 * side, 0.0, -0.02)), (0.075, 0.02, 0.032),
                                       rot=(0, 0, -10 * side), subdiv=3))
        goat.part(bone, "ear_inner", kit.blob(base + Vector((0.075 * side, -0.012, -0.02)), (0.055, 0.008, 0.02),
                                              rot=(0, 0, -10 * side)))

    # Horns: curving up and back over the neck, ridged by their segments.
    sweep = (0.3, 0.13, 0.045) if male else (0.12, 0.06, 0.025)
    length, rise, thick = sweep
    for side in (1, -1):
        points = []
        for k in range(7):
            a = math.radians(100 * k / 6)
            points.append(Vector((0.04 * side + 0.05 * side * k / 6, -0.48 + length * (1 - math.cos(a)) * 0.9,
                                  0.98 + rise * math.sin(a) * 1.3 - length * 0.25 * (k / 6) ** 2)))
        radii = [thick * (1 - 0.95 * k / 6) for k in range(7)]
        goat.part("head", "horn", kit.sweep(points, radii, radii))

    # A short upright tail.
    tail_y = BODY_SIZE[1] / 2 - 0.02
    goat.part("tail", coat, kit.capsule((0, tail_y, 0.74), (0, tail_y + 0.04, 0.84), 0.028, 0.02, 10, 3))

    # --- legs -----------------------------------------------------------------------------
    for leg, (x, y) in LEGS.items():
        front = y < 0
        goat.part(leg, coat, kit.capsule((x, y, KNEE_Z), (x, y, HIP_Z), 0.05, 0.07))
        radii = (0.08, 0.1, 0.1) if front else (0.085, 0.12, 0.11)
        goat.part(leg, coat, kit.blob((x, y, HIP_Z + 0.06), radii, subdiv=3))
        goat.part(leg + ".lower", coat, kit.capsule((x, y, 0.06), (x, y, KNEE_Z), 0.04, 0.046))
        goat.part(leg + ".lower", "hoof", kit.cylinder((x, y, 0.0), HOOF_RADIUS, 0.065, segments=16))

    bones = [
        ("root", (0, 0, 0), None),
        ("body", (0, 0, BODY_PIVOT_Z), "root"),
        ("neck", (0, -0.32, 0.66), "body"),
        ("head", (0, -0.45, 0.86), "neck"),
        ("jaw", (0, -0.58, MOUTH_Z), "head"),
        ("eyes", (0, -0.56, EYE_Z), "head"),
        ("ear.l", (0.07, -0.48, 0.95), "head"),
        ("ear.r", (-0.07, -0.48, 0.95), "head"),
        ("tail", (0, tail_y, 0.74), "body"),
        *([] if male else [("udder", (0, 0.22, 0.46), "body")]),
        *[(leg, (x, y, HIP_Z), "body") for leg, (x, y) in LEGS.items()],
        *[(leg + ".lower", (x, y, KNEE_Z), leg) for leg, (x, y) in LEGS.items()],
    ]
    goat.build(bones, LEGS, HIP_Z, KNEE_Z, BODY_PIVOT_Z, HOOF_RADIUS, leg_radius=0.08, tail=("tail",),
               shade_range=SHADE)
    if male:
        goat.rig.scale = (MALE_SCALE,) * 3

    # --- animations -----------------------------------------------------------------------

    def lying(p, front, back):
        for leg in ("leg.fl", "leg.fr"):
            p.fold(leg, -78, 158, 4, front)
        for leg in ("leg.bl", "leg.br"):
            p.fold(leg, -84, 166, 22, back)
        return p.add("udder", scale=(1.1, 1 - 0.6 * back, 1.1))

    def flick(p, t, cycles=2, amount=16):
        """The short tail flicking."""
        return p.tail(wag=amount * wave(t, cycles), lift=6)

    @goat.animation("Idle", 90)
    def idle(t):
        p = goat.pose()
        p.add("neck", rot=(1.5 * wave(t), 0, 0))
        p.add("head", rot=(2 * wave(t, 1, 0.25), 0, 0))
        p.ears(droop=10 * bump(t, 0.3, 0.05) + 10 * bump(t, 0.7, 0.04))
        p.chew(t, rate=7, amount=0.5)
        flick(p, t, cycles=3, amount=10 * bump(t, 0.5, 0.15))
        return p.blink(bump(t, 0.6, 0.04))

    @goat.animation("Idle_LookAround", 120)
    def idle_look_around(t):
        look = 32 * (ramp(t, 0.1, 0.22) - ramp(t, 0.4, 0.52)) - 32 * (ramp(t, 0.6, 0.7) - ramp(t, 0.85, 0.95))
        p = goat.pose()
        p.add("neck", rot=(0, look * 0.45, 0))
        p.add("head", rot=(-4 * abs(look) / 32, look * 0.55, 0))
        p.ears(back=-10 * abs(look) / 32)
        flick(p, t, amount=6)
        return p.blink(bump(t, 0.3, 0.03) + bump(t, 0.78, 0.03))

    @goat.animation("Walking", 32)
    def walking(t):
        p = kit.gait(goat.pose(), t, kit.WALK, stance=0.64, stride=0.26, lift=0.08)
        p.add("body", rot=(0, 2 * wave(t), 0))
        p.add("neck", rot=(3 * wave(t, 2), 0, 0))
        return flick(p, t, cycles=1, amount=8)

    @goat.animation("Running", 18)
    def running(t):
        p = kit.gait(goat.pose(), t, kit.GALLOP, stance=0.4, stride=0.44, lift=0.13)
        p.add("body", rot=(6 * wave(t, 1, 0.25), 0, 0), loc=(0, -0.02 + 0.012 * wave(t, 1, 0.25), 0))
        p.add("neck", rot=(-6 * wave(t, 1, 0.25), 0, 0))
        p.tail(lift=20)
        return p.ears(back=30)

    @goat.animation("Eating_Grass", 80)
    def eating_grass(t):
        p = goat.pose()
        p.add("body", rot=(10, 0, 0), loc=(0, -0.06, 0))
        p.add("neck", rot=(92, 0, 0), loc=(0, -0.075, 0.08))
        p.add("head", rot=(-4 + 3 * wave(t, 1), 0, 0))
        p.chew(t, rate=6)
        p.ears(droop=10)
        flick(p, t, amount=8)
        return p.blink(bump(t, 0.55, 0.04))

    @goat.animation("Bleat", 50)
    def bleat(t):
        w = envelope(t, 0.08, 0.22, 0.65, 0.9)
        p = goat.pose()
        p.add("neck", rot=(-10 * w, 0, 0))
        p.add("head", rot=(-18 * w, 0, 0))
        p.add("jaw", rot=(22 * w * (0.85 + 0.15 * wave(t, 8)), 0, 0))  # the wavering "meh-eh-eh"
        p.ears(back=14 * w)
        flick(p, t, cycles=4, amount=12 * w)
        return p.blink(0.3 * w)

    @goat.animation("Being_Fed", 90)
    def being_fed(t):
        reach = envelope(t, 0.0, 0.12, 0.85, 1.0)
        chewing = envelope(t, 0.15, 0.2, 0.8, 0.85)
        p = goat.pose()
        p.add("neck", rot=(-12 * reach, 0, 0))
        p.add("head", rot=(-8 * reach + 6 * bump(t, 0.16, 0.05), 0, 0))
        p.chew(t, rate=9, amount=1.3 * chewing)
        p.ears(back=-14 * reach)
        flick(p, t, cycles=5, amount=18 * chewing)
        return p.blink(0.4 * envelope(t, 0.2, 0.3, 0.75, 0.85))

    @goat.animation("Getting_Love", 100)
    def getting_love(t):
        w = envelope(t, 0.05, 0.2, 0.8, 0.95)
        p = goat.pose()
        p.add("neck", rot=(6 * w, 10 * w, 0))
        p.add("head", rot=(4 * w, 0, 16 * w))
        p.ears(droop=20 * w)
        flick(p, t, cycles=4, amount=12 * w)
        return p.blink(0.95 * w)

    @goat.animation("Lie_Down", 45)
    def lie_down(t):
        # Front first, but the hind end follows soon after so the chest never digs in.
        front, back = ramp(t, 0.0, 0.55), ramp(t, 0.2, 0.9)
        return lying(goat.pose(), front, back)

    @goat.animation("Lie_Idle", 90)
    def lie_idle(t):
        p = lying(goat.pose(), 1, 1)
        p.add("head", rot=(2 * wave(t, 1, 0.25), 6 * wave(t), 0))
        p.chew(t, rate=7, amount=0.6)
        p.ears(droop=10 * bump(t, 0.7, 0.05))
        return p.blink(bump(t, 0.4, 0.04))

    @goat.animation("Lie_StandUp", 45)
    def lie_stand_up(t):
        back, front = 1 - ramp(t, 0.0, 0.55), 1 - ramp(t, 0.2, 0.9)
        return lying(goat.pose(), front, back)

    @goat.animation("Sleeping", 120)
    def sleeping(t):
        p = lying(goat.pose(), 1, 1)
        p.add("neck", rot=(30, 0, 0))
        p.add("head", rot=(20 + 2 * wave(t), 0, 10))
        p.ears(droop=18)
        return p.blink(1.0)

    if male:
        @goat.animation("Headbutt", 40)
        def headbutt(t):
            # Rear back, then drop the head and lunge forward horns first.
            rear = envelope(t, 0.0, 0.3, 0.35, 0.45)
            butt = envelope(t, 0.4, 0.5, 0.6, 0.9)
            p = goat.pose()
            p.add("body", rot=(-8 * rear + 6 * butt, 0, 0), loc=(0, 0, -0.04 * rear + 0.08 * butt))
            p.add("neck", rot=(-10 * rear + 34 * butt, 0, 0))
            p.add("head", rot=(26 * butt, 0, 0))
            p.ears(back=25 * (rear + butt))
            return p

    goat.finish()


for variant in ("Female", "Male"):
    build(variant)
