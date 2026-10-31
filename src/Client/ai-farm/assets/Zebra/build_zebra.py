"""Builds the KayKit-style horses and donkeys, which share a body plan:
- Horse (🐎 🐴): Horse_Female (a chestnut mare with a white blaze and socks) and Horse_Male (a
  bay stallion: bigger, with a heavier crested neck, black points and a star).
- Donkey (🫏): Donkey_Female (a grey jenny) and Donkey_Male (a bigger jack), with long ears, a
  short upright mane, a pale muzzle and belly, a tufted tail and the dark cross over the shoulders.
Each gets its model, palette texture, rig and animations, using the shared animal kit
(assets/AnimalKit/animal_kit.py; see it for how to run this and for the rig conventions). The
donkeys are written to assets/Donkey."""

import importlib
import math
import os
import sys

import bmesh
from mathutils import Matrix, Vector

KIT_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "AnimalKit")
if KIT_DIR not in sys.path:
    sys.path.insert(0, KIT_DIR)
import animal_kit as kit  # noqa: E402

importlib.reload(kit)  # pick up kit edits when re-run in the same Blender session

if "OUT_DIR" not in globals():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    OUT_DIR = argv[0] if argv else os.path.dirname(os.path.abspath(__file__))
ASSETS_DIR = os.path.dirname(os.path.abspath(OUT_DIR))

PALETTE = {
    "chestnut": ((206, 120, 64), (160, 82, 42)),
    "bay": ((150, 86, 50), (106, 56, 34)),
    "grey": ((176, 168, 160), (128, 120, 114)),
    "pale": ((246, 240, 230), (206, 198, 188)),
    "white": ((255, 255, 255), (220, 220, 226)),
    "black": ((62, 52, 50), (30, 26, 26)),
    "mane_red": ((170, 92, 50), (120, 60, 34)),
    "nose": ((92, 76, 76), (56, 46, 46)),
    "hoof": ((90, 80, 76), (50, 44, 42)),
    "eye": ((36, 30, 30), (14, 12, 12)),
    "ear_inner": ((120, 96, 90), (80, 64, 60)),
    "mouth": ((120, 40, 54), (70, 20, 32)),
}

wave, ramp, envelope, bump = kit.wave, kit.ramp, kit.envelope, kit.bump

# --------------------------------------------------------------------------------------
# Proportions: a long barrel high on long legs, a long neck, and a long head carried at an angle
# --------------------------------------------------------------------------------------

HIP_Z, KNEE_Z = 0.78, 0.41
BODY_PIVOT_Z = 0.96
LEGS = {"leg.fl": (0.19, -0.40), "leg.fr": (-0.19, -0.40), "leg.bl": (0.19, 0.40), "leg.br": (-0.19, 0.40)}
HOOF_RADIUS = 0.09
SHADE = (0.0, 1.9)
BODY_CENTER, BODY_SIZE = (0, 0, 0.98), (0.64, 1.14, 0.62)
HEAD_BUILT_AT = Vector((0, -0.8, 1.58))  # the head is modelled around here, level...
HEAD_PIVOT = Vector((0, -0.84, 1.56))  # ...then moved to the top of the neck (the head bone)
HEAD_TILT = 52  # degrees the head is tipped nose-down from level
MOUTH_Z = 1.5  # mouth line height in the head's own (level) frame

SPECIES = {
    "Zebra": {"ear": (0.035, 0.1), "mane_up": True, "scale": {"Female": 1.0, "Male": 1.08}},
    "Donkey": {"ear": (0.05, 0.19), "mane_up": True, "scale": {"Female": 0.82, "Male": 0.88}},
}


def head_frame():
    """Tips parts built level (nose pointing -Y) down about the head pivot into place."""
    return (Matrix.Translation(HEAD_PIVOT) @ Matrix.Rotation(math.radians(HEAD_TILT), 4, "X")
            @ Matrix.Diagonal((1.3, 1.2, 1.2, 1.0))
            @ Matrix.Translation(-HEAD_BUILT_AT))


