"""Builds the KayKit-style cattle (🐄 🐮 🐂, black-and-white Holsteins): Cow_Female (a cow, with an
udder) and Cow_Male (a bull: heavier, with a shoulder hump, long horns and a nose ring). Each gets
its model, palette texture, rig and animations, using the shared animal kit
(assets/AnimalKit/animal_kit.py; see it for how to run this and for the rig conventions)."""

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
    "white": ((255, 255, 255), (214, 216, 226)),
    "black": ((64, 62, 70), (30, 29, 34)),
    "pink": ((250, 186, 186), (220, 140, 146)),
    "nostril": ((150, 78, 88), (110, 50, 60)),
    "hoof": ((88, 76, 72), (48, 40, 38)),
    "horn": ((250, 238, 214), (214, 192, 160)),
    "eye": ((36, 32, 38), (16, 14, 18)),
    "ear_inner": ((246, 176, 182), (214, 132, 144)),
    "mouth": ((120, 40, 54), (70, 20, 32)),
    "ring": ((236, 214, 140), (170, 140, 70)),
}

wave, ramp, envelope, bump = kit.wave, kit.ramp, kit.envelope, kit.bump

# --------------------------------------------------------------------------------------
# Proportions (shared; the bull is built heavier and scaled up a little)
# --------------------------------------------------------------------------------------

HIP_Z, KNEE_Z = 0.5, 0.26
BODY_PIVOT_Z = 0.74
LEGS = {"leg.fl": (0.25, -0.46), "leg.fr": (-0.25, -0.46), "leg.bl": (0.25, 0.46), "leg.br": (-0.25, 0.46)}
HOOF_RADIUS = 0.1
SHADE = (0.0, 1.3)  # heights every part's colour gradient runs between, so parts match
BODY_CENTER, BODY_SIZE = (0, 0, 0.82), (0.94, 1.38, 0.74)
EYE_Z = 1.06
MOUTH_Z = 0.8
MALE_SCALE = 1.1

# Black Holstein patches: 3D blobs, so they wrap smoothly over whatever part they cross.
SPOTS = {
    "Female": [
        ((0.42, 0.1, 0.86), (0.25, 0.27, 0.2)),     # left flank
        ((0.4, -0.4, 0.68), (0.2, 0.14, 0.13)),     # left shoulder
        ((0.26, 0.58, 0.98), (0.2, 0.14, 0.15)),    # left of the rump
        ((-0.42, -0.14, 0.84), (0.25, 0.28, 0.18)),  # right side
        ((-0.4, 0.46, 0.62), (0.22, 0.17, 0.16)),   # right haunch
        ((0.02, 0.06, 1.16), (0.2, 0.22, 0.1)),     # across the back
        ((0.14, -0.68, 1.04), (0.15, 0.15, 0.15)),  # back of the neck
        ((0.15, -0.98, 1.13), (0.12, 0.2, 0.11)),   # round the left eye and up to the ear
        ((-0.1, -0.86, 1.2), (0.1, 0.14, 0.07)),    # top of the head
        ((-0.3, -0.46, 0.4), (0.12, 0.12, 0.12)),   # right foreleg
    ],
    "Male": [
        ((0.44, -0.12, 0.84), (0.27, 0.34, 0.24)),  # left side
        ((0.3, 0.58, 0.9), (0.22, 0.18, 0.2)),      # left of the rump
        ((-0.44, 0.18, 0.86), (0.27, 0.3, 0.22)),   # right side
        ((-0.3, -0.56, 0.98), (0.2, 0.2, 0.2)),     # right of the hump
        ((0.04, -0.34, 1.3), (0.26, 0.26, 0.14)),   # over the hump
        ((-0.16, -0.7, 1.0), (0.16, 0.16, 0.16)),   # right of the neck
        ((-0.15, -0.98, 1.13), (0.12, 0.2, 0.11)),  # round the right eye
        ((0.08, -0.84, 1.22), (0.1, 0.12, 0.08)),   # top of the head
        ((0.3, 0.46, 0.4), (0.12, 0.12, 0.12)),     # left hind leg
    ],
}


