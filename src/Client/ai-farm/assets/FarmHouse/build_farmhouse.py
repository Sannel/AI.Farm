"""Builds the farm house from KayKit Dungeon Pack pieces: stone walls, a wood floor, an open
front door and a furnished room, under a red shingle roof made here (the pack has no roofs).

Run inside a live Blender session (e.g. through the Blender MCP server):
    exec(open(".../build_farmhouse.py").read(), {"OUT_DIR": ".../assets/FarmHouse"})
or headless:
    blender -b --python build_farmhouse.py -- <output_dir> [<dungeon pack gltf dir>]

It builds into its own "FarmHouse" scene, then writes FarmHouse.blend (just the house) and
FarmHouse.glb to the output directory. The pack pieces are imported from the pack's exported
glTF files, which stay outside the repo.

Layout is planned in pack units (walls are 4 x 4) and the whole house is scaled by SCALE so
the 4-unit walls stand about 2.8 m tall next to our characters. The front faces -Y in Blender
(+Z in Godot). Boxes named "*-colonly" become static collision in Godot: the walls (with a gap
for the door) and the bigger furniture. The door is left closed and without collision; Door.cs
gives it a body that swings with it.
"""

import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

PACK_GLTF = os.path.expanduser(
    "~/.config/itch/apps/kaykit-dungeon-pack/KayKit_Dungeon_Pack_1.1_EXTRA/Assets/gltf")

if "OUT_DIR" not in globals():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    OUT_DIR = argv[0] if argv else os.path.dirname(os.path.abspath(__file__))
    if len(argv) > 1:
        PACK_GLTF = argv[1]
OUT_DIR = os.path.abspath(OUT_DIR)
os.makedirs(OUT_DIR, exist_ok=True)

SCALE = 0.7
HALF_X, HALF_Y = 6, 4          # wall centre lines, in pack units
WALL_H, WALL_T = 4, 1
DOOR_X = 2                     # centre of the doorway piece along the front wall
DOOR_W = 2.4                   # walk-through gap left in the front collider

# --------------------------------------------------------------------------------------
# Scene
# --------------------------------------------------------------------------------------

