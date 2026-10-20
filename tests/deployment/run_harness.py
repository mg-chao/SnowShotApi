"""Real nginx + two API replicas + PostgreSQL/Redis switchover acceptance test."""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/deployment"))
import agent

RUNTIME = ROOT / "artifacts/deployment-harness"
COMPOSE = ["docker", "compose", "-f", str(Path(__file__).with_name("compose.yaml")), "-p", "snowshot-deployment-test"]
URL = "http://127.0.0.1:18443"


def compose(*arguments, check=True, timeout=600):
    result = subprocess.run([*COMPOSE, *arguments], cwd=ROOT, check=check, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout)
    return result.stdout.strip()


def sql(statement):
    return compose("exec", "-T", "postgres", "psql", "-U", "snowshot", "-d", "snowshot", "-At", "-c", statement)


def request(path, *, body=None, client="198.51.100.1", request_id=None, first_chunk=None):
    headers = {"Content-Type": "application/json", "X-Test-Client": client}
    if request_id:
        headers["X-Request-ID"] = request_id
    if isinstance(body, bytes):
        boundary = "snowshot-deployment-fixture"
        headers["Content-Type"] = f"multipart/form-data; boundary={boundary}"
        data = (f'--{boundary}\r\nContent-Disposition: form-data; name="image"; filename="fixture.webp"\r\n'
                'Content-Type: image/webp\r\n\r\n').encode() + body + f"\r\n--{boundary}--\r\n".encode()
    else:
        data = json.dumps(body).encode() if body is not None else None
    outgoing = urllib.request.Request(URL + path, data=data, headers=headers)
    with urllib.request.urlopen(outgoing, timeout=100) as response:
        content = b""
        if first_chunk:
            content = response.readline()
            assert content.startswith(b"data:"), content
            first_chunk.set()
        content += response.read()
        return response.status, content, response.headers.get("X-Snowshot-Release")


class HarnessAgent(agent.ApiAgent):
    def nginx(self, *arguments):
        return compose("exec", "-T", "nginx", "nginx", *arguments)

    def nginx_workers(self):
        output = compose("exec", "-T", "nginx", "sh", "-c",
                         'for p in $(cat /proc/1/task/1/children); do cat /proc/$p/stat 2>/dev/null || true; done')
        identities = []
        for line in output.splitlines():
            fields = line.rsplit(")", 1)[1].split()
            identities.append({"pid": int(line.split()[0]), "start": fields[19]})
        return identities

    def routed_release(self):
        return request("/health/ready")[2]

    def probe(self, port):
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/health/ready", timeout=15) as response:
            assert json.load(response)["status"] == "ready"
        fingerprint = sql('SELECT encode("Fingerprint",\'hex\') FROM snowshot.policy_revisions WHERE "Revision"=15;')
        return {"revision": 15, "fingerprint": fingerprint}

    def upstream(self, release):
        return (f"upstream snowshot_api {{ server api-{release['id']}:8080; }}\n"
                f"map $host $snowshot_release {{ default {release['id']}; }}\n")

    def compose(self, release, *arguments, **_):
        return compose(*arguments[:-1], f"api-{release['id']}")


def chat(label, delay, client, first_chunk=None):
    status, body, release = request("/api/v1/chat/completions", request_id=label, client=client,
        first_chunk=first_chunk, body={"model": "ai-model", "stream": True,
                                      "messages": [{"role": "user", "content": f"{label} delay={delay}"}]})
    assert status == 200 and b"[DONE]" in body and b"finished" in body, body
    return release


def wait(function, timeout=90):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            if function():
                return
        except (OSError, ValueError, AssertionError, subprocess.SubprocessError):
            pass
        time.sleep(0.3)
    raise AssertionError("Timed out waiting for harness condition")


