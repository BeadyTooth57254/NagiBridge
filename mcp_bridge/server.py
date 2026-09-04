"""NagiBridge remote MCP bridge (runs on Zeabur).

Exposes the Stardew game-control tools as MCP tools over Streamable HTTP (the modern
MCP transport) at /mcp, so the phone client (e.g. operit) can connect directly. It also
serves /health and a WebSocket "reverse tunnel" at /tunnel for the player's PC client.

Why streamable_http_app is the ROOT app: FastMCP's transport needs its lifespan to
initialise the session manager, and an outer path-mount breaks its /mcp route. So we
build the Starlette app and add our own /tunnel + /health + / routes onto it.

Security: the MCP endpoint's tools only do something while the PC tunnel (authenticated
with NAGI_BRIDGE_TOKEN) is up and a real game is running — the tunnel token is the real
gate. Optionally set NAGI_BRIDGE_MCP_AUTH=1 to also require a Bearer token on /mcp.

Env:
    PORT                   (Zeabur provides; default 8000)
    NAGI_BRIDGE_TOKEN      shared secret, must match the PC client
    NAGI_BRIDGE_MCP_AUTH   optional: if '1', require `Authorization: Bearer <token>` on /mcp
"""
import asyncio
import json
import logging
import os
import uuid

from mcp.server.fastmcp import FastMCP
from starlette.middleware.base import BaseHTTPMiddleware
from starlette.responses import JSONResponse

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("nagibridge.mcp")

TOKEN = os.environ.get("NAGI_BRIDGE_TOKEN", "changeme")
# MCP auth is ON by default: /mcp requires `Authorization: Bearer <token>`.
# Set NAGI_BRIDGE_MCP_AUTH=0 to disable (only if you trust the network).
REQUIRE_MCP_AUTH = os.environ.get("NAGI_BRIDGE_MCP_AUTH", "1") == "1"
PORT = int(os.environ.get("PORT", "8000"))

mcp = FastMCP("NagiBridge Game Control")

# ── reverse-tunnel state ──
_pc_ws = None
_pc_lock = asyncio.Lock()
_pending = {}  # call_id -> asyncio.Future


async def _call_pc(method: str, args: dict, timeout: float = 25.0):
    """Forward a tool call to the PC tunnel and await its result."""
    global _pc_ws
    async with _pc_lock:
        ws = _pc_ws
    if ws is None:
        return {"ok": False,
                "error": "game not connected — make sure the PC tunnel client is running and Stardew is open"}
    cid = uuid.uuid4().hex
    fut = asyncio.get_running_loop().create_future()
    _pending[cid] = fut
    try:
        await ws.send_text(json.dumps({"type": "call", "id": cid, "method": method, "args": args or {}}))
        return await asyncio.wait_for(fut, timeout=timeout)
    except asyncio.TimeoutError:
        return {"ok": False, "error": f"timeout waiting for game (method={method})"}
    finally:
        _pending.pop(cid, None)


async def _tool(method: str, args: dict) -> str:
    return json.dumps(await _call_pc(method, args), ensure_ascii=False)


# ── MCP tools (the phone's model drives these directly) ──

@mcp.tool()
async def get_state() -> str:
    """Current game state: farmer position/health/stamina, time, location, inventory, active menu/event."""
    return await _tool("get_state", {})


@mcp.tool()
async def ctx(radius: int = 8) -> str:
    """ASCII text map of tiles around the farmer (radius 1-20, default 8). Symbols: P player, C crop, M machine, T tree, . open, # wall, o object."""
    return await _tool("ctx", {"radius": radius})


@mcp.tool()
async def surroundings(radius: int = 10) -> str:
    """Descriptive surroundings: nearby objects, crops, machines and their mod names."""
    return await _tool("surroundings", {"radius": radius})


@mcp.tool()
async def machines() -> str:
    """List machines you can see (kegs, furnaces, etc.) with position + ready state."""
    return await _tool("machines", {})


@mcp.tool()
async def move_to(x: int, y: int) -> str:
    """Walk the farmer to tile (x, y) using auto-pathfinding."""
    return await _tool("move_to", {"x": x, "y": y})


@mcp.tool()
async def interact() -> str:
    """Interact with the object/NPC/tile the farmer is facing (same as the interact key)."""
    return await _tool("interact", {})


@mcp.tool()
async def use_tool(name: str = "current") -> str:
    """Swing a tool by name (Hoe, Watering Can, Axe, Pickaxe...) or 'current'."""
    return await _tool("use_tool", {"name": name})


@mcp.tool()
async def press_key(key: str, count: int = 1) -> str:
    """Press a key once or `count` times. Accepts letters/digits/F1-F24, SMAPI names (Back, OemPipe, LeftControl), and chords like 'leftcontrol+leftshift+f6'."""
    return await _tool("press_key", {"key": key, "count": count})


@mcp.tool()
async def dialogue_next(count: int = 1) -> str:
    """Advance dialogue / close a menu by pressing confirm (`count` times)."""
    return await _tool("dialogue_next", {"count": count})