if bpy.context.window:
    if bpy.context.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    old = bpy.data.scenes.get("FarmHouse")
    if old:
        for obj in list(old.collection.all_objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.scenes.remove(old, do_unlink=True)
    scene = bpy.data.scenes.new("FarmHouse")
    bpy.context.window.scene = scene
else:
    scene = bpy.context.scene
    scene.name = "FarmHouse"
    for obj in list(scene.collection.all_objects):
        bpy.data.objects.remove(obj, do_unlink=True)
view_layer = scene.view_layers[0]

# Leftovers from an earlier build (orphaned once their objects are gone).
for mesh in list(bpy.data.meshes):
    if mesh.users == 0:
        bpy.data.meshes.remove(mesh)

root = bpy.data.objects.new("FarmHouse", None)
scene.collection.objects.link(root)

# --------------------------------------------------------------------------------------
# Pack pieces
# --------------------------------------------------------------------------------------

material = None
_pieces = {}


def piece(name):
    """Imports a pack model once; returns [(part name, mesh, local matrix)]."""
    global material
    if name in _pieces:
        return _pieces[name]
    before = set(bpy.data.objects)
    with bpy.context.temp_override(scene=scene, view_layer=view_layer):
        bpy.ops.import_scene.gltf(filepath=os.path.join(PACK_GLTF, name + ".gltf"))
    parts = []
    for obj in set(bpy.data.objects) - before:
        if obj.type == "MESH":
            # Every import brings its own copy of the one pack material; share the first.
            for slot in obj.material_slots:
                if material is None:
                    material = slot.material
                    material.name = "dungeon"
                elif slot.material is not material:
                    old_material = slot.material
                    slot.material = material
                    obj.data.materials[slot.slot_index] = material
                    if old_material.users == 0:
                        bpy.data.materials.remove(old_material)
            obj.data.name = obj.name
            parts.append((obj.name, obj.data, obj.matrix_world.copy()))
    for obj in set(bpy.data.objects) - before:
        bpy.data.objects.remove(obj, do_unlink=True)
    parts.sort(key=lambda part: part[0])
    _pieces[name] = parts
    return parts


def place(name, x, y, z=0.0, rot=0.0):
    """Places a pack piece at (x, y, z) pack units, turned rot degrees about Z."""
    base = Matrix.Translation(Vector((x, y, z)) * SCALE) @ Matrix.Rotation(math.radians(rot), 4, "Z") \
        @ Matrix.Scale(SCALE, 4)
    placed = []
    for part, mesh, local in piece(name):
        obj = bpy.data.objects.new(f"{part}", mesh)
        obj.matrix_world = base @ local
        obj.parent = root
        scene.collection.objects.link(obj)
        placed.append(obj)
    return placed


# --------------------------------------------------------------------------------------
# Our own meshes (roof, gables) use the pack palette: an 8 x 4 grid of vertical gradients
# --------------------------------------------------------------------------------------

def palette_uv(col, row, t=0.5):
    """UV inside palette cell (col, row); t runs 0 (top of the gradient) to 1 (bottom)."""
    return ((col + 0.5) / 8, 1 - (row + 0.1 + 0.8 * t) / 4)


RED, WOOD, DARK_WOOD, STONE = (3, 2), (2, 0), (4, 0), (0, 0)


def add_mesh(name, build, collision=False):
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    build(bm, uv)
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    if not collision:
        mesh.materials.append(material)
        for poly in mesh.polygons:
            poly.use_smooth = False
    obj = bpy.data.objects.new(name, mesh)
    obj.parent = root
    scene.collection.objects.link(obj)
    return obj


def box(bm, uv, corners, cell, t=0.5):
    """Adds a hexahedron from 8 corners (bottom 4 then top 4, counter-clockwise from above)."""
    verts = [bm.verts.new(Vector(c) * SCALE) for c in corners]
    faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    for f in faces:
        face = bm.faces.new([verts[i] for i in f])
        for loop in face.loops:
            loop[uv].uv = palette_uv(*cell, t)
    return verts


def slab(bm, uv, x0, x1, y0, z0, y1, z1, thick, cell, t=0.5):
    """A board running along X whose cross-section goes from (y0, z0) to (y1, z1), lifted
    `thick` along its normal."""
    d = Vector((0, y1 - y0, z1 - z0)).normalized()
    n = Vector((0, -d.z, d.y)) if d.y >= 0 else Vector((0, d.z, -d.y))
    a, b = Vector((0, y0, z0)), Vector((0, y1, z1))
    corners =[(x0, a.y, a.z), (x0, b.y, b.z), (x1, b.y, b.z), (x1, a.y, a.z)]
    corners += [(x0, *(a + n * thick).yz), (x0, *(b + n * thick).yz),
                (x1, *(b + n * thick).yz), (x1, *(a + n * thick).yz)]
    verts = box(bm, uv, corners, cell, t)
    bmesh.ops.recalc_face_normals(bm, faces=list({f for v in verts for f in v.link_faces}))


# --------------------------------------------------------------------------------------
# Floor and walls
# --------------------------------------------------------------------------------------

for x in (-4, 0, 4):
    for y in (-2, 2):
        place("floor_wood_large", x, y)

# Corners: the L-shaped piece's arms point -X and -Y at 0 degrees.
place("wall_corner", HALF_X, HALF_Y, rot=0)
place("wall_corner", -HALF_X, HALF_Y, rot=90)
place("wall_corner", -HALF_X, -HALF_Y, rot=180)
place("wall_corner", HALF_X, -HALF_Y, rot=270)

# Back wall, then the front with a window and the open door (pieces face their inside +Y).
place("wall", -2, HALF_Y, rot=180)
place("wall_window_open", 2, HALF_Y, rot=180)
place("wall_window_open", -2, -HALF_Y, rot=0)
# The door is exported closed; the game swings it open (Door.cs).
place("wall_doorway", DOOR_X, -HALF_Y, rot=0)
place("wall_window_open", -HALF_X, 0, rot=90)
place("wall", HALF_X, 0, rot=270)

# --------------------------------------------------------------------------------------
# Roof: gable ends of planks and a red shingle roof, ridge along X
# --------------------------------------------------------------------------------------

RIDGE_Z = WALL_H + 3.2
EAVE_Y, EAVE_Z = HALF_Y + 1.1, WALL_H - 0.55
ROOF_X = HALF_X + 0.9
ROWS = 4


def build_gables(bm, uv):
    for side in (-1, 1):
        x0, x1 = side * (HALF_X - 0.45), side * (HALF_X + 0.45)
        lo, hi = min(x0, x1), max(x0, x1)
        # Triangle prism from the wall tops to the ridge.
        tri = [(-HALF_Y - 0.5, WALL_H), (HALF_Y + 0.5, WALL_H), (0, RIDGE_Z - 0.2)]
        vs = [bm.verts.new(Vector((x, y, z)) * SCALE) for x in (lo, hi) for y, z in tri]
        for f in ((0, 2, 1), (3, 4, 5), (0, 1, 4, 3), (1, 2, 5, 4), (2, 0, 3, 5)):
            face = bm.faces.new([vs[i] for i in f])
            for loop in face.loops:
                # Planks get lighter towards the peak.
                loop[uv].uv = palette_uv(*WOOD, 0.75 - 0.5 * (loop.vert.co.z / SCALE - WALL_H) / 3.2)
        # A dark beam across the bottom of each gable.
        box(bm, uv, [(lo - 0.08, -HALF_Y - 0.45, WALL_H - 0.1), (hi + 0.08, -HALF_Y - 0.45, WALL_H - 0.1),
                     (hi + 0.08, HALF_Y + 0.45, WALL_H - 0.1), (lo - 0.08, HALF_Y + 0.45, WALL_H - 0.1),
                     (lo - 0.08, -HALF_Y - 0.45, WALL_H + 0.25), (hi + 0.08, -HALF_Y - 0.45, WALL_H + 0.25),
                     (hi + 0.08, HALF_Y + 0.45, WALL_H + 0.25), (lo - 0.08, HALF_Y + 0.45, WALL_H + 0.25)],
            DARK_WOOD)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])


