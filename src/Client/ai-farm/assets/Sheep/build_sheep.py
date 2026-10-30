"""Builds the KayKit-style sheep: model, palette texture, rig and animations.

Run with Blender 5.x from anywhere:
    blender -b --python build_sheep.py -- <output_dir> [<preview_dir>]

Writes Sheep.blend, Sheep.glb and sheep_texture.png to <output_dir>. If a preview
directory is given it also renders a few poses there for a quick visual check.

The sheep faces -Y in Blender, which becomes +Z (towards the camera) in Godot, matching
KayKit characters. Every bone is vertical with no roll, so in pose space:
    rotation X = pitch (positive tips the front of the part down)
    rotation Y = yaw around the vertical axis (positive turns towards the sheep's left)
    rotation Z = roll
    location Y = up/down

Legs are two bones (upper leg, then lower leg + hoof) and are never keyed by hand: each
pose says where every hoof should be, and a two-bone IK solve turns that into joint angles.
Hooves therefore stay planted while the body bobs, squats or lies down, and walk cycles
lift and place them instead of sliding them along the ground.
"""

import math
import os
import random
import sys

import bmesh
import bpy
from mathutils import Euler, Matrix, Vector
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT_DIR = os.path.abspath(argv[0] if argv else os.path.dirname(os.path.abspath(__file__)))
PREVIEW_DIR = os.path.abspath(argv[1]) if len(argv) > 1 else None
os.makedirs(OUT_DIR, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.context.preferences.filepaths.save_version = 0  # no Sheep.blend1 backups
scene = bpy.context.scene
scene.render.fps = 30

# --------------------------------------------------------------------------------------
# Palette texture: an 8x8 grid of vertical gradients, light at the top of each cell,
# the same way KayKit textures are laid out.
# --------------------------------------------------------------------------------------

GRID = 8
TEXTURE_SIZE = 256
PALETTE = {
    # name: (cell index, top colour, bottom colour)
    "wool": (0, (255, 255, 255), (208, 214, 228)),
    "face": (1, (252, 238, 222), (220, 188, 164)),
    "leg": (2, (98, 90, 98), (56, 50, 58)),
    "hoof": (3, (60, 52, 56), (34, 30, 34)),
    "eye": (4, (36, 32, 38), (16, 14, 18)),
    "nose": (5, (244, 160, 170), (206, 112, 128)),
    "ear_inner": (6, (250, 188, 192), (224, 146, 156)),
    "poop": (7, (134, 90, 54), (86, 56, 34)),
}


def make_texture():
    by_index = {index: (top, bottom) for index, top, bottom in PALETTE.values()}
    cell = TEXTURE_SIZE // GRID
    pixels = [0.0] * (TEXTURE_SIZE * TEXTURE_SIZE * 4)
    for y in range(TEXTURE_SIZE):
        from_top = TEXTURE_SIZE - 1 - y
        t = (from_top % cell) / (cell - 1)
        for x in range(TEXTURE_SIZE):
            index = (from_top // cell) * GRID + x // cell
            top, bottom = by_index.get(index, ((255, 255, 255), (255, 255, 255)))
            offset = (y * TEXTURE_SIZE + x) * 4
            for c in range(3):
                pixels[offset + c] = (top[c] + (bottom[c] - top[c]) * t) / 255.0
            pixels[offset + 3] = 1.0

    image = bpy.data.images.new("sheep_texture", TEXTURE_SIZE, TEXTURE_SIZE, alpha=False)
    image.pixels = pixels
    image.filepath_raw = os.path.join(OUT_DIR, "sheep_texture.png")
    image.file_format = "PNG"
    image.save()
    return image


def cell_uv(index, t):
    """UV inside a palette cell; t runs 0 (top/light) to 1 (bottom/dark)."""
    col, row = index % GRID, index // GRID
    margin = 0.15 / GRID
    v_top = 1.0 - row / GRID - margin
    v_bottom = 1.0 - (row + 1) / GRID + margin
    return ((col + 0.5) / GRID, v_top - t * (v_top - v_bottom))


# --------------------------------------------------------------------------------------
# Geometry. Every part is built in its own bmesh, coloured through the palette with a
# top-to-bottom gradient, then appended to one mesh and skinned 100% to a single bone.
# --------------------------------------------------------------------------------------

final = bmesh.new()
scratch_mesh = bpy.data.meshes.new("scratch")
parts = []  # (bone, first vertex, end vertex)


def transform(center, rot=(0, 0, 0), scale=(1, 1, 1)):
    return (
        Matrix.Translation(center)
        @ Euler([math.radians(a) for a in rot]).to_matrix().to_4x4()
        @ Matrix.Diagonal((*scale, 1.0))
    )


def blob(center, radii, subdiv=2, rot=(0, 0, 0)):
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=subdiv, radius=1.0)
    bm.transform(transform(center, rot, radii))
    return bm


def rounded_box(center, size, bevel, segments=3, rot=(0, 0, 0)):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bm.transform(Matrix.Diagonal((*size, 1.0)))
    bmesh.ops.bevel(bm, geom=list(bm.edges), offset=bevel, segments=segments,
                    profile=0.5, affect="EDGES", clamp_overlap=True)
    bm.transform(transform(center, rot))
    return bm


def cylinder(bottom_center, radius, height, segments=10, bevel=0.015):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segments,
                          radius1=radius, radius2=radius, depth=height)
    bmesh.ops.bevel(bm, geom=[e for e in bm.edges if not e.is_manifold or e.calc_face_angle(0) > 0.5],
                    offset=bevel, segments=2, profile=0.5, affect="EDGES", clamp_overlap=True)
    bm.transform(Matrix.Translation(Vector(bottom_center) + Vector((0, 0, height / 2))))
    return bm