def head_shape():
    """One continuous poll, cheek, nasal bridge and muzzle, before cutting the jaw.

    Joining two separately bevelled boxes only at their corners left a pinched
    bridge: in game the muzzle read as a second block dangling below the face.
    These shared cross-sections keep the whole closed face one connected shell.
    """
    sections = [
        # forward position, centre height, half width, half depth
        (-0.76, 1.640, 0.115, 0.100),
        (-0.82, 1.650, 0.120, 0.125),
        (-0.94, 1.625, 0.115, 0.105),
        (-1.00, 1.595, 0.105, 0.105),
        (-1.07, 1.565, 0.110, 0.100),
        (-1.14, 1.565, 0.105, 0.095),
        (-1.18, 1.565, 0.075, 0.065),
    ]
    bm = kit.sweep([(0, y, z) for y, z, _, _ in sections],
                   [w for _, _, w, _ in sections],
                   [d for _, _, _, d in sections], segments=12)
    # Fill out the underside of the jowl. A symmetric back ring put the
    # throat attachment up at the poll, leaving a deep V-shaped recess and a
    # dark flat wedge behind the jaw. Only the lower halves of the first two
    # rings move; the face, ears, upper poll and mane stay unchanged.
    bm.verts.ensure_lookup_table()
    to_level = head_frame().to_3x3().inverted()
    for row, displacement in enumerate(((0, -0.06, -0.13), (0, -0.06, -0.09))):
        delta = to_level @ Vector(displacement)
        for index in range(12):
            lower_half = max(0.0, -math.sin(math.tau * index / 12)) ** 2
            bm.verts[row * 12 + index].co += delta * lower_half
    throat_heights = [(head_frame() @ bm.verts[row * 12 + 9].co).z for row in range(3)]
    assert max(throat_heights) - min(throat_heights) < 0.035, "the throat must not step up into a deep recess behind the jaw"
    # Preserve the back ring through the jaw cut, so the neck can share its
    # actual vertices instead of overlapping another capped primitive.
    join = bm.verts.layers.int.new("poll_join")
    bm.verts.ensure_lookup_table()
    for index in range(12):
        bm.verts[index][join] = index + 1
    return bm


def join_neck(upper, to_head, neck_base):
    """Extend the skull's open back ring into one welded, smoothly skinned neck."""
    weights = upper.verts.layers.float.new("head_weight")
    join = upper.verts.layers.int["poll_join"]
    ring = sorted((v for v in upper.verts if v[join]), key=lambda v: v[join])
    assert len(ring) == 12
    caps = [f for f in upper.faces if all(v[join] for v in f.verts)]
    assert len(caps) == 1, "the poll must have exactly one back cap to remove"
    upper.faces.remove(caps[0])
    for vertex in upper.verts:
        vertex[weights] = 1.0
    sections = [
        # centre, half width, half depth, depth direction, head influence
        ((0, -0.18, 0.99), neck_base, 0.23, (0, 0.53, 0.848), 0.0),
        ((0, -0.43, 1.14), neck_base * 0.94, 0.205, (0, 0.65, 0.760), 0.0),
        ((0, -0.65, 1.36), 0.18, 0.17, (0, 0.74, 0.672), 0.15),
        ((0, -0.77, 1.54), 0.155, 0.14, (0, 0.20, 0.980), 0.65),
    ]
    rows = []
    for center, width, depth, normal, influence in sections:
        center, normal = Vector(center), Vector(normal).normalized()
        row = []
        for index in range(12):
            angle = math.tau * index / 12
            vertex = upper.verts.new(center + Vector((width * math.cos(angle), 0, 0))
                                     + normal * (depth * math.sin(angle)))
            vertex[weights] = influence
            row.append(vertex)
        rows.append(row)
    rows.append(ring)
    for lower, higher in zip(rows, rows[1:]):
        for index in range(12):
            next_index = (index + 1) % 12
            upper.faces.new((lower[index], lower[next_index], higher[next_index], higher[index]))
    upper.faces.new(tuple(reversed(rows[0])))
    kit.subdivided(upper, 2)
    # Round the poll itself, not the cheeks or mouth cut. This removes the
    # old hard nape wedge while preserving the face and matching lip boundary.
    from_head = to_head.inverted()
    poll = [v for v in upper.verts if v.co.z > 1.40 and (from_head @ v.co).y > -0.83]
    for _ in range(3):
        bmesh.ops.smooth_vert(upper, verts=poll, factor=0.35,
                             use_axis_x=True, use_axis_y=True, use_axis_z=True)
    # Round the lower throat/cheek transition separately. Keep the animated
    # mouth boundary and the eyes outside this relaxation region.
    throat = [v for v in upper.verts if 1.24 < v.co.z < 1.58
              and (from_head @ v.co).y > -0.94 and (from_head @ v.co).z < 1.60]
    for _ in range(4):
        bmesh.ops.smooth_vert(upper, verts=throat, factor=0.30,
                             use_axis_x=True, use_axis_y=True, use_axis_z=True)
    bmesh.ops.recalc_face_normals(upper, faces=list(upper.faces))
    assert all(e.is_manifold for e in upper.edges), "head and neck must be one closed surface"
    assert all(v[weights] > 0.999 for v in upper.verts
               if (from_head @ v.co).y < -1.02 and (from_head @ v.co).z > 1.45), "face must follow the head"
    return upper


