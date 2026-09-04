"""NagiBridge PC tunnel client (run on the same machine as the game).

Connects OUT to the Zeabur MCP bridge over WebSocket, authenticates with the shared
token, and answers tool calls by mapping them to the game's NagiBridge HTTP API
(http://127.0.0.1:<port>). Reconnects automatically if the link drops.

Env:
    NAGI_BRIDGE_URL       e.g. wss://<your-zeabur-url>/tunnel  (default ws://localhost:8000/tunnel)
    NAGI_BRIDGE_TOKEN     shared secret, must match the server
    NAGI_GAME_URL         game HTTP API (default http://127.0.0.1:58331)
    NAGI_MODS_JSON        path to mods_keybinds.json (default ../scripts/mods_keybinds.json)
"""
import asyncio
import json
import logging
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

import websockets

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("nagibridge.tunnel")

_DIR = os.path.dirname(os.path.abspath(__file__))
# A list of bridge endpoints to stay attached to at once (comma-separated).
# LAN default: run server.py on this same PC, phone reaches it over Wi-Fi.
# Add a cloud URL for away-from-home control, e.g. "ws://127.0.0.1:8000/tunnel,wss://<your-domain>/tunnel".
BRIDGE_URLS = [u.strip() for u in os.environ.get(
    "NAGI_BRIDGE_URLS",
    os.environ.get("NAGI_BRIDGE_URL", "ws://127.0.0.1:8000/tunnel"),
).split(",") if u.strip()]
TOKEN = os.environ.get("NAGI_BRIDGE_TOKEN", "changeme")
GAME_URL = os.environ.get("NAGI_GAME_URL", "http://127.0.0.1:58331")
MODS_JSON = os.environ.get(
    "NAGI_MODS_JSON",
    os.path.normpath(os.path.join(_DIR, "..", "scripts", "mods_keybinds.json")),
)


# ── game HTTP (stdlib, blocking; call via asyncio.to_thread) ──
def http(method: str, path: str, args: dict = None):
    args = args or {}
    url = GAME_URL + path
    if method == "GET" and args:
        url += "?" + urllib.parse.urlencode(args)
    data = None
    headers = {}
    if method == "POST":
        data = json.dumps(args).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.URLError as e:
        return {"ok": False, "error": f"game HTTP unreachable: {e}"}
    except Exception as e:
        return {"ok": False, "error": str(e)}


# ── keybind lookup (local file, mirrors tool_agent.py) ──
def _norm(s):
    return s.replace(" ", "").replace("-", "").replace("_", "").lower()


def query_keybinds(mod="", query=""):
    try:
        with open(MODS_JSON, encoding="utf-8") as fh:
            data = json.load(fh)
    except Exception as e:
        return {"ok": True, "note": "mods_keybinds.json not available", "error": str(e), "data": []}
    out = []
    for e in data:
        if mod and _norm(mod) not in _norm(e["mod"]):
            continue
        hay = _norm(f"{e['mod']} {e['feature']} {e['path']}")
        if query and _norm(query) not in hay:
            continue
        keys = e.get("keys", []) or []
        out.append({
            "mod": e["mod"], "feature": e["feature"], "path": e["path"],
            "keys": keys, "keychain": "+".join(keys).lower(),
        })
    return {"ok": True, "count": len(out), "data": out}


# ── method -> HTTP route table ──
ROUTES = {
    "get_state": ("GET", "/state", None),
    "status": ("GET", "/status", None),
    "ctx": ("GET", "/ctx", {"radius": None}),
    "surroundings": ("GET", "/surroundings", {"radius": None}),
    "machines": ("GET", "/machines", None),
    "menu": ("GET", "/menu", None),
    "move_to": ("POST", "/move", {"x": None, "y": None}),
    "stop": ("POST", "/stop", None),
    "face": ("POST", "/face", {"direction": None}),
    "interact": ("POST", "/interact", None),
    "use_tool": ("POST", "/tool", {"name": None}),
    "press_key": ("POST", "/key", {"key": None, "count": None}),
    "dialogue_next": ("POST", "/key", {"key": "confirm", "count": None}),
    "drop": ("POST", "/drop", None),
    "follow": ("POST", "/follow", {"target": None}),
    "area": ("POST", "/area", {"op": None, "x1": None, "y1": None, "x2": None, "y2": None}),
    "emote": ("POST", "/emote", {"id": None}),
    "warp": ("POST", "/warp", {"location": None, "x": None, "y": None}),
    "chat": ("POST", "/chat", {"message": None}),
    "select": ("POST", "/select", {"name": None}),
}


def dispatch(method: str, args: dict):
    if method == "keybind":
        return query_keybinds(mod=args.get("mod", ""), query=args.get("query", ""))
    if method not in ROUTES:
        return {"ok": False, "error": f"unknown method: {method}"}
    m, p, params = ROUTES[method]
    body = {}
    if params:
        for k, default in params.items():
            v = args.get(k, default)
            if v is not None:
                body[k] = v
        # dialogue_next always sends confirm as the key
    return http(m, p, body)


async def session(uri):
    async with websockets.connect(uri, ping_interval=20, ping_timeout=20, open_timeout=15) as ws:
        await ws.send(json.dumps({"type": "hello", "token": TOKEN, "game": GAME_URL}))
        hello = json.loads(await ws.recv())
        if hello.get("type") == "unauthorized":
            log.error("Bridge rejected token — check NAGI_BRIDGE_TOKEN")
            raise RuntimeError("unauthorized")
        log.info("Connected to bridge (%s)", hello.get("game"))
        async for raw in ws:
            try:
                msg = json.loads(raw)
            except Exception:
                continue
            if msg.get("type") == "call":
                cid = msg.get("id")
                method = msg.get("method")
                args = msg.get("args") or {}
                result = await asyncio.to_thread(dispatch, method, args)
                await ws.send(json.dumps({"type": "result", "id": cid, "data": result}))
            elif msg.get("type") == "ping":
                await ws.send(json.dumps({"type": "pong"}))


async def _keepalive(uri):
    """Stay attached to one bridge, reconnecting forever."""
    while True:
        try:
            await session(uri)
        except Exception as e:
            log.warning("bridge %s lost (%s) — retrying in 3s", uri, e)
            await asyncio.sleep(3)


async def main():
    assert TOKEN != "changeme", "Set NAGI_BRIDGE_TOKEN to match the bridge."
    await asyncio.gather(*(_keepalive(u) for u in BRIDGE_URLS))


if __name__ == "__main__":
    print(f"NagiBridge tunnel client | bridges={BRIDGE_URLS} game={GAME_URL}")
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nstopped")