def add_part(bone, colour, bm, z_range=None):
    """Colours a part from the palette and appends it to the sheep mesh."""
    uv = bm.loops.layers.uv.verify()
    if z_range is None:
        zs = [v.co.z for v in bm.verts]
        z_range = (min(zs), max(zs))
    z_min, z_max = z_range
    height = max(z_max - z_min, 1e-6)
    index = PALETTE[colour][0]
    for face in bm.faces:
        face.smooth = True
        for loop in face.loops:
            t = min(max((z_max - loop.vert.co.z) / height, 0.0), 1.0)
            loop[uv].uv = cell_uv(index, t)

    bm.to_mesh(scratch_mesh)
    bm.free()
    start = len(final.verts)
    final.from_mesh(scratch_mesh)
    final.verts.ensure_lookup_table()
    parts.append((bone, start, len(final.verts)))


WOOL_BODY_RANGE = (0.22, 1.0)

# Body: a soft core covered in a dense coat of small wool balls, spread evenly over the
# surface (a Fibonacci spiral) with random size and height so it clumps like fleece.
BODY_CENTER, BODY_RADII = Vector((0, 0.02, 0.62)), Vector((0.36, 0.48, 0.30))
add_part("body", "wool", blob(BODY_CENTER, BODY_RADII), WOOL_BODY_RANGE)
wool_rng = random.Random(7)
WOOL_BALLS = 130
for i in range(WOOL_BALLS):
    z = 1 - 2 * (i + 0.5) / WOOL_BALLS
    ring = math.sqrt(1 - z * z)
    angle = i * math.pi * (3 - math.sqrt(5))
    direction = Vector((ring * math.cos(angle), ring * math.sin(angle), z))
    if direction.z < -0.8:
        continue  # leave the underside between the legs bare
    point = BODY_CENTER + Vector(d * r for d, r in zip(direction, BODY_RADII))
    point += direction * wool_rng.uniform(-0.02, 0.04)
    r = wool_rng.uniform(0.09, 0.14)
    add_part("body", "wool", blob(point, (r, r, r * 0.9)), WOOL_BODY_RANGE)

# Neck wool: hidden inside the body and head at rest, it fills the gap when the neck
# stretches forward and down (grazing).
for center, r in [((0, -0.50, 0.70), 0.12), ((0, -0.53, 0.60), 0.11), ((0.07, -0.50, 0.65), 0.10),
                  ((-0.07, -0.50, 0.65), 0.10), ((0, -0.56, 0.68), 0.10)]:
    add_part("neck", "wool", blob(center, (r, r, r * 0.9)), WOOL_BODY_RANGE)