def build_roof(bm, uv):
    for side in (-1, 1):
        # Overlapping shingle rows, lowest first, each tipped out a little at its bottom edge.
        for row in range(ROWS):
            t0, t1 = row / ROWS, (row + 1.25) / ROWS
            y0 = side * (EAVE_Y * (1 - t0))
            z0 = EAVE_Z + (RIDGE_Z - EAVE_Z) * t0 + 0.05 * row
            y1 = side * (EAVE_Y * (1 - min(t1, 1)))
            z1 = EAVE_Z + (RIDGE_Z - EAVE_Z) * min(t1, 1) + 0.05 * row
            slab(bm, uv, -ROOF_X, ROOF_X, y0, z0 - 0.1, y1, z1, 0.32, RED, 0.25 + 0.6 * (1 - t0))
    # Ridge cap.
    slab(bm, uv, -ROOF_X - 0.1, ROOF_X + 0.1, -0.55, RIDGE_Z + 0.1, 0.55, RIDGE_Z + 0.1, 0.35, DARK_WOOD)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])


def build_chimney(bm, uv):
    cx, cy, size = 3.6, 2.0, 0.7
    z0, z1 = WALL_H, RIDGE_Z + 1.4
    box(bm, uv, [(cx - size, cy - size, z0), (cx + size, cy - size, z0), (cx + size, cy + size, z0),
                 (cx - size, cy + size, z0), (cx - size, cy - size, z1), (cx + size, cy - size, z1),
                 (cx + size, cy + size, z1), (cx - size, cy + size, z1)], STONE, 0.3)
    s2 = size + 0.15
    box(bm, uv, [(cx - s2, cy - s2, z1), (cx + s2, cy - s2, z1), (cx + s2, cy + s2, z1),
                 (cx - s2, cy + s2, z1), (cx - s2, cy - s2, z1 + 0.35), (cx + s2, cy - s2, z1 + 0.35),
                 (cx + s2, cy + s2, z1 + 0.35), (cx - s2, cy + s2, z1 + 0.35)], STONE, 0.1)