def build(sex):
    male = sex == "Male"
    spots = SPOTS[sex]

    def coat(p):
        return "black" if any(kit.inside(p, c, r) for c, r in spots) else "white"

    cow = kit.Animal(f"Cow_{sex}", OUT_DIR, PALETTE)

    # --- body: a deep barrel with a full belly ------------------------------------------
    size = tuple(s + 0.04 for s in BODY_SIZE) if male else BODY_SIZE
    body_bm = kit.subdivided(kit.body_loft(BODY_CENTER, size, shoulder=0.14 if male else 0.0), 2)
    cow.part("body", coat, body_bm)
    if not male:
        # Udder under the belly, between the hind legs, with four teats.
        # On a bone of its own (at the belly) so it can flatten against the ground when lying.
        cow.part("udder", "pink", kit.blob((0, 0.3, 0.36), (0.15, 0.14, 0.09), subdiv=3))
        for dx in (-0.055, 0.055):
            for dy in (-0.05, 0.05):
                cow.part("udder", "pink", kit.tapered((dx, 0.3 + dy, 0.3), (dx, 0.3 + dy, 0.24), 0.02, 0.014, 8))

    # --- neck and head --------------------------------------------------------------------
    neck_radii = (0.3, 0.28, 0.31) if male else (0.25, 0.26, 0.28)
    cow.part("neck", coat, kit.blob((0, -0.66, 0.92), neck_radii, subdiv=3))

    def head_shape():
        return kit.rounded_box((0, -0.92, 1.03), (0.46 if male else 0.41, 0.36, 0.38), 0.14, segments=4)

    muzzle_width = 0.4 if male else 0.37

    def muzzle_shape():
        return kit.rounded_box((0, -1.11, MOUTH_Z + 0.065), (muzzle_width, 0.18, 0.27), 0.08, segments=3)

    head_target, muzzle_target = kit.surface(head_shape()), kit.surface(muzzle_shape())
    cow.part("head", coat, kit.subdivided(head_shape(), 2))

    # Pink muzzle cut at the mouth line: the top on the head, the bottom on the jaw. Closed, they
    # meet flush with a thin dark line between; open, the dark cut faces are the inside of the mouth.
    def lips(p):
        return "mouth" if abs(p.z - MOUTH_Z) < 0.006 else "pink"

    upper_lip, lower_jaw = kit.split(kit.subdivided(muzzle_shape(), 1), MOUTH_Z)
    cow.part("head", lips, upper_lip)
    cow.part("jaw", lips, lower_jaw)
    nostrils = []
    for side in (1, -1):
        nostril = kit.hit(muzzle_target, (0.078 * side, -2.0, 0.9), (0, 1, 0))
        nostrils.append(nostril)
        cow.part("head", "nostril", kit.blob(nostril + Vector((0, 0.008, 0)), (0.03, 0.015, 0.02)))
    if male:
        # A ring through the nose: across it (facing forward), its top through the septum between
        # the nostrils and the rest hanging in front of the muzzle.
        middle = (nostrils[0] + nostrils[1]) / 2
        cow.part("head", "ring", kit.torus(middle + Vector((0, 0.004, -0.038)), 0.042, 0.01, rot=(90, 0, 0)))

    # Eyes on the sides of the face, half sunk in; at the eyes bone's height so blinks squash in place.
    for side in (1, -1):
        eye = kit.hit(head_target, (0.6 * side, -1.12, EYE_Z), (-side, 0.35, 0))
        cow.part("eyes", "eye", kit.blob(eye, (0.03, 0.035, 0.042)))

    # Floppy ears sticking out sideways, and horns above them: short cream nubs on the cow, long
    # and curving forward and up on the bull.
    half_head = 0.23 if male else 0.205
    for side, bone in ((1, "ear.l"), (-1, "ear.r")):
        base = Vector((half_head * side, -0.86, 1.09))
        cow.part(bone, coat, kit.blob(base + Vector((0.09 * side, 0, -0.01)), (0.11, 0.035, 0.055),
                                      rot=(0, 0, 12 * side)))
        cow.part(bone, "ear_inner", kit.blob(base + Vector((0.1 * side, -0.022, -0.01)), (0.08, 0.015, 0.036),
                                             rot=(0, 0, 12 * side)))
        root = Vector(((half_head - 0.09) * side, -0.88, 1.16))
        if male:
            bend = root + Vector((0.17 * side, -0.02, 0.03))
            tip = bend + Vector((0.09 * side, -0.08, 0.17))
            cow.part("head", "horn", kit.tapered(root, bend, 0.05, 0.034))
            cow.part("head", "horn", kit.blob(bend, (0.034, 0.034, 0.034)))
            cow.part("head", "horn", kit.tapered(bend, tip, 0.034, 0.008))
        else:
            cow.part("head", "horn", kit.tapered(root, root + Vector((0.08 * side, 0, 0.12)), 0.036, 0.012))

    # Tail hanging from the top of the rump, with a black tuft.
    tail_y = size[1] / 2 - 0.02
    cow.part("tail", coat, kit.capsule((0, tail_y, 0.98), (0, tail_y + 0.05, 0.74), 0.032, 0.025, 10, 3))
    cow.part("tail.2", coat, kit.capsule((0, tail_y + 0.05, 0.74), (0, tail_y + 0.08, 0.5), 0.025, 0.02, 10, 3))
    cow.part("tail.2", "black", kit.blob((0, tail_y + 0.085, 0.45), (0.048, 0.048, 0.085), subdiv=3))

    # --- legs: thick and short, painted with the coat -------------------------------------
    # Each leg is two capsules meeting round the knee, so the joint stays round as it bends.
    for leg, (x, y) in LEGS.items():
        front = y < 0
        cow.part(leg, coat, kit.capsule((x, y, KNEE_Z), (x, y, HIP_Z), 0.1, 0.125))
        # Shoulder or haunch, tucked up into the body, so the top of the leg swells into it.
        radii = (0.14, 0.17, 0.17) if front else (0.15, 0.2, 0.19)
        cow.part(leg, coat, kit.blob((x, y + (0 if front else 0.02), HIP_Z + 0.06), radii, subdiv=3))
        cow.part(leg + ".lower", coat, kit.capsule((x, y, 0.1), (x, y, KNEE_Z), 0.09, 0.094))
        cow.part(leg + ".lower", "hoof", kit.cylinder((x, y, 0.0), HOOF_RADIUS, 0.1, segments=16))

    bones = [
        ("root", (0, 0, 0), None),
        ("body", (0, 0, BODY_PIVOT_Z), "root"),
        ("neck", (0, -0.58, 0.86), "body"),
        ("head", (0, -0.8, 0.95), "neck"),
        ("jaw", (0, -1.0, MOUTH_Z), "head"),
        ("eyes", (0, -0.97, EYE_Z), "head"),
        ("ear.l", (half_head, -0.86, 1.09), "head"),
        ("ear.r", (-half_head, -0.86, 1.09), "head"),
        ("tail", (0, tail_y, 0.98), "body"),
        *([] if male else [("udder", (0, 0.3, 0.42), "body")]),
        ("tail.2", (0, tail_y + 0.05, 0.74), "tail"),
        *[(leg, (x, y, HIP_Z), "body") for leg, (x, y) in LEGS.items()],
        *[(leg + ".lower", (x, y, KNEE_Z), leg) for leg, (x, y) in LEGS.items()],
    ]
    cow.build(bones, LEGS, HIP_Z, KNEE_Z, BODY_PIVOT_Z, HOOF_RADIUS, leg_radius=0.16, tail=("tail", "tail.2"),
              shade_range=SHADE)
    if male:
        cow.rig.scale = (MALE_SCALE,) * 3

    # --- animations -----------------------------------------------------------------------

    def lying(p, front, back):
        """Lying down: forelegs tucked under the chest, hind legs folded alongside the belly. front
        and back weight each end separately, so it can kneel down front first."""
        for leg in ("leg.fl", "leg.fr"):
            p.fold(leg, -78, 158, 4, front)
        for leg in ("leg.bl", "leg.br"):
            p.fold(leg, -84, 166, 22, back)
        return p.add("udder", scale=(1.1, 1 - 0.6 * back, 1.1))

    def swish(p, t, cycles=1, amount=16):
        """The slow side-to-side tail swish cattle keep up to brush flies off."""
        return p.tail(wag=amount * wave(t, cycles), lift=-4)

    @cow.animation("Idle", 90)
    def idle(t):
        p = cow.pose()
        p.add("neck", rot=(1.2 * wave(t), 0, 0))
        p.add("head", rot=(1.5 * wave(t, 1, 0.25), 0, 0))
        p.ears(droop=12 * bump(t, 0.3, 0.05) + 8 * bump(t, 0.75, 0.04))
        p.chew(t, rate=6, amount=0.5)  # chewing the cud
        swish(p, t)
        return p.blink(bump(t, 0.6, 0.04))

    @cow.animation("Idle_LookAround", 120)
    def idle_look_around(t):
        look = 28 * (ramp(t, 0.1, 0.25) - ramp(t, 0.4, 0.55)) - 28 * (ramp(t, 0.6, 0.72) - ramp(t, 0.85, 0.97))
        p = cow.pose()
        p.add("neck", rot=(0, look * 0.45, 0))
        p.add("head", rot=(-3 * abs(look) / 28, look * 0.55, 0))
        p.ears(back=-10 * abs(look) / 28)
        swish(p, t)
        return p.blink(bump(t, 0.3, 0.03) + bump(t, 0.78, 0.03))

    @cow.animation("Walking", 40)
    def walking(t):
        p = kit.gait(cow.pose(), t, kit.WALK, stance=0.65, stride=0.36, lift=0.1)
        p.add("body", rot=(0, 2 * wave(t), 0))
        p.add("neck", rot=(3 * wave(t, 2), 0, 0))
        swish(p, t, amount=10)
        return p

    @cow.animation("Running", 20)
    def running(t):
        p = kit.gait(cow.pose(), t, kit.GALLOP, stance=0.4, stride=0.6, lift=0.16)
        p.add("body", rot=(6 * wave(t, 1, 0.25), 0, 0), loc=(0, -0.025 + 0.015 * wave(t, 1, 0.25), 0))
        p.add("neck", rot=(-6 * wave(t, 1, 0.25), 0, 0))
        p.tail(lift=25, wag=6 * wave(t))
        return p.ears(back=30)

    @cow.animation("Eating_Grass", 80)
    def eating_grass(t):
        p = cow.pose()
        # Lean in a little and reach forward and down with the neck so the mouth meets the grass.
        # Neck and head together tip the face about 70 degrees: muzzle down, forehead forward.
        p.add("body", rot=(6, 0, 0), loc=(0, -0.064, 0))
        p.add("neck", rot=(40 if male else 46, 0, 0), loc=(0, -0.155, 0.15))
        p.add("head", rot=(12 + 2 * wave(t, 1), 0, 0))
        p.chew(t, rate=5)
        p.ears(droop=10)
        swish(p, t)
        return p.blink(bump(t, 0.55, 0.04))

    @cow.animation("Moo", 60)
    def moo(t):
        w = envelope(t, 0.08, 0.25, 0.7, 0.92)
        p = cow.pose()
        p.add("neck", rot=(-14 * w, 0, 0))
        p.add("head", rot=(-16 * w, 0, 0))
        p.add("jaw", rot=(24 * w * (0.9 + 0.1 * wave(t, 5)), 0, 0))
        p.ears(back=18 * w)
        swish(p, t)
        return p.blink(0.4 * w)

    @cow.animation("Being_Fed", 90)
    def being_fed(t):
        reach = envelope(t, 0.0, 0.12, 0.85, 1.0)
        chewing = envelope(t, 0.15, 0.2, 0.8, 0.85)
        p = cow.pose()
        p.add("neck", rot=(-14 * reach, 0, 0))
        p.add("head", rot=(-6 * reach + 6 * bump(t, 0.16, 0.05), 0, 0))
        p.chew(t, rate=8, amount=1.3 * chewing)
        p.ears(back=-14 * reach)
        swish(p, t, cycles=3, amount=20 * chewing + 6)
        return p.blink(0.4 * envelope(t, 0.2, 0.3, 0.75, 0.85))

    @cow.animation("Getting_Love", 100)
    def getting_love(t):
        w = envelope(t, 0.05, 0.2, 0.8, 0.95)
        p = cow.pose()
        p.add("neck", rot=(6 * w, 10 * w, 0))
        p.add("head", rot=(4 * w, 0, 16 * w))
        p.ears(droop=22 * w)
        swish(p, t, cycles=3, amount=14 * w + 6)
        return p.blink(0.95 * w)

    @cow.animation("Lie_Down", 50)
    def lie_down(t):
        # Cattle go down front first (onto their knees), then the hind end settles.
        front, back = ramp(t, 0.0, 0.55), ramp(t, 0.35, 0.95)
        return swish(lying(cow.pose(), front, back), t, amount=8)

    @cow.animation("Lie_Idle", 90)
    def lie_idle(t):
        p = lying(cow.pose(), 1, 1)
        p.add("head", rot=(2 * wave(t, 1, 0.25), 5 * wave(t), 0))
        p.chew(t, rate=6, amount=0.6)
        p.ears(droop=10 * bump(t, 0.7, 0.05))
        swish(p, t, amount=8)
        return p.blink(bump(t, 0.4, 0.04))

    @cow.animation("Lie_StandUp", 50)
    def lie_stand_up(t):
        # And up hind end first.
        back, front = 1 - ramp(t, 0.0, 0.55), 1 - ramp(t, 0.35, 0.95)
        return swish(lying(cow.pose(), front, back), t, amount=8)

    @cow.animation("Sleeping", 120)
    def sleeping(t):
        p = lying(cow.pose(), 1, 1)
        p.add("neck", rot=(24, 0, 0))
        p.add("head", rot=(14 + 2 * wave(t), 0, 10))
        p.ears(droop=20)
        p.tail(wag=4 * wave(t))
        return p.blink(1.0)

    if male:
        @cow.animation("Paw_Ground", 60)
        def paw_ground(t):
            # Head lowered, horns forward, scraping the ground twice with the right forehoof.
            w = envelope(t, 0.0, 0.15, 0.85, 1.0)
            p = cow.pose()
            p.add("neck", rot=(22 * w, 0, 0))
            p.add("head", rot=(10 * w, 0, 0))
            # Two scrapes: lift the hoof and swing it forward, then drag it back along the ground
            # (eased in and out so the hoof starts and ends planted where it stands).
            scrape, ease = ((t - 0.15) / 0.35) % 1.0, envelope(t, 0.1, 0.2, 0.8, 0.9)
            if scrape < 0.4:
                q = scrape / 0.4
                p.foot("leg.fr", ease * (-0.1 + 0.26 * kit.smooth(q)), ease * 0.1 * math.sin(math.pi * q))
            else:
                p.foot("leg.fr", ease * (0.16 - 0.26 * (scrape - 0.4) / 0.6))
            p.ears(back=24 * w)
            p.tail(wag=10 * wave(t, 2), lift=10 * w)
            return p

    cow.finish()


for variant in ("Female", "Male"):
    build(variant)
