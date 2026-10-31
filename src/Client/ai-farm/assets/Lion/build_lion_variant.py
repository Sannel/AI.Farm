"""Builds the KayKit-style sheepdog (a black-and-white border collie): model, palette texture,
rig and herding animations.

Run inside a live Blender session (e.g. through the Blender MCP server):
    exec(open(".../build_dog.py").read(), {"OUT_DIR": ".../assets/Dog"})
or headless:
    blender -b --python build_dog.py -- <output_dir>

It builds into its own NAME scene, so whatever file is open is left alone, then writes
Dog.blend (just the dog), Dog.glb and dog_texture.png to the output directory.

Conventions match the sheep: the dog faces -Y in Blender (+Z in Godot), every bone is vertical
with no roll, so rotation X pitches a part (positive tips its front down), rotation Y yaws it
(positive turns to the dog's left), location Y moves it up and down. Legs are two bones solved
by IK from where each paw should be, so paws plant instead of sliding; all four joints fold
the same way, knee forward.
"""

import math
import os
import sys

import bmesh
import numpy as np
import bpy
from mathutils import Euler, Matrix, Vector
from mathutils.bvhtree import BVHTree

NAME = globals().get("NAME", "Lion_Male")
FEMALE = globals().get("SEX", "Male") == "Female"
HARNESS = globals().get("HARNESS")

if "OUT_DIR" not in globals():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    OUT_DIR = argv[0] if argv else os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.abspath(OUT_DIR)
os.makedirs(OUT_DIR, exist_ok=True)

# Build in a scene of our own. In a live session that's a new scene shown in the window;
# headless there's no window to switch, so the startup scene is emptied and used instead.
if bpy.context.window:
    if bpy.context.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    # Clear out a previous build so re-running doesn't leave Dog.001 copies behind.
    old = bpy.data.scenes.get(NAME)
    if old:
        bpy.data.scenes.remove(old, do_unlink=True)
    for collection, names in ((bpy.data.objects, (NAME, NAME + "_Rig")), (bpy.data.meshes, (NAME,)),
                              (bpy.data.armatures, (NAME + "_Rig",)), (bpy.data.materials, (NAME.lower(),))):
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
# Palette texture (8x8 grid of vertical gradients, KayKit style)
# --------------------------------------------------------------------------------------

GRID = 8
TEXTURE_SIZE = 512
# The palette lives in the top rows of the texture. The bottom half is a coat map: the body's
# markings drawn as smooth shapes and projected onto its sides (left side on the left half,
# right side on the right), so splotches get clean edges no matter how the mesh is cut.
COAT_Y, COAT_Z = (-0.32, 0.36), (0.28, 0.62)
# The head's markings are painted into two maps under the palette row: the face seen from the
# front (x, z), and the top of the head seen from above (x, y). Rows are texture pixels.
FACE_X, FACE_Z, HEAD_TOP_Y = (-0.15, 0.15), (0.5, 0.82), (-0.72, -0.28)
HEAD_MAP_ROWS = (64, 192)
PALETTE = {
    "black": (0, (70, 70, 80), (30, 30, 36)),
    "white": (1, (255, 255, 255), (210, 214, 226)),
    "nose": (2, (40, 38, 42), (14, 13, 16)),
    "eye": (3, (36, 32, 38), (16, 14, 18)),
    "tongue": (4, (246, 132, 152), (206, 86, 112)),
    "ear_inner": (5, (242, 172, 182), (212, 130, 146)),
    "tan": (6, (222, 150, 80), (172, 100, 48)),
    "mouth": (7, (120, 40, 54), (70, 20, 32)),
}


# Orange tabby and black cat use the same original feline mesh.
BLACK_CAT = globals().get("BLACK_CAT", False)
PALETTE["black"] = (0, (48,47,52), (22,21,24)) if BLACK_CAT else (0, (207,157,88), (150,100,50))
PALETTE["white"] = (1, (48,47,52), (22,21,24)) if BLACK_CAT else (1, (242,220,192), (197,170,144))
PALETTE["tan"] = (6, (48,47,52), (22,21,24)) if BLACK_CAT else (6, (151,93,42), (98,52,24))
if BLACK_CAT: PALETTE["eye"] = (3, (236,208,90), (171,145,42))


if HARNESS:
    PALETTE["tan"] = (6, *HARNESS)


