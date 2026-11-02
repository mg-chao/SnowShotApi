"""Deterministic provider and recognition doubles for the deployment harness."""
import json
import re
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

CALLS = {}
LOCK = threading.Lock()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        pass

    def response(self, value, status=200):
        body = json.dumps(value).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_HEAD(self):
        self.send_response(405)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_GET(self):
        self.response(CALLS if self.path == "/metrics" else {"status": "ready"})

    def do_POST(self):
        body = self.rfile.read(int(self.headers["Content-Length"]))
        if self.path.startswith("/v2/"):
            with LOCK:
                CALLS[self.path] = CALLS.get(self.path, 0) + 1
            self.response({"html": "<table><tr><td>deployment</td></tr></table>"} if "table" in self.path
                          else {"latex": "\\frac{a+b}{c-d}"})
            return
        request = json.loads(body)
        text = request["messages"][-1]["content"]
        with LOCK:
            CALLS[text] = CALLS.get(text, 0) + 1
        delay = re.search(r"delay=(\d+(?:\.\d+)?)", text)
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(b'data: {"id":"deployment","choices":[{"delta":{"content":"started"}}]}\n\n')
        self.wfile.flush()
        time.sleep(float(delay.group(1)) if delay else 0)
        self.wfile.write(b'data: {"id":"deployment","choices":[{"delta":{"content":" finished"},"finish_reason":"stop"}]}\n\n')
        self.wfile.write(b'data: {"id":"deployment","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}\n\n')
        self.wfile.write(b'data: [DONE]\n\n')
        self.wfile.flush()
        self.close_connection = True


ThreadingHTTPServer(("0.0.0.0", 8080), Handler).serve_forever()
