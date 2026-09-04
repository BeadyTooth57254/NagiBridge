# NagiBridge

**English** | [**中文**](README.zh-CN.md)

Stardew Valley SMAPI mod for **co-op**: it turns your game into an HTTP/MCP-controlled
instance, so an AI assistant (e.g. **operit** on your phone) can **chat with you in-game**
and **control a farmhand farmer**.

- **In-game AI chat** — as the host player, press `` ` `` to open the chat panel and talk to
  the AI. It reads your messages and replies right in your chat panel.
- **AI farmhand control** — the AI controls the farmhand via an MCP bridge (move, use tools,
  interact, farm, warp, and more).
- **Co-op aware** — when *both* the host and the farmhand load the mod, role isolation is
  automatic: the **host** only forwards chat to the bridge, the **farmhand** is only the
  AI-controlled character (`Game1.player.IsMainPlayer` decides the role).

## How it works

```
[Phone: operit]
     │  streamable HTTP /mcp (Bearer token)
     ▼
[Python MCP bridge :8000]  ──WS tunnel──▶  [client.py]  ──HTTP──▶  [Stardew game]
        server.py                              │                     host :58331
                                                 └── in-game chat ──▶  farmhand :58332
```

- The mod (in each game instance) runs a small HTTP server on `http://localhost:<port>`:
  - **Host** instance → `HostPort` (default `58331`)
  - **Farmhand** instance → `FarmhandPort` (default `58332`)
- A **Python MCP bridge** in `mcp_bridge/` exposes the game as MCP tools over **streamable
  HTTP** at `/mcp`, protected by a Bearer token.
- **operit** (or any MCP client) connects to the bridge and can call `read_ingame`/`send_ingame`
  (chat) and the farmhand-control tools.

## Install (players)

> The release zip ships the compiled `NagiBridge.dll` — no build needed.

1. Install [SMAPI](https://smapi.io/).
2. Download the latest release and unzip it.
3. Move the `NagiBridge` folder into `Stardew Valley/Mods/`.
4. Launch the game through SMAPI.

Requires **Stardew Valley 1.6+ / SMAPI 4.0+**. The same `.dll` works on Windows, macOS and Linux.

## Build (developers)

```bash
dotnet build -c Release
```

Uses [`Pathoschild.Stardew.ModBuildConfig`](https://github.com/Pathoschild/SMAPI/blob/develop/docs/technical/mod-package.md),
which auto-detects your game folder and copies the built mod to `Stardew Valley/Mods/<ModFolderName>/`.
Set a `GamePath` property or `GAME_PATH` env var if auto-detection fails.

## Co-op setup

Both the host and the farmhand load NagiBridge; the role is decided automatically by whether the
instance is the main player.

`config.json` (shared by both instances):

```json
{
  "Mode": "operit",
  "HostPort": 58331,
  "FarmhandPort": 58332,
  "OperitBridgeUrl": "http://127.0.0.1:8000",
  "OperitBridgeToken": "<shared token>"
}
```

### Run the MCP bridge

```bash
cd mcp_bridge
gen_token.bat        # once: writes token.txt (the shared secret)
launcher.bat         # opens the bridge (server.py on :8000) + tunnel client (client.py)
```

Or manually:

```bash
set NAGI_BRIDGE_TOKEN=<token>
python server.py     # bridge, listens on 0.0.0.0:8000, /mcp + /ingame-in + /health
python client.py     # tunnel client -> farmhand localhost:58332, host localhost:58331
```

### Connect operit (phone)

- URL: `http://<PC-LAN-IP>:8000/mcp`
- Auth: `Authorization: Bearer <token>` — same value as `token.txt`

The phone must use the PC's **LAN IP** (e.g. `192.168.100.236`), not `127.0.0.1`/`localhost`.

## In-game chat

Press `` ` `` (backtick) to open the chat panel. With `Mode: "operit"`, the host's messages are
forwarded to the bridge (`POST /ingame-in`); operit reads them via `read_ingame` and replies via
`send_ingame`, which the bridge pushes back to the host's panel through `/chat/push`.

## HTTP API (game)

Each game instance exposes a small HTTP API (used by the bridge client):

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/status` | GET | Role, world-ready, multiplayer state |
| `/chat/push` | POST | Inject an AI message into the panel |
| `/chat` | POST | Send a chat message to all players |
| `/move`, `/tool`, `/interact` | POST | Farmhand control |
| `/warp`, `/use`, `/select`, `/face` | POST | Farmhand control |
| `/state`, `/surroundings`, `/ctx`, `/map` | GET | Game state |
| `/sleep`, `/wakeup`, `/stop`, `/pause`, `/resume` | POST | Session control |

Full endpoint list: [AGENTS.md](AGENTS.md)

## Chat panel controls

| Key | Action |
|-----|--------|
| `` ` `` | Open / close chat panel |
| `Enter` | Send message |
| `Ctrl+V` | Paste from clipboard |

## Config reference

| Field | Meaning | Default |
|-------|---------|---------|
| `Mode` | Chat backend: `operit` (MCP bridge) | `cc` |
| `HostPort` | Port the host instance binds | `58331` |
| `FarmhandPort` | Port the farmhand instance binds | `58332` |
| `OperitBridgeUrl` | Bridge base URL used for in-game chat forwarding | `http://127.0.0.1:8000` |
| `OperitBridgeToken` | Shared token sent to the bridge for chat forwarding | empty |
| `ApiProvider`/`ApiUrl`/`ApiKey`/`Model` | Legacy direct-LLM chat option | — |
| `ChannelServerUrl` | Legacy channel-server chat option | `http://localhost:9000/chat` |

## License

Licensed under the [MIT License](LICENSE). Original framework credit to
[anqinou-art](https://github.com/anqinou-art/NagiBridge); co-op / MCP bridge additions by BeadyTooth57254.
