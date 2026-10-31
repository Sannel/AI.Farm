# AI Farm

## Game Client

The Godot 4.7 (.NET) client lives in `src/Client/ai-farm`. Open `project.godot` in the
Godot .NET editor; the main menu scene is `scenes/main_menu.tscn`.

**Connect** asks for the server address (e.g. `localhost:8080`) and your name; both are
remembered. Joining loads the server's world: its animals, and other players as farmers, each
with a name and mood tag. Your position goes to the server 20 times a second, and the server
follows it no faster than you can walk or run. A HUD shows your energy, mood and age. Hold Shift
(or click the left stick) to run, while you have energy. Leaving the game, or losing the server,
returns to the menu.

Players with the `spawn_animals` permission can press Q (or Select on a controller) to open a
menu of every animal the server offers. Picking one spawns it a few metres in front of them,
and it's saved like any other animal. The default user **Adam** has this permission; default
users and their permissions are set under `Users:Defaults` in the server's `appsettings.json`
and are created at startup. Players are identified by name only for now, so anyone who joins as
Adam gets his permissions. Players with `destroy_animals` (Adam again) also get **Destroy…** in
the animation menu (E / A on an animal), with one more confirm before the animal and its
memories are removed for good. People can't be destroyed: neither players nor NPCs.

**NPCs** are non-player people played by AI agents through a persona. The first is **Claude,
the farmer**, who lives by the farmhouse and tends the animals gifted by his human drivers. His
persona is `src/Server/AIFarm.Server/Personas/farmer.md` (re-read on every server start), and
NPCs are listed under `Npcs:Defaults` in the server's `appsettings.json`. An NPC's memory is a **memory file**: a notebook of a fixed size (`BRAIN_MEMORY_SIZE`, 4,000
characters by default) that it rewrites whole, keeping within size by summarising. It also has a
file for each player it knows, a third of that size. Each time it thinks, it's given its memory
file plus the files of the players within `BRAIN_PLAYER_MEMORY_RANGE` metres (15 by default).
Writes over the size are refused, with how much to cut.

An agent takes him on through the MCP prompt `play_npc` (name "Claude"), which gives the persona,
his entity id, what's around him and his memory files. The agent then uses `list_npcs`, `get_persona` and `npc_move_to`, the world
tools, and the entity memory tools with his id. Left alone he potters about near home. Running `scenes/game.tscn` straight from the editor plays offline with the
scene's own animals.

## Server

The .NET 10 server lives in `src/Server`. It runs the game (the authoritative world
simulation, pushed to clients over SignalR at `/hubs/game`), stores everything in PostgreSQL,
and is a long-term memory store for AI agents, searchable by meaning through pgvector.

Everything in the world, players included, is a row in `world_entities`. The game loop saves
the position of anything that moved every 10 seconds (`Game:SaveInterval`), when a player
leaves, and on shutdown. Players are kept by name, so joining again puts you back where you
left off. Living entities (players and animals) also have vitals, saved alongside:
- **Mood:** Content, Happy, Playful, Tired, Sad, Scared or Angry. Agents set it with
  `set_mood` or `PUT /api/entities/{id}/mood`. Running out of energy makes anyone Tired until
  they've recovered.
- **Energy (0–100%):** moving uses it up and standing still brings it back. A tired animal lies
  down until it's rested, and a worn-out player can only walk.
- **Age, in game days:** a game day lasts `Game:DayLength` of real time, 20 minutes by default.

Entities with `HasMemory` (every animal) keep their own memories in `memories`. Each
memory is linked to its entity, records where the entity was standing when it formed, and is
deleted along with the entity.

| Endpoint | What it is |
|---|---|
| `/mcp` | MCP server (Streamable HTTP). Agent memory tools: `remember`, `recall`, `recent_memories`, `forget`. Entity memory tools: `entity_remember`, `entity_recall`, `entity_recent_memories`, `entity_forget`. World tools: `world_snapshot`, `find_nearby`, `get_entity`, `set_mood`. NPC tools: `list_npcs`, `get_persona`, `npc_move_to`, `read_memory_file`, `rewrite_memory_file`, plus the `play_npc` prompt |
| `/api/agents/{agentId}/memories` | An agent's memories over plain HTTP (`POST` to store, `POST .../search`, `GET` recent, `DELETE .../{id}`) |
| `/api/entities/{entityId}/memories` | The same for an entity in the world that keeps memories |
| `/hubs/game` | SignalR hub for game clients; the protocol is in `AIFarm.Contracts` |
| `/api/world` | The current world snapshot |
| `/api/entities/{entityId}/mood` | `PUT {"mood": "Happy"}` sets a living entity's mood (even an offline player's) |
| `/health` | Health check (including the database) |

Run the whole stack (PostgreSQL with pgvector, Ollama for embeddings, and the server) with
Docker or Podman from `src/Server`:

```sh
cp .env.example .env   # optional: set a password, an API key, ports
docker compose up -d --build    # or: podman compose up -d --build
```

The server listens on port 8080. Set `AIFARM_API_KEY` to require
`Authorization: Bearer <key>` on `/mcp` and the memory APIs. Memories are embedded with
`nomic-embed-text` (768 dimensions, the size of the database column). Any OpenAI-compatible
embeddings endpoint works through the `Embeddings__*` settings, as long as it produces 768
dimensions.

To run the models on another Ollama (a machine with a GPU, say), set `OLLAMA_URL` in
`src/Server/.env`, e.g. `OLLAMA_URL=http://my-gpu-box:11434`. `BRAIN_MODEL` picks the model that
plays the NPCs (default `gemma4:e4b`; `gemma4:12b` is better with a GPU). `ollama-pull` then
makes sure both models are on that server. `.env` is git-ignored, so private addresses stay out
of the repo.