# Tail: a little cluster of wool balls.
for x, y, z, r in [(0, 0.57, 0.74, 0.075), (0.04, 0.6, 0.7, 0.06), (-0.04, 0.6, 0.7, 0.06),
                   (0, 0.62, 0.78, 0.055), (0, 0.55, 0.68, 0.06)]:
    add_part("tail", "wool", blob((x, y, z), (r, r, r)), WOOL_BODY_RANGE)

# Head and snout. Their surfaces are kept for ray casts, so the eyes and nose can be set into
# the face instead of floating in front of the rounded corners.
FACE_RANGE = (0.66, 1.04)
head_bm = rounded_box((0, -0.62, 0.85), (0.30, 0.34, 0.32), 0.10)
snout_bm = rounded_box((0, -0.775, 0.79), (0.24, 0.16, 0.18), 0.07)
face_surfaces = [BVHTree.FromBMesh(head_bm), BVHTree.FromBMesh(snout_bm)]
add_part("head", "face", head_bm, FACE_RANGE)
add_part("head", "face", snout_bm, FACE_RANGE)
add_part("jaw", "face", rounded_box((0, -0.765, 0.712), (0.16, 0.12, 0.05), 0.02, segments=2), FACE_RANGE)


def face_point(x, z):
    """Where a ray fired straight back from in front of the sheep first hits the face."""
    hits = [tree.ray_cast(Vector((x, -2.0, z)), Vector((0, 1, 0))) for tree in face_surfaces]
    return min((hit[0] for hit in hits if hit[0] is not None), key=lambda p: p.y)


# Nose, set into the front of the snout.
nose = face_point(0, 0.83)
add_part("head", "nose", blob(nose + Vector((0, 0.008, 0)), (0.055, 0.022, 0.032)))

# Wool tuft on top of the head.
for x, y, z, r in [(0, -0.60, 1.03, 0.07), (0.07, -0.55, 1.01, 0.06), (-0.07, -0.55, 1.01, 0.06),
                   (0.05, -0.66, 1.0, 0.055), (-0.05, -0.66, 1.0, 0.055), (0, -0.70, 0.99, 0.05),
                   (0, -0.52, 1.03, 0.065), (0.1, -0.6, 0.98, 0.05), (-0.1, -0.6, 0.98, 0.05),
                   (0.06, -0.5, 0.99, 0.05), (-0.06, -0.5, 0.99, 0.05)]:
    add_part("head", "wool", blob((x, y, z), (r, r, r * 0.9)), (0.9, 1.14))

# Plain black eyes (KayKit style, no highlight), half sunk into the face; both sit on the
# "eyes" bone so they blink together.
for side in (1, -1):
    surface = face_point(0.085 * side, 0.9)
    add_part("eyes", "eye", blob(surface + Vector((0, 0.006, 0)), (0.038, 0.02, 0.048)))

# Floppy ears, drooping slightly outwards.
for side, bone in ((1, "ear.l"), (-1, "ear.r")):
    add_part(bone, "face", blob((0.20 * side, -0.60, 0.95), (0.12, 0.04, 0.055), rot=(0, 20 * side, 0)))
    add_part(bone, "ear_inner", blob((0.20 * side, -0.625, 0.95), (0.08, 0.015, 0.03), rot=(0, 20 * side, 0)))

# Stubby two-part legs. The sheep faces -Y, so its left side is +X.
HIP_Z, KNEE_Z = 0.42, 0.22
UPPER_LEG, LOWER_LEG = HIP_Z - KNEE_Z, KNEE_Z  # lower leg runs from the knee to the hoof's sole
LEGS = {"leg.fl": (0.19, -0.30), "leg.fr": (-0.19, -0.30), "leg.bl": (0.19, 0.30), "leg.br": (-0.19, 0.30)}
for bone, (x, y) in LEGS.items():
    add_part(bone, "leg", cylinder((x, y, KNEE_Z - 0.02), 0.075, HIP_Z - KNEE_Z + 0.04))
    add_part(bone + ".lower", "leg", blob((x, y, KNEE_Z), (0.072, 0.072, 0.06), subdiv=1))
    add_part(bone + ".lower", "leg", cylinder((x, y, 0.07), 0.065, KNEE_Z - 0.07))
    add_part(bone + ".lower", "hoof", cylinder((x, y, 0.0), 0.08, 0.08))

