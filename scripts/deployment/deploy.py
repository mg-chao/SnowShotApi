"""Workstation controller for the private SnowShot blue/green agents (stdlib only)."""
from __future__ import annotations

import argparse
import base64
import contextlib
import getpass
import json
from pathlib import Path
import shlex
import subprocess
import sys
import time

from agent import DeploymentError, RELEASE_ID, require

HERE = Path(__file__).resolve().parent


def validate_manifest(manifest):
    require(isinstance(manifest, dict) and set(manifest) <= {"id", "api", "workers", "migration"},
            "Unknown release manifest fields")
    require(isinstance(manifest.get("id"), str) and RELEASE_ID.fullmatch(manifest["id"]), "Invalid release ID")
    require(isinstance(manifest.get("api", {}), dict), "API manifest must be an object")
    workers = manifest.get("workers", {})
    require(isinstance(workers, dict) and set(workers) <= {"table", "latex"}, "Unknown worker assignments")
    require(all(isinstance(value, dict) for value in workers.values()), "Worker specifications must be objects")
    if "migration" in manifest:
        migration = manifest["migration"]
        require(isinstance(migration, dict) and set(migration) == {"image", "backward_compatible", "secrets_directory"}
                and migration["backward_compatible"] is True, "Migration requires an explicit backward-compatibility review")


class Session:
    def __init__(self, host, command):
        self.process = subprocess.Popen(
            ["ssh", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=10",
             "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", host, command],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", bufsize=1)
        try:
            self.read()
        except Exception:
            self.close()
            raise

    def read(self):
        line = self.process.stdout.readline()
        require(bool(line), "Deployment agent disconnected; use status before retrying")
        response = json.loads(line)
        require(response.get("ok"), response.get("error", "Deployment agent failed"))
        return response.get("result")

    def call(self, operation, **arguments):
        self.process.stdin.write(json.dumps({"operation": operation, "arguments": arguments}) + "\n")
        self.process.stdin.flush()
        return self.read()

    def close(self):
        if self.process.stdin:
            with contextlib.suppress(OSError):
                self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            self.process.wait(timeout=10)
        self.process.stdout.close()
        self.process.stderr.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def bootstrap(target, password=None):
    files = {name: base64.b64encode((HERE / name).read_bytes()).decode()
             for name in ("agent.py", "launch.py", "tunnel_recovery.py")}
    installer = (HERE / "install.py").read_text()
    for role, host in (("worker", target["worker_host"]), ("api", target["api_host"])):
        payload = json.dumps({"role": role, "target": target, "files": files}) + "\n"
        command = "python3 -c " + shlex.quote(installer)
        if role == "worker":
            command = "sudo -S -p '' " + command
            payload = (password if password is not None else getpass.getpass("Worker sudo password: ")) + "\n" + payload
        result = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
                                 "-o", "ConnectTimeout=10", host, command], input=payload.encode(),
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
        require(result.returncode == 0, f"{role} bootstrap failed; no credential output was logged")
        print(f"{role}: deployment helper installed", flush=True)