**The NPC brain on Azure AI Foundry.** Instead of Ollama, a model you've deployed in Azure AI
Foundry can play the NPCs. It needs tool calling (gpt-4o, gpt-4.1, gpt-5 and others do). In
`.env`:

```sh
BRAIN_PROVIDER=AzureFoundry
BRAIN_ENDPOINT=https://my-resource.openai.azure.com   # or https://my-resource.services.ai.azure.com
                                                      # (a pasted URL ending in /responses uses the Responses API)
BRAIN_MODEL=my-deployment-name                        # the deployment's name, not the model's
BRAIN_API_KEY=<key from the Foundry portal>           # or leave empty to sign in with Entra ID
BRAIN_TEMPERATURE=                                    # empty for reasoning models (gpt-5, o-series)
```

The server talks to Foundry's OpenAI-compatible `/openai/v1/` route (added to the endpoint for
you). With an empty `BRAIN_API_KEY` it signs in with Entra ID instead: `az login` on a
development machine, a managed identity in Azure, or `AZURE_TENANT_ID` / `AZURE_CLIENT_ID` /
`AZURE_CLIENT_SECRET` for a service principal. The local Ollama is then only used for memory
embeddings. The Azure path is tested against a stand-in for Foundry that checks what the server
sends, not yet against a live deployment. Use a real key in `.env` only: it is git-ignored.

**NPC voices** are made on the server with [Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M)
v1.0 (through Sherpa-ONNX, on the CPU) and streamed to the game as they're generated, so speech
starts before the whole line is done. Compose downloads the model (~350 MB) on first start;
`VOICE_MODEL` picks another build and `VOICE_DIR=` (empty) turns voices off, leaving speech
bubbles. Voices are chosen by name per NPC (`Voice` under `Npcs:Defaults`; the farmer is
`am_michael`; `af_heart` and `bm_george` are others). Kokoro has 54 in all, named like
`<language><f|m>_<name>`. Kokoro pads each sentence with about 200 ms of silence, which sounds like
a long stop at every full stop, so that padding is trimmed and `VOICE_SENTENCE_PAUSE_MS` (default
90) is put back between sentences. The voice plays from the speaker's head in the game world, so
it gets louder as you walk up and pans to the side they're on.

**Conversations.** When the farmer speaks to you he turns to face you and stays put while you
answer (and follows you with his eyes if you walk round him), then goes back to work. You're
offered five things to say, made by the model: a friendly one, a curious one, a practical one, a
negative one, and "I'll talk to you later". Pick with 1–5, the D-pad and A, or say nothing with
Backspace / B. Animals, the farmer and players keep out of each other's way: they walk around
whatever is in front of them and nudge apart if they end up overlapping.

**Water.** A river rises in a spring pool at the foot of the north-west mountains, winds across
the plain and runs into a lake west of the farm. The shape of both (`WaterMap` in `AIFarm.Contracts`)
is shared by the client, which digs them into the ground, and the server, which keeps animals and
the farmer out of the deep part of the lake. Stand near the water and look at it, then press E
(controller A) to fill your bucket: it holds 5, shown in the HUD. The farmer can `find_water`,
`collect_water` and `give_water` to a tired animal, which restores some energy. Wading is slower.
The river is a still surface with a flowing shader; the valley floor itself is flat.

**Swimming and diving.** Where the water is over about 1.2 m deep you swim instead of wading: you
float with your head just out, and hold Ctrl or C (left bumper) to dive and Space (right bumper) to
come up. You have 30 seconds of breath, shown as an Air bar when it isn't full; it refills at the
surface, and with none left you drown, which drains your energy fast. Swimming tires you faster
than walking. Animals have the same rules by species (`Game/Swimming.cs`): cats, chickens, giraffes,
primates, small rodents and camels never go near the water; the farmer wades but stays out of his
depth; most others swim at the surface; hippos, dogs, beavers and polar bears also dive, each with
its own breath, and come up before they run out. Only the beaver and polar bear have a swimming
animation so far.

For development, start just the dependencies (`docker compose up -d postgres ollama ollama-pull`;
they are published on localhost only) and run `dotnet run --project src/Server/AIFarm.Server`.
Run the tests with `dotnet test src/Server/AIFarm.Server.Tests`; they start their own
PostgreSQL container, so Docker or Podman must be running. Migrations are applied on startup;
add new ones with `dotnet tool restore` and then
`dotnet ef migrations add <Name> -p src/Server/AIFarm.Server -o Data/Migrations`.

## Credits

- Nature models: [KayKit Forest Nature Pack](https://kaylousberg.itch.io/) by Kay Lousberg,
  www.kaylousberg.com (CC0, license in `src/Client/ai-farm/assets/kaykit_forest/License.txt`)
- Farmer character: KayKit Mystery Monthly Series 6 by Kay Lousberg,
  www.kaylousberg.com (CC0, license in `src/Client/ai-farm/assets/kaykit_farmers/License.txt`)
- Character animations: KayKit Character Animations by Kay Lousberg,
  www.kaylousberg.com (CC0, license in `src/Client/ai-farm/assets/kaykit_animations/License.txt`)
- Farm house pieces and furniture: KayKit Dungeon Pack by Kay Lousberg,
  www.kaylousberg.com (CC0, license in `src/Client/ai-farm/assets/FarmHouse/License.txt`)
- Font: [Lilita One](https://fonts.google.com/specimen/Lilita+One) by Juan Montoreano
  (SIL Open Font License, see `src/Client/ai-farm/assets/fonts/OFL.txt`)