# Droppings, hidden (scaled to nothing) except during the Pooping animation.
for x, y, z, r in [(0, 0.50, 0.30, 0.045), (0.035, 0.53, 0.28, 0.04), (-0.03, 0.52, 0.27, 0.04)]:
    add_part("poop", "poop", blob((x, y, z), (r, r, r * 0.85), subdiv=1))

# --------------------------------------------------------------------------------------
# Mesh object and material
# --------------------------------------------------------------------------------------

mesh = bpy.data.meshes.new("Sheep")
final.to_mesh(mesh)
final.free()
bpy.data.meshes.remove(scratch_mesh)
sheep = bpy.data.objects.new("Sheep", mesh)
scene.collection.objects.link(sheep)

texture = make_texture()
material = bpy.data.materials.new("sheep")
material.use_nodes = True
nodes = material.node_tree.nodes
bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
bsdf.inputs["Roughness"].default_value = 0.85
tex_node = nodes.new("ShaderNodeTexImage")
tex_node.image = texture
material.node_tree.links.new(tex_node.outputs["Color"], bsdf.inputs["Base Color"])
mesh.materials.append(material)

# --------------------------------------------------------------------------------------
# Rig: vertical bones with no roll, placed at each part's pivot.
# --------------------------------------------------------------------------------------

BONES = [
    # name, pivot, parent
    ("root", (0, 0, 0), None),
    ("body", (0, 0, 0.55), "root"),
    ("neck", (0, -0.45, 0.72), "body"),
    ("head", (0, -0.55, 0.82), "neck"),
    ("jaw", (0, -0.70, 0.73), "head"),
    ("eyes", (0, -0.78, 0.90), "head"),
    ("ear.l", (0.13, -0.60, 0.95), "head"),
    ("ear.r", (-0.13, -0.60, 0.95), "head"),
    ("tail", (0, 0.50, 0.72), "body"),
    *[(leg, (x, y, HIP_Z), "body") for leg, (x, y) in LEGS.items()],
    *[(leg + ".lower", (x, y, KNEE_Z), leg) for leg, (x, y) in LEGS.items()],
    ("poop", (0, 0.50, 0.30), "body"),
]

armature_data = bpy.data.armatures.new("Sheep_Rig")
rig = bpy.data.objects.new("Sheep_Rig", armature_data)
scene.collection.objects.link(rig)
bpy.context.view_layer.objects.active = rig
bpy.ops.object.mode_set(mode="EDIT")
for name, pivot, parent in BONES:
    bone = armature_data.edit_bones.new(name)
    bone.head = pivot
    bone.tail = Vector(pivot) + Vector((0, 0, 0.1))
    bone.roll = 0.0
    if parent:
        bone.parent = armature_data.edit_bones[parent]
bpy.ops.object.mode_set(mode="OBJECT")

for bone, start, end in parts:
    group = sheep.vertex_groups.get(bone) or sheep.vertex_groups.new(name=bone)
    group.add(list(range(start, end)), 1.0, "REPLACE")
sheep.parent = rig
sheep.modifiers.new("Armature", "ARMATURE").object = rig

for pose_bone in rig.pose.bones:
    pose_bone.rotation_mode = "XYZ"

# --------------------------------------------------------------------------------------
# Animation helpers. Each animation is a function of normalised time t in [0, 1] that
# returns a Pose; it's sampled every couple of frames. Looping animations use periodic
# functions so the last frame matches the first.
# --------------------------------------------------------------------------------------

TAU = 2 * math.pi


def wave(t, cycles=1.0, phase=0.0):
    return math.sin(TAU * (cycles * t + phase))


