"""Shared toolkit for the KayKit-style animals: build the model from smooth parts, colour it
from a palette texture (plus painted maps for markings), rig it, animate it and export it.

Each animal has its own build script in assets/<Animal>/ that describes its shape and its
animations with this kit. Run an animal's script inside a live Blender session (e.g. through
the Blender MCP server):
    exec(open(".../build_cow.py").read(), {"OUT_DIR": ".../assets/Cow"})
or headless:
    blender -b --python build_cow.py -- <output_dir>

Conventions (same as the sheep and dog):
- The animal faces -Y in Blender, which becomes +Z (towards the camera) in Godot.
- Every bone is vertical with no roll, so in pose space rotation X pitches a part (positive tips
  its front down), rotation Y yaws it (positive turns to the animal's left), rotation Z rolls it,
  and location Y moves it up and down (location Z moves it forward).
- Legs are two bones solved by IK from where each foot should be, so feet plant instead of
  sliding. Every joint bends the same way, knee forward, with the lower leg folding back.
- Standing animations never bob the body up and down: near full stretch a few millimetres of
  bob swings the knees through 10-20 degrees and the legs flicker. Breathing and excitement
  go in the neck, head, ears and tail.
"""

import math
import os

import bmesh
import bpy
from mathutils import Euler, Matrix, Vector
from mathutils.bvhtree import BVHTree

TAU = 2 * math.pi

# --------------------------------------------------------------------------------------
# Timing curves for animations (t runs 0..1 over the clip)
# --------------------------------------------------------------------------------------


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


# --------------------------------------------------------------------------------------
# Shapes. Each returns a fresh bmesh in Blender units (metres).
# --------------------------------------------------------------------------------------


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


def cylinder(bottom_center, radius, height, segments=12, bevel=0.012):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segments,
                          radius1=radius, radius2=radius, depth=height)
    bmesh.ops.bevel(bm, geom=[e for e in bm.edges if e.calc_face_angle(0) > 0.5],
                    offset=bevel, segments=2, profile=0.5, affect="EDGES", clamp_overlap=True)
    bm.transform(Matrix.Translation(Vector(bottom_center) + Vector((0, 0, height / 2))))
    return bm


def tapered(start, end, r_start, r_end, segments=12):
    """A smooth tapered limb (or horn, or tail) running from start to end."""
    start, end = Vector(start), Vector(end)
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segments,
                          radius1=r_start, radius2=max(r_end, 1e-4), depth=(end - start).length)
    align = Vector((0, 0, 1)).rotation_difference((end - start).normalized()).to_matrix().to_4x4()
    bm.transform(Matrix.Translation((start + end) / 2) @ align)
    return bm


def sweep(points, widths, depths, width_axis=(1, 0, 0), segments=8):
    """One closed, continuous tapered surface, rather than overlapping beads.

    widths/depths are half extents at each station. With width_axis=None the
    broad face follows the YZ curve (feathers viewed from the animal's side).
    """
    assert len(points) == len(widths) == len(depths) and len(points) >= 2
    points = [Vector(p) for p in points]
    bm = bmesh.new()
    rows = []
    for i, p in enumerate(points):
        tangent = (points[min(i + 1, len(points) - 1)] - points[max(0, i - 1)]).normalized()
        axis = Vector(width_axis) if width_axis is not None else tangent.cross(Vector((1, 0, 0)))
        across = (axis - tangent * axis.dot(tangent)).normalized()
        assert across.length > 0.5, "sweep width axis must not follow the curve"
        normal = tangent.cross(across).normalized()
        rows.append([bm.verts.new(p + across * (widths[i] * math.cos(TAU * j / segments))
                                  + normal * (depths[i] * math.sin(TAU * j / segments)))
                     for j in range(segments)])
    for a, b in zip(rows, rows[1:]):
        for j in range(segments):
            k = (j + 1) % segments
            bm.faces.new((a[j], a[k], b[k], b[j]))
    bm.faces.new(tuple(reversed(rows[0])))
    bm.faces.new(tuple(rows[-1]))
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    return bm


def body_loft(center, size, shoulder=0.0):
    """Broad low-poly barrel planes, with chest/rump integrated into one mesh."""
    x, y, z = center
    width, length, height = size
    stations = [(-0.50, 0.48), (-0.43, 0.88), (-0.28, 1.0), (0.12, 1.02),
                (0.35, 0.96), (0.46, 0.76), (0.50, 0.35)]
    return sweep([(x, y + f * length, z - 0.02 * r + shoulder * max(0, 1 - abs(f + 0.28) / 0.3))
                  for f, r in stations],
                 [width * 0.5 * r for _, r in stations],
                 [height * 0.5 * r for _, r in stations], segments=12)


def capsule(start, end, r_start, r_end, segments=16, rings=4):
    """A tapered limb with rounded ends, each end a half sphere centred on its point. Joints made
    of two capsules meeting at a pivot stay round however far they bend (no seam or ridge)."""
    start, end = Vector(start), Vector(end)
    length = (end - start).length
    bm = bmesh.new()
    loops = []
    # Rings from the bottom pole (around start, pointing away from end) to the top pole.
    for i in range(rings, -1, -1):  # bottom cap: angle from the equator down to the pole
        a = math.pi / 2 * i / rings
        loops.append((-r_start * math.sin(a), r_start * math.cos(a)))
    for i in range(0, rings + 1):  # top cap
        a = math.pi / 2 * i / rings
        loops.append((length + r_end * math.sin(a), r_end * math.cos(a)))
    rows = []
    for z, r in loops:
        if r < 1e-6:
            rows.append([bm.verts.new((0, 0, z))])
        else:
            rows.append([bm.verts.new((r * math.cos(TAU * j / segments), r * math.sin(TAU * j / segments), z))
                         for j in range(segments)])
    for lower, upper in zip(rows, rows[1:]):
        for j in range(segments):
            k = (j + 1) % segments
            if len(lower) == 1:
                bm.faces.new((lower[0], upper[k], upper[j]))
            elif len(upper) == 1:
                bm.faces.new((lower[j], lower[k], upper[0]))
            else:
                bm.faces.new((lower[j], lower[k], upper[k], upper[j]))
    align = Vector((0, 0, 1)).rotation_difference((end - start).normalized()).to_matrix().to_4x4()
    bm.transform(Matrix.Translation(start) @ align)
    return bm