class Controller:
    def __init__(self, api, worker, target, directory):
        self.api, self.worker, self.target, self.directory = api, worker, target, directory

    def fixtures(self, kind):
        fixture = self.target["fixtures"][kind]
        path = (self.directory / fixture["path"]).resolve()
        return {"fixture": base64.b64encode(path.read_bytes()).decode(), "expected": fixture["expected"]}

    def verify_workers(self, release_id, workers):
        for kind, record in workers.items():
            credentials = self.api.call("worker_credentials", release_id=release_id, kind=kind)
            try:
                self.worker.call("probe", record=record, credentials=credentials, **self.fixtures(kind))
            finally:
                credentials.clear()
            self.api.call("worker_probe", release_id=release_id, kind=kind, worker=record, **self.fixtures(kind))
            print(f"{kind}: local and tunnel mTLS/recognition checks passed", flush=True)

    def adopt(self, release_id):
        workers = self.worker.call("inventory")
        result = self.api.call("adopt", release_id=release_id, container=self.target["legacy_container"], workers=workers)
        print(f"Adopted {result['active']} without restarting services", flush=True)
        self.verify_workers(release_id, workers)

    def stage(self, manifest):
        validate_manifest(manifest)
        status = self.api.call("public_status")
        active = status["releases"][status["active"]]
        selected = dict(active["workers"])
        created = []
        prepared = False
        try:
            for kind, spec in manifest.get("workers", {}).items():
                require(kind in ("table", "latex"), "Unknown worker kind")
                record = self.worker.call("prepare", kind=kind, base=selected[kind], spec=spec, release_id=manifest["id"])
                selected[kind] = record
                created.append(record)
            self.api.call("prepare", manifest=manifest, workers=selected)
            prepared = True
            if manifest.get("migration"):
                migration = manifest["migration"]
                self.api.call("migration", release_id=manifest["id"], image=migration["image"],
                              reviewed=migration.get("backward_compatible") is True,
                              secrets_directory=migration["secrets_directory"])
            for record in created:
                self.worker.call("start", record=record)
            self.verify_workers(manifest["id"], selected)
            self.api.call("start", release_id=manifest["id"])
            print(f"Staged {manifest['id']}; production routing is unchanged", flush=True)
        except Exception:
            # Cleanup is reference-aware. Never stop active or shared components.
            if prepared:
                with contextlib.suppress(Exception):
                    self.cleanup(wait=False)
            else:
                for record in created:
                    with contextlib.suppress(Exception):
                        self.worker.call("stop", record=record)
            raise

    def ensure_running(self, release):
        if release["status"] != "running":
            self.api.call("check_worker_ports", workers=release["workers"])
            for record in release["workers"].values():
                self.worker.call("start", record=record)
            self.verify_workers(release["id"], release["workers"])
            self.api.call("start", release_id=release["id"])

    def switch(self, release_id, *, observation=300):
        status = self.api.call("public_status")
        previous = status["active"]
        self.ensure_running(status["releases"][release_id])
        self.api.call("switch", release_id=release_id)
        print(f"Traffic switched to {release_id}; observing for {observation}s", flush=True)
        deadline = time.monotonic() + observation
        next_update = time.monotonic() + 30
        try:
            while True:
                self.api.call("check", release_id=release_id)
                if time.monotonic() >= deadline:
                    break
                if time.monotonic() >= next_update:
                    print(f"{release_id}: readiness and release identity remain healthy", flush=True)
                    next_update = time.monotonic() + 30
                time.sleep(min(5, max(0, deadline - time.monotonic())))
        except (Exception, KeyboardInterrupt):
            print("Observation interrupted or failed; restoring the previous verified release", flush=True)
            self.api.call("switch", release_id=previous)
            self.cleanup()
            raise
        print(f"{release_id}: observation passed", flush=True)

    def cleanup(self, *, wait=True):
        while True:
            result = self.api.call("cleanup")
            failures = []
            for record in result["retirable_workers"]:
                try:
                    self.worker.call("stop", record=record)
                except Exception:
                    failures.append(record["id"])
            require(not failures, "Worker cleanup needs attention: " + ", ".join(failures))
            if not result["pending"]:
                print("Inactive API processes and unreferenced workers stopped; artifacts retained", flush=True)
                return
            if not wait or any(time.time() >= item["deadline"] for item in result["pending"]):
                print("Cleanup unfinished: old nginx workers still have requests; processes retained", flush=True)
                return
            time.sleep(2)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", type=Path, required=True)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("bootstrap", help="Install reviewed root-owned helpers; prompts once for worker sudo")
    adopt = commands.add_parser("adopt")
    adopt.add_argument("release")
    stage = commands.add_parser("stage")
    stage.add_argument("manifest", type=Path)
    switch = commands.add_parser("switch")
    switch.add_argument("release")
    commands.add_parser("rollback")
    commands.add_parser("status")
    commands.add_parser("cleanup")
    arguments = parser.parse_args()
    target = json.loads(arguments.target.read_text())
    if arguments.command == "bootstrap":
        bootstrap(target)
        return
    root = target["api_root"]
    with Session(target["api_host"], "python3 " + shlex.quote(root + "/agent.py") + " --api-root " + shlex.quote(root)) as api:
        if arguments.command == "status":
            print(json.dumps(api.call("public_status"), indent=2))
            return
        with Session(target["worker_host"], "sudo -n /usr/local/libexec/snowshot-deploy-worker") as worker:
            controller = Controller(api, worker, target, arguments.target.resolve().parent)
            if arguments.command == "adopt":
                controller.adopt(arguments.release)
            elif arguments.command == "stage":
                manifest = json.loads(arguments.manifest.read_text())
                controller.stage(manifest)
            elif arguments.command == "switch":
                controller.switch(arguments.release)
            elif arguments.command == "rollback":
                previous = api.call("public_status")["previous"]
                require(previous, "There is no previous release")
                controller.switch(previous)
            elif arguments.command == "cleanup":
                controller.cleanup()


if __name__ == "__main__":
    try:
        main()
    except (DeploymentError, OSError, ValueError, subprocess.SubprocessError) as exception:
        message = str(exception) if isinstance(exception, DeploymentError) else type(exception).__name__
        print(f"Deployment stopped: {message}", file=sys.stderr)
        sys.exit(1)