def smooth(x):
    x = min(max(x, 0.0), 1.0)
    return x * x * (3 - 2 * x)


def ramp(t, start, end):
    return smooth((t - start) / (end - start))


def envelope(t, in_start, in_end, out_start, out_end):
    return ramp(t, in_start, in_end) * (1 - ramp(t, out_start, out_end))


def bump(t, at, width):
    return max(0.0, 1 - abs(t - at) / width)


class Pose:
    def __init__(self):
        self.bones = {}
        # Where each hoof is: (forward offset from under its hip, height off the ground).
        self.feet = {leg: (0.0, 0.0) for leg in LEGS}
        # The droppings stay hidden unless an animation shows them.
        self.add("poop", scale=(0.001, 0.001, 0.001))

    def foot(self, leg, forward, lift=0.0):
        self.feet[leg] = (forward, lift)
        return self

    def _entry(self, bone):
        return self.bones.setdefault(bone, [[0.0, 0.0, 0.0], [0.0, 0.0, 0.0], [1.0, 1.0, 1.0]])

    def add(self, bone, rot=None, loc=None, scale=None):
        entry = self._entry(bone)
        for i in range(3):
            if rot:
                entry[0][i] += rot[i]
            if loc:
                entry[1][i] += loc[i]
            if scale:
                entry[2][i] *= scale[i]
        return self

    def blink(self, amount):
        """amount 0 = open, 1 = closed (scales the eyes flat vertically)."""
        return self.add("eyes", scale=(1, 1 - 0.88 * min(max(amount, 0), 1), 1))

    def lying(self, w):
        """Blend towards lying down; the planted hooves make the legs fold underneath."""
        return self.add("body", loc=(0, -0.24 * w, 0))

    def ears(self, droop=0.0, back=0.0):
        self.add("ear.l", rot=(0, back, -droop))
        self.add("ear.r", rot=(0, -back, droop))
        return self


def gait(pose, t, phases, stance, stride, lift):
    """Moves each hoof through a step: planted and sliding back under the body for the
    stance part of the cycle, then lifted in an arc and swung forward."""
    for leg, offset in phases.items():
        p = (t + offset) % 1.0
        if p < stance:
            pose.foot(leg, stride / 2 - stride * p / stance)
        else:
            q = (p - stance) / (1 - stance)
            pose.foot(leg, -stride / 2 + stride * smooth(q), lift * math.sin(math.pi * q))
    return pose


BODY_PIVOT_Z = 0.55


def solve_legs(pose):
    """Two-bone IK: turns each hoof target into upper/lower leg angles, accounting for how the
    body's height and pitch move the hips. Front knees bend forward, hind hocks backward."""
    body_rot, body_loc, _ = pose.bones.get("body", ((0, 0, 0), (0, 0, 0), (1, 1, 1)))
    pitch = math.radians(body_rot[0])
    for leg, (x, rest_y) in LEGS.items():
        # Hip position in world (y, z) after the body bone's pitch and lift.
        rel_y, rel_z = rest_y, HIP_Z - BODY_PIVOT_Z
        hip_y = rel_y * math.cos(pitch) - rel_z * math.sin(pitch)
        hip_z = BODY_PIVOT_Z + body_loc[1] + rel_y * math.sin(pitch) + rel_z * math.cos(pitch)

        forward, lift = pose.feet[leg]
        foot_y, foot_z = rest_y - forward, lift
        reach_forward, reach_down = -(foot_y - hip_y), hip_z - foot_z

        d = math.hypot(reach_forward, reach_down)
        d = min(max(d, abs(UPPER_LEG - LOWER_LEG) + 1e-4), UPPER_LEG + LOWER_LEG - 1e-4)
        bend = math.pi - math.acos((UPPER_LEG ** 2 + LOWER_LEG ** 2 - d ** 2) / (2 * UPPER_LEG * LOWER_LEG))
        spread = math.acos((UPPER_LEG ** 2 + d ** 2 - LOWER_LEG ** 2) / (2 * UPPER_LEG * d))
        aim = math.atan2(-reach_forward, reach_down)

        front = rest_y < 0
        upper = aim - spread if front else aim + spread
        lower = bend if front else -bend
        # Legs are children of the body, so remove its pitch to get local angles.
        pose.add(leg, rot=(math.degrees(upper - pitch), 0, 0))
        pose.add(leg + ".lower", rot=(math.degrees(lower), 0, 0))