@mcp.tool()
async def keybind(mod: str = "", query: str = "") -> str:
    """Look up a mod's keybind from its extracted config (query matches mod/feature/path). Returns a ready 'keychain' for press_key. Examples: query='quick stack', query='npc map'."""
    return await _tool("keybind", {"mod": mod, "query": query})


@mcp.tool()
async def drop() -> str:
    """Drop the currently held item on the ground beside the farmer."""
    return await _tool("drop", {})


@mcp.tool()
async def follow(target: str = "") -> str:
    """Auto-follow a target: 'player:X', 'npc:X' or 'x,y'. Empty stops following."""
    return await _tool("follow", {"target": target})


@mcp.tool()
async def area(op: str, x1: int, y1: int, x2: int, y2: int) -> str:
    """Batch-op a box of farmland tiles in one call. op: 'water' or 'unwater'."""
    return await _tool("area", {"op": op, "x1": x1, "y1": y1, "x2": x2, "y2": y2})


@mcp.tool()
async def emote(id: int) -> str:
    """Play an emote from the farmer (id 0-23, e.g. 12 heart, 8 exclamation)."""
    return await _tool("emote", {"id": id})


@mcp.tool()
async def warp(location: str, x: int = 0, y: int = 0) -> str:
    """Warp the farmer to a named location (optional x/y tile)."""
    return await _tool("warp", {"location": location, "x": x, "y": y})


# ── build the root Starlette app and add our routes on top ──

app = mcp.streamable_http_app()  # must stay the ROOT app so /mcp works


if REQUIRE_MCP_AUTH:
    class _AuthMiddleware(BaseHTTPMiddleware):
        async def dispatch(self, request, call_next):
            if request.url.path.startswith("/mcp"):
                if request.headers.get("authorization", "") != f"Bearer {TOKEN}":
                    return JSONResponse({"error": "unauthorized"}, status_code=401)
            return await call_next(request)

    app.add_middleware(_AuthMiddleware)


async def _health(request):
    async with _pc_lock:
        connected = _pc_ws is not None
    return JSONResponse({"ok": True, "gameConnected": connected})


def _lan_ip():
    """Override with NAGI_LAN_IP if set (handy for VPN/multi-NIC/no-internet), else auto-detect."""
    override = os.environ.get("NAGI_LAN_IP", "").strip()
    if override:
        return override
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return "127.0.0.1"


async def _root(request):
    return JSONResponse({
        "service": "NagiBridge MCP Bridge",
        "mcp": "/mcp", "tunnel": "/tunnel", "health": "/health",
        "phone_lan_url": f"http://{_lan_ip()}:{PORT}/mcp",
    })


async def _tunnel(ws):
    """The player's PC client connects here to receive tool calls for itself."""
    global _pc_ws
    await ws.accept()
    try:
        hello = await ws.receive_json()
    except Exception:
        await ws.close()
        return
    if hello.get("token") != TOKEN:
        await ws.send_json({"type": "unauthorized"})
        await ws.close()
        return
    await ws.send_json({"type": "hello_ok", "game": hello.get("game")})
    async with _pc_lock:
        _pc_ws = ws  # a fresh PC connection replaces any previous one
    log.info("PC tunnel connected (%s)", hello.get("game"))
    try:
        while True:
            msg = await ws.receive_json()
            if msg.get("type") == "result":
                fut = _pending.get(msg.get("id"))
                if fut and not fut.done():
                    fut.set_result(msg.get("data", {"ok": True}))
            elif msg.get("type") == "status":
                pass
    except Exception:
        pass
    finally:
        async with _pc_lock:
            if _pc_ws is ws:
                _pc_ws = None
        for fut in _pending.values():
            if not fut.done():
                fut.set_result({"ok": False, "error": "game disconnected"})
        _pending.clear()
        log.info("PC tunnel disconnected")


app.router.add_route("/health", _health, ["GET"])
app.router.add_route("/", _root, ["GET"])
app.router.add_websocket_route("/tunnel", _tunnel)


if __name__ == "__main__":
    import socket
    import uvicorn

    lan = _lan_ip()
    print("NagiBridge MCP bridge")
    print(f"  MCP endpoint (streamable HTTP):  /mcp")
    print(f"  Phone on home Wi-Fi (LAN):        http://{lan}:{PORT}/mcp")
    print(f"  Phone anywhere (cloud):           https://<your-domain>/mcp")
    print(f"  PC client tunnel:                 /tunnel  (set NAGI_BRIDGE_URLS on the PC client)")
    if not REQUIRE_MCP_AUTH:
        print("  !!! WARNING: /mcp has NO authentication (set NAGI_BRIDGE_MCP_AUTH=1 to require a Bearer token).")
    if TOKEN == "changeme":
        print("  !!! WARNING: NAGI_BRIDGE_TOKEN is still 'changeme' — set a strong random secret.")
    uvicorn.run(app, host="0.0.0.0", port=PORT)
