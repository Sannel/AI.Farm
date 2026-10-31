"""Regression checks on the *exported* animals, including interpolated poses.

Run with Blender, not system Python:
    blender -b --python-exit-code 1 --python verify_animals.py -- --report /tmp/animals.json
Optional: --assets-dir <staging/assets> --samples 81 --tolerance 0.005
No source files are saved or changed. Ground heights are in Blender metres.
"""

import argparse
import json
import math
import os
import sys

import bpy
import numpy as np


COMMON = {"Pooping", "Idle", "Idle_LookAround", "Walking", "Running", "Eating_Grass", "Being_Fed",
          "Getting_Love", "Lie_Down", "Lie_Idle", "Lie_StandUp", "Sleeping"}
CALLS = {"Cow": "Moo", "Pig": "Oink", "Chicken": "Cluck", "Goat": "Bleat", "Horse": "Neigh", "Donkey": "Bray", "Sheep": "Bleat", "Monkey": "Chatter", "Gorilla": "Chatter", "Orangutan": "Chatter", "Unicorn": "Neigh", "Zebra": "Neigh", "Deer": "Bleat", "Moose": "Bleat", "Bison": "Moo", "WaterBuffalo": "Moo", "Boar": "Oink", "Camel": "Bleat", "BactrianCamel": "Bleat", "Llama": "Bleat", "Giraffe": "Hum", "Elephant": "Trumpet", "Mammoth": "Trumpet", "Rhino": "Snort", "Hippo": "Snort", "Mouse": "Squeak", "Rat": "Squeak", "Hamster": "Squeak", "Rabbit": "Sniff", "Chipmunk": "Squeak", "Beaver": "Squeak", "Hedgehog": "Squeak", "Bat": "Squeak", "Bear": "Growl", "PolarBear": "Growl", "Koala": "Growl", "Panda": "Growl", "Sloth": "Chitter", "Otter": "Squeak", "Skunk": "Squeak", "Kangaroo": "Chitter", "Badger": "Squeak", "Turkey": "Gobble"}
MALE_EXTRAS = {"Cow": "Paw_Ground", "Goat": "Headbutt", "Horse": "Rear_Up", "Sheep": "Headbutt", "Unicorn": "Rear_Up", "Zebra": "Rear_Up", "Deer": "Rear_Up", "Moose": "Rear_Up", "Bison": "Paw_Ground", "WaterBuffalo": "Paw_Ground", "Camel": "Rear_Up", "BactrianCamel": "Rear_Up", "Llama": "Rear_Up"}


class SceneSandbox:
    """Keep the user's open scenes intact, including when run through MCP."""

    def __init__(self):
        self.scene = bpy.context.window.scene
        self.collections = (bpy.data.objects, bpy.data.scenes, bpy.data.collections,
                            bpy.data.meshes, bpy.data.armatures, bpy.data.materials,
                            bpy.data.images, bpy.data.actions, bpy.data.cameras,
                            bpy.data.lights, bpy.data.worlds)
        self.originals = [set(c) for c in self.collections]
        self.actions = set(bpy.data.actions)

    def restore(self):
        bpy.context.window.scene = self.scene
        for collection, original in zip(self.collections, self.originals):
            for item in set(collection) - original:
                collection.remove(item, do_unlink=True)

    def new_scene(self):
        self.restore()
        bpy.context.window.scene = bpy.data.scenes.new("Animal_Validation_Temporary")


def equine_poll(obj):
    """The head and neck must share a surface, not just intersect at rest.

    glTF splits vertices at UV/normal seams. Weld coincident positions for the
    topology check, then check those duplicates stay coincident in every pose.
    """
    names = {g.index: g.name for g in obj.vertex_groups}
    eligible, weights, by_position = {}, {}, {}
    for vertex in obj.data.vertices:
        skin = {names[g.group]: g.weight for g in vertex.groups}
        if skin.get("neck", 0.0) + skin.get("head", 0.0) < 0.999:
            continue
        position = tuple(round(c, 5) for c in vertex.co)
        eligible[vertex.index] = position
        weights[vertex.index] = skin.get("head", 0.0)
        by_position.setdefault(position, []).append(vertex.index)
    adjacency = {position: set() for position in by_position}
    for polygon in obj.data.polygons:
        vertices = list(polygon.vertices)
        for first, second in zip(vertices, vertices[1:] + vertices[:1]):
            if first in eligible and second in eligible:
                a, b = eligible[first], eligible[second]
                adjacency[a].add(b)
                adjacency[b].add(a)
    remaining, components = set(adjacency), []
    while remaining:
        seed = remaining.pop()
        component, stack = {seed}, [seed]
        while stack:
            for neighbour in adjacency[stack.pop()] - component:
                component.add(neighbour)
                remaining.discard(neighbour)
                stack.append(neighbour)
        components.append(component)
    assert components, f"{obj.name}: no head/neck skin"
    main = max(components, key=len)
    indices = [i for p in main for i in by_position[p]]
    values = [weights[i] for i in indices]
    mixed = sum(0.001 < w < 0.999 for w in values)
    assert min(values) < 0.001 and max(values) > 0.999 and mixed >= 30, f"{obj.name}: head and neck are not welded and smoothly weighted"
    return [by_position[p] for p in main if len(by_position[p]) > 1], mixed