def make_texture():
    by_index = {index: (top, bottom) for index, top, bottom in PALETTE.values()}
    cell = TEXTURE_SIZE // GRID
    pixels = [0.0] * (TEXTURE_SIZE * TEXTURE_SIZE * 4)
    half = TEXTURE_SIZE // 2
    for y in range(TEXTURE_SIZE):
        from_top = TEXTURE_SIZE - 1 - y
        for x in range(TEXTURE_SIZE):
            in_head_map = HEAD_MAP_ROWS[0] <= from_top < HEAD_MAP_ROWS[1]
            if in_head_map:
                # Head maps: ray cast onto the skull to find which marking is at this pixel.
                a = ((x % half) + 0.5) / half
                b = 1 - (from_top - HEAD_MAP_ROWS[0] + 0.5) / (HEAD_MAP_ROWS[1] - HEAD_MAP_ROWS[0])
                head_x = FACE_X[0] + a * (FACE_X[1] - FACE_X[0])
                if x < half:
                    head_z = FACE_Z[0] + b * (FACE_Z[1] - FACE_Z[0])
                    hit = face_surfaces[0].ray_cast(Vector((head_x, -2.0, head_z)), Vector((0, 1, 0)))[0]
                else:
                    head_y = HEAD_TOP_Y[0] + b * (HEAD_TOP_Y[1] - HEAD_TOP_Y[0])
                    hit = face_surfaces[0].ray_cast(Vector((head_x, head_y, 2.0)), Vector((0, 0, -1)))[0]
                name = head_colour(hit) if hit is not None else "black"
                top, bottom = PALETTE[name][1], PALETTE[name][2]
                z = hit.z if hit is not None else HEAD_RANGE[1]
                t = min(max((HEAD_RANGE[1] - z) / (HEAD_RANGE[1] - HEAD_RANGE[0]), 0.0), 1.0)
            elif y < half:
                # Coat map: which marking covers this point of the body's side, shaded by height.
                side = 1 if x < half else -1
                body_y = COAT_Y[0] + (x % half + 0.5) / half * (COAT_Y[1] - COAT_Y[0])
                body_z = COAT_Z[0] + (y + 0.5) / half * (COAT_Z[1] - COAT_Z[0])
                name = body_colour(Vector((0.14 * side, body_y, body_z)))
                top, bottom = PALETTE[name][1], PALETTE[name][2]
                t = (COAT_Z[1] - body_z) / (COAT_Z[1] - COAT_Z[0])
            else:
                index = (from_top // cell) * GRID + x // cell
                top, bottom = by_index.get(index, ((255, 255, 255), (255, 255, 255)))
                t = (from_top % cell) / (cell - 1)
            offset = (y * TEXTURE_SIZE + x) * 4
            for c in range(3):
                pixels[offset + c] = (top[c] + (bottom[c] - top[c]) * t) / 255.0
            pixels[offset + 3] = 1.0

    old = bpy.data.images.get(NAME.lower() + "_texture")
    if old:
        bpy.data.images.remove(old)
    image = bpy.data.images.new(NAME.lower() + "_texture", TEXTURE_SIZE, TEXTURE_SIZE, alpha=False)
    image.pixels = pixels
    image.filepath_raw = os.path.join(OUT_DIR, NAME.lower() + "_texture.png")
    image.file_format = "PNG"
    image.save()
    return image


def coat_uv(co, face):
    """Projects a body vertex sideways onto the coat map in the bottom half of the texture.
    The side is chosen per face so faces along the spine don't stretch across both halves."""
    left = face.calc_center_median().x >= 0
    u = min(max((co.y - COAT_Y[0]) / (COAT_Y[1] - COAT_Y[0]), 0.002), 0.998) * 0.5
    v = min(max((co.z - COAT_Z[0]) / (COAT_Z[1] - COAT_Z[0]), 0.002), 0.998) * 0.5
    return (u if left else u + 0.5, v)


def head_uv(co, face):
    """Face-on parts of the skull read their colour from the front map, the crown from the
    top map, so marking edges come out smooth. Sides keep plain per-face colours (None)."""
    rows = HEAD_MAP_ROWS[1] - HEAD_MAP_ROWS[0]

    def to_uv(a, b, right_half):
        a = min(max(a, 0.002), 0.998)
        b = min(max(b, 0.002), 0.998)
        from_top = HEAD_MAP_ROWS[0] + (1 - b) * rows
        return (a * 0.5 + (0.5 if right_half else 0.0), 1 - from_top / TEXTURE_SIZE)

    a = (co.x - FACE_X[0]) / (FACE_X[1] - FACE_X[0])
    if face.normal.y < -0.35:
        return to_uv(a, (co.z - FACE_Z[0]) / (FACE_Z[1] - FACE_Z[0]), False)
    if face.normal.z > 0.35:
        return to_uv(a, (co.y - HEAD_TOP_Y[0]) / (HEAD_TOP_Y[1] - HEAD_TOP_Y[0]), True)
    return None


def cell_uv(index, t):
    col, row = index % GRID, index // GRID
    margin = 0.15 / GRID
    v_top = 1.0 - row / GRID - margin
    v_bottom = 1.0 - (row + 1) / GRID + margin
    return ((col + 0.5) / GRID, v_top - t * (v_top - v_bottom))


# --------------------------------------------------------------------------------------
# Geometry helpers: each part is coloured from the palette and skinned 100% to one bone.
# --------------------------------------------------------------------------------------

final = bmesh.new()
scratch_mesh = bpy.data.meshes.new("dog_scratch")
parts = []  # (bone, first vertex, end vertex)


def transform(center, rot=(0, 0, 0), scale=(1, 1, 1)):
    return (Matrix.Translation(center) @ Euler([math.radians(a) for a in rot]).to_matrix().to_4x4()
            @ Matrix.Diagonal((*scale, 1.0)))


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


def cylinder(bottom_center, radius, height, segments=10, bevel=0.012):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segments,
                          radius1=radius, radius2=radius, depth=height)
    bmesh.ops.bevel(bm, geom=[e for e in bm.edges if e.calc_face_angle(0) > 0.5],
                    offset=bevel, segments=2, profile=0.5, affect="EDGES", clamp_overlap=True)
    bm.transform(Matrix.Translation(Vector(bottom_center) + Vector((0, 0, height / 2))))
    return bm


