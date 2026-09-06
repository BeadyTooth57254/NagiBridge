# NagiBridge v1.0.0 — in-game AI chat + AI-controlled farmhand (co-op)

> Ships with a compiled `NagiBridge.dll` — just unzip and use, **no build required**.
>
> This release includes **two archives**:
> - **`NagiBridge.zip`** — the mod itself. Unzip the whole `NagiBridge/` folder into `Stardew Valley/Mods/`.
> - **`NagiBridge-mcp_bridge.zip`** — the **Python bridge** for co-op AI (`server.py` + `client.py` + `launcher.bat` + `gen_token.bat`). Unzip it locally — it is **not** installed into Mods; it runs on your PC.

## This release

- **Co-op support**: the host instance binds `58331` (in-game chat); the farmhand instance binds `58332` (AI control). Role is decided automatically via `IsMainPlayer`.
- **In-game AI chat**: press `` ` `` to open the chat panel. With `"Mode": "operit"`, your messages forward to the MCP bridge (`/ingame-in`); the AI (operit) replies in-game via `read_ingame` / `send_ingame`.
- **AI farmhand control**: operit drives the farmhand through the MCP bridge (move, use tools, interact, farm, warp, etc.) — **17 tools**.
- **Per-role `localhost` bind**: no more `http://+` URLACL needed; takes effect on restart.

## Install

1. Install [SMAPI](https://smapi.io/) (Stardew Valley 1.6+ / SMAPI 4.0+).
2. Unzip `NagiBridge.zip`, put the `NagiBridge` folder into `Stardew Valley/Mods/`.
3. Launch the game via SMAPI.

## Co-op + AI (advanced, optional)

Both instances (host / farmhand) share one `config.json`:

```json
{
  "Mode": "operit",
  "HostPort": 58331,
  "FarmhandPort": 58332,
  "OperitBridgeUrl": "http://127.0.0.1:8000",
  "OperitBridgeToken": "<shared token>"
}
```

Unzip `NagiBridge-mcp_bridge.zip`, double-click `launcher.bat` to start the bridge (server `:8000` + client), then point operit at `http://<PC-LAN-IP>:8000/mcp` (`Authorization: Bearer <token>`). Run `gen_token.bat` once to create `token.txt`. See the repo README for full details.

## Platform

Windows / macOS / Linux.