def key_pose(frame, pose):
    solve_legs(pose)
    for pose_bone in rig.pose.bones:
        rot, loc, scale = pose.bones.get(pose_bone.name, ((0, 0, 0), (0, 0, 0), (1, 1, 1)))
        pose_bone.rotation_euler = [math.radians(a) for a in rot]
        pose_bone.location = loc
        pose_bone.scale = scale
        pose_bone.keyframe_insert("rotation_euler", frame=frame)
        pose_bone.keyframe_insert("location", frame=frame)
        pose_bone.keyframe_insert("scale", frame=frame)


ACTIONS = []


def animation(name, frames):
    def register(pose_fn):
        action = bpy.data.actions.new(name)
        action.use_fake_user = True
        rig.animation_data_create()
        rig.animation_data.action = action
        for frame in list(range(0, frames, 2)) + [frames]:
            key_pose(frame, pose_fn(frame / frames))
        ACTIONS.append(action)
        return pose_fn
    return register


# --------------------------------------------------------------------------------------
# Animations
# --------------------------------------------------------------------------------------

@animation("Idle", 60)
def idle(t):
    p = Pose()
    p.add("body", loc=(0, 0.008 * wave(t), 0))
    p.add("head", rot=(2 * wave(t, 1, 0.25), 0, 0))
    p.add("tail", rot=(0, 6 * wave(t), 0))
    p.ears(droop=10 * bump(t, 0.55, 0.06))
    return p.blink(bump(t, 0.8, 0.05))


@animation("Idle_LookAround", 120)
def idle_look_around(t):
    look = 30 * (ramp(t, 0.10, 0.25) - ramp(t, 0.40, 0.55)) - 30 * (ramp(t, 0.60, 0.72) - ramp(t, 0.85, 0.97))
    p = Pose()
    p.add("body", loc=(0, 0.008 * wave(t, 2), 0))
    p.add("neck", rot=(0, look * 0.4, 0))
    p.add("head", rot=(-3 * abs(look) / 30, look * 0.6, 0))
    p.ears(back=8 * abs(look) / 30)
    return p.blink(bump(t, 0.3, 0.03) + bump(t, 0.78, 0.03))


@animation("Walking", 30)
def walking(t):
    # Four-beat walk: left hind, left fore, right hind, right fore, a quarter cycle apart.
    p = gait(Pose(), t, {"leg.bl": 0.0, "leg.fl": 0.75, "leg.br": 0.5, "leg.fr": 0.25},
             stance=0.65, stride=0.26, lift=0.09)
    p.add("body", loc=(0, 0.012 * wave(t, 2, 0.25), 0), rot=(0, 2 * wave(t), 0))
    p.add("head", rot=(3 * wave(t, 2), 0, 0))
    p.add("tail", rot=(0, 10 * wave(t), 0))
    return p


@animation("Running", 18)
def running(t):
    # Bounding gallop: the front pair lands together, then the hind pair, with a moment
    # where all four hooves are off the ground.
    p = gait(Pose(), t, {"leg.fl": 0.0, "leg.fr": 0.92, "leg.bl": 0.5, "leg.br": 0.42},
             stance=0.38, stride=0.5, lift=0.14)
    p.add("body", rot=(7 * wave(t, 1, 0.25), 0, 0), loc=(0, 0.035 + 0.03 * wave(t, 1, 0.25), 0))
    p.add("head", rot=(-6 * wave(t, 1, 0.25), 0, 0))
    p.add("tail", rot=(-25, 0, 0))
    return p.ears(back=35)