def subdivided(bm, cuts):
    """Splits a smooth part into small faces so colour patches can be painted onto it."""
    bmesh.ops.subdivide_edges(bm, edges=list(bm.edges), cuts=cuts, use_grid_fill=True)
    return bm


def tapered(start, end, r_start, r_end, segments=12):
    """A smooth tapered limb running from start to end."""
    start, end = Vector(start), Vector(end)
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segments,
                          radius1=r_start, radius2=r_end, depth=(end - start).length)
    align = Vector((0, 0, 1)).rotation_difference((end - start).normalized()).to_matrix().to_4x4()
    bm.transform(Matrix.Translation((start + end) / 2) @ align)
    return bm


def snout(center, radii, taper, droop, subdiv=3):
    """An egg that narrows (by `taper`) and dips (by `droop`) towards its front (-Y) end: a
    muzzle or lower jaw that tapers from the face down to the nose."""
    bm = blob(center, radii, subdiv)
    back = center[1] + radii[1]
    for v in bm.verts:
        t = min(max((back - v.co.y) / (2 * radii[1]), 0.0), 1.0)
        scale = 1 - taper * t
        v.co.x = center[0] + (v.co.x - center[0]) * scale
        v.co.z = center[2] + (v.co.z - center[2]) * scale - droop * t
    return bm


def inside(p, center, radii):
    return sum(((p[i] - center[i]) / radii[i]) ** 2 for i in range(3)) <= 1.0


def add_part(bone, colour, bm, z_range=None, uv_map=None):
    """colour is a palette name, or a function of a face's centre returning one (for patches).
    uv_map, if given, maps each vertex position (and its face) straight to a UV in one of the
    painted maps instead; where it returns None the face falls back to its palette colour."""
    uv = bm.loops.layers.uv.verify()
    bm.normal_update()
    if z_range is None:
        zs = [v.co.z for v in bm.verts]
        z_range = (min(zs), max(zs))
    z_min, z_max = z_range
    height = max(z_max - z_min, 1e-6)
    for face in bm.faces:
        face.smooth = True
        mapped = [uv_map(loop.vert.co, face) for loop in face.loops] if uv_map else [None]
        if None not in mapped:
            for loop, coord in zip(face.loops, mapped):
                loop[uv].uv = coord
            continue
        name = colour(face.calc_center_median()) if callable(colour) else colour
        index = PALETTE[name][0]
        for loop in face.loops:
            t = min(max((z_max - loop.vert.co.z) / height, 0.0), 1.0)
            loop[uv].uv = cell_uv(index, t)
    bm.to_mesh(scratch_mesh)
    bm.free()
    start = len(final.verts)
    final.from_mesh(scratch_mesh)
    final.verts.ensure_lookup_table()
    parts.append((bone, start, len(final.verts)))


# --------------------------------------------------------------------------------------
# Model: a tricolour border collie. Smooth, flat KayKit-style shapes; the markings are painted
# on as colour patches (white collar, chest, belly, blaze, socks and splotches; tan brows and
# cheeks) rather than modelled as separate lumps.
# --------------------------------------------------------------------------------------

COAT_RANGE = (0.28, 0.62)


def body_colour(p):
    return "white" if p.z < .37 or (p.y < -.20 and p.z < .50) else "black"


add_part("body", "black", subdivided(rounded_box((0, 0.02, 0.45), (0.28, 0.56, 0.26), 0.11), 2),
         uv_map=coat_uv)

# Neck with a white collar; it rides on the neck bone so it follows the head down.
add_part("neck", lambda p: "black" if p.z > 0.6 and p.y > -0.34 else "white",
         blob((0, -0.30, 0.53), (0.12, 0.11, 0.13), subdiv=3), (0.36, 0.66))

# Head: a rounded skull with a snout that tapers down to the nose, and a lower jaw shaped to
# match that hinges at the back. Eye positions are found first (ray cast onto the face) so the
# tan brows can be painted just above them.
HEAD_RANGE = (0.52, 0.8)
head_bm = blob((0, -0.42, 0.67), (0.135, 0.13, 0.12), subdiv=4)
muzzle_bm = snout((0, -0.545, 0.634), (0.075, 0.055, 0.050), taper=0.32, droop=0.014)
face_surfaces = [BVHTree.FromBMesh(head_bm), BVHTree.FromBMesh(muzzle_bm)]


def face_point(x, z):
    """Where a ray fired straight back from in front of the dog first hits the face."""
    hits = [tree.ray_cast(Vector((x, -2.0, z)), Vector((0, 1, 0))) for tree in face_surfaces]
    return min((hit[0] for hit in hits if hit[0] is not None), key=lambda p: p.y)


EYES = {side: face_point(0.075 * side, 0.705) for side in (1, -1)}


def head_colour(p):
    return "white" if p.z < .625 and p.y < -.48 else "black"


add_part("head", head_colour, head_bm, HEAD_RANGE, uv_map=head_uv)
add_part("head", "white", muzzle_bm, HEAD_RANGE)
# Dark inside of the mouth, tucked between snout and jaw; it shows when the jaw drops.
add_part("head", "mouth", snout((0, -0.54, 0.598), (0.055, 0.05, 0.022), taper=0.3, droop=0.0, subdiv=2))
add_part("jaw", "white", snout((0, -0.545, 0.584), (0.055, 0.05, 0.022), taper=0.35, droop=0.0), HEAD_RANGE)
# A wide, rounded tongue lying on top of the lower jaw (inside the closed mouth); panting
# slides it out over the lower lip.
add_part("tongue", "tongue", blob((0, -0.54, 0.612), (0.035, 0.035, 0.009), subdiv=3))

