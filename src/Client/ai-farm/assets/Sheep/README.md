# Sheep

- `Sheep_Female`: ewe, retaining the original sheep appearance and all 18 clips.
- `Sheep_Male`: ram, 12% larger with curled horns, warmer face colors, and 19 clips
  (the ewe's set plus `Headbutt`).
- `Sheep.glb` / `Sheep.blend`: compatibility copies of the ewe for older scenes and saves.

Both carry palette textures, one skinned mesh, embedded animation clips, and `Pooping`.
Droppings use the separate `Droppings/Pellets.glb` world prop. The paired builder uses
full-body leg IK and 60 Hz animation baking to preserve hoof contact through body turns.

```sh
blender -b --python-exit-code 1 --python src/Client/ai-farm/assets/Sheep/build_sheep.py -- /tmp/sheep-pair
```

`build_sheep.py` builds both sexes through `build_sheep_variant.py` and writes compatibility
copies. The variant implementation is original project source, including the ram horns;
no purchased source assets are included. Use `NAME`, `SEX`, and `OUT_DIR` globals with
`runpy.run_path` when invoking the variant implementation programmatically.

Both sexes are in the offline animal display and online spawn catalog. A new server world
starts with both; existing saved worlds are preserved and can spawn a ram through the menu.
Legacy server spawn requests for `Sheep` resolve to the ewe.
