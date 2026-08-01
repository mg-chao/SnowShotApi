from __future__ import annotations

import json
import os
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
COMPOSE = Path(__file__).with_name("compose.yaml")
COMPOSE_COMMAND = ["docker", "compose", "-f", str(COMPOSE), "-p", "snowshot-fault"]
API_URLS = ("http://127.0.0.1:5101", "http://127.0.0.1:5102")
PROVIDER_URL = "http://127.0.0.1:5300"
REDIS_PORT = int(os.environ.get("SNOWSHOT_FAULT_REDIS_PORT", "46380"))


def compose(*arguments: str, capture: bool = False, check: bool = True) -> str:
    completed = subprocess.run(
        [*COMPOSE_COMMAND, *arguments],
        cwd=ROOT,
        check=check,
        text=True,
        stdout=subprocess.PIPE if capture else None,
        stderr=subprocess.STDOUT if capture else None,
    )
    return completed.stdout.strip() if capture else ""


def request(method: str, url: str, body: dict[str, Any] | None = None, request_id: str | None = None,
            timeout: float = 15.0) -> tuple[int, bytes]:
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json", "Accept-Language": "en-US"}
    if request_id is not None:
        headers["X-Request-ID"] = request_id
    outgoing = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(outgoing, timeout=timeout) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as exception:
        return exception.code, exception.read()


def wait_ready(url: str, timeout: float = 90.0) -> None:
    deadline = time.monotonic() + timeout
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        try:
            status, body = request("GET", f"{url}/health/ready", timeout=2)
            if status == 200 and json.loads(body)["status"] == "ready":
                return
        except (OSError, ValueError, KeyError) as exception:
            last_error = exception
        time.sleep(0.5)
    raise AssertionError(f"{url} did not become ready: {last_error}")


def provider_metrics() -> dict[str, int]:
    status, body = request("GET", f"{PROVIDER_URL}/metrics", timeout=2)
    assert status == 200
    return json.loads(body)


def reset_provider() -> None:
    status, body = request("POST", f"{PROVIDER_URL}/control/reset", {})
    assert status == 200, body.decode(errors="replace")


def redis_queue_length() -> int:
    command = b"*2\r\n$5\r\nZCARD\r\n$34\r\n{snowshot:translation}:queue:order\r\n"
    with socket.create_connection(("127.0.0.1", REDIS_PORT), timeout=2) as connection:
        connection.sendall(command)
        response = connection.recv(64)
    if not response.startswith(b":"):
        raise AssertionError(f"unexpected Redis response: {response!r}")
    return int(response[1:].split(b"\r\n", 1)[0])


def translation(index: int, request_id: str, delay: float) -> tuple[int, bytes]:
    body = {
        "type": 0,
        "content": [f"load-{index} delay={delay}"],
        "from": "en",
        "to": "zh-CHS",
        "domain": "general",
    }
    return request("POST", f"{API_URLS[index % 2]}/api/v2/translation/translate", body, request_id, 20)


def assert_translation_batch() -> None:
    reset_provider()
    items = [f"batch-{index} delay=0.2" for index in range(8)]
    items[3] += " fail-once"
    body = {
        "type": 0,
        "content": items,
        "from": "en",
        "to": "zh-CHS",
        "domain": "general",
    }
    status, response = request("POST", f"{API_URLS[0]}/api/v2/translation/translate", body, "batch-concurrency", 20)
    assert status == 200, f"translation batch failed: {status} {response[:512]!r}"
    translated = [item["content"] for item in json.loads(response)["data"]["results"]]
    assert translated == [f"translated:{item}" for item in items], f"translation results reordered: {translated}"
    metrics = provider_metrics()
    assert metrics["max_active"] == 4, f"translation batch did not use exactly four conversations: {metrics}"
    assert metrics["requests"] == 9, f"translation retry count was not isolated to one item: {metrics}"
    assert metrics["calls"][items[3]] == 2, f"fail-once item was not retried once: {metrics}"
    assert all(metrics["calls"][item] == (2 if index == 3 else 1) for index, item in enumerate(items)), metrics


def sql(statement: str) -> str:
    return compose(
        "exec", "-T", "postgres", "psql", "-U", "snowshot", "-d", "snowshot",
        "-v", "ON_ERROR_STOP=1", "-Atc", statement, capture=True,
    )


def assert_admission() -> None:
    reset_provider()
    with ThreadPoolExecutor(max_workers=20) as executor:
        futures = [executor.submit(translation, index, f"load-{index}", 1.5) for index in range(20)]
        time.sleep(0.5)
        queue_length = redis_queue_length()
        results = [future.result() for future in futures]
    statuses = [status for status, _ in results]
    metrics = provider_metrics()
    assert queue_length <= 2, f"Redis queue exceeded capacity: {queue_length}"
    assert metrics["max_active"] <= 2, f"provider concurrency exceeded limit: {metrics}"
    response_bodies = [body[:512].decode(errors="replace") for status, body in results if status != 429]
    assert statuses.count(200) == 4, (
        f"expected two active and two queued successes: statuses={statuses}, "
        f"metrics={metrics}, responses={response_bodies}"
    )
    assert statuses.count(429) == 16, f"expected exact queue overflow rejection: {statuses}"
    assert metrics["requests"] == 4, f"rejected requests reached the provider: {metrics}"


def assert_duplicate() -> None:
    reset_provider()
    with ThreadPoolExecutor(max_workers=2) as executor:
        futures = [executor.submit(translation, index, "same-request-id", 1.0) for index in range(2)]
        results = [future.result() for future in futures]
    statuses = sorted(status for status, _ in results)
    metrics = provider_metrics()
    assert statuses == [200, 409], f"concurrent duplicate did not execute exactly once: {statuses}"
    assert metrics["requests"] == 1, f"duplicate reached provider more than once: {metrics}"