def snout(center, radii, taper, droop, subdiv=3):
    """An egg that narrows (by `taper`) and dips (by `droop`) towards its front (-Y) end."""
    bm = blob(center, radii, subdiv)
    back = center[1] + radii[1]
    for v in bm.verts:
        t = min(max((back - v.co.y) / (2 * radii[1]), 0.0), 1.0)
        scale = 1 - taper * t
        v.co.x = center[0] + (v.co.x - center[0]) * scale
        v.co.z = center[2] + (v.co.z - center[2]) * scale - droop * t
    return bm


def split(bm, z):
    """Cuts a part in two at height z (e.g. a snout into upper lip and lower jaw), capping both cut
    faces. Returns (upper, lower); closed, the halves meet without a seam."""
    lower = bm.copy()
    for half, keep_above in ((bm, True), (lower, False)):
        geom = list(half.verts) + list(half.edges) + list(half.faces)
        bmesh.ops.bisect_plane(half, geom=geom, dist=1e-6, plane_co=(0, 0, z), plane_no=(0, 0, 1),
                               clear_inner=keep_above, clear_outer=not keep_above)
        bmesh.ops.holes_fill(half, edges=list(half.edges), sides=0)
        bmesh.ops.recalc_face_normals(half, faces=list(half.faces))
    return bm, lower


def subdivided(bm, cuts):
    """Splits a part into small faces so colour patches can be painted onto it."""
    bmesh.ops.subdivide_edges(bm, edges=list(bm.edges), cuts=cuts, use_grid_fill=True)
    return bm


def barrel(bm, center, length, widen, sag):
    """Rounds a body out: up to `widen` (a fraction) wider through the middle than at the ends,
    and its underside up to `sag` (a fraction) deeper there, like a full belly."""
    cx, cy, cz = center
    for v in bm.verts:
        s = math.cos(min(abs(v.co.y - cy) / (length / 2), 1.0) * math.pi / 2)
        v.co.x = cx + (v.co.x - cx) * (1 + widen * s)
        if v.co.z < cz:
            v.co.z = cz + (v.co.z - cz) * (1 + sag * s)
    return bm


def torus(center, radius, thickness, rot=(0, 0, 0), segments=16, sides=8):
    """A ring (a nose ring, a collar) lying in the XY plane before rotating."""
    bm = bmesh.new()
    rings = []
    for i in range(segments):
        a = TAU * i / segments
        ring = []
        for j in range(sides):
            b = TAU * j / sides
            r = radius + thickness * math.cos(b)
            ring.append(bm.verts.new((r * math.cos(a), r * math.sin(a), thickness * math.sin(b))))
        rings.append(ring)
    for i in range(segments):
        for j in range(sides):
            a, b = rings[i], rings[(i + 1) % segments]
            bm.faces.new((a[j], b[j], b[(j + 1) % sides], a[(j + 1) % sides]))
    bm.transform(transform(center, rot))
    return bm


def inside(p, center, radii):
    """Whether point p is inside the ellipsoid (for drawing markings)."""
    return sum(((p[i] - center[i]) / radii[i]) ** 2 for i in range(3)) <= 1.0


def surface(*bms):
    """A ray-cast target made of these (not yet added) parts, for placing eyes and the like."""
    merged = bmesh.new()
    scratch = bpy.data.meshes.new("kit_surface_scratch")
    for bm in bms:
        bm.to_mesh(scratch)
        merged.from_mesh(scratch)
    bpy.data.meshes.remove(scratch)
    tree = BVHTree.FromBMesh(merged)
    merged.free()
    return tree


def hit(tree, origin, direction):
    """First point where a ray from origin along direction meets the surface (or None)."""
    return tree.ray_cast(Vector(origin), Vector(direction).normalized())[0]


# --------------------------------------------------------------------------------------
# Pose: a description of one frame, turned into bone transforms by the animal's leg solver
# --------------------------------------------------------------------------------------


class Pose:
    def __init__(self, animal):
        self.animal = animal
        self.bones = {}
        # Where each foot is: (forward offset from under its hip, height off the ground).
        self.feet = {leg: (0.0, 0.0) for leg in animal.legs}
        # Legs posed directly instead of by IK: leg -> ((upper, lower, splay) degrees, weight).
        self.folds = {}

    def add(self, bone, rot=None, loc=None, scale=None):
        if bone not in self.animal.bone_names:
            return self
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

    def fold(self, leg, upper, lower, splay=0.0, weight=1.0):
        """Poses a leg directly (degrees; negative upper swings it forward, lower bends the knee,
        splay swings the foot outwards), blended in by weight from straight. Once every leg is
        folded the body is lowered and tilted to rest on them (lying down, sitting)."""
        self.folds[leg] = ((upper, lower, splay), weight)
        return self

    def blink(self, amount):
        """amount 0 = open, 1 = closed (squashes the eyes flat)."""
        return self.add("eyes", scale=(1, 1 - 0.88 * min(max(amount, 0), 1), 1))

    def ears(self, back=0.0, droop=0.0):
        """back > 0 pins the ears back, < 0 pricks them forward; droop tips them outwards."""
        self.add("ear.l", rot=(-back, 0, -droop))
        self.add("ear.r", rot=(-back, 0, droop))
        return self

    def tail(self, wag=0.0, lift=0.0):
        """wag swings the tail side to side (degrees, spread down the chain); lift raises it."""
        chain = self.animal.tail_bones
        for i, bone in enumerate(chain):
            share = (i + 1) / len(chain)
            self.add(bone, rot=(lift * share * 0.6, wag * share, 0))
        return self

    def chew(self, t, rate=4, amount=1.0):
        return self.add("jaw", rot=(amount * 10 * (wave(t, rate) + 1) / 2, 0, 0))


