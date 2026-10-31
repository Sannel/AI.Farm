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

## Secrets: never print `.env`

`.env` files (e.g. `src/Server/.env`) hold real secrets such as API keys, and anything an agent
prints lands in the conversation. **Never print, cat, echo, grep, or otherwise show the contents
of a `.env` file**, or copy its values into a command, a file in the repo, or a reply. That
includes:

- Reading it with the Read tool, or `cat`, `head`, `tail`, `sed`, `less`, `grep`, `diff`, etc.
- Commands that print its values after interpolating them, such as `docker compose config`
  (and `podman compose config`), `docker inspect`, and `env` / `printenv` inside a container.
  Check settings by asking for specific non-secret names, or redact (`... | sed 's/KEY=.*/KEY=***/'`).
- Echoing secrets when summarising a change or pasting logs. Redact them.

To change a `.env`, edit only the lines involved, and never write a real secret into
`.env.example`, the README, tests, or any tracked file: those get placeholders. Ask the user to
put keys in `.env` themselves. `.env` is in `.gitignore`; keep it that way.