def main():
    RUNTIME.mkdir(parents=True, exist_ok=True)
    os.environ["SNOWSHOT_DEPLOY_NGINX_DIRECTORY"] = str(RUNTIME)
    (RUNTIME / "main.txt").write_text("worker_processes 2;\nevents { worker_connections 1024; }\nhttp { include /etc/nginx/conf.d/*.conf; }\n")
    (RUNTIME / "server.conf").write_text("""server {
        listen 8080;
        location / {
            proxy_pass http://snowshot_api;
            proxy_next_upstream off;
            proxy_buffering off;
            proxy_read_timeout 310s;
            proxy_set_header Host example.test;
            proxy_set_header X-Forwarded-For $http_x_test_client;
            proxy_set_header X-Forwarded-Proto https;
            add_header X-Snowshot-Release $snowshot_release always;
        }
    }\n""")
    api = HarnessAgent(RUNTIME / "state", {"public_host": "example.test", "nginx_include": str(RUNTIME / "upstream.conf"),
                                          "legacy_container": "unused"})
    for name, port, slot in (("blue", 15101, "blue"), ("green", 15102, "green")):
        api.state["releases"][name] = {"id": name, "slot": slot, "port": port, "container": name,
             "status": "running", "workers": {}, "drain_generations": []}
    api.state["active"] = "blue"
    agent.atomic(RUNTIME / "upstream.conf", api.upstream(api.release("blue")), 0o644)
    errors, stop = [], threading.Event()
    try:
        print("Building deployment acceptance images", flush=True)
        compose("build", "api-blue", "migrate")
        # No network and no dependencies: description must not activate policy or call a provider.
        description = json.loads(compose("run", "--rm", "--no-deps", "describe", timeout=30))
        assert description["policy_revision"] == 15 and description["drain_timeout_seconds"] == 360
        compose("up", "-d", "--wait", "postgres", "redis", "provider", "worker-blue", "worker-green")
        compose("run", "--rm", "migrate")
        compose("up", "-d", "api-blue", "api-green", "nginx")
        wait(lambda: api.probe(15101) == api.probe(15102))
        for release in api.state["releases"].values():
            release["policy"] = api.probe(release["port"])
        wait(lambda: request("/health/ready")[2] == "blue")
        fixture = (Path(__file__).parent / "fixtures/table.webp").read_bytes()
        for kind in ("table", "latex"):
            assert request(f"/api/v1/{kind}/extract", body=fixture, request_id=f"before-{kind}")[0] == 200

        def continuous():
            while not stop.wait(0.05):
                try:
                    assert request("/api/v1/chat/models")[0] == 200
                except Exception as error:
                    errors.append(type(error).__name__)

        monitor = threading.Thread(target=continuous)
        monitor.start()
        first_chunk = threading.Event()
        with ThreadPoolExecutor(max_workers=3) as pool:
            long = pool.submit(chat, "long-stream", 40, "198.51.100.1", first_chunk)
            assert first_chunk.wait(15), "Stream did not flush before the switch"
            queued = pool.submit(chat, "queued-before-switch", 0.2, "198.51.100.2")
            wait(lambda: int(compose("exec", "-T", "redis", "redis-cli", "ZCARD", "{snowshot:ai-model}:queue:order")) >= 1)
            print("Switching while a >30s stream and queued request are active", flush=True)
            api.switch("green")
            assert not long.done(), "Long stream ended before switchover"
            assert any(identity in api.nginx_workers() for identity in api.release("blue")["drain_generations"])
            new = pool.submit(chat, "new-after-switch", 0.2, "198.51.100.3")
            assert long.result() == "blue"
            assert queued.result() == "blue"
            assert new.result() == "green"
        stop.set()
        monitor.join(timeout=10)
        assert not errors, errors
        for kind in ("table", "latex"):
            assert request(f"/api/v1/{kind}/extract", body=fixture, client="198.51.100.4", request_id=f"after-{kind}")[0] == 200
        for port in (15301, 15302):
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/metrics") as response:
                assert json.load(response) == {"/v2/table/extract": 1, "/v2/latex/extract": 1}
        with urllib.request.urlopen("http://127.0.0.1:15300/metrics") as response:
            calls = json.load(response)
        assert len(calls) == 3 and all(value == 1 for value in calls.values()), calls
        wait(lambda: sql('SELECT count(*) FROM snowshot.usage_operations WHERE "State" IN (0,1);') == "0")
        assert sql("SELECT count(*) FROM snowshot.usage_operations;") == "7"
        assert sql("SELECT count(*) FROM snowshot.usage_events;") == "7"
        assert sql('SELECT sum("Requests") FROM snowshot.daily_aggregates;') == "7"
        assert sql('SELECT count(*) FROM (SELECT "OperationId" FROM snowshot.usage_events GROUP BY 1 HAVING count(*)<>1) x;') == "0"
        assert sql('SELECT sum("ReservedNanoYuan") FROM snowshot.allowance_periods;') == "0"
        assert sql('SELECT sum("ReservedNanoYuan") FROM snowshot.operator_budget_periods;') == "0"
        print("Rehearsing rollback and final forward switch", flush=True)
        api.switch("blue")
        api.switch("green")
        wait(lambda: not any(item in api.nginx_workers() for item in api.release("blue")["drain_generations"]))
        with patch.object(agent, "alive", side_effect=lambda identity: identity in api.nginx_workers()):
            assert not api.cleanup()["pending"]
        assert request("/health/ready")[2] == "green"
        print("Deployment harness passed: uninterrupted stream/queue, worker routing, rollback, seven exactly-once settlements", flush=True)
    except Exception:
        print(compose("logs", "--tail", "35", check=False), file=sys.stderr)
        raise
    finally:
        stop.set()
        if os.environ.get("SNOWSHOT_KEEP_DEPLOYMENT_STACK") != "1":
            compose("down", "--volumes", "--remove-orphans", check=False)


if __name__ == "__main__":
    main()