@animation("Eating_Grass", 60)
def eating_grass(t):
    chew = (wave(t, 4) + 1) / 2
    p = Pose()
    # Lean in and stretch the neck forward and down so the mouth reaches the grass.
    p.add("body", rot=(14, 0, 0), loc=(0, -0.04, 0))
    p.add("neck", rot=(60, 0, 0), loc=(0, -0.15, 0.08))
    p.add("head", rot=(23 + 3 * wave(t, 1), 0, 0))
    p.add("jaw", rot=(14 * chew, 0, 0))
    p.add("tail", rot=(0, 8 * wave(t, 2), 0))
    p.ears(droop=8)
    return p.blink(bump(t, 0.6, 0.04))


@animation("Pooping", 75)
def pooping(t):
    squat = envelope(t, 0.05, 0.2, 0.75, 0.9)
    strain = envelope(t, 0.15, 0.25, 0.42, 0.52)
    drop = ramp(t, 0.36, 0.5) ** 2
    # The droppings ride on the body bone, so hide them before the sheep straightens up.
    # (In game, spawn a droppings prop on the ground at this point.)
    shown = ramp(t, 0.33, 0.37) * (1 - ramp(t, 0.70, 0.74))
    p = Pose()
    p.add("body", loc=(0, -0.04 * squat, 0), rot=(-6 * squat, 0, 0))
    p.add("tail", rot=(-50 * squat, 0, 0))
    # Pose() starts the droppings at 0.001; scale them up to full size while shown.
    size = max(shown, 0.001) / 0.001
    p.add("poop", scale=(size, size, size), loc=(0, -0.15 * drop, 0))
    p.add("tail", rot=(0, 20 * wave(t, 6) * envelope(t, 0.75, 0.8, 0.92, 0.98), 0))
    return p.blink(0.5 * strain)


@animation("Being_Fed", 90)
def being_fed(t):
    reach = envelope(t, 0.0, 0.12, 0.85, 1.0)
    chewing = envelope(t, 0.15, 0.2, 0.8, 0.85)
    happy = envelope(t, 0.25, 0.35, 0.8, 0.9)
    p = Pose()
    p.add("neck", rot=(-18 * reach, 0, 0))
    p.add("head", rot=(-8 * reach + 6 * bump(t, 0.16, 0.05), 0, 0))
    p.add("jaw", rot=(14 * chewing * (wave(t, 8) + 1) / 2, 0, 0))
    p.add("tail", rot=(0, 25 * wave(t, 10) * happy, 0))
    p.add("body", loc=(0, 0.01 * wave(t, 4) * happy, 0))
    p.ears(back=-15 * reach)
    return p.blink(0.4 * envelope(t, 0.2, 0.3, 0.75, 0.85))


@animation("Getting_Love", 100)
def getting_love(t):
    w = envelope(t, 0.05, 0.2, 0.8, 0.95)
    p = Pose()
    p.add("head", rot=(5 * w, 0, 18 * w))
    p.add("neck", rot=(0, 10 * w, 0))
    p.add("body", rot=(0, 0, 4 * wave(t, 2) * w), loc=(0, 0.01 * wave(t, 4) * w, 0))
    p.add("tail", rot=(0, 20 * wave(t, 8) * w, 0))
    p.ears(droop=20 * w)
    return p.blink(0.95 * w)


@animation("Lie_Down", 45)
def lie_down(t):
    # Front end goes down first, so the front legs fold before the hind legs.
    front, back = ramp(t, 0.0, 0.55), ramp(t, 0.35, 0.95)
    p = Pose()
    p.add("body", loc=(0, -0.24 * (front + back) / 2, 0), rot=(12 * (front - back), 0, 0))
    return p


@animation("Lie_Idle", 90)
def lie_idle(t):
    p = Pose().lying(1)
    p.add("body", loc=(0, 0.006 * wave(t), 0))
    p.add("head", rot=(2 * wave(t, 1, 0.25), 0, 0))
    p.add("tail", rot=(0, 5 * wave(t), 0))
    p.ears(droop=10 * bump(t, 0.75, 0.05))
    return p.blink(bump(t, 0.4, 0.04))


@animation("Sleeping", 120)
def sleeping(t):
    p = Pose().lying(1)
    p.add("body", loc=(0, 0.01 * wave(t), 0))
    p.add("neck", rot=(30, 0, 0))
    p.add("head", rot=(15 + 2 * wave(t), 0, 12))
    p.ears(droop=18)
    return p.blink(1.0)