def build(species, sex):
    spec = SPECIES[species]
    male = sex == "Male"
    donkey = species == "Donkey"
    to_head = head_frame()
    from_head = to_head.inverted()

    def coat(p):
        # Bands wrap around the lower legs; broad flank stripes follow body height.
        if p.z < .7:
            return "black" if math.sin(36*p.z + 2*p.y) > .25 else "white"
        return "black" if math.sin(25*p.y + 1.5*math.sin(p.z*7)) > .25 else "white"

    mane_colour = "black"
    neck_colour = "white"
    def body_coat(p):
        return "black" if math.sin(24*p.y + 1.8*math.sin(p.z*7)) > .25 else "white"

    horse = kit.Animal(f"{species}_{sex}", os.path.join(ASSETS_DIR, species), PALETTE)

    # --- body -----------------------------------------------------------------------------
    horse.part("body", body_coat, kit.subdivided(kit.body_loft(BODY_CENTER, BODY_SIZE), 2))

    # The neck is added below as part of the same welded surface as the skull.
    neck_base = 0.24 if male else 0.22

    # Mane along the top of the neck: flowing to one side on the horse, short and upright on the donkey.
    # A single crest with a cut silhouette, not a chain of spherical beads.
    mane_points = [(0.0 if donkey else 0.05, y, z) for y, z in
                   [(-0.25, 1.26), (-0.36, 1.40), (-0.52, 1.55), (-0.67, 1.66), (-0.79, 1.70)]]
    mane = kit.sweep(mane_points, [0.035 if donkey else 0.045] * len(mane_points),
                     [0.045, 0.060, 0.065, 0.045, 0.030] if not donkey else [0.035] * len(mane_points), segments=6)
    mane_weights = mane.verts.layers.float.new("head_weight")
    for vertex in mane.verts:
        vertex[mane_weights] = kit.smooth((vertex.co.z - 1.38) / 0.34)
    horse.part("neck", mane_colour, mane, blend=("head", "head_weight"))
    if not spec["mane_up"]:
        # A thin swept forelock follows the skull, rather than a floating oval
        # on the blaze that reads as a third eye in close-up.
        forelock = kit.sweep([(0, -0.795, 1.755), (0, -0.86, 1.770), (0.02, -0.925, 1.735)],
                            [0.055, 0.045, 0.002], [0.012, 0.014, 0.002], segments=6)
        forelock.transform(to_head)
        horse.part("head", mane_colour, forelock)

    # --- head: built level, then tipped nose-down into place ------------------------------
    def level(bm):
        bm.transform(to_head)
        return bm

    def face_coat(p):
        q = from_head @ p
        if q.y < -1.05: return "nose"
        return "black" if math.sin(34*q.y + 7*q.z) > .25 else "white"

    # The upper face and lower lip share the same cut boundary. The muzzle
    # never moves independently of the skull, even during chewing and neighing.
    skull = head_shape()

    def lips(p):
        q = from_head @ p
        if q.y < -0.95 and abs(q.z - MOUTH_Z) < 0.002:
            return "mouth"
        return face_coat(p) if q.y < -0.80 and q.z > 1.40 else body_coat(p)

    upper, chin = kit.split(skull, MOUTH_Z)
    upper = join_neck(level(upper), to_head, neck_base)
    # Ray-cast facial details onto the final smoothed skin, not the old skull.
    target = upper.copy()
    target.transform(from_head)
    head_target = kit.surface(target)
    target.free()
    horse.part("neck", lips, upper, blend=("head", "head_weight"))
    horse.part("jaw", lips, level(kit.subdivided(chin, 2)))
    for side in (1, -1):
        # Nostrils belong on the muzzle's flanks, not as pig-like dots on its
        # lower front face. Flatten them into the surface just like the eyes.
        nostril, nasal_normal, _, _ = head_target.ray_cast(Vector((0.5 * side, -1.145, 1.59)), Vector((-side, 0, 0)))
        assert nostril is not None, "nostril must sit on the muzzle"
        nostril_shape = kit.blob((0, 0, 0), (0.007, 0.020, 0.015))
        nasal_frame = Vector((side, 0, 0)).rotation_difference(nasal_normal).to_matrix().to_4x4()
        nostril_shape.transform(Matrix.Translation(nostril - nasal_normal * 0.002) @ nasal_frame)
        horse.part("head", "nose", level(nostril_shape))
        eye, normal, _, _ = head_target.ray_cast(Vector((0.6 * side, -0.87, 1.68)), Vector((-side, 0, 0)))
        assert eye is not None, "eye must sit on the continuous cheek"
        eye = to_head @ eye
        normal = (to_head.to_3x3().inverted().transposed() @ normal).normalized()
        up = (Vector((0, 0, 1)) - normal * normal.z).normalized()
        across = up.cross(normal).normalized()
        eye_frame = Matrix((normal, across, up)).transposed().to_4x4()
        # A pale surround keeps each black eye readable against dark stripes.
        eye_rim = kit.blob((0, 0, 0), (0.009, 0.034, 0.038), subdiv=3)
        eye_rim.transform(Matrix.Translation(eye - normal * 0.005) @ eye_frame)
        horse.part("eyes", "white", eye_rim)
        eye_shape = kit.blob((0, 0, 0), (0.010, 0.023, 0.028), subdiv=3)
        eye_shape.transform(Matrix.Translation(eye - normal * 0.004) @ eye_frame)
        horse.part("eyes", "eye", eye_shape)

    # Ears standing up from the back of the head (long on the donkey), rooted a little into it.
    def ear_base(side):
        return to_head @ Vector((0.075 * side, -0.815, 1.715))

    ear_width, ear_length = spec["ear"]
    for side, bone in ((1, "ear.l"), (-1, "ear.r")):
        base = ear_base(side)
        tip = base + Vector((0.03 * side, 0.02, ear_length))
        ear = kit.blob((base + tip) / 2, (ear_width, 0.02, ear_length / 2 + 0.01), subdiv=3)
        for v in ear.verts:  # pointed at the tip
            v.co.x = (base.x + tip.x) / 2 + (v.co.x - (base.x + tip.x) / 2) * (1 - 0.6 * max(0.0, (v.co.z - base.z) / ear_length))
        horse.part(bone, neck_colour, ear, SHADE)
        horse.part(bone, "ear_inner", kit.blob((base + tip) / 2 + Vector((0, -0.014, 0)),
                                               (ear_width * 0.6, 0.008, ear_length / 2 - 0.01)))

    # --- tail: long hair falling from the top of the rump (a tufted rope on the donkey) ----
    tail_root = Vector((0, BODY_SIZE[1] / 2 - 0.015, 1.13))
    tail_mid = tail_root + Vector((0, 0.1, -0.28))
    tail_end = tail_mid + Vector((0, 0.04, -0.4))
    if donkey:
        horse.part("tail", coat, kit.capsule(tail_root, tail_mid, 0.03, 0.025, 10, 3))
        horse.part("tail.2", coat, kit.capsule(tail_mid, tail_end + Vector((0, 0, 0.12)), 0.025, 0.02, 10, 3))
        horse.part("tail.2", "black", kit.blob(tail_end + Vector((0, 0, 0.08)), (0.05, 0.05, 0.1), subdiv=3))
    else:
        horse.part("tail", mane_colour, kit.capsule(tail_root, tail_mid, 0.05, 0.07, 12, 3))
        horse.part("tail.2", mane_colour, kit.capsule(tail_mid, tail_end, 0.07, 0.04, 12, 3))

    # --- legs: long and slender, with hooves --------------------------------------------
    for leg, (x, y) in LEGS.items():
        front = y < 0
        horse.part(leg, coat, kit.capsule((x, y, KNEE_Z), (x, y, HIP_Z), 0.075, 0.115, segments=12))
        radii = (0.13, 0.15, 0.17) if front else (0.14, 0.17, 0.19)
        horse.part(leg, coat, kit.blob((x, y + (0 if front else 0.02), HIP_Z + 0.08), radii, subdiv=3))
        horse.part(leg + ".lower", coat, kit.capsule((x, y, 0.1), (x, y, KNEE_Z), 0.06, 0.07, segments=12))
        horse.part(leg + ".lower", coat, kit.blob((x, y, 0.14), (0.055, 0.055, 0.05)))  # fetlock
        horse.part(leg + ".lower", "hoof", kit.cylinder((x, y, 0.0), HOOF_RADIUS, 0.1, segments=16))

    jaw_pivot = to_head @ Vector((0, -0.99, MOUTH_Z))
    bones = [
        ("root", (0, 0, 0), None),
        ("body", (0, 0, BODY_PIVOT_Z), "root"),
        ("neck", (0, -0.38, 1.04), "body"),
        ("head", tuple(HEAD_PIVOT), "neck"),
        ("jaw", tuple(jaw_pivot), "head"),
        ("eyes", tuple(to_head @ Vector((0, -0.87, 1.68))), "head"),
        ("ear.l", tuple(ear_base(1)), "head"),
        ("ear.r", tuple(ear_base(-1)), "head"),
        ("tail", tuple(tail_root), "body"),
        ("tail.2", tuple(tail_mid), "tail"),
        *[(leg, (x, y, HIP_Z), "body") for leg, (x, y) in LEGS.items()],
        *[(leg + ".lower", (x, y, KNEE_Z), leg) for leg, (x, y) in LEGS.items()],
    ]
    horse.build(bones, LEGS, HIP_Z, KNEE_Z, BODY_PIVOT_Z, HOOF_RADIUS, leg_radius=0.11, tail=("tail", "tail.2"),
                shade_range=SHADE)
    scale = spec["scale"][sex]
    horse.rig.scale = (scale,) * 3

    # --- animations -----------------------------------------------------------------------

    def lying(p, front, back):
        for leg in ("leg.fl", "leg.fr"):
            p.fold(leg, -80, 160, 4, front)
        for leg in ("leg.bl", "leg.br"):
            p.fold(leg, -84, 166, 22, back)
        return p.tail(lift=110 * max(front, back))  # clear the ground throughout the transition

    def swish(p, t, cycles=1, amount=14):
        return p.tail(wag=amount * wave(t, cycles), lift=-2)

    @horse.animation("Idle", 100)
    def idle(t):
        p = horse.pose()
        p.add("neck", rot=(1.5 * wave(t), 0, 0))
        p.add("head", rot=(2 * wave(t, 1, 0.25), 0, 0))
        p.ears(back=8 * bump(t, 0.3, 0.06), droop=6 * bump(t, 0.7, 0.05))
        swish(p, t)
        return p.blink(bump(t, 0.55, 0.03))

    @horse.animation("Idle_LookAround", 120)
    def idle_look_around(t):
        look = 30 * (ramp(t, 0.1, 0.25) - ramp(t, 0.4, 0.55)) - 30 * (ramp(t, 0.6, 0.72) - ramp(t, 0.85, 0.97))
        p = horse.pose()
        p.add("neck", rot=(-6 * abs(look) / 30, look * 0.5, 0))
        p.add("head", rot=(0, look * 0.5, 0))
        p.ears(back=-12 * abs(look) / 30)
        swish(p, t)
        return p.blink(bump(t, 0.3, 0.03) + bump(t, 0.8, 0.03))

    @horse.animation("Walking", 40)
    def walking(t):
        p = kit.gait(horse.pose(), t, kit.WALK, stance=0.62, stride=0.5, lift=0.14)
        p.add("neck", rot=(4 * wave(t, 2), 0, 0))
        p.add("body", rot=(0, 2 * wave(t), 0))
        return swish(p, t, amount=8)

    @horse.animation("Trotting", 24)
    def trotting(t):
        p = kit.gait(horse.pose(), t, kit.TROT, stance=0.5, stride=0.6, lift=0.2)
        p.add("body", loc=(0, -0.02, 0))
        p.add("neck", rot=(-4, 0, 0))
        p.ears(back=-6)
        return p.tail(lift=14, wag=6 * wave(t, 2))

    @horse.animation("Running", 20)
    def running(t):
        p = kit.gait(horse.pose(), t, kit.GALLOP, stance=0.38, stride=0.9, lift=0.24)
        p.add("body", rot=(6 * wave(t, 1, 0.25), 0, 0), loc=(0, -0.04 + 0.02 * wave(t, 1, 0.25), 0))
        p.add("neck", rot=(-10 * wave(t, 1, 0.25) + 6, 0, 0))
        p.tail(lift=30, wag=6 * wave(t))
        return p.ears(back=28)

    @horse.animation("Eating_Grass", 80)
    def eating_grass(t):
        p = horse.pose()
        p.add("body", rot=(8, 0, 0), loc=(0, -0.08, 0))
        # The larger head reaches the grass with a natural downward neck,
        # rather than folding back underneath the chest.
        p.add("neck", rot=(62, 0, 0), loc=(0, -0.1, 0.08))
        p.add("head", rot=(-26 + 3 * wave(t, 1), 0, 0))
        p.chew(t, rate=6)
        p.ears(droop=10)
        swish(p, t)
        return p.blink(bump(t, 0.55, 0.04))

    @horse.animation("Bray" if donkey else "Neigh", 60)
    def call(t):
        w = envelope(t, 0.06, 0.2, 0.7, 0.92)
        p = horse.pose()
        p.add("neck", rot=(-16 * w, 0, 0))
        p.add("head", rot=(-26 * w, 0, 0))
        p.add("jaw", rot=(26 * w * (0.8 + 0.2 * wave(t, 5 if donkey else 9)), 0, 0))
        p.ears(back=(-10 if donkey else 20) * w)
        swish(p, t, cycles=2, amount=10)
        return p.blink(0.4 * w)

    @horse.animation("Being_Fed", 90)
    def being_fed(t):
        reach = envelope(t, 0.0, 0.12, 0.85, 1.0)
        chewing = envelope(t, 0.15, 0.2, 0.8, 0.85)
        p = horse.pose()
        p.add("neck", rot=(12 * reach, 0, 0))
        p.add("head", rot=(-20 * reach + 6 * bump(t, 0.16, 0.05), 0, 0))
        p.chew(t, rate=7, amount=1.3 * chewing)
        p.ears(back=-14 * reach)
        swish(p, t, cycles=2)
        return p.blink(0.4 * envelope(t, 0.2, 0.3, 0.75, 0.85))

    @horse.animation("Getting_Love", 100)
    def getting_love(t):
        w = envelope(t, 0.05, 0.2, 0.8, 0.95)
        p = horse.pose()
        p.add("neck", rot=(8 * w, 10 * w, 0))
        p.add("head", rot=(4 * w, 0, 14 * w))
        p.ears(droop=20 * w)
        swish(p, t, cycles=2, amount=10 * w + 4)
        return p.blink(0.9 * w)

    @horse.animation("Lie_Down", 60)
    def lie_down(t):
        # Front end first, the hind end close behind so the long body never tips far.
        front, back = ramp(t, 0.0, 0.7), ramp(t, 0.15, 0.85)
        return swish(lying(horse.pose(), front, back), t, amount=6)

    @horse.animation("Lie_Idle", 100)
    def lie_idle(t):
        p = lying(horse.pose(), 1, 1)
        p.add("neck", rot=(-10, 0, 0))
        p.add("head", rot=(2 * wave(t, 1, 0.25), 5 * wave(t), 0))
        p.ears(droop=8 * bump(t, 0.7, 0.05))
        return p.blink(bump(t, 0.4, 0.04))

    @horse.animation("Lie_StandUp", 50)
    def lie_stand_up(t):
        # Horses push up with the front legs first, then the hind end.
        front, back = 1 - ramp(t, 0.0, 0.7), 1 - ramp(t, 0.15, 0.85)
        return swish(lying(horse.pose(), front, back), t, amount=6)

    @horse.animation("Sleeping", 120)
    def sleeping(t):
        # Dozing on its feet: head low, eyes shut, one hind hoof cocked up on its toe.
        p = horse.pose()
        p.add("neck", rot=(24, 0, 0))
        p.add("head", rot=(8 + 2 * wave(t), 0, 0))
        p.foot("leg.br", -0.04, 0.06)
        p.ears(droop=24)
        return p.blink(1.0)

    if male and not donkey:
        @horse.animation("Rear_Up", 60)
        def rear_up(t):
            # Up on the hind legs, forelegs tucked and pawing, then back down.
            w = envelope(t, 0.05, 0.35, 0.65, 0.95)
            p = horse.pose()
            p.add("body", rot=(-38 * w, 0, 0), loc=(0, 0.14 * w, -0.1 * w))
            for leg, phase in (("leg.fl", 0.0), ("leg.fr", 0.25)):
                p.fold(leg, -60 * w + 12 * w * wave(t, 3, phase), 110 * w, 0, 1.0)
            for leg in ("leg.bl", "leg.br"):
                p.foot(leg, 0.1 * w)
            p.add("neck", rot=(-20 * w, 0, 0))
            p.add("head", rot=(-14 * w, 0, 0))
            p.add("jaw", rot=(14 * envelope(t, 0.3, 0.4, 0.6, 0.7), 0, 0))
            p.tail(lift=-20 * w)
            return p.ears(back=30 * w)

    horse.finish()


for species in ("Zebra",):
    for variant in ("Female", "Male"):
        build(species, variant)