nose = face_point(0, 0.645)
add_part("head", "nose", blob(nose + Vector((0, 0.008, 0)), (0.020, 0.012, 0.014)))
# Plain black eyes, half sunk into the face.
for eye in EYES.values():
    add_part("eyes", "eye", blob(eye + Vector((0, 0.005, 0)), (0.032, 0.018, 0.04)))

# Fine whiskers extend from the cheeks and follow the head.
for side in (-1,1):
    for j in (-1,0,1):
        add_part("head", "white", tapered((side*.05,-.59,.62+j*.009),
                 (side*.18,-.61,.62+j*.025),.003,.001,segments=6))

# Pointed ears, anchored into the skull and weighted to the ear bones.
EAR_BASE = {}
for side, bone in ((1, "ear.l"), (-1, "ear.r")):
    top = face_surfaces[0].ray_cast(Vector((0.09 * side, -0.42, 2.0)), Vector((0, 0, -1)))[0]
    EAR_BASE[bone] = top
    tip = top + Vector((.025*side, 0, .10))
    add_part(bone, "black", blob(top+Vector((.015*side,0,.045)),(.05,.032,.052),subdiv=2))
    add_part(bone, "ear_inner", blob(top+Vector((.008*side,-.032,.045)),(.026,.01,.030),subdiv=2))

# Tail: one smooth tapered sweep over three bones, with a white tip.
TAIL = [("tail", (0, 0.28, 0.50)), ("tail.2", (0, 0.36, 0.56)), ("tail.3", (0, 0.39, 0.66))]
TAIL_END = (0, 0.37, 0.77)
TAIL_RADII = [0.035, 0.035, 0.03, 0.022]
for i, (bone, start) in enumerate(TAIL):
    end = TAIL[i + 1][1] if i + 1 < len(TAIL) else TAIL_END
    add_part(bone, "white" if bone == "tail.3" else "black",
             tapered(start, end, TAIL_RADII[i], TAIL_RADII[i + 1]), COAT_RANGE)
add_part("tail.3", "white", blob(TAIL_END, (0.022, 0.022, 0.022)), COAT_RANGE)

# Two-part legs. Each upper leg grows out of a rounded shoulder or haunch centred on the joint
# it swings from, so the join with the body stays smooth at any angle instead of showing the
# end of a tube. Front legs are white to the elbow; hind legs have white socks. Paw soles sit
# on the ground.
HIP_Z, KNEE_Z = 0.36, 0.19
UPPER_LEG, LOWER_LEG = HIP_Z - KNEE_Z, KNEE_Z
LEGS = {"leg.fl": (0.1, -0.2), "leg.fr": (-0.1, -0.2), "leg.bl": (0.1, 0.22), "leg.br": (-0.1, 0.22)}
for bone, (x, y) in LEGS.items():
    front = y < 0
    sock_top = HIP_Z - 0.06 if front else KNEE_Z + 0.04
    leg_colour = lambda p, top=sock_top: "white" if p.z < top else "black"
    add_part(bone, leg_colour,
             subdivided(tapered((x, y, KNEE_Z - 0.02), (x, y, HIP_Z), 0.048, 0.07), 2))
    # Shoulder (front, white like the chest) or haunch (hind, black): bigger at the back, like
    # a real dog's thighs. One colour each, so no jagged edge shows when the leg swings.
    radii = (0.068, 0.09, 0.1) if front else (0.075, 0.115, 0.12)
    shape = blob((x, y + (0 if front else 0.01), HIP_Z + 0.02), radii, subdiv=3)
    add_part(bone, "white" if front else "black", shape)
    add_part(bone + ".lower", "white", blob((x, y, KNEE_Z), (0.045, 0.045, 0.038)))
    add_part(bone + ".lower", "white", tapered((x, y, 0.04), (x, y, KNEE_Z), 0.046, 0.048))
    add_part(bone + ".lower", "white", blob((x, y - 0.015, 0.035), (0.058, 0.07, 0.035)), (0.0, 0.07))

# Assistance harness follows the torso bone through every animation.
if HARNESS:
    for side in (-1, 1):
        add_part("body", "tan", tapered((.14*side,-.20,.46),(.14*side,.18,.46),.018,.018))
        add_part("body", "tan", tapered((.14*side,-.16,.46),(.075*side,-.16,.585),.018,.018))
        add_part("body", "tan", tapered((.14*side,.13,.46),(.075*side,.13,.585),.018,.018))
        add_part("body", "tan", tapered((.075*side,-.16,.585),(.075*side,.13,.585),.018,.018))
        add_part("body", "tan", tapered((.075*side,.04,.585),(.075*side,.04,.72),.015,.015))
    add_part("body", "tan", tapered((-.075,.04,.72),(.075,.04,.72),.015,.015))

# The male's mane is anchored to the head and neck, leaving the face clear.
if not FEMALE:
    for x,y,z,r in [(0,-.34,.81,.10),(.09,-.34,.79,.095),(-.09,-.34,.79,.095),
                     (.14,-.36,.72,.09),(-.14,-.36,.72,.09),(.13,-.35,.63,.09),
                     (-.13,-.35,.63,.09),(0,-.33,.56,.09)]:
        add_part("head","tan",blob((x,y,z),(r,r,r),subdiv=2))
