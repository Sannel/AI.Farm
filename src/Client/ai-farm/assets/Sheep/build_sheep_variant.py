"""Builds the KayKit-style sheep: model, palette texture, rig and animations.

Run inside a live Blender session (e.g. through the Blender MCP server):
    exec(open(".../build_sheep.py").read(), {"OUT_DIR": ".../assets/Sheep"})
or headless:
    blender -b --python build_sheep.py -- <output_dir> [<preview_dir>]

It builds into its own NAME scene, so whatever file is open is left alone, then writes
Sheep.blend (just the sheep), Sheep.glb and sheep_texture.png to the output directory. If a
preview directory is given it also renders a few poses there for a quick visual check.

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

NAME = globals().get("NAME", "Sheep_Female")
MALE = globals().get("SEX", "Female") == "Male"
PREVIEW_DIR = globals().get("PREVIEW_DIR")
if "OUT_DIR" not in globals():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    OUT_DIR = argv[0] if argv else os.path.dirname(os.path.abspath(__file__))
    PREVIEW_DIR = os.path.abspath(argv[1]) if len(argv) > 1 else None
OUT_DIR = os.path.abspath(OUT_DIR)
os.makedirs(OUT_DIR, exist_ok=True)

# Build in a scene of our own. In a live session that's a new scene shown in the window;
# headless there's no window to switch, so the startup scene is emptied and used instead.
if bpy.context.window:
    if bpy.context.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    # Clear out a previous build so re-running doesn't leave Sheep.001 copies behind.
    old = bpy.data.scenes.get(NAME)
    if old:
        bpy.data.scenes.remove(old, do_unlink=True)
    for collection, names in ((bpy.data.objects, (NAME, NAME + "_Rig")), (bpy.data.meshes, (NAME,)),
                              (bpy.data.armatures, (NAME + "_Rig",)), (bpy.data.materials, (NAME.lower(),)),
                              (bpy.data.images, (NAME.lower() + "_texture",))):
        for name in names:
            if name in collection:
                collection.remove(collection[name])
    scene = bpy.data.scenes.new(NAME)
    bpy.context.window.scene = scene
else:
    scene = bpy.context.scene
    scene.name = NAME
    for obj in list(scene.collection.all_objects):
        bpy.data.objects.remove(obj, do_unlink=True)
scene.render.fps = 60
view_layer = scene.view_layers[0]

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
    "horn": (8, (218, 185, 132), (126, 94, 57)),
}


if MALE:
    PALETTE["face"] = (1, (226, 211, 188), (177, 151, 120))


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

    image = bpy.data.images.new(NAME.lower() + "_texture", TEXTURE_SIZE, TEXTURE_SIZE, alpha=False)
    image.pixels = pixels
    image.filepath_raw = os.path.join(OUT_DIR, NAME.lower() + "_texture.png")
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

# Rams have thick curled horns, swept around the ears towards the cheeks.
# The roots are on the head, so horns follow every head/neck animation.
if MALE:
    kit_path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "AnimalKit")
    if kit_path not in sys.path: sys.path.insert(0, kit_path)
    from animal_kit import sweep
    for side in (1, -1):
        # Start inside the skull and sweep through the fleece before curling
        # outwards. All of the horn is weighted to the head bone.
        root = Vector((side * 0.06, -0.58, 0.94))
        assert face_surfaces[0].ray_cast(root, Vector((0, 0, 1)))[0] is not None, "Horn root must be inside the head"
        points = [root, (side * 0.11, -0.60, 1.01)]
        radii = [0.045, 0.060]
        for i in range(25):
            t = i / 24
            angle = math.radians(140 - 290 * t)
            radius = 0.19 * (1 - 0.58 * t)
            points.append((side * (0.15 + 0.14 * math.sin(math.pi * t / 2)),
                           -0.50 + radius * math.cos(angle),
                           0.94 + radius * math.sin(angle)))
            radii.append(0.058 * (1 - t) ** 0.75 + 0.003)
        add_part("head", "horn", sweep(points, radii, radii, width_axis=(1, 0, 0), segments=10))

# Stubby two-part legs. The sheep faces -Y, so its left side is +X.
HIP_Z, KNEE_Z = 0.42, 0.22
UPPER_LEG, LOWER_LEG = HIP_Z - KNEE_Z, KNEE_Z  # lower leg runs from the knee to the hoof's sole
LEGS = {"leg.fl": (0.19, -0.30), "leg.fr": (-0.19, -0.30), "leg.bl": (0.19, 0.30), "leg.br": (-0.19, 0.30)}
for bone, (x, y) in LEGS.items():
    add_part(bone, "leg", cylinder((x, y, KNEE_Z - 0.02), 0.075, HIP_Z - KNEE_Z + 0.04))
    add_part(bone + ".lower", "leg", blob((x, y, KNEE_Z), (0.072, 0.072, 0.06), subdiv=1))
    add_part(bone + ".lower", "leg", cylinder((x, y, 0.07), 0.065, KNEE_Z - 0.07))
    add_part(bone + ".lower", "hoof", cylinder((x, y, 0.0), 0.08, 0.08))

# Droppings are separate world props, spawned by the game during Pooping.

# --------------------------------------------------------------------------------------
# Mesh object and material
# --------------------------------------------------------------------------------------

mesh = bpy.data.meshes.new(NAME)
final.to_mesh(mesh)
final.free()
bpy.data.meshes.remove(scratch_mesh)
sheep = bpy.data.objects.new(NAME, mesh)
scene.collection.objects.link(sheep)

texture = make_texture()
material = bpy.data.materials.new(NAME.lower())
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

armature_data = bpy.data.armatures.new(NAME + "_Rig")
rig = bpy.data.objects.new(NAME + "_Rig", armature_data)
scene.collection.objects.link(rig)
view_layer.objects.active = rig
with bpy.context.temp_override(scene=scene, view_layer=view_layer, active_object=rig, object=rig):
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

if MALE:
    rig.scale = (1.12,) * 3

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
        return self.add("body", loc=(0, -LIE_DROP * w, 0))

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


LIE_DROP = 0.24  # how far the body drops to lie on the ground
HOOF_TIP = 0.08  # the hoof's radius: how far its rim dips when the shin tilts


def reach(down, back):
    """Two-bone IK in the leg's plane: angles (radians, from straight down; negative swings
    forward) of the upper and lower leg putting the hoof `down` below and `back` behind the hip.
    Every joint bends the same way, the lower leg folding back under the body (knee forward)."""
    d = math.hypot(back, down)
    d = min(max(d, abs(UPPER_LEG - LOWER_LEG) + 1e-4), UPPER_LEG + LOWER_LEG - 1e-4)
    bend = math.pi - math.acos((UPPER_LEG ** 2 + LOWER_LEG ** 2 - d ** 2) / (2 * UPPER_LEG * LOWER_LEG))
    spread = math.acos((UPPER_LEG ** 2 + d ** 2 - LOWER_LEG ** 2) / (2 * UPPER_LEG * d))
    aim = math.atan2(back, down)
    return aim - spread, bend


def solve_legs(pose):
    """Solve in the full body frame, including yaw/roll, and settle tilted hoof rims."""
    body_rot, body_loc, body_scale = pose.bones.get("body", ((0, 0, 0), (0, 0, 0), (1, 1, 1)))
    basis = rig.data.bones["body"].matrix_local.to_3x3()
    body_matrix = (basis @ Euler([math.radians(a) for a in body_rot]).to_matrix()
                   @ Matrix.Diagonal(body_scale) @ basis.inverted())
    translation = basis @ Vector(body_loc)
    pivot = Vector((0, 0, BODY_PIVOT_Z))
    for leg, (x, rest_y) in LEGS.items():
        hip = pivot + translation + body_matrix @ Vector((x, rest_y, HIP_Z - BODY_PIVOT_Z))
        forward, lift = pose.feet[leg]
        rim = 0.002
        for _ in range(8):
            target = Vector((x, rest_y - forward, lift + rim))
            delta = body_matrix.inverted() @ (target - hip)
            splay = math.atan2(delta.x, -delta.z)
            upper, lower = reach(math.hypot(delta.x, delta.z), delta.y)
            shin = body_matrix @ Matrix.Rotation(-splay, 3, "Y") @ Matrix.Rotation(upper + lower, 3, "X")
            rim = 0.002 + HOOF_TIP * math.hypot(shin[2][0], shin[2][1])
        pose.add(leg, rot=(math.degrees(upper), 0, math.degrees(splay)))
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
        old = bpy.data.actions.get(NAME + "_" + name)
        if old:
            bpy.data.actions.remove(old)
        # Prefixed in Blender so the sheep's actions never mix with the dog's when both are
        # open; the NLA track (and so the exported animation) uses the plain name.
        action = bpy.data.actions.new(NAME + "_" + name)
        action.use_fake_user = True
        rig.animation_data_create()
        rig.animation_data.action = action
        samples = frames * 2
        for frame in range(samples + 1):
            key_pose(frame, pose_fn(frame / samples))
        ACTIONS.append((name, action))
        return pose_fn
    return register


# --------------------------------------------------------------------------------------
# Animations
# --------------------------------------------------------------------------------------

# Standing animations never bob the body up and down: the legs are straight, and near full
# stretch a few millimetres of bob swings the knees through 10-20 degrees, so the legs flicker.
# Breathing and excitement show in the neck, head and tail instead.

@animation("Idle", 60)
def idle(t):
    p = Pose()
    p.add("neck", rot=(1.5 * wave(t), 0, 0))
    p.add("head", rot=(2 * wave(t, 1, 0.25), 0, 0))
    p.add("tail", rot=(0, 6 * wave(t), 0))
    p.ears(droop=10 * bump(t, 0.55, 0.06))
    return p.blink(bump(t, 0.8, 0.05))


@animation("Idle_LookAround", 120)
def idle_look_around(t):
    look = 30 * (ramp(t, 0.10, 0.25) - ramp(t, 0.40, 0.55)) - 30 * (ramp(t, 0.60, 0.72) - ramp(t, 0.85, 0.97))
    p = Pose()
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
    # Bounds along low enough that the stretched legs still reach the ground mid-stride.
    p.add("body", rot=(7 * wave(t, 1, 0.25), 0, 0), loc=(0, -0.02 + 0.015 * wave(t, 1, 0.25), 0))
    p.add("head", rot=(-6 * wave(t, 1, 0.25), 0, 0))
    p.add("tail", rot=(-25, 0, 0))
    return p.ears(back=35)


@animation("Eating_Grass", 60)
def eating_grass(t):
    chew = (wave(t, 4) + 1) / 2
    p = Pose()
    # Lean in a little (legs stay nearly straight, as a grazing sheep's do) and let the neck do
    # the reaching, stretched forward and down so the mouth touches the grass. The lean lifts
    # the hips; the body drops just enough that the hind hooves stay on the ground.
    p.add("body", rot=(8, 0, 0), loc=(0, -0.05, 0))
    p.add("neck", rot=(72, 0, 0), loc=(0, -0.20, 0.1))
    p.add("head", rot=(29 + 3 * wave(t, 1), 0, 0))
    p.add("jaw", rot=(14 * chew, 0, 0))
    p.add("tail", rot=(0, 8 * wave(t, 2), 0))
    p.ears(droop=8)
    return p.blink(bump(t, 0.6, 0.04))


@animation("Pooping", 75)
def pooping(t):
    squat = envelope(t, 0.05, 0.2, 0.75, 0.9)
    strain = envelope(t, 0.15, 0.25, 0.42, 0.52)
    p = Pose()
    # Keep planted hooves above the floor while the neck and tail show tension.
    p.add("neck", rot=(8 * squat, 0, 0))
    p.add("head", rot=(-5 * squat, 0, 0))
    p.add("tail", rot=(-50 * squat, 0, 0))
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
    p.ears(back=-15 * reach)
    return p.blink(0.4 * envelope(t, 0.2, 0.3, 0.75, 0.85))


@animation("Getting_Love", 100)
def getting_love(t):
    w = envelope(t, 0.05, 0.2, 0.8, 0.95)
    p = Pose()
    p.add("head", rot=(5 * w, 0, 18 * w))
    p.add("neck", rot=(0, 10 * w, 0))
    p.add("body", rot=(0, 0, 4 * wave(t, 2) * w))
    p.add("tail", rot=(0, 20 * wave(t, 8) * w, 0))
    p.ears(droop=20 * w)
    return p.blink(0.95 * w)


@animation("Lie_Down", 45)
def lie_down(t):
    # Front end goes down first, so the front legs fold before the hind legs.
    front, back = ramp(t, 0.0, 0.55), ramp(t, 0.35, 0.95)
    p = Pose()
    p.add("body", loc=(0, -LIE_DROP * (front + back) / 2, 0), rot=(12 * (front - back), 0, 0))
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
    p.add("body", loc=(0, -LIE_DROP * (front + back) / 2, 0), rot=(12 * (front - back), 0, 0))
    return p


@animation("Bleat", 45)
def bleat(t):
    w = envelope(t, 0.08, 0.25, 0.7, 0.9)
    p = Pose()
    p.add("neck", rot=(-20 * w, 0, 0))
    p.add("head", rot=(-10 * w, 0, 0))
    p.add("jaw", rot=(22 * w * (0.85 + 0.15 * wave(t, 6)), 0, 0))
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


# --- Being herded -----------------------------------------------------------------------

@animation("Alert_Idle", 60)
def alert_idle(t):
    """Head up and ears forward, watching the dog."""
    p = Pose()
    p.add("neck", rot=(-18, 4 * wave(t, 1), 0))
    p.add("head", rot=(-8, 8 * wave(t, 1, 0.1), 0))
    p.add("tail", rot=(-10, 0, 0))
    p.ears(back=-22)
    return p.blink(bump(t, 0.7, 0.04))


@animation("Startled", 36)
def startled(t):
    """A jump back with the head thrown up, as when the dog gets too close."""
    jump = math.sin(math.pi * min(max(t / 0.35, 0.0), 1.0))
    shock = envelope(t, 0.0, 0.08, 0.55, 0.9)
    p = Pose()
    p.add("body", loc=(0, 0.14 * jump, -0.06 * jump), rot=(-8 * shock, 0, 0))
    for leg in LEGS:
        p.foot(leg, 0.04 * jump, 0.1 * jump)
    p.add("neck", rot=(-25 * shock, 0, 0))
    p.add("head", rot=(-10 * shock, 0, 0))
    p.add("tail", rot=(-35 * shock, 0, 0))
    return p.ears(back=-25 * shock)


@animation("Trotting", 20)
def trotting(t):
    """The brisk two-beat pace a flock moves at when pushed: diagonal legs together."""
    p = gait(Pose(), t, {"leg.fl": 0.0, "leg.br": 0.0, "leg.fr": 0.5, "leg.bl": 0.5},
             stance=0.5, stride=0.38, lift=0.1)
    p.add("body", loc=(0, 0.012 + 0.012 * wave(t, 2, 0.25), 0))
    p.add("head", rot=(3 * wave(t, 2), 0, 0))
    p.add("tail", rot=(-10, 8 * wave(t), 0))
    return p.ears(back=12)


@animation("Walking_LookBack", 30)
def walking_look_back(t):
    """Walking on while glancing back over the shoulder at the dog."""
    look = 0.5 + 0.5 * wave(t, 1, -0.25)  # eases between a half and a full turn
    p = gait(Pose(), t, {"leg.bl": 0.0, "leg.fl": 0.75, "leg.br": 0.5, "leg.fr": 0.25},
             stance=0.65, stride=0.26, lift=0.09)
    p.add("body", loc=(0, 0.012 * wave(t, 2, 0.25), 0), rot=(0, -4, 0))
    p.add("neck", rot=(-6, 25 + 15 * look, 0))
    p.add("head", rot=(0, 20 + 15 * look, 8))
    p.add("tail", rot=(0, 10 * wave(t), 0))
    p.ears(back=15)
    return p.blink(bump(t, 0.45, 0.04))


# --------------------------------------------------------------------------------------
if MALE:
    @animation("Headbutt", 45)
    def headbutt(t):
        windup = envelope(t, 0.05, 0.25, 0.35, 0.48)
        strike = envelope(t, 0.35, 0.42, 0.57, 0.9)
        p = Pose()
        p.add("neck", rot=(-12 * windup + 30 * strike, 0, 0))
        p.add("head", rot=(-8 * windup + 18 * strike, 0, 0))
        return p.ears(back=25 * strike)


# NLA tracks (one per animation) so the export only picks up the sheep's own actions.
# --------------------------------------------------------------------------------------

for name, action in ACTIONS:
    track = rig.animation_data.nla_tracks.new()
    track.name = name
    track.strips.new(name, 0, action)
rig.animation_data.action = None
scene.frame_start, scene.frame_end = 0, 60

# --------------------------------------------------------------------------------------
# Export, then save just the sheep (with the viewport left on Idle)
# --------------------------------------------------------------------------------------

with bpy.context.temp_override(scene=scene, view_layer=view_layer):
    bpy.ops.export_scene.gltf(
        filepath=os.path.join(OUT_DIR, NAME + ".glb"),
        export_format="GLB",
        use_active_scene=True,
        export_animations=True,
        export_animation_mode="NLA_TRACKS",
        export_yup=True,
    )
# Export done: mute the tracks (they'd all play at once) and show Idle in the viewport.
for track in rig.animation_data.nla_tracks:
    track.mute = True
rig.animation_data.action = bpy.data.actions[NAME + "_Idle"]
scene.frame_set(0)
bpy.data.libraries.write(os.path.join(OUT_DIR, NAME + ".blend"), {scene, *(action for _, action in ACTIONS)},
                         path_remap="RELATIVE_ALL", fake_user=True)
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
        rig.animation_data.action = bpy.data.actions[NAME + "_" + name]
        scene.frame_set(frame * 2)
        scene.render.filepath = os.path.join(PREVIEW_DIR, f"{name}.png")
        bpy.ops.render.render(write_still=True)
    print("SHEEP: previews in", PREVIEW_DIR)