add_mesh("Gables", build_gables)
add_mesh("Roof", build_roof)
add_mesh("Chimney", build_chimney)

# --------------------------------------------------------------------------------------
# Furniture inside, a few props by the door
# --------------------------------------------------------------------------------------

place("bed_A_double", -3.8, 1.9, rot=180)
place("table_medium", 0.2, 0.3)
place("chair", -1.1, 0.3, rot=180)  # chairs face -X, backrest on +X
place("chair", 1.5, 0.3, rot=0)
# Wall stones stand out 0.33 from a wall's centre line; furniture goes just in front of them.
# The shelves hang from their back edge, the bookcase is centred on its origin.
place("shelves_decorated", -0.9, HALF_Y - 0.2, rot=0)
place("barrel_large", 4.4, 2.4)
place("crates_stacked", 4.4, -0.6, rot=20)
place("bookcase_single_decoratedA", -HALF_X + 0.64, -2.0, rot=90)

place("torch_mounted", 0.0, -HALF_Y - 0.5, z=2.6, rot=0)
place("bench", -2.2, -HALF_Y - 1.1, rot=0)
place("barrel_small", 4.4, -HALF_Y - 1.2)
place("crate_small", 5.3, -HALF_Y - 1.0, rot=15)

# --------------------------------------------------------------------------------------
# Collision boxes (Godot turns "-colonly" meshes into static bodies)
# --------------------------------------------------------------------------------------

def collider(name, x0, y0, x1, y1, z1=WALL_H):
    def build(bm, uv):
        box(bm, uv, [(x0, y0, 0), (x1, y0, 0), (x1, y1, 0), (x0, y1, 0),
                     (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)], STONE)
    add_mesh(name + "-colonly", build, collision=True).display_type = "WIRE"


T = WALL_T / 2
collider("WallBack", -HALF_X - T, HALF_Y - T, HALF_X + T, HALF_Y + T)
collider("WallLeft", -HALF_X - T, -HALF_Y - T, -HALF_X + T, HALF_Y + T)
collider("WallRight", HALF_X - T, -HALF_Y - T, HALF_X + T, HALF_Y + T)
collider("WallFrontLeft", -HALF_X - T, -HALF_Y - T, DOOR_X - DOOR_W / 2, -HALF_Y + T)
collider("WallFrontRight", DOOR_X + DOOR_W / 2, -HALF_Y - T, HALF_X + T, -HALF_Y + T)
collider("Bed", -5.5, 0.4, -2.1, 3.5, 1.0)
collider("Table", -0.8, -0.7, 1.2, 1.3, 1.0)
collider("Barrel", 3.5, 1.5, 5.3, 3.3, 2.0)
collider("Crates", 3.3, -1.7, 5.5, 0.5, 2.1)

# --------------------------------------------------------------------------------------
# Export, then save just the house
# --------------------------------------------------------------------------------------

with bpy.context.temp_override(scene=scene, view_layer=view_layer):
    bpy.ops.export_scene.gltf(
        filepath=os.path.join(OUT_DIR, "FarmHouse.glb"),
        export_format="GLB",
        use_active_scene=True,
        export_animations=False,
        export_yup=True,
    )
bpy.data.libraries.write(os.path.join(OUT_DIR, "FarmHouse.blend"), {scene}, path_remap="RELATIVE_ALL",
                         fake_user=True)
print(f"FARMHOUSE: {len(root.children)} parts, {len(_pieces)} pack models -> {OUT_DIR}")
