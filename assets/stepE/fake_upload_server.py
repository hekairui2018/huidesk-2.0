"""步E 探针用的假上报端点：收 POST → 落 jsonl → 回 200。
顺带做一条红线检查：若请求体里出现 password 字段，记一笔 redline_hit。
"""
import json
import socketserver
import http.server
import sys

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 59777
SINK = sys.argv[2] if len(sys.argv) > 2 else "received.jsonl"


class Handler(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(length).decode("utf-8")
        try:
            record = json.loads(raw)
        except Exception:
            record = {"_unparsed": raw}
        record["_upload_key_present"] = bool(self.headers.get("X-Upload-Key"))
        record["_redline_password_in_body"] = "password" in json.dumps(record)
        with open(SINK, "a", encoding="utf-8") as handle:
            handle.write(json.dumps(record, ensure_ascii=False) + "\n")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b'{"ok":true}')

    def log_message(self, *args):
        pass


socketserver.TCPServer.allow_reuse_address = True
with socketserver.TCPServer(("127.0.0.1", PORT), Handler) as server:
    server.serve_forever()
