"""End-to-end smoke test for the in-game-chat-to-operit channel (no real game needed).

Simulates the whole loop with a mock HOST game:
  host game POST /ingame-in  ->  bridge inbox  ->  operit read_ingame()
  operit send_ingame(...)    ->  bridge -> PC tunnel -> host game /chat/push

Run from the mcp_bridge dir:  python test_chat_loop.py
"""
import asyncio
import json
import os
import sys
import threading
import time
import urllib.request
import urllib.error

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _HERE)

TOKEN = "testtoken"
BRIDGE_PORT = 8010
HOST_MOCK_PORT = 59331
BRIDGE_WS = f"ws://127.0.0.1:{BRIDGE_PORT}/tunnel"
BRIDGE_HTTP = f"http://127.0.0.1:{BRIDGE_PORT}"
BRIDGE_MCP = f"http://127.0.0.1:{BRIDGE_PORT}/mcp"

os.environ["WHEATSTOOK_BRIDGE_TOKEN"] = TOKEN
os.environ["WHEATSTOOK_BRIDGE_MCP_AUTH"] = "1"
os.environ["WHEATSTOOK_GAME_URL"] = f"http://127.0.0.1:{HOST_MOCK_PORT + 1}"
os.environ["WHEATSTOOK_HOST_URL"] = f"http://127.0.0.1:{HOST_MOCK_PORT}"
os.environ["PORT"] = str(BRIDGE_PORT)

import client as client_mod          # noqa: E402
import server as server_mod          # noqa: E402


# ---- mock HOST game: records /chat/push bodies ----
received = []


class HostHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        n = int(self.headers.get("content-length", 0))
        body = self.rfile.read(n).decode("utf-8")
        if self.path == "/chat/push":
            received.append(json.loads(body))
            data = json.dumps({"ok": True}).encode()
            self.send_response(200)
            self.send_header("content-type", "application/json")
            self.send_header("content-length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        else:
            self.send_response(404)
            self.send_header("content-length", "0")
            self.end_headers()

    def log_message(self, *a):
        pass


host_srv = ThreadingHTTPServer(("127.0.0.1", HOST_MOCK_PORT), HostHandler)
threading.Thread(target=host_srv.serve_forever, daemon=True).start()


def start_bridge():
    import uvicorn
    uvicorn.run(server_mod.app, host="127.0.0.1", port=BRIDGE_PORT, log_level="warning")


threading.Thread(target=start_bridge, daemon=True).start()
time.sleep(2.0)


def http(method, path, body=None, headers=None):
    req = urllib.request.Request(
        BRIDGE_HTTP + path,
        data=json.dumps(body).encode() if body is not None else None,
        headers=headers or {"content-type": "application/json"},
        method=method,
    )
    try:
        with urllib.request.urlopen(req, timeout=6) as r:
            return r.status, json.loads(r.read().decode("utf-8") or "{}")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read().decode("utf-8") or "{}")


async def mcp_session(expect_msg):
    from mcp.client.streamable_http import streamablehttp_client
    from mcp import ClientSession
    async with streamablehttp_client(BRIDGE_MCP, headers={"Authorization": f"Bearer {TOKEN}"}) as (r, w, _):
        async with ClientSession(r, w) as sess:
            await sess.initialize()
            # read path: the message posted to /ingame-in should be here
            res = await sess.call_tool("read_ingame", {})
            res0 = json.loads(res.content[0].text)
            msgs = res0.get("messages", [])
            assert msgs and msgs[-1].get("message") == expect_msg, f"read_ingame got {res0}"
            # inbox cleared after read
            res = await sess.call_tool("read_ingame", {})
            assert json.loads(res.content[0].text).get("messages") == [], "inbox should be cleared"
            # reply into the host game's chat panel
            res = await sess.call_tool("send_ingame", {"message": "收到，我这就来"})
            print("send_ingame result:", res.content[0].text)


async def run_loop():
    # attach the PC tunnel (client) to the bridge in this same loop
    tunnel = asyncio.create_task(client_mod.session(BRIDGE_WS))
    await asyncio.sleep(2.0)

    # host game posts a player chat message into the bridge
    st, body = http("POST", "/ingame-in", {"sender": "Es", "message": "今天天气怎么样"}, {"x-token": TOKEN, "content-type": "application/json"})
    assert st == 200 and body.get("ok") is True, f"/ingame-in failed: {st} {body}"

    # operit reads the message and replies
    await mcp_session("今天天气怎么样")
    # after send_ingame, the mock host should have received the reply
    await asyncio.sleep(0.5)
    assert received and received[-1].get("message") == "收到，我这就来", f"host /chat/push got: {received}"
    print("e2e chat loop OK: ingame-in -> read_ingame -> send_ingame -> host /chat/push")

    tunnel.cancel()
    host_srv.shutdown()


async def main():
    try:
        # A) direct client routing
        client_mod.HOST_URL = f"http://127.0.0.1:{HOST_MOCK_PORT}"
        client_mod.GAME_URL = f"http://127.0.0.1:{HOST_MOCK_PORT + 1}"
        res = client_mod.dispatch("send_ingame", {"sender": "Nagi", "message": "direct"})
        assert res.get("ok") is True, f"client dispatch failed: {res}"
        print("A) client.py send_ingame -> host /chat/push (direct)  OK", res)

        # C) server exposes route + tools
        routes = [r.path for r in server_mod.app.routes]
        assert "/ingame-in" in routes, f"missing /ingame-in: {routes}"
        assert callable(server_mod.read_ingame) and callable(server_mod.send_ingame)
        print("C) server.py tools + /ingame-in route  OK")

        # full e2e over the tunnel
        await run_loop()
    finally:
        host_srv.shutdown()


if __name__ == "__main__":
    asyncio.run(main())
    print("ALL OK")
