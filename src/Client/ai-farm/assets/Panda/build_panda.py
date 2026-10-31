"""Build a pair of black-and-white pandas with textured rigs and animation."""

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
    "pink": ((239, 235, 222), (193, 191, 184)),
    "snout": ((231, 226, 213), (189, 184, 178)),
    "nostril": ((68, 67, 67), (38, 37, 38)),
    "hoof": ((61, 61, 64), (30, 30, 33)),
    "eye": ((36, 32, 38), (16, 14, 18)),
    "ear_inner": ((67, 65, 66), (38, 36, 38)),
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
BODY_CENTER, BODY_SIZE = (0, 0, 0.52), (0.72, 1.05, 0.67)
EYE_Z = 0.64
MOUTH_Z = 0.47
MALE_SCALE = 1.12


def build(sex):
    male = sex == "Male"
    pig = kit.Animal(f"Panda_{sex}", OUT_DIR, PALETTE)

    # --- body ---------------------------------------------------------------------------
    size = (BODY_SIZE[0] + 0.04, BODY_SIZE[1], BODY_SIZE[2] + 0.03) if male else BODY_SIZE
    pig.part("body", lambda p: "hoof" if -.30 < p.y < -.05 else "pink", kit.subdivided(kit.body_loft(BODY_CENTER, size, shoulder=0.09), 3), SHADE)
    # --- head: a round head straight onto the shoulders, the snout out front ---------------
    pig.part("neck", "hoof", kit.blob((0, -0.42, 0.55), (0.25, 0.2, 0.23), subdiv=3), SHADE)

    def head_shape():
        return kit.blob((0, -0.58, 0.61), (0.25, 0.22, 0.23), subdiv=3)

    head_target = kit.surface(head_shape())
    pig.part("head", "pink", head_shape(), SHADE)

    # Rounded muzzle with a dark nose and small rounded ears.
    pig.part("head", "snout", kit.blob((0,-.76,.52),(.17,.18,.13),subdiv=3), SHADE)
    pig.part("head", "nostril", kit.blob((0,-.93,.56),(.06,.022,.04),subdiv=2),SHADE)
    pig.part("jaw", "snout", kit.blob((0,-.75,.44),(.12,.13,.035),subdiv=2),SHADE)
    for side in (-1,1):
        eye = kit.hit(head_target,(.6*side,-.73,EYE_Z),(-side,0,0))
        assert eye is not None
        pig.part("head", "hoof", kit.blob(eye,(.026,.075,.075),subdiv=2))
        pig.part("eyes", "snout", kit.blob(eye + Vector((.024*side,0,0)),(.008,.010,.010),subdiv=2))
        bone = "ear.l" if side == 1 else "ear.r"
        pig.part(bone,"hoof",kit.blob((.16*side,-.52,.80),(.095,.05,.10),subdiv=2),SHADE)
        pig.part(bone,"ear_inner",kit.blob((.16*side,-.56,.80),(.06,.015,.06),subdiv=2),SHADE)
    tail_root = Vector((0,.48,.61))
    curl = [tail_root+Vector((0,.012*i,0)) for i in range(7)]
    pig.part("tail","pink",kit.blob((0,.51,.61),(.07,.07,.07),subdiv=2),SHADE)

    # --- legs ---------------------------------------------------------------------------
    for leg, (x, y) in LEGS.items():
        front = y < 0
        pig.part(leg, "hoof", kit.capsule((x, y, KNEE_Z), (x, y, HIP_Z), 0.07, 0.085), SHADE)
        radii = (0.09, 0.1, 0.09) if front else (0.1, 0.12, 0.1)
        pig.part(leg, "hoof", kit.blob((x, y, HIP_Z + 0.07), radii, subdiv=3), SHADE)
        pig.part(leg + ".lower", "hoof", kit.capsule((x, y, 0.06), (x, y, KNEE_Z), 0.06, 0.066), SHADE)
        pig.part(leg + ".lower", "hoof", kit.cylinder((x,y,0.0), HOOF_RADIUS, 0.065, segments=16))

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
    pig.rig.scale = ((1.32 if male else 1.20),) * 3

    # --- animations -----------------------------------------------------------------------

    def lying(p, front, back):
        """Lying down: forelegs tucked under the chest, hind legs folded alongside the belly."""
        for leg in ("leg.fl", "leg.fr"):
            p.fold(leg, -78, 158, 4, front)
        for leg in ("leg.bl", "leg.br"):
            p.fold(leg, -84, 166, 22, back)
        p.add("body", loc=(0, .06 * min(front, back), 0))
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
        p.add("body", rot=(0, 3 * wave(t), 2 * wave(t, 2)), loc=(0, .03, 0))
        p.add("head", rot=(3 * wave(t, 2), 0, 0))
        p.ears(droop=6 * wave(t, 2))
        return wiggle(p, t, cycles=1, amount=10)

    @pig.animation("Running", 16)
    def running(t):
        p = kit.gait(pig.pose(), t, kit.TROT, stance=0.45, stride=0.34, lift=0.1)
        p.add("body", rot=(4 * wave(t, 2), 0, 0), loc=(0, 0.04, 0))
        p.add("head", rot=(-5 * wave(t, 2), 0, 0))
        p.ears(back=25, droop=8 * wave(t, 2))
        return wiggle(p, t, cycles=2, amount=24)

    @pig.animation("Eating_Grass", 80)
    def eating_grass(t):
        # Rooting: snout down at the ground, nudging and snuffling forward.
        p = pig.pose()
        p.add("body", rot=(5, 0, 0), loc=(0, -0.03, 0))
        p.add("neck", rot=(24, 0, 0), loc=(0, 0.02, 0.05))
        p.add("head", rot=(22 + 4 * wave(t, 3), 6 * wave(t, 1), 0))
        p.chew(t, rate=6, amount=0.8)
        p.ears(droop=10)
        wiggle(p, t, cycles=3, amount=18)
        return p.blink(bump(t, 0.5, 0.04))

    @pig.animation("Growl", 40)
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
