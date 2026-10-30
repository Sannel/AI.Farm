# Agent Instructions

## Third-party assets (itch.io)

Purchased asset packs live outside this repo, under `~/.config/itch/apps/` (e.g. the KayKit packs).
Many are paid SOURCE/EXTRA editions. Even when the license (often CC0) allows redistribution,
do not republish the author's paid source files in this repo.

**Never copy source assets into this repo.** This includes:

- Anything from a pack's `SOURCE/` folder
- Editable source files: `.blend`, `.blend1`, `.psd`, `.kra`, `.xcf`, `.ma`, `.mb`, `.max`, `.spp`, `.unitypackage`
- Whole packs or whole folders copied in bulk

**Compiled / exported assets are fine** to copy, but only the specific ones the project uses:

- Models: `.gltf` + `.bin`, `.glb`, `.fbx`, `.obj` + `.mtl`
- Textures the copied models reference (`.png`)

These rules apply only to third-party files. Original source files created for this project
(including `.blend` files) belong in the repo. A project `.blend` may link or append exported
pack models, but don't save the pack's own `SOURCE/` `.blend` (or a copy of it) into the repo.

When copying a third-party asset in, keep the pack's license file alongside it and credit the
author (e.g. "KayKit by Kay Lousberg, www.kaylousberg.com") in the README.