@animation("Lie_StandUp", 45)
def lie_stand_up(t):
    # Sheep get their back end up first, so the hind legs straighten before the front.
    back, front = 1 - ramp(t, 0.0, 0.55), 1 - ramp(t, 0.35, 0.95)
    p = Pose()
    p.add("body", loc=(0, -0.24 * (front + back) / 2, 0), rot=(12 * (front - back), 0, 0))
    return p


@animation("Bleat", 45)
def bleat(t):
    w = envelope(t, 0.08, 0.25, 0.7, 0.9)
    p = Pose()
    p.add("neck", rot=(-20 * w, 0, 0))
    p.add("head", rot=(-10 * w, 0, 0))
    p.add("jaw", rot=(22 * w * (0.85 + 0.15 * wave(t, 6)), 0, 0))
    p.add("body", loc=(0, 0.01 * w, 0))
    p.ears(back=20 * w)
    return p.blink(0.3 * w)


@animation("Hop", 30)
def hop(t):
    air = math.sin(math.pi * min(max((t - 0.2) / 0.55, 0.0), 1.0))
    crouch = bump(t, 0.12, 0.12) + bump(t, 0.85, 0.12)
    p = Pose()
    p.add("body", loc=(0, 0.3 * air - 0.05 * crouch, 0), rot=(-5 * air, 0, 0))
    # Hooves stay planted through the crouch and push-off, then tuck up while airborne.
    for leg, (_, y) in LEGS.items():
        tuck = 0.12 * air if y < 0 else -0.08 * air
        p.foot(leg, tuck, 0.4 * air)
    p.add("tail", rot=(-30 * air, 0, 0))
    return p.ears(droop=-15 * air)


rig.animation_data.action = bpy.data.actions["Idle"]
scene.frame_start, scene.frame_end = 0, 60

# --------------------------------------------------------------------------------------
# Save and export
# --------------------------------------------------------------------------------------

bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT_DIR, "Sheep.blend"), relative_remap=True)
bpy.ops.export_scene.gltf(
    filepath=os.path.join(OUT_DIR, "Sheep.glb"),
    export_format="GLB",
    export_animations=True,
    export_animation_mode="ACTIONS",
    export_yup=True,
)
print(f"SHEEP: wrote {len(mesh.vertices)} vertices, {len(BONES)} bones, {len(ACTIONS)} animations")

# --------------------------------------------------------------------------------------
# Optional preview renders (not saved into the .blend)
# --------------------------------------------------------------------------------------

if PREVIEW_DIR:
    os.makedirs(PREVIEW_DIR, exist_ok=True)
    ground = bpy.data.objects.new("PreviewGround", bpy.data.meshes.new("PreviewGround"))
    gbm = bmesh.new()
    bmesh.ops.create_grid(gbm, x_segments=1, y_segments=1, size=3)
    gbm.to_mesh(ground.data)
    gbm.free()
    scene.collection.objects.link(ground)

    camera = bpy.data.objects.new("PreviewCamera", bpy.data.cameras.new("PreviewCamera"))
    scene.collection.objects.link(camera)
    camera.location = (1.7, -2.3, 1.25)
    direction = Vector((0, -0.1, 0.5)) - camera.location
    camera.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    scene.camera = camera

    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "TEXTURE"
    scene.render.resolution_x = scene.render.resolution_y = 360

    shots = [("Idle", 0), ("Eating_Grass", 16), ("Pooping", 40), ("Being_Fed", 46), ("Getting_Love", 50),
             ("Running", 4), ("Hop", 14), ("Bleat", 20), ("Lie_Idle", 0), ("Sleeping", 0)]
    for name, frame in shots:
        rig.animation_data.action = bpy.data.actions[name]
        scene.frame_set(frame)
        scene.render.filepath = os.path.join(PREVIEW_DIR, f"{name}.png")
        bpy.ops.render.render(write_still=True)
    print("SHEEP: previews in", PREVIEW_DIR)