def gait(pose, t, phases, stance, stride, lift):
    """Moves each foot through a step: planted and sliding back under the body for the stance
    part of the cycle, then lifted in an arc and swung forward."""
    for leg, offset in phases.items():
        p = (t + offset) % 1.0
        if p < stance:
            pose.foot(leg, stride / 2 - stride * p / stance)
        else:
            q = (p - stance) / (1 - stance)
            pose.foot(leg, -stride / 2 + stride * smooth(q), lift * math.sin(math.pi * q))
    return pose


WALK = {"leg.bl": 0.0, "leg.fl": 0.75, "leg.br": 0.5, "leg.fr": 0.25}
TROT = {"leg.fl": 0.0, "leg.br": 0.0, "leg.fr": 0.5, "leg.bl": 0.5}
BIPED = {"leg.l": 0.0, "leg.r": 0.5}
GALLOP = {"leg.fl": 0.0, "leg.fr": 0.12, "leg.br": 0.45, "leg.bl": 0.57}


# --------------------------------------------------------------------------------------
# The animal being built
# --------------------------------------------------------------------------------------


class Animal:
    """Collects parts, builds the textured, rigged mesh, and records animations.

    palette: name -> (top colour, bottom colour), up to 16 entries; each part's colour is a
    vertical gradient from top (light) to bottom (dark), over the heights in shade_range.

    The texture is a strip of palette cells down its right edge (for plain parts) and, in the
    square to their left, the painted parts: parts coloured by a function of position (spots,
    stripes, a pale belly) are unwrapped there and every texel takes the colour of the 3D point
    it lands on, so markings wrap smoothly round the body whichever way it is viewed, and their
    edges are anti-aliased."""

    TEXTURE_SIZE = 1024
    CELL = 64  # palette cell height; the strip is one cell wide
    MARGIN = 0.004  # gap between painted islands, as a fraction of the painted square

    def __init__(self, name, out_dir, palette):
        self.name = name
        self.out_dir = os.path.abspath(out_dir)
        os.makedirs(self.out_dir, exist_ok=True)
        assert len(palette) <= self.TEXTURE_SIZE // self.CELL, "the palette has room for 16 colours"
        self.palette = {key: (i, top, bottom) for i, (key, (top, bottom)) in enumerate(palette.items())}
        self.painters = []  # colour functions of painted parts; a face's index into this is its painter

        self.scene = self._fresh_scene()
        self.view_layer = self.scene.view_layers[0]
        self.final = bmesh.new()
        self.painter_layer = self.final.faces.layers.int.new("painter")
        self.scratch = bpy.data.meshes.new(name + "_scratch")
        self.parts = []  # (bone, first vertex, end vertex, label)
        self.blends = []  # (primary bone, secondary bone, first vertex, secondary weights)
        self.actions = []  # (name, action)
        self.legs = {}
        self.tail_bones = []
        self.bone_names = set()
        self.rig = None
        self.mesh_object = None

    # --- scene ---------------------------------------------------------------------------

    def _fresh_scene(self):
        """Builds in a scene of its own: in a live session a new scene shown in the window;
        headless (no window) the startup scene is emptied and used instead."""
        name = self.name
        if bpy.context.window:
            if bpy.context.mode != "OBJECT":
                bpy.ops.object.mode_set(mode="OBJECT")
            old = bpy.data.scenes.get(name)
            if old:
                for obj in list(old.collection.all_objects):
                    bpy.data.objects.remove(obj, do_unlink=True)
                bpy.data.scenes.remove(old, do_unlink=True)
            for collection, names in ((bpy.data.objects, (name, name + "_Rig")), (bpy.data.meshes, (name,)),
                                      (bpy.data.armatures, (name + "_Rig",)),
                                      (bpy.data.materials, (name.lower(),)),
                                      (bpy.data.images, (name.lower() + "_texture",))):
                for item in names:
                    if item in collection:
                        collection.remove(collection[item])
            scene = bpy.data.scenes.new(name)
            bpy.context.window.scene = scene
        else:
            scene = bpy.context.scene
            scene.name = name
            for obj in list(scene.collection.all_objects):
                bpy.data.objects.remove(obj, do_unlink=True)
        # Pose functions describe 30-fps clip lengths. Bake at 60 fps so fast
        # hoof arcs stay above the floor between exported samples as well.
        scene.render.fps = 60
        return scene

    # --- texture -------------------------------------------------------------------------

    def cell_uv(self, index, t):
        size, cell = self.TEXTURE_SIZE, self.CELL
        v_top = 1.0 - (index * cell + 4) / size
        v_bottom = 1.0 - ((index + 1) * cell - 4) / size
        return ((size - cell / 2) / size, v_top - t * (v_top - v_bottom))

    def _shade(self, name, z):
        """The colour (0..1 RGB) of palette entry `name` at height z."""
        _, top, bottom = self.palette[name]
        z_low, z_high = self.shade_range
        t = min(max((z_high - z) / (z_high - z_low), 0.0), 1.0)
        return [(top[c] + (bottom[c] - top[c]) * t) / 255.0 for c in range(3)]

    def _unwrap_painted(self, obj):
        """Unwraps the painted faces into the painted square (UVs 0..1 for now)."""
        mesh = obj.data
        painters = [0] * len(mesh.polygons)
        mesh.attributes["painter"].data.foreach_get("value", painters)
        # The unwrap can touch more than the selected faces, so keep the plain parts' UVs aside.
        plain_uvs = [0.0] * (2 * len(mesh.loops))
        mesh.uv_layers.active.data.foreach_get("uv", plain_uvs)
        self.view_layer.objects.active = obj
        with bpy.context.temp_override(scene=self.scene, view_layer=self.view_layer, active_object=obj,
                                       object=obj, selected_objects=[obj], selected_editable_objects=[obj]):
            bpy.ops.object.mode_set(mode="EDIT")
            bm = bmesh.from_edit_mesh(mesh)
            bm.faces.ensure_lookup_table()
            # Only the painted faces are unwrapped and packed: the plain ones are hidden meanwhile.
            for face in bm.faces:
                face.select_set(False)
            for face in bm.faces:
                if painters[face.index] >= 0:
                    face.select_set(True)
                else:
                    face.hide_set(True)
            bm.select_flush_mode()
            bmesh.update_edit_mesh(mesh)
            bpy.ops.uv.select_all(action="SELECT")
            bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=self.MARGIN,
                                     area_weight=0.0, correct_aspect=True, scale_to_bounds=False)
            bpy.ops.uv.select_all(action="SELECT")
            bpy.ops.uv.pack_islands(rotate=True, margin=self.MARGIN)
            bpy.ops.mesh.reveal(select=False)
            bpy.ops.object.mode_set(mode="OBJECT")
        uv_layer = mesh.uv_layers.active.data
        for polygon in mesh.polygons:
            if painters[polygon.index] < 0:
                for i in polygon.loop_indices:
                    uv_layer[i].uv = plain_uvs[2 * i:2 * i + 2]
        return painters

    def _paint(self, mesh, painters, image_pixels, square):
        """Rasterises the painted faces at twice the texture's resolution to find the 3D point
        under each sample, colours one sample per texel (all four where the colour changes, for
        smooth edges), then spreads colour a few texels past island edges so mipmaps don't
        bleed the background in."""
        import numpy as np

        sub = square * 2
        positions = np.zeros((sub, sub, 3), dtype=np.float32)
        owner = np.full((sub, sub), -1, dtype=np.int32)
        uv_layer = mesh.uv_layers.active.data
        mesh.calc_loop_triangles()
        for tri in mesh.loop_triangles:
            painter = painters[tri.polygon_index]
            if painter < 0:
                continue
            uvs = np.array([uv_layer[i].uv for i in tri.loops], dtype=np.float64)
            xs, ys = uvs[:, 0] * sub, (1.0 - uvs[:, 1]) * sub
            corners = np.array([mesh.vertices[v].co for v in tri.vertices], dtype=np.float64)
            x0, x1 = max(int(xs.min()) - 1, 0), min(int(xs.max()) + 2, sub)
            y0, y1 = max(int(ys.min()) - 1, 0), min(int(ys.max()) + 2, sub)
            if x0 >= x1 or y0 >= y1:
                continue
            gx, gy = np.meshgrid(np.arange(x0, x1) + 0.5, np.arange(y0, y1) + 0.5)
            denom = (ys[1] - ys[2]) * (xs[0] - xs[2]) + (xs[2] - xs[1]) * (ys[0] - ys[2])
            if abs(denom) < 1e-12:
                continue
            w0 = ((ys[1] - ys[2]) * (gx - xs[2]) + (xs[2] - xs[1]) * (gy - ys[2])) / denom
            w1 = ((ys[2] - ys[0]) * (gx - xs[2]) + (xs[0] - xs[2]) * (gy - ys[2])) / denom
            w2 = 1.0 - w0 - w1
            covered = (w0 >= -1e-6) & (w1 >= -1e-6) & (w2 >= -1e-6)
            if covered.any():
                points = (w0[..., None] * corners[0] + w1[..., None] * corners[1] + w2[..., None] * corners[2])
                region = positions[y0:y1, x0:x1]
                region[covered] = points[covered]
                owner[y0:y1, x0:x1][covered] = painter
            # Slivers can miss every sample; stamp their corners and centre so they still get
            # their own colour instead of whatever bleeds in from a neighbouring island.
            for weights in ((1, 0, 0), (0, 1, 0), (0, 0, 1), (1 / 3, 1 / 3, 1 / 3)):
                sx = int(min(max(sum(w * x for w, x in zip(weights, xs)), 0), sub - 1))
                sy = int(min(max(sum(w * y for w, y in zip(weights, ys)), 0), sub - 1))
                if owner[sy, sx] < 0:
                    positions[sy, sx] = sum(w * c for w, c in zip(weights, corners))
                    owner[sy, sx] = painter

        # Per texel: its four samples.
        owner4 = owner.reshape(square, 2, square, 2).transpose(0, 2, 1, 3).reshape(square, square, 4)
        pos4 = positions.reshape(square, 2, square, 2, 3).transpose(0, 2, 1, 3, 4).reshape(square, square, 4, 3)
        has = owner4 >= 0
        filled = has.any(axis=2)
        first = has.argmax(axis=2)
        names = {}
        name_ids = np.full((square, square), -1, dtype=np.int32)
        colours = np.zeros((square, square, 3), dtype=np.float32)
        rows, cols = np.nonzero(filled)
        for r, c in zip(rows.tolist(), cols.tolist()):
            k = first[r, c]
            point = Vector(pos4[r, c, k].tolist())
            name = self.painters[owner4[r, c, k]](point)
            name_ids[r, c] = names.setdefault(name, len(names))
            colours[r, c] = self._shade(name, point.z)
        # Texels where the colour changes take the average of all their samples.
        edge = np.zeros_like(filled)
        for dr, dc in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            shifted = np.roll(name_ids, (dr, dc), axis=(0, 1))
            edge |= filled & (shifted >= 0) & (shifted != name_ids)
        for r, c in zip(*[a.tolist() for a in np.nonzero(edge)]):
            total, count = np.zeros(3), 0
            for k in range(4):
                if has[r, c, k]:
                    point = Vector(pos4[r, c, k].tolist())
                    total += self._shade(self.painters[owner4[r, c, k]](point), point.z)
                    count += 1
            colours[r, c] = total / count
        # Bleed colour outwards over the gaps between islands.
        for _ in range(6):
            empty = ~filled
            if not empty.any():
                break
            total = np.zeros_like(colours)
            count = np.zeros((square, square), dtype=np.float32)
            for dr, dc in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                total += np.roll(colours * filled[..., None], (dr, dc), axis=(0, 1))
                count += np.roll(filled.astype(np.float32), (dr, dc), axis=(0, 1))
            grow = empty & (count > 0)
            colours[grow] = total[grow] / count[grow][:, None]
            filled = filled | grow
        image_pixels[:square, :square, :3] = colours

    def _make_texture(self, obj):
        import numpy as np

        size, cell = self.TEXTURE_SIZE, self.CELL
        square = size - cell
        pixels = np.ones((size, size, 4), dtype=np.float32)  # rows from the top
        for name, (index, top, bottom) in self.palette.items():
            t = np.linspace(0.0, 1.0, cell)[:, None]
            gradient = (np.array(top) + (np.array(bottom) - np.array(top)) * t) / 255.0
            pixels[index * cell:(index + 1) * cell, square:, :3] = gradient[:, None, :]

        if self.painters:
            painters = self._unwrap_painted(obj)
            mesh = obj.data
            self._paint(mesh, painters, pixels, square)
            # Move the painted UVs from 0..1 into the painted square.
            uv_layer = mesh.uv_layers.active.data
            scale = square / size
            for polygon in mesh.polygons:
                if painters[polygon.index] >= 0:
                    for i in polygon.loop_indices:
                        u, v = uv_layer[i].uv
                        uv_layer[i].uv = (u * scale, 1.0 - (1.0 - v) * scale)

        key = self.name.lower() + "_texture"
        old = bpy.data.images.get(key)
        if old:
            bpy.data.images.remove(old)
        image = bpy.data.images.new(key, size, size, alpha=False)
        image.pixels.foreach_set(pixels[::-1].ravel())
        image.filepath_raw = os.path.join(self.out_dir, key + ".png")
        image.file_format = "PNG"
        image.save()
        return image

    # --- parts ---------------------------------------------------------------------------

    def part(self, bone, colour, bm, z_range=None, blend=None):
        """Adds a part, normally skinned 100% to one bone.

        colour: a palette name, or a function of a 3D point returning one, which paints the part
        (markings that wrap round it). z_range: the heights a plain part's colour gradient runs
        between (default: the part's own); painted parts use the animal's shade_range.
        blend: optional (secondary bone, vertex float layer name). That layer
        supplies its weight; the primary bone gets the remainder. Subdivision
        interpolates the layer, so welded joints can bend without opening seams.
        """
        uv = bm.loops.layers.uv.verify()
        painter_layer = bm.faces.layers.int.get("painter") or bm.faces.layers.int.new("painter")
        bm.normal_update()
        if z_range is None:
            zs = [v.co.z for v in bm.verts]
            z_range = (min(zs), max(zs))
        z_min, z_max = z_range
        height = max(z_max - z_min, 1e-6)
        painter = -1
        if callable(colour):
            painter = len(self.painters)
            self.painters.append(colour)
        for face in bm.faces:
            face.smooth = True
            face[painter_layer] = painter
            if painter >= 0:
                continue
            index = self.palette[colour][0]
            for loop in face.loops:
                t = min(max((z_max - loop.vert.co.z) / height, 0.0), 1.0)
                loop[uv].uv = self.cell_uv(index, t)
        secondary_weights = None
        if blend is not None:
            weight_layer = bm.verts.layers.float.get(blend[1])
            assert weight_layer is not None, f"missing skin weight layer {blend[1]}"
            bm.verts.index_update()
            secondary_weights = [0.0] * len(bm.verts)
            for vertex in bm.verts:
                secondary_weights[vertex.index] = min(max(vertex[weight_layer], 0.0), 1.0)
        bm.to_mesh(self.scratch)
        bm.free()
        start = len(self.final.verts)
        self.final.from_mesh(self.scratch)
        self.final.verts.ensure_lookup_table()
        label = colour if isinstance(colour, str) else getattr(colour, "__name__", "painted")
        self.parts.append((bone, start, len(self.final.verts), label))
        if secondary_weights is not None:
            self.blends.append((bone, blend[0], start, secondary_weights))

    # --- rig -----------------------------------------------------------------------------

    def build(self, bones, legs, hip_z, knee_z, body_pivot_z, foot_tip, leg_radius, tail=(),
              shade_range=(0.0, 1.0), roughness=0.85, knee_back=False):
        """Makes the mesh, texture, material and rig.

        bones: [(name, pivot, parent)], parents listed first. legs: {leg bone: (x, y)} hip
        positions; each has a "<leg>.lower" bone at knee height. foot_tip: how far the foot
        reaches past the shin (its rim dips below the sole when the shin tilts). leg_radius: how
        thick a leg lying flat is (with a little give), which a lying body rests on. knee_back:
        the joint bends backwards (a bird's heel) instead of forwards. A "<leg>.foot" bone, if
        there is one, is kept level so toes stay flat on the ground."""
        self.legs = dict(legs)
        self.knee_back = knee_back
        self.hip_z, self.knee_z, self.body_pivot_z, self.foot_tip = hip_z, knee_z, body_pivot_z, foot_tip
        self.leg_radius = leg_radius
        self.upper_leg, self.lower_leg = hip_z - knee_z, knee_z
        self.tail_bones = list(tail)
        self.bone_names = {name for name, _, _ in bones}

        mesh = bpy.data.meshes.new(self.name)
        self.final.to_mesh(mesh)
        self.final.free()
        bpy.data.meshes.remove(self.scratch)
        obj = bpy.data.objects.new(self.name, mesh)
        self.scene.collection.objects.link(obj)
        self.mesh_object = obj

        self.shade_range = shade_range
        material = bpy.data.materials.new(self.name.lower())
        material.use_nodes = True
        nodes = material.node_tree.nodes
        bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
        bsdf.inputs["Roughness"].default_value = roughness
        tex_node = nodes.new("ShaderNodeTexImage")
        tex_node.image = self._make_texture(obj)
        material.node_tree.links.new(tex_node.outputs["Color"], bsdf.inputs["Base Color"])
        mesh.materials.append(material)
        # Preserve deliberate creases (hoof soles, feather ridges, muzzle caps),
        # while leaving curved cheeks and limbs smooth, as in the reference pack.
        mesh.set_sharp_from_angle(angle=math.radians(55))

        armature = bpy.data.armatures.new(self.name + "_Rig")
        rig = bpy.data.objects.new(self.name + "_Rig", armature)
        self.scene.collection.objects.link(rig)
        self.view_layer.objects.active = rig
        with bpy.context.temp_override(scene=self.scene, view_layer=self.view_layer, active_object=rig, object=rig):
            bpy.ops.object.mode_set(mode="EDIT")
            for name, pivot, parent in bones:
                bone = armature.edit_bones.new(name)
                bone.head = pivot
                bone.tail = Vector(pivot) + Vector((0, 0, 0.08))
                bone.roll = 0.0
                if parent:
                    bone.parent = armature.edit_bones[parent]
            bpy.ops.object.mode_set(mode="OBJECT")

        for bone, start, end, _ in self.parts:
            group = obj.vertex_groups.get(bone) or obj.vertex_groups.new(name=bone)
            group.add(list(range(start, end)), 1.0, "REPLACE")
        for primary, secondary, start, weights in self.blends:
            assert primary in self.bone_names and secondary in self.bone_names
            first = obj.vertex_groups[primary]
            second = obj.vertex_groups.get(secondary) or obj.vertex_groups.new(name=secondary)
            for index, weight in enumerate(weights, start):
                if weight >= 1.0:
                    first.remove([index])
                else:
                    first.add([index], 1.0 - weight, "REPLACE")
                if weight > 0.0:
                    second.add([index], weight, "REPLACE")
        # Actual sole / joint geometry is used for folded-leg ground contact.
        # A cylinder's tilted rim and a splayed capsule cannot be represented by
        # the old single leg_radius approximation.
        import numpy as np
        self._leg_points = {}
        for leg in self.legs:
            self._leg_points[leg] = {}
            for bone in (leg, leg + ".lower", leg + ".foot"):
                indices = [i for owner, start, end, _ in self.parts if owner == bone for i in range(start, end)]
                self._leg_points[leg][bone] = np.array([(*mesh.vertices[i].co, 1.0) for i in indices])
        obj.parent = rig
        obj.modifiers.new("Armature", "ARMATURE").object = rig
        for pose_bone in rig.pose.bones:
            pose_bone.rotation_mode = "XYZ"
        rig.animation_data_create()
        self.rig = rig
        return obj

    # --- animation -----------------------------------------------------------------------

    def pose(self):
        return Pose(self)

    def _reach(self, down, back):
        """Two-bone IK in the leg's plane: angles (radians from straight down; negative swings
        forward) of the upper and lower leg so the foot lands `down` below and `back` behind
        the hip, with the knee forward (or backward for knee_back)."""
        upper_leg, lower_leg = self.upper_leg, self.lower_leg
        d = math.hypot(back, down)
        d = min(max(d, abs(upper_leg - lower_leg) + 1e-4), upper_leg + lower_leg - 1e-4)
        bend = math.pi - math.acos((upper_leg ** 2 + lower_leg ** 2 - d ** 2) / (2 * upper_leg * lower_leg))
        spread = math.acos((upper_leg ** 2 + d ** 2 - lower_leg ** 2) / (2 * upper_leg * d))
        if self.knee_back:
            return math.atan2(back, down) + spread, -bend
        return math.atan2(back, down) - spread, bend

    def _leg_height(self, upper, lower, leg=None, splay=0.0, pitch=0.0):
        """How high a leg posed at these angles (radians from straight down) holds its hip: the
        drop from hip to its lowest point, plus the leg's own thickness lying there."""
        if leg is not None:
            import numpy as np
            x, y = self.legs[leg]
            hip, knee = Vector((x, y, self.hip_z)), Vector((x, y, self.knee_z))
            foot_bone = self.rig.data.bones.get(leg + ".foot")
            foot = foot_bone.head_local.copy() if foot_bone else Vector((x, y, 0))
            basis = self.rig.data.bones[leg].matrix_local.to_3x3().to_4x4()
            upper_matrix = (Matrix.Translation(hip) @ basis @ Euler((upper, 0, splay)).to_matrix().to_4x4()
                            @ basis.inverted() @ Matrix.Translation(-hip))
            lower_matrix = upper_matrix @ Matrix.Translation(knee) @ Matrix.Rotation(lower, 4, "X") @ Matrix.Translation(-knee)
            # Bird toes are kept level by a separate foot bone.
            foot_matrix = lower_matrix @ Matrix.Translation(foot) @ Matrix.Rotation(-(pitch + upper + lower), 4, "X") @ Matrix.Translation(-foot)
            body_matrix = Matrix.Translation(hip) @ Matrix.Rotation(pitch, 4, "X") @ Matrix.Translation(-hip)
            minimum = self.hip_z
            for bone, matrix in ((leg, upper_matrix), (leg + ".lower", lower_matrix), (leg + ".foot", foot_matrix)):
                points = self._leg_points[leg][bone]
                if len(points):
                    minimum = min(minimum, float((points @ np.array(body_matrix @ matrix)[2]).min()))
            return self.hip_z - minimum + 0.003
        shin = upper + lower
        knee = self.upper_leg * math.cos(upper)
        foot = knee + self.lower_leg * math.cos(shin)
        # Whichever is lower rests on the ground: the foot, or (thighs swung far forward, as on
        # a long-legged animal tipping down onto its chest) the knee.
        return max(foot + self.leg_radius * (1 - abs(math.cos(shin))), knee + self.leg_radius)

    def _rest_body_on_legs(self, pose):
        """For folded legs: lower and tilt the body so it stands (or lies) on them."""
        angles = {leg: [a * w for a in fixed[:2]] for leg, (fixed, w) in pose.folds.items()}
        front_legs = [leg for leg, (_, y) in self.legs.items() if y < 0]
        hind_legs = [leg for leg, (_, y) in self.legs.items() if y >= 0]
        if not front_legs:  # two legs: just sink to their height
            heights = [self._leg_height(math.radians(u), math.radians(l), leg,
                       math.radians(pose.folds[leg][0][2] * pose.folds[leg][1]) * (1 if self.legs[leg][0] > 0 else -1))
                       for leg, (u, l) in angles.items()]
            pose.add("body", loc=(0, sum(heights) / len(heights) - self.hip_z, 0))
            return
        front_y = sum(self.legs[leg][1] for leg in front_legs) / len(front_legs)
        hind_y = sum(self.legs[leg][1] for leg in hind_legs) / len(hind_legs)
        pitch = 0.0
        for _ in range(5):  # converge using the actual tilted/splayed mesh, not a radius estimate
            heights = {leg: self._leg_height(math.radians(u), math.radians(l), leg,
                       math.radians(pose.folds[leg][0][2] * pose.folds[leg][1]) * (1 if self.legs[leg][0] > 0 else -1), pitch)
                        for leg, (u, l) in angles.items()}
            front = sum(heights[leg] for leg in front_legs) / len(front_legs)
            hind = sum(heights[leg] for leg in hind_legs) / len(hind_legs)
            sin_pitch = max(-1.0, min(1.0, (hind - front) / (hind_y - front_y)))
            pitch = math.asin(sin_pitch)
        drop = front - self.hip_z - front_y * sin_pitch
        pose.add("body", rot=(math.degrees(pitch), 0, 0), loc=(0, drop, 0))

    def _solve_legs(self, pose):
        if self.legs and len(pose.folds) == len(self.legs):
            self._rest_body_on_legs(pose)
        body_rot, body_loc, body_scale = pose.bones.get("body", ((0, 0, 0), (0, 0, 0), (1, 1, 1)))
        basis = self.rig.data.bones["body"].matrix_local.to_3x3()
        body_matrix = (basis @ Euler([math.radians(a) for a in body_rot]).to_matrix()
                       @ Matrix.Diagonal(body_scale) @ basis.inverted())
        body_translation = basis @ Vector(body_loc)
        body_pivot = Vector((0, 0, self.body_pivot_z))
        for leg, (x, rest_y) in self.legs.items():
            # Hip position after the body's pitch and lift (body loc Z moves it forward, -Y).
            hip = body_pivot + body_translation + body_matrix @ Vector((x, rest_y, self.hip_z - self.body_pivot_z))
            forward, lift = pose.feet[leg]
            # The foot tilts with the shin and its rim dips below the sole; aim that much higher
            # (which tilts the shin a little more, so settle it over a few rounds).
            rim = 0.002
            for _ in range(8):
                target = Vector((x, rest_y - forward, lift + rim))
                delta = body_matrix.inverted() @ (target - hip)
                splay = math.atan2(delta.x, -delta.z)
                upper, lower = self._reach(math.hypot(delta.x, delta.z), delta.y)
                shin = body_matrix @ Matrix.Rotation(-splay, 3, "Y") @ Matrix.Rotation(upper + lower, 3, "X")
                # Exact support radius of the tilted sole plane; this includes
                # body roll/yaw, which the previous sagittal-only IK ignored.
                rim = (0.002 if leg + ".foot" in self.bone_names else
                       0.002 + self.foot_tip * math.hypot(shin[2][0], shin[2][1]))
            upper, lower, splay = math.degrees(upper), math.degrees(lower), math.degrees(splay)
            if leg in pose.folds:
                # Posed directly, in the body's frame (the body was already set to rest on them).
                (fixed_upper, fixed_lower, fixed_splay), w = pose.folds[leg]
                upper, lower = fixed_upper * w, fixed_lower * w
                splay = fixed_splay * w * (1 if x > 0 else -1)  # +Z swings a left foot outwards
            pose.add(leg, rot=(upper, 0, splay))
            pose.add(leg + ".lower", rot=(lower, 0, 0))
            shin = body_matrix @ Matrix.Rotation(math.radians(-splay), 3, "Y") @ Matrix.Rotation(math.radians(upper + lower), 3, "X")
            foot_rotation = (basis.inverted() @ shin.inverted() @ basis).to_euler()
            pose.add(leg + ".foot", rot=tuple(math.degrees(a) for a in foot_rotation))

    def _key(self, frame, pose):
        self._solve_legs(pose)
        for pose_bone in self.rig.pose.bones:
            rot, loc, scale = pose.bones.get(pose_bone.name, ((0, 0, 0), (0, 0, 0), (1, 1, 1)))
            pose_bone.rotation_euler = [math.radians(a) for a in rot]
            pose_bone.location = loc
            pose_bone.scale = scale
            pose_bone.keyframe_insert("rotation_euler", frame=frame)
            pose_bone.keyframe_insert("location", frame=frame)
            pose_bone.keyframe_insert("scale", frame=frame)

    def animation(self, name, frames):
        """Decorator: records an animation from a function of t (0..1) returning a Pose."""
        def register(pose_fn):
            # Prefixed in Blender so different animals' actions never mix when several are
            # open; the NLA track (and so the exported animation) uses the plain name.
            key = f"{self.name}_{name}"
            old = bpy.data.actions.get(key)
            if old:
                bpy.data.actions.remove(old)
            action = bpy.data.actions.new(key)
            action.use_fake_user = True
            self.rig.animation_data.action = action
            # Keep existing clip durations while solving contact at 60 Hz.
            samples = frames * 2
            for frame in range(samples + 1):
                self._key(frame, pose_fn(frame / samples))
            self.actions.append((name, action))
            return pose_fn
        return register

    # --- export --------------------------------------------------------------------------

    def check_attached(self, tolerance=0.004):
        """Every part must touch, cut into or sit inside another, all joined up to the largest
        part (the body): returns descriptions of any that float free, and prints them. Checked
        on the rest pose, where parts were placed."""
        mesh = self.mesh_object.data
        part_of = {}
        for index, (_, start, end, _) in enumerate(self.parts):
            for v in range(start, end):
                part_of[v] = index
        polygons = [[] for _ in self.parts]
        for polygon in mesh.polygons:
            index = part_of[polygon.vertices[0]]
            start = self.parts[index][1]
            polygons[index].append([v - start for v in polygon.vertices])
        trees, points = [], []
        for index, (_, start, end, _) in enumerate(self.parts):
            co = [mesh.vertices[v].co.copy() for v in range(start, end)]
            points.append(co)
            trees.append(BVHTree.FromPolygons(co, polygons[index]))

        def inside(point, tree):
            hits, origin = 0, point.copy()
            for _ in range(64):  # count crossings straight up
                location, _, _, _ = tree.ray_cast(origin + Vector((0, 0, 1e-5)), Vector((0, 0, 1)))
                if location is None:
                    break
                hits += 1
                origin = location
            return hits % 2 == 1

        count = len(self.parts)
        linked = [set() for _ in range(count)]
        for i in range(count):
            for j in range(i + 1, count):
                if (trees[i].overlap(trees[j])
                        or any(trees[j].find_nearest(c, tolerance)[0] is not None for c in points[i][::3])
                        or inside(points[i][0], trees[j]) or inside(points[j][0], trees[i])):
                    linked[i].add(j)
                    linked[j].add(i)
        biggest = max(range(count), key=lambda i: len(points[i]))
        reached, stack = {biggest}, [biggest]
        while stack:
            for j in linked[stack.pop()] - reached:
                reached.add(j)
                stack.append(j)
        floating = []
        for i in range(count):
            if i not in reached:
                bone, _, _, label = self.parts[i]
                centre = sum(points[i], Vector()) / len(points[i])
                floating.append(f"part {i} ({label} on {bone}) at {tuple(round(c, 3) for c in centre)}")
        for line in floating:
            print(f"{self.name}: FLOATING {line}")
        return floating

    def finish(self, show="Idle"):
        """Exports <Name>.glb (one animation per NLA track), then saves <Name>.blend with just
        this animal, left showing `show` in the viewport. Refuses if any part floats free."""
        if not any(name == "Pooping" for name, _ in self.actions):
            @self.animation("Pooping", 75)
            def pooping(t):
                hold = envelope(t, 0.05, 0.22, 0.72, 0.95)
                # Keep feet planted; tension reads through the neck and raised tail.
                pose = self.pose().add("neck", rot=(8 * hold, 0, 0))
                pose.add("head", rot=(-5 * hold, 0, 0))
                pose.add("tail", rot=(-40 * hold, 0, 0))
                return pose.blink(0.55 * hold)
        floating = self.check_attached()
        assert not floating, f"{self.name} has parts not attached to the body: {floating}"
        rig = self.rig
        for name, action in self.actions:
            track = rig.animation_data.nla_tracks.new()
            track.name = name
            track.strips.new(name, 0, action)
        rig.animation_data.action = None
        with bpy.context.temp_override(scene=self.scene, view_layer=self.view_layer):
            bpy.ops.export_scene.gltf(
                filepath=os.path.join(self.out_dir, self.name + ".glb"),
                export_format="GLB",
                use_active_scene=True,
                export_animations=True,
                export_animation_mode="NLA_TRACKS",
                export_yup=True,
            )
        for track in rig.animation_data.nla_tracks:
            track.mute = True
        rig.animation_data.action = bpy.data.actions[f"{self.name}_{show}"]
        self.scene.frame_start, self.scene.frame_end = 0, 120
        self.scene.frame_set(0)
        bpy.data.libraries.write(os.path.join(self.out_dir, self.name + ".blend"),
                                 {self.scene, *(action for _, action in self.actions)},
                                 path_remap="RELATIVE_ALL", fake_user=True)
        print(f"{self.name.upper()}: {len(self.mesh_object.data.vertices)} vertices, "
              f"{len(self.bone_names)} bones, {len(self.actions)} animations -> {self.out_dir}")