# Dark tail tuft.
add_part("tail.3","nose",blob(TAIL_END,(.055,.055,.065),subdiv=2))

# --------------------------------------------------------------------------------------
# Mesh object, material and rig
# --------------------------------------------------------------------------------------

mesh = bpy.data.meshes.new(NAME)
final.to_mesh(mesh)
final.free()
bpy.data.meshes.remove(scratch_mesh)
dog = bpy.data.objects.new(NAME, mesh)
scene.collection.objects.link(dog)

material = bpy.data.materials.new(NAME.lower())
material.use_nodes = True
nodes = material.node_tree.nodes
bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
bsdf.inputs["Roughness"].default_value = 0.85
tex_node = nodes.new("ShaderNodeTexImage")
tex_node.image = make_texture()
material.node_tree.links.new(tex_node.outputs["Color"], bsdf.inputs["Base Color"])
mesh.materials.append(material)

BODY_PIVOT_Z = 0.44
BONES = [
    ("root", (0, 0, 0), None),
    ("body", (0, 0, BODY_PIVOT_Z), "root"),
    ("neck", (0, -0.28, 0.52), "body"),
    ("head", (0, -0.36, 0.60), "neck"),
    ("jaw", (0, -0.5, 0.588), "head"),
    ("tongue", (0, -0.53, 0.61), "jaw"),
    ("eyes", (0, -0.55, 0.705), "head"),
    ("ear.l", tuple(EAR_BASE["ear.l"]), "head"),
    ("ear.r", tuple(EAR_BASE["ear.r"]), "head"),
    ("tail", TAIL[0][1], "body"),
    ("tail.2", TAIL[1][1], "tail"),
    ("tail.3", TAIL[2][1], "tail.2"),
    *[(leg, (x, y, HIP_Z), "body") for leg, (x, y) in LEGS.items()],
    *[(leg + ".lower", (x, y, KNEE_Z), leg) for leg, (x, y) in LEGS.items()],
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
        bone.tail = Vector(pivot) + Vector((0, 0, 0.08))
        bone.roll = 0.0
        if parent:
            bone.parent = armature_data.edit_bones[parent]
    bpy.ops.object.mode_set(mode="OBJECT")

for bone, start, end in parts:
    group = dog.vertex_groups.get(bone) or dog.vertex_groups.new(name=bone)
    group.add(list(range(start, end)), 1.0, "REPLACE")
dog.parent = rig
dog.modifiers.new("Armature", "ARMATURE").object = rig
if not FEMALE:
    rig.scale = (1.12,) * 3

for pose_bone in rig.pose.bones:
    pose_bone.rotation_mode = "XYZ"

# --------------------------------------------------------------------------------------
# Animation helpers (same approach as the sheep)
# --------------------------------------------------------------------------------------

TAU = 2 * math.pi

SIT_PITCH = -26
SIT_TAIL = 6   # lowered just enough to sweep along the ground, not through it

# Lying down: leg angles (upper, lower; negative swings forward). Front: upper arm angled down
# to the elbow on the ground, forearm flat out in front. Hind: thigh forward along the side,
# hock folded back under and splayed out (third value, degrees) so the folded shin lies beside
# the haunch, not in it. The body's height and tilt come from the legs (see solve_legs), so it
# settles onto them and the paws stay on the ground all the way down.
LEG_RADIUS = 0.075  # plus a little give, so a leg lying flat holds the body just off the ground
PAW_TIP = 0.07      # how far the paw reaches past the leg, front and back
LIE_FRONT_LEG = (-76, -10, 0)
LIE_HIND_LEG = (-82, 165, 22)


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
        self.feet = {leg: (0.0, 0.0) for leg in LEGS}
        # Legs posed directly instead of by IK: leg -> ((upper, lower, splay) degrees, weight).
        self.legs = {}

    def add(self, bone, rot=None, loc=None, scale=None):
        entry = self.bones.setdefault(bone, [[0.0, 0.0, 0.0], [0.0, 0.0, 0.0], [1.0, 1.0, 1.0]])
        for i in range(3):
            if rot:
                entry[0][i] += rot[i]
            if loc:
                entry[1][i] += loc[i]
            if scale:
                entry[2][i] *= scale[i]
        return self

    def foot(self, leg, forward, lift=0.0):
        self.feet[leg] = (forward, lift)
        return self

    def blink(self, amount):
        return self.add("eyes", scale=(1, 1 - 0.88 * min(max(amount, 0), 1), 1))

    def ears(self, back=0.0, droop=0.0):
        """back > 0 pins the ears back, < 0 pricks them forward; droop tips them outwards."""
        self.add("ear.l", rot=(-back, 0, -droop))
        self.add("ear.r", rot=(-back, 0, droop))
        return self

    def tail(self, wag=0.0, lift=0.0):
        """wag swings the tail side to side (degrees, spread down the chain); lift raises it."""
        for bone, share in (("tail", 0.4), ("tail.2", 0.8), ("tail.3", 1.0)):
            self.add(bone, rot=(lift * share * 0.6, wag * share, 0))
        return self

    def pant(self, t, rate=6, open_amount=1.0):
        # Cats rest with closed mouths; meowing is animated separately.
        return self

    def lying(self, w, front=None, back=None):
        """Sphinx down: belly on the ground, forearms flat out in front, hind legs folded
        alongside the body. front/back weight each end separately (they settle in turn)."""
        front = w if front is None else front
        back = w if back is None else back
        for leg in ("leg.fl", "leg.fr"):
            self.legs[leg] = (LIE_FRONT_LEG, front)
        for leg in ("leg.bl", "leg.br"):
            self.legs[leg] = (LIE_HIND_LEG, back)
        return self

    def sitting(self, w):
        """Rear dropped to the ground with the front legs straight. The body tips back by
        SIT_PITCH and drops just enough for the straight front legs to still reach the ground."""
        pitch = SIT_PITCH * w
        drop = -0.01 * w + LEGS["leg.fl"][1] * -math.sin(math.radians(pitch))
        self.add("body", rot=(pitch, 0, 0), loc=(0, drop, 0.03 * w))
        self.add("neck", rot=(22 * w, 0, 0))
        for leg in ("leg.bl", "leg.br"):
            self.foot(leg, 0.12 * w)
        for leg in ("leg.fl", "leg.fr"):
            self.foot(leg, -0.03 * w)
        return self

    def crouch(self, w):
        """The herding 'eye': body low, head down level with the back, staring forward."""
        self.add("body", loc=(0, -0.11 * w, 0), rot=(5 * w, 0, 0))
        self.add("neck", rot=(30 * w, 0, 0))
        self.add("head", rot=(-28 * w, 0, 0))
        return self.ears(back=-12 * w).tail(lift=-45 * w)


def gait(pose, t, phases, stance, stride, lift):
    for leg, offset in phases.items():
        p = (t + offset) % 1.0
        if p < stance:
            pose.foot(leg, stride / 2 - stride * p / stance)
        else:
            q = (p - stance) / (1 - stance)
            pose.foot(leg, -stride / 2 + stride * smooth(q), lift * math.sin(math.pi * q))
    return pose


def leg_height(upper, lower):
    """How high a leg posed at these angles (radians, relative to straight down) holds its hip:
    the drop from hip to paw, plus the leg's own thickness once it lies flat."""
    shin = upper + lower
    return (UPPER_LEG * math.cos(upper) + LOWER_LEG * math.cos(shin)
            + LEG_RADIUS * (1 - abs(math.cos(shin))))


def rest_body_on_legs(pose):
    """For directly posed legs (lying): lower and tilt the body so it stands on them."""
    angles = {leg: [a * w for a in fixed[:2]] for leg, (fixed, w) in pose.legs.items()}
    pitch = 0.0
    for _ in range(2):  # the tilt changes the legs' world angles; one refinement is plenty
        heights = {leg: leg_height(math.radians(u) + pitch, math.radians(l)) for leg, (u, l) in angles.items()}
        front = (heights["leg.fl"] + heights["leg.fr"]) / 2
        hind = (heights["leg.bl"] + heights["leg.br"]) / 2
        front_y, hind_y = LEGS["leg.fl"][1], LEGS["leg.bl"][1]
        sin_pitch = (hind - front) / (hind_y - front_y)
        pitch = math.asin(max(-1.0, min(1.0, sin_pitch)))
    drop = front - HIP_Z - front_y * sin_pitch
    pose.add("body", rot=(math.degrees(pitch), 0, 0), loc=(0, drop, 0))


def reach(down, back):
    """Two-bone IK in the leg's plane: angles (radians, from straight down; negative swings
    forward) of the upper and lower leg so the paw lands `down` below and `back` behind the hip.
    Every joint bends the same way, the lower leg folding back under the body (knee forward)."""
    d = math.hypot(back, down)
    d = min(max(d, abs(UPPER_LEG - LOWER_LEG) + 1e-4), UPPER_LEG + LOWER_LEG - 1e-4)
    bend = math.pi - math.acos((UPPER_LEG ** 2 + LOWER_LEG ** 2 - d ** 2) / (2 * UPPER_LEG * LOWER_LEG))
    spread = math.acos((UPPER_LEG ** 2 + d ** 2 - LOWER_LEG ** 2) / (2 * UPPER_LEG * d))
    aim = math.atan2(back, down)
    return aim - spread, bend


def solve_legs(pose):
    if len(pose.legs) == len(LEGS):
        rest_body_on_legs(pose)
    body_rot, body_loc, _ = pose.bones.get("body", ((0, 0, 0), (0, 0, 0), (1, 1, 1)))
    pitch = math.radians(body_rot[0])
    for leg, (x, rest_y) in LEGS.items():
        # Hip position after the body's pitch and lift (body loc Z moves it forward, -Y).
        rel_y, rel_z = rest_y, HIP_Z - BODY_PIVOT_Z
        hip_y = rel_y * math.cos(pitch) - rel_z * math.sin(pitch) - body_loc[2]
        hip_z = BODY_PIVOT_Z + body_loc[1] + rel_y * math.sin(pitch) + rel_z * math.cos(pitch)
        forward, lift = pose.feet[leg]
        foot_y = rest_y - forward
        upper, lower = reach(hip_z - lift, foot_y - hip_y)
        # The flat paw tilts with the shin and its edge dips below the sole; aim the foot that
        # much higher so the paw rests on the ground instead of sinking into it.
        upper, lower = reach(hip_z - lift - PAW_TIP * abs(math.sin(upper + lower)), foot_y - hip_y)
        upper, lower, splay = math.degrees(upper - pitch), math.degrees(lower), 0.0
        if leg in pose.legs:
            # Posed directly, in the body's frame (rest_body_on_legs already set the body).
            (fixed_upper, fixed_lower, fixed_splay), w = pose.legs[leg]
            upper, lower = fixed_upper * w, fixed_lower * w
            # Positive Z swings a left leg's foot outwards; mirror it for the right.
            splay = fixed_splay * w * (1 if x > 0 else -1)
        pose.add(leg, rot=(upper, 0, splay))
        pose.add(leg + ".lower", rot=(lower, 0, 0))


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
rig.animation_data_create()


def animation(name, frames):
    def register(pose_fn):
        old = bpy.data.actions.get(NAME + "_" + name)
        if old:
            bpy.data.actions.remove(old)
        # Prefixed in Blender so the dog's actions never mix with the sheep's when both
        # files are open; the NLA track (and so the exported animation) uses the plain name.
        action = bpy.data.actions.new(NAME + "_" + name)
        action.use_fake_user = True
        rig.animation_data.action = action
        # Bake at 60 Hz and correct the root when any part touches below the ground.
        # This also handles folded paws and tail during lying and sitting poses.
        samples = frames * 2
        for frame in range(samples + 1):
            key_pose(frame, pose_fn(frame / samples))
            scene.frame_set(frame)
            view_layer.update()
            evaluated = dog.evaluated_get(bpy.context.evaluated_depsgraph_get())
            deformed = evaluated.to_mesh()
            coords = np.empty(len(deformed.vertices) * 3, dtype=np.float32)
            deformed.vertices.foreach_get("co", coords)
            coords = coords.reshape(-1, 3)
            world = np.asarray(evaluated.matrix_world)
            minimum = float((coords @ world[2, :3] + world[2, 3]).min())
            evaluated.to_mesh_clear()
            if minimum < 0.002:
                root = rig.pose.bones["root"]
                root.location.y += 0.002 - minimum
                root.keyframe_insert("location", frame=frame)
        ACTIONS.append((name, action))
        return pose_fn
    return register


WALK = {"leg.bl": 0.0, "leg.fl": 0.75, "leg.br": 0.5, "leg.fr": 0.25}
TROT = {"leg.fl": 0.0, "leg.br": 0.0, "leg.fr": 0.5, "leg.bl": 0.5}
GALLOP = {"leg.fl": 0.0, "leg.fr": 0.12, "leg.br": 0.45, "leg.bl": 0.57}

# --------------------------------------------------------------------------------------
# Animations
# --------------------------------------------------------------------------------------

# Standing animations never bob the body up and down: the legs are straight, and near full
# stretch a few millimetres of bob swings the knees through 10-20 degrees, so the legs flicker.
# Breathing and excitement show in the neck, head and tail instead.


@animation("Idle", 60)
def idle(t):
    p = Pose().pant(t).tail(wag=14 * wave(t, 2))
    p.add("head", rot=(2 * wave(t, 1, 0.25), 3 * wave(t, 1), 0))
    p.ears(back=10 * bump(t, 0.5, 0.06))
    return p.blink(bump(t, 0.8, 0.05))


@animation("Walking", 32)
def walking(t):
    p = gait(Pose(), t, WALK, stance=0.62, stride=0.3, lift=0.08)
    p.add("body", loc=(0, 0.01 * wave(t, 2, 0.25), 0), rot=(0, 2 * wave(t), 0))
    p.add("head", rot=(3 * wave(t, 2), 0, 0))
    return p.tail(wag=12 * wave(t), lift=10).pant(t, rate=4, open_amount=0.6)


@animation("Trotting", 20)
def trotting(t):
    p = gait(Pose(), t, TROT, stance=0.5, stride=0.44, lift=0.1)
    p.add("body", loc=(0, 0.012 + 0.012 * wave(t, 2, 0.25), 0))
    p.add("head", rot=(3 * wave(t, 2), 0, 0))
    return p.tail(wag=10 * wave(t), lift=5).ears(back=10).pant(t, rate=4, open_amount=0.7)


@animation("Running", 16)
def running(t):
    p = gait(Pose(), t, GALLOP, stance=0.36, stride=0.62, lift=0.14)
    # Bounds along low enough that the stretched legs still reach the ground mid-stride.
    p.add("body", rot=(8 * wave(t, 1, 0.25), 0, 0), loc=(0, -0.02 + 0.015 * wave(t, 1, 0.25), 0))
    p.add("neck", rot=(10, 0, 0))
    p.add("head", rot=(-6 * wave(t, 1, 0.25) - 8, 0, 0))
    return p.tail(lift=-20, wag=6 * wave(t)).ears(back=35).pant(t, rate=2, open_amount=0.8)


@animation("Stalking", 48)
def stalking(t):
    """Herding creep: low and slow, head level with the back, eyes locked on the sheep."""
    p = gait(Pose().crouch(1.0), t, WALK, stance=0.75, stride=0.18, lift=0.05)
    p.add("body", loc=(0, 0.005 * wave(t, 2), 0))
    return p.blink(bump(t, 0.6, 0.03))


@animation("Crouch_Idle", 60)
def crouch_idle(t):
    """The herding 'eye': crouched and staring, shifting weight slightly, ready to move."""
    p = Pose().crouch(1.0)
    p.add("body", loc=(0, 0.006 * wave(t), 0.01 * wave(t, 1, 0.25)))
    p.add("head", rot=(0, 3 * wave(t, 1, 0.1), 0))
    return p.blink(bump(t, 0.4, 0.03))


@animation("Roar", 50)
def bark(t):
    p = Pose()
    for at in (0.2, 0.55):
        b = bump(t, at, 0.12)
        p.add("jaw", rot=(26 * b, 0, 0))
        p.add("neck", rot=(-10 * b, 0, 0))
        p.add("head", rot=(-8 * b, 0, 0))
        p.add("body", loc=(0, 0, 0.02 * b))  # lunges forward, not up
    p.ears(back=-15 * envelope(t, 0.05, 0.15, 0.75, 0.95))
    return p.tail(wag=20 * wave(t, 3), lift=15)


@animation("Lie_Down", 40)
def lie_down(t):
    # Front end goes down first, then the hind end settles.
    front, back = ramp(t, 0.0, 0.6), ramp(t, 0.3, 0.95)
    p = Pose().lying(0, front=front, back=back)
    return p.tail(wag=8 * wave(t, 2))


@animation("Lie_Idle", 90)
def lie_idle(t):
    p = Pose().lying(1).pant(t, rate=5, open_amount=0.8).tail(wag=6 * wave(t, 2))
    p.add("head", rot=(2 * wave(t, 1, 0.25), 6 * wave(t, 1), 0))
    return p.blink(bump(t, 0.4, 0.04))


@animation("Lie_StandUp", 30)
def lie_stand_up(t):
    back, front = 1 - ramp(t, 0.0, 0.6), 1 - ramp(t, 0.3, 0.95)
    return Pose().lying(0, front=front, back=back)


@animation("Sit_Down", 24)
def sit_down(t):
    return Pose().sitting(ramp(t, 0.0, 1.0)).tail(lift=-SIT_TAIL * ramp(t, 0.3, 1.0))


@animation("Sit_Idle", 60)
def sit_idle(t):
    # Tail sweeps along the ground while it waits.
    p = Pose().sitting(1).pant(t).tail(lift=-SIT_TAIL, wag=18 * wave(t, 2))
    p.add("head", rot=(2 * wave(t, 1, 0.25), 4 * wave(t, 1), 0))
    return p.blink(bump(t, 0.7, 0.04))


@animation("Sit_StandUp", 20)
def sit_stand_up(t):
    return Pose().sitting(1 - ramp(t, 0.0, 1.0)).tail(lift=-SIT_TAIL * (1 - ramp(t, 0.0, 0.7)))


@animation("Getting_Love", 90)
def getting_love(t):
    w = envelope(t, 0.05, 0.2, 0.8, 0.95)
    p = Pose()
    p.add("head", rot=(5 * w, 0, 18 * w))
    p.add("neck", rot=(0, 10 * w, 0))
    p.add("body", rot=(0, 0, 4 * wave(t, 3) * w))
    p.tail(wag=35 * wave(t, 10) * w, lift=15 * w)
    p.ears(back=25 * w, droop=10 * w)
    return p.pant(t, open_amount=0.6 * w).blink(0.95 * w)


@animation("Shake", 36)
def shake(t):
    """A wet-dog shake from the head down to the tail."""
    w = envelope(t, 0.05, 0.15, 0.75, 0.95)
    p = Pose()
    p.add("body", rot=(0, 0, 14 * wave(t, 6) * w))
    p.add("neck", rot=(0, 0, -10 * wave(t, 6, 0.1) * w))
    p.add("head", rot=(0, 0, 18 * wave(t, 6, 0.2) * w))
    p.ears(droop=25 * wave(t, 6, 0.3) * w)
    p.tail(wag=30 * wave(t, 6, 0.3) * w)
    return p.blink(0.8 * w)


@animation("Sleeping", 120)
def sleeping(t):
    p = Pose().lying(1)
    p.add("body", loc=(0, 0.008 * wave(t), 0))
    p.add("neck", rot=(32, 0, 0))
    p.add("head", rot=(14 + 2 * wave(t), 0, 10))
    p.ears(back=15, droop=10)
    return p.tail(lift=-30).blink(1.0)


# --------------------------------------------------------------------------------------
@animation("Pooping", 75)
def pooping(t):
    hold = envelope(t, 0.05, 0.22, 0.72, 0.95)
    p = Pose()
    p.add("neck", rot=(12 * hold, 0, 0))
    p.add("head", rot=(-8 * hold, 0, 0))
    p.tail(lift=-55 * hold)
    return p.blink(0.5 * hold)


# NLA tracks (one per animation) so the export only picks up the dog's own actions.
# --------------------------------------------------------------------------------------

for name, action in ACTIONS:
    track = rig.animation_data.nla_tracks.new()
    track.name = name
    track.strips.new(name, 0, action)
rig.animation_data.action = None
scene.frame_start, scene.frame_end = 0, 60

# --------------------------------------------------------------------------------------
# Export, then save just the dog (with the viewport left on Idle)
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
print(f"DOG: {len(mesh.vertices)} vertices, {len(BONES)} bones, {len(ACTIONS)} animations -> {OUT_DIR}")