def _verify_all(assets_dir, samples, tolerance, sandbox):
    assert samples >= 2 and tolerance >= 0
    report, errors = [], []
    for species, call in CALLS.items():
        for sex in ("Female", "Male"):
            name = f"{species}_{sex}"
            path = os.path.join(assets_dir, species, name + ".glb")
            assert os.path.isfile(path), path
            sandbox.new_scene()
            bpy.ops.import_scene.gltf(filepath=path)
            scene = bpy.context.scene
            rigs = [o for o in scene.objects if o.type == "ARMATURE"]
            meshes = [o for o in scene.objects if o.type == "MESH" and
                      not any(c.name.startswith("glTF_not_exported") for c in o.users_collection)]
            assert len(rigs) == 1 and len(meshes) == 1, name
            rig, obj = rigs[0], meshes[0]
            assert obj.parent == rig and len(obj.data.uv_layers), name
            assert all(v.groups for v in obj.data.vertices), f"{name}: unweighted vertices"
            assert all(abs(sum(g.weight for g in v.groups) - 1.0) < 0.0001 for v in obj.data.vertices), f"{name}: skin weights must total one"
            seams, mixed = equine_poll(obj) if species in ("Horse", "Donkey", "Unicorn", "Zebra", "Deer", "Moose", "Camel", "BactrianCamel", "Llama") else ([], 0)
            images = [n.image for m in obj.data.materials for n in m.node_tree.nodes if n.type == "TEX_IMAGE"]
            # Imported images are lazy: has_data alone can be False until the
            # pixels are requested, even with a valid packed GLB image.
            assert images and all(i is not None and len(i.pixels) > 0 for i in images), f"{name}: missing image data"
            expected = COMMON | {"Crow" if species == "Chicken" and sex == "Male" else call}
            if species in ("Horse", "Donkey", "Unicorn", "Zebra", "Deer", "Moose", "Camel", "BactrianCamel", "Llama"):
                expected.add("Trotting")
            if species in ("Chicken", "Turkey"):
                expected.add("Flap_Wings")
            if species == "Rabbit":
                expected.add("Hop")
            if species in ("Beaver", "PolarBear", "Otter"):
                expected.add("Swimming")
            if species == "Bat":
                expected.add("Flying")
            if species == "Sloth":
                expected.add("Hang_Idle")
            if species == "Sheep":
                expected.update({"Hop", "Alert_Idle", "Startled", "Trotting", "Walking_LookBack"})
            if sex == "Male" and species in MALE_EXTRAS:
                expected.add(MALE_EXTRAS[species])
            actions = {}
            for action in set(bpy.data.actions) - sandbox.actions:
                # Existing scenes may already own an action called Idle, etc.
                name_parts = action.name.rsplit(".", 1)
                clip_name = name_parts[0] if len(name_parts) == 2 and name_parts[1].isdigit() else action.name
                actions[clip_name] = action
            assert set(actions) == expected, f"{name}: clips {set(actions) ^ expected}"
            rig.animation_data_create()
            for track in rig.animation_data.nla_tracks:
                track.mute = True
            item = {"name": name, "triangles": sum(len(p.vertices) - 2 for p in obj.data.polygons),
                    "poll_blended_vertices": mixed, "clips": []}
            for clip, action in actions.items():
                rig.animation_data.action = action
                if action.slots:
                    rig.animation_data.action_slot = action.slots[0]
                start, end = action.frame_range
                assert end > start, f"{name}/{clip}: empty animation"
                minimum, worst = math.inf, start
                for frame in np.linspace(start, end, samples):
                    scene.frame_set(int(frame), subframe=float(frame - int(frame)))
                    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
                    mesh = evaluated.to_mesh()
                    positions = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
                    mesh.vertices.foreach_get("co", positions)
                    positions = positions.reshape(-1, 3)
                    assert np.isfinite(positions).all(), f"{name}/{clip}: invalid geometry"
                    for seam in seams:
                        assert np.ptp(positions[seam], axis=0).max() < 0.00005, f"{name}/{clip}: a head/neck seam opens at frame {frame}"
                    matrix = np.array(evaluated.matrix_world)
                    z = positions @ matrix[2, :3] + matrix[2, 3]
                    if float(z.min()) < minimum:
                        minimum, worst = float(z.min()), float(frame)
                    evaluated.to_mesh_clear()
                item["clips"].append({"clip": clip, "min_z": minimum, "worst_frame": worst})
                if minimum < -tolerance:
                    errors.append(f"{name}/{clip}: ground penetration {-minimum:.4f}m at frame {worst:.2f}")
            report.append(item)
            print(f"{name}: {len(actions)} clips checked", flush=True)
    return report, errors


def verify(assets_dir, samples=41, tolerance=0.005):
    sandbox = SceneSandbox()
    try:
        return _verify_all(assets_dir, samples, tolerance, sandbox)
    finally:
        sandbox.restore()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets-dir", default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    parser.add_argument("--samples", type=int, default=41)
    parser.add_argument("--tolerance", type=float, default=0.005)
    parser.add_argument("--report")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    results, failures = verify(args.assets_dir, args.samples, args.tolerance)
    if args.report:
        with open(args.report, "w") as stream:
            json.dump({"animals": results, "errors": failures}, stream, indent=2)
    assert not failures, "\n" + "\n".join(failures)
    print(f"PASS: {len(results)} animals; {sum(len(a['clips']) for a in results)} clips; {args.samples} samples per clip")
