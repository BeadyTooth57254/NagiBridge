# WheatStook v1.1.0 — in-game AI chat + AI-controlled farmhand (co-op)

> Ships with a compiled `WheatStook.dll` — just unzip and use, **no build required**.
>
> This release includes **two archives**:
> - **`WheatStook.zip`** — the mod itself. Unzip the whole `WheatStook/` folder into `Stardew Valley/Mods/`.
> - **`WheatStook-mcp_bridge.zip`** — the **Python bridge** for co-op AI (`server.py` + `client.py` + `launcher.bat` + `gen_token.bat` + `scripts/mods_keybinds.json`). Unzip it locally — it is **not** installed into Mods; it runs on your PC.

## New since v1.0.0

- **Renamed NagiBridge → WheatStook (麦垛)**: packaging, manifest and README aligned; the compat `CompatRules.json` comments updated too.
- **AI-farmhand bridge: honest + expanded**:
  - `get_state` is **light** by default; a new **`get_state_full`** does the full read (inventory/buildings/mods/teleports/enchantments) **only when explicitly called** (saves tokens, keeps the AI from being distracted by irrelevant data).
  - **`inventory`**: reads **one row (12 slots)**, injecting the in-game time + local map at the same time; labels the equip hotkeys (1-9,0) and how to switch rows (`row=N`) — Stardew has no hotkey to switch inventory rows, stated honestly.
  - **`wheatstook_selftest`**: server binding / config / mod count / memory / compat + bridge-channel status, for the AI to self-diagnose.
  - **Coordinate chunking**: any returned coordinate carries precise (x,y) + the chunk coords + the sub-position inside that chunk (chunkSize defines the grid).
  - **`drop` now really drops on the ground** (`Game1.createItemDebris`) and can be picked back up by walking over it — no more silent consummation.
  - **In-game chat is visible**: the AI's `chat` messages go through the mod's own ChatHud panel (drawn on the current viewport) and **auto-open**, replacing the vanilla `Game1.chatBox` which only rendered in one player's viewport (so you couldn't see it before).
  - Every response now carries `timeOfDay`/`gameTime`; the `send_ingame` (host 58331) vs `chat` (farmhand 58332) channel distinction is explicit.
- **Fixed a batch of AI timing/truthfulness issues**: `handle_tool`/`handle_select`/`handle_move`/`follow`/`area` ok/used/found/steps are now computed synchronously or return "accepted/queued" (they used to report false); `findModDir` has per-folder try/catch; GMCM uses the real API.
- **Auto-compat layer**: data-driven `CompatRules.json` + `enableAutoCompat` detection by UniqueID (extra ring slots / bigger backpack / custom crops / new regions / professions); the active list is written into /state.
- **keybind**: a static `scripts/mods_keybinds.json`, re-read on every query (not once per startup); added Joja Express, removed the dead FA F6.
- **Mod knowledge base / long-term memory / auto-reaction layer**: on-demand queries of installed-mod behavior, `wheatstook_mem` memory, a short daily-morning briefing.

## Install

1. Install [SMAPI](https://smapi.io/) (Stardew Valley 1.6+ / SMAPI 4.0+).
2. Unzip `WheatStook.zip`, put the `WheatStook` folder into `Stardew Valley/Mods/`.
3. Launch the game via SMAPI.

## Co-op + AI (advanced, optional)

Both instances (host / farmhand) share one `config.json` — copy from `config.example.json`, minimum:

```json
{
  "Mode": "operit",
  "HostPort": 58331,
  "FarmhandPort": 58332,
  "OperitBridgeUrl": "http://127.0.0.1:8000",
  "OperitBridgeToken": "<shared token>"
}
```

Unzip `WheatStook-mcp_bridge.zip`, double-click `launcher.bat` to start the bridge (server `:8000` + client), then point operit at `http://<PC-LAN-IP>:8000/mcp` (`Authorization: Bearer <token>`). Run `gen_token.bat` once to create `token.txt`. See the repo README for full details.

## Platform

Windows / macOS / Linux.
