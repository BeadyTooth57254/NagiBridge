"""Tiny mock of the NagiBridge game HTTP API, for LOCAL bridge development only.

Run on the game machine (or wherever the tunnel client points NAGI_GAME_URL) so the
whole server->tunnel->client->game round-trip can be validated without opening Stardew.
Defaults to port 58331 (the same the real mod uses).
"""
import json
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import urlparse, parse_qs


class Handler(BaseHTTPRequestHandler):
    def _send(self, obj, code=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        u = urlparse(self.path)
        if u.path == "/state":
            return self._send({
                "ok": True,
                "player": {"x": 20, "y": 30, "health": 100, "maxHealth": 100,
                           "stamina": 90, "maxStamina": 100, "isMoving": False, "facingDirection": 2},
                "location": {"name": "Farm"}, "time": "6:00", "menu": None, "inventory": [],
            })
        if u.path == "/ctx":
            q = parse_qs(u.query)
            r = int(q.get("radius", ["8"])[0])
            return self._send({
                "ok": True, "radius": r,
                "grid": [
                    ".....P....",
                    "...CC#....",
                    "..o.......",
                    "....TT....",
                ],
                "legend": "P player, C crop, M machine, T tree, . open, # wall, o object",
            })
        if u.path == "/surroundings":
            return self._send({"ok": True, "tiles": [{"x": 21, "y": 30, "crop": "Parsnip"}]})
        if u.path == "/machines":
            return self._send({"ok": True, "machines": [{"x": 73, "y": 14, "name": "Furnace", "ready": True}]})
        if u.path == "/status":
            return self._send({"ok": True, "running": True, "port": 58331})
        if u.path == "/menu":
            return self._send({"ok": True, "type": "none"})
        return self._send({"ok": False, "error": f"unknown GET {u.path}"}, 404)

    def do_POST(self):
        u = urlparse(self.path)
        try:
            n = int(self.headers.get("Content-Length", 0))
            args = json.loads(self.rfile.read(n)) if n else {}
        except Exception:
            args = {}
        if u.path == "/move":
            return self._send({"ok": True, "moved": True, "x": args.get("x"), "y": args.get("y")})
        if u.path == "/key":
            return self._send({"ok": True, "pressed": args.get("key"), "count": args.get("count", 1)})
        if u.path == "/interact":
            return self._send({"ok": True, "interacted": True, "at": args})
        if u.path == "/emote":
            return self._send({"ok": True, "emote": args.get("id")})
        if u.path == "/drop":
            return self._send({"ok": True, "dropped": True})
        if u.path == "/follow":
            return self._send({"ok": True, "following": args.get("target")})
        if u.path == "/area":
            return self._send({"ok": True, "area": {k: args.get(k) for k in ("op", "x1", "y1", "x2", "y2")}})
        if u.path == "/warp":
            return self._send({"ok": True, "warped": args.get("location")})
        if u.path == "/tool":
            return self._send({"ok": True, "tool": args.get("name")})
        if u.path == "/chat":
            return self._send({"ok": True, "chat": args.get("message")})
        return self._send({"ok": False, "error": f"unknown POST {u.path}"}, 404)

    def log_message(self, fmt, *a):
        pass


if __name__ == "__main__":
    import os

    port = int(os.environ.get("NAGI_MOCK_PORT", "58331"))
    HTTPServer(("127.0.0.1", port), Handler).serve_forever()