def assert_queued_replica_death() -> None:
    reset_provider()
    with ThreadPoolExecutor(max_workers=3) as executor:
        active = [
            executor.submit(translation, 1, "queued-death-active-1", 8.0),
            executor.submit(translation, 3, "queued-death-active-2", 8.0),
        ]
        deadline = time.monotonic() + 10
        while provider_metrics()["active"] != 2:
            if time.monotonic() >= deadline:
                raise AssertionError("queued-death setup did not occupy both admission slots")
            time.sleep(0.1)
        queued = executor.submit(translation, 0, "queued-owner-dies", 0.1)
        deadline = time.monotonic() + 5
        while redis_queue_length() != 1:
            if time.monotonic() >= deadline:
                raise AssertionError("request did not enter the Redis queue before replica death")
            time.sleep(0.1)
        compose("kill", "-s", "KILL", "api1")
        try:
            queued.result(timeout=10)
        except (OSError, TimeoutError, urllib.error.URLError):
            pass
        assert all(future.result()[0] == 200 for future in active)

    time.sleep(6)
    status, body = translation(1, "queued-death-probe", 0.1)
    assert status == 200, f"expired dead-replica ticket blocked future work: {status} {body[:512]!r}"
    assert redis_queue_length() == 0, "expired dead-replica ticket was not evicted"
    compose("up", "-d", "api1")
    wait_ready(API_URLS[0])


def assert_crash_reconciliation() -> None:
    reset_provider()
    unknown_before = int(sql('SELECT count(*) FROM snowshot.usage_operations WHERE "State" = 4;'))
    chat = {
        "model": "qwen-flash",
        "messages": [{"role": "user", "content": "delay=120"}],
        "temperature": 0,
        "max_tokens": 512,
        "enable_thinking": False,
        "thinking_budget_tokens": 4096,
    }
    with ThreadPoolExecutor(max_workers=1) as executor:
        future = executor.submit(
            request,
            "POST",
            f"{API_URLS[0]}/api/v1/chat/completions",
            chat,
            "crash-after-dispatch",
            150,
        )
        deadline = time.monotonic() + 15
        while provider_metrics()["active"] != 1:
            if time.monotonic() >= deadline:
                raise AssertionError("crash request never reached the provider")
            time.sleep(0.1)
        compose("kill", "-s", "KILL", "api1")
        try:
            future.result(timeout=15)
        except (OSError, TimeoutError, urllib.error.URLError):
            pass

    deadline = time.monotonic() + 55
    while time.monotonic() < deadline:
        if sql('SELECT count(*) FROM snowshot.usage_operations WHERE "State" IN (0, 1);') == "0":
            break
        time.sleep(2)
    else:
        raise AssertionError("crashed replica left nonterminal accounting after lease expiry")

    unknown_after = int(sql('SELECT count(*) FROM snowshot.usage_operations WHERE "State" = 4;'))
    assert unknown_after == unknown_before + 1, (
        f"crashed provider dispatch did not add exactly one unknown-cost operation: "
        f"before={unknown_before}, after={unknown_after}"
    )


def assert_accounting() -> None:
    assert sql('SELECT count(*) FROM snowshot.policy_revisions WHERE "Revision" = 1;') == "1"
    assert sql('SELECT "ActiveRevision" FROM snowshot.policy_state WHERE "Id" = 1;') == "1"
    assert sql('SELECT count(*) FROM snowshot.allowance_periods WHERE "AppliedPolicyRevision" <> 1;') == "0"
    assert sql('SELECT count(*) FROM snowshot.operator_budget_periods WHERE "AppliedPolicyRevision" <> 1;') == "0"
    assert sql('SELECT count(*) FROM (SELECT "IdempotencyHash" FROM snowshot.usage_operations GROUP BY "IdempotencyHash" HAVING count(*) > 1) AS duplicates;') == "0"
    assert sql('SELECT count(*) FROM (SELECT "OperationId" FROM snowshot.usage_events GROUP BY "OperationId" HAVING count(*) <> 1) AS invalid;') == "0"
    operations = sql("SELECT count(*) FROM snowshot.usage_operations;")
    events = sql("SELECT count(*) FROM snowshot.usage_events;")
    assert operations == events, f"terminal operation/event mismatch: operations={operations}, events={events}"
    aggregate = sql('SELECT coalesce(sum("Requests"), 0) FROM snowshot.daily_aggregates;')
    assert aggregate == events, f"aggregate contribution mismatch: aggregate={aggregate}, events={events}"
    assert sql('SELECT coalesce(sum("ReservedNanoYuan"), 0) FROM snowshot.allowance_periods;') == "0"
    assert sql('SELECT coalesce(sum("ReservedNanoYuan"), 0) FROM snowshot.operator_budget_periods;') == "0"


def main() -> int:
    keep = os.environ.get("SNOWSHOT_KEEP_LOAD_STACK") == "1"
    try:
        compose("up", "-d", "--build", "--wait", "postgres", "redis", "fake-provider")
        compose("run", "--rm", "--build", "migrator")
        compose("build", "api1", "api2")
        compose("up", "-d", "api1", "api2")
        for url in API_URLS:
            wait_ready(url)
        assert_translation_batch()
        assert_admission()
        assert_queued_replica_death()
        assert_duplicate()
        assert_crash_reconciliation()
        assert_accounting()
        print("Two-replica fault/load harness passed.")
        return 0
    finally:
        if not keep:
            compose("down", "--volumes", "--remove-orphans", check=False)


if __name__ == "__main__":
    sys.exit(main())
