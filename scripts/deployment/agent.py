"""Private JSON-line deployment agent. Installed root-owned on each server.

Only stdout is the protocol. Configuration and container inspection stay in private
server-side snapshots; the workstation receives release metadata, never secrets.
"""
from __future__ import annotations

import argparse
import base64
import contextlib
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import socket
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

SLOTS = {"blue": {"api": 5000, "table": 18080, "latex": 18081},
         "green": {"api": 5001, "table": 28080, "latex": 28081}}
WORKER_ID = re.compile(r"^(table|latex)-(blue|green)-[a-z0-9][a-z0-9-]{0,62}$")
RELEASE_ID = re.compile(r"^[a-z0-9][a-z0-9-]{0,62}$")
IMAGE_REF = re.compile(r"^(?:sha256:[0-9a-f]{64}|[a-z0-9][a-z0-9._:/-]*@sha256:[0-9a-f]{64})$")
WORKER_OPTIONS = {"table": "Table", "latex": "Latex"}


class DeploymentError(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise DeploymentError(message)


def run(arguments, *, input=None, timeout=180, check=True):
    result = subprocess.run(arguments, input=input, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, timeout=timeout)
    if check and result.returncode:
        # Commands can contain secrets in their output. Do not relay raw diagnostics.
        raise DeploymentError(f"{Path(arguments[0]).name} failed (exit {result.returncode})")
    return result


def atomic(path, value, mode=0o600):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor, temporary = tempfile.mkstemp(prefix=".pending-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(value.encode() if isinstance(value, str) else value)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
        if os.name == "posix":
            directory = os.open(path.parent, os.O_RDONLY)
            try:
                os.fsync(directory)
            finally:
                os.close(directory)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def write_json(path, value):
    atomic(path, json.dumps(value, indent=2, sort_keys=True) + "\n")


def read_json(path):
    return json.loads(Path(path).read_text())


def tree_hash(path, exclusions=()):
    path = Path(path)
    digest = hashlib.sha256()
    files = sorted(path.rglob("*")) if path.is_dir() else [path]
    for item in files:
        relative = item.relative_to(path).as_posix() if item != path else "."
        if any(part in {"__pycache__", ".git", "logs", "evidence", ".pytest_cache"}
               for part in item.relative_to(path).parts) or item.name in exclusions:
            continue
        # Venvs have interpreter symlinks. Hash the link target without following it.
        digest.update(relative.encode() + b"\0")
        if item.is_symlink():
            digest.update(b"link\0" + os.readlink(item).encode())
        elif item.is_file():
            with item.open("rb") as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(block)
    return digest.hexdigest()


def preserve_ownership(source, destination):
    """copy2/copytree preserve mode but not UID/GID; bind-mounted secrets need both."""
    if os.name != "posix":
        return
    source, destination = Path(source), Path(destination)
    paths = [source, *source.rglob("*")] if source.is_dir() else [source]
    for item in paths:
        stat = item.stat()
        target = destination / item.relative_to(source) if item != source else destination
        os.chown(target, stat.st_uid, stat.st_gid)


def free_memory():
    values = dict(line.split(":", 1) for line in Path("/proc/meminfo").read_text().splitlines())
    return int(values["MemAvailable"].split()[0]) * 1024


def capacity(additional, reserve):
    require(free_memory() >= additional + reserve,
            "Insufficient available memory; the active release is unchanged")


def port_free(port):
    with socket.socket() as listener:
        try:
            listener.bind(("127.0.0.1", port))
        except OSError:
            return False
    return True


def environment(text):
    result = {}
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        key, separator, value = line.partition("=")
        require(separator and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key), "Invalid environment file")
        result[key] = shlex.split(value)[0] if value[:1] in ("'", '"') else value
    return result


def properties(unit):
    output = run(["systemctl", "show", unit, "-p", "ActiveState", "-p", "MainPID",
                  "-p", "ExecStart", "-p", "EnvironmentFiles", "-p", "Environment",
                  "-p", "CPUAffinity", "-p", "MemoryCurrent", "-p", "User", "-p", "NRestarts"]).stdout.decode()
    result = {}
    for line in output.splitlines():
        if "=" in line:
            key, value = line.split("=", 1)
            result[key] = result.get(key, "") + " " + value if key == "EnvironmentFiles" else value
    return result


def policy_pair(report):
    policy = report["policy"]
    pair = (policy["configured_revision"], policy["configured_fingerprint"])
    require(pair == (policy["active_revision"], policy["active_fingerprint"]), "Policy is not converged")
    return {"revision": pair[0], "fingerprint": pair[1]}


def process_identity(pid):
    try:
        fields = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
        return {"pid": int(pid), "start": fields[19]}
    except (FileNotFoundError, ProcessLookupError):
        return None


def alive(identity):
    return process_identity(identity["pid"]) == identity


class ApiAgent:
    def __init__(self, root, settings):
        self.root = Path(root)
        self.settings = settings
        self.state_file = self.root / "state.json"
        self.state = read_json(self.state_file) if self.state_file.exists() else {
            "version": 1, "active": None, "previous": None, "pending": None, "releases": {}, "events": []}

    def save(self, event=None):
        if event:
            self.state["events"].append({"at": time.time(), "event": event})
            self.state["events"] = self.state["events"][-200:]
        write_json(self.state_file, self.state)

    def release(self, release_id):
        require(release_id in self.state["releases"], "Unknown release")
        return self.state["releases"][release_id]

    def directory(self, release_id):
        require(RELEASE_ID.fullmatch(release_id), "Invalid release ID")
        return self.root / "releases" / release_id

    def probe(self, port):
        request = urllib.request.Request(f"http://127.0.0.1:{port}/health/components", headers={
            "Host": self.settings["public_host"], "X-Forwarded-For": "127.0.0.1", "X-Forwarded-Proto": "https"})
        with urllib.request.urlopen(request, timeout=15) as response:
            report = json.load(response)
        identity = policy_pair(report)
        require(report["components"] and all(report["components"].values()), "API dependencies are not ready")
        return identity

    def routed_release(self):
        host = self.settings["public_host"]
        result = run(["curl", "--fail", "--silent", "--show-error", "--max-time", "15",
                      "--resolve", f"{host}:443:127.0.0.1", "--dump-header", "-", "--output", "/dev/null",
                      f"https://{host}/health/ready"])
        match = re.search(r"(?im)^X-Snowshot-Release:\s*([^\r\n]+)", result.stdout.decode())
        return match.group(1).strip() if match else None

    def nginx(self, *arguments):
        return run(["nginx", *arguments])

    def nginx_workers(self):
        master = Path(self.settings.get("nginx_pid", "/run/nginx.pid")).read_text().strip()
        children = Path(f"/proc/{master}/task/{master}/children").read_text().split()
        result = []
        for pid in children:
            try:
                if b"worker process" in Path(f"/proc/{pid}/cmdline").read_bytes():
                    identity = process_identity(pid)
                    if identity:
                        result.append(identity)
            except FileNotFoundError:
                pass
        return result

    def upstream(self, release):
        return (f"# Managed by SnowShot deployment. Release {release['id']}\n"
                f"upstream snowshot_api {{ server 127.0.0.1:{release['port']}; }}\n"
                f"map $host $snowshot_release {{ default {release['id']}; }}\n")

    def compose(self, release, *arguments, timeout=180):
        return run(["docker", "compose", "-p", f"snowshot-bg-{release['id']}", "-f",
                    str(self.directory(release["id"]) / "compose.json"), *arguments], timeout=timeout)

    def snapshot(self, release_id, inspected):
        directory = self.directory(release_id)
        require(not directory.exists(), "Release directory already exists; inspect status before retrying")
        directory.mkdir(parents=True, mode=0o700)
        template = copy.deepcopy(inspected)
        for index, mount in enumerate(template["Mounts"]):
            require(mount["Type"] == "bind" and not mount["RW"], "Adoption requires read-only bind mounts")
            source = Path(mount["Source"])
            require(not source.is_symlink(), "Configuration mount symlinks are unsupported")
            destination = directory / "configuration" / str(index)
            destination.parent.mkdir(exist_ok=True, mode=0o700)
            if source.is_dir():
                require(not any(item.is_symlink() for item in source.rglob("*")), "Configuration contains symlinks")
                shutil.copytree(source, destination, copy_function=shutil.copy2)
            else:
                shutil.copy2(source, destination)
            preserve_ownership(source, destination)
            mount["Source"] = str(destination)
        write_json(directory / "template.json", template)
        return template

    @staticmethod
    def env(template):
        return dict(item.split("=", 1) for item in template["Config"]["Env"])

    @staticmethod
    def host_path(template, container_path):
        for mount in sorted(template["Mounts"], key=lambda value: len(value["Destination"]), reverse=True):
            destination = mount["Destination"]
            if container_path == destination or container_path.startswith(destination.rstrip("/") + "/"):
                return Path(mount["Source"]) / container_path[len(destination):].lstrip("/")
        raise DeploymentError("Required credential path is not in a frozen mount")

    def effective_option(self, template, key):
        values = self.env(template)
        value = values.get(key)
        settings_mount = next((m for m in template["Mounts"]
                               if m["Destination"] == "/app/appsettings.Production.json"), None)
        if value is None and settings_mount:
            item = read_json(settings_mount["Source"])
            for part in key.split("__"):
                item = item.get(part, {}) if isinstance(item, dict) else {}
            if not isinstance(item, dict):
                value = item
        if values.get("SNOWSHOT_SECRETS_DIRECTORY"):
            secret = self.host_path(template, values["SNOWSHOT_SECRETS_DIRECTORY"]) / key
            if secret.exists():
                value = secret.read_text().rstrip("\r\n")
        return value

    def set_option(self, template, key, value):
        values = self.env(template)
        values[key] = value
        template["Config"]["Env"] = [f"{key}={item}" for key, item in values.items()]
        if values.get("SNOWSHOT_SECRETS_DIRECTORY"):
            secret = self.host_path(template, values["SNOWSHOT_SECRETS_DIRECTORY"]) / key
            # Mounted secrets have higher precedence than environment variables.
            if secret.exists():
                secret.write_text(value + "\n")

    def configuration_identity(self, template, *, legacy=False):
        exclusions = {"Providers__Table__BaseUrl", "Providers__Latex__BaseUrl"} if legacy else set()
        values = self.env(template)
        if legacy:
            values = {k: v for k, v in values.items() if k not in exclusions | {"ASPNETCORE_URLS"}}
        payload = {"image": template["Image"], "env": values,
                   "mounts": {m["Destination"]: tree_hash(m["Source"], exclusions) for m in template["Mounts"]}}
        return hashlib.sha256(json.dumps(payload, sort_keys=True).encode()).hexdigest()

    def make_compose(self, release, template):
        config = template["Config"]
        service = {"image": template["Image"], "container_name": release["container"],
                   "network_mode": "host", "restart": "unless-stopped", "stop_grace_period": "390s",
                   "environment": self.env(template), "entrypoint": config["Entrypoint"],
                   "command": config.get("Cmd"), "working_dir": config.get("WorkingDir", "/app"),
                   "user": config.get("User", ""),
                   "volumes": [{"type": "bind", "source": m["Source"], "target": m["Destination"],
                                "read_only": True} for m in template["Mounts"]]}
        if template["HostConfig"].get("ExtraHosts"):
            service["extra_hosts"] = template["HostConfig"]["ExtraHosts"]
        if template["HostConfig"].get("LogConfig", {}).get("Type"):
            log = template["HostConfig"]["LogConfig"]
            service["logging"] = {"driver": log["Type"], "options": log["Config"]}
        write_json(self.directory(release["id"]) / "compose.json", {"services": {"api": service}})

    def adopt(self, release_id, container, workers):
        require(not self.state["active"], "Production is already adopted")
        require(RELEASE_ID.fullmatch(release_id), "Invalid release ID")
        inspected = json.loads(run(["docker", "inspect", container]).stdout)[0]
        require(inspected["HostConfig"]["NetworkMode"] == "host", "Only host-network production API adoption is supported")
        require(self.env(inspected).get("ASPNETCORE_URLS") == "http://127.0.0.1:5000", "Legacy API listener must be loopback port 5000")
        identity = self.probe(5000)
        template = self.snapshot(release_id, inspected)
        release = {"id": release_id, "slot": "blue", "port": 5000, "container": container,
                   "image": inspected["Image"], "policy": identity, "workers": workers,
                   "status": "running", "legacy": True, "created_at": time.time(), "drain_generations": []}
        release["configuration_hash"] = self.configuration_identity(template)
        release["legacy_inputs_hash"] = self.configuration_identity(template, legacy=True)
        self.make_compose(release, template)
        backup = self.root / "nginx-backup"
        backup.mkdir(mode=0o700, exist_ok=True)
        originals = {}
        include = Path(self.settings["nginx_include"])
        require(not include.exists(), "Managed nginx include already exists without adopted state")
        try:
            # Publish the write-ahead record before changing ingress.
            self.state["pending"] = {"operation": "adopt", "release": release_id}
            self.state["releases"][release_id] = release
            self.save("adoption prepared")
            for index, configured_path in enumerate(self.settings["nginx_files"]):
                path = Path(configured_path).resolve()
                content = path.read_text()
                originals[path] = content
                atomic(backup / f"{index}.conf", content)
                updated, count = re.subn(r"proxy_pass\s+http://(?:localhost|127\.0\.0\.1):5000;",
                                        "proxy_pass http://snowshot_api;\n        proxy_next_upstream off;", content)
                if not count:
                    continue
                updated = re.sub(r"(location\s+=\s+/health/(?:live|ready)\s*\{)",
                                 r"\1\n        add_header X-Snowshot-Release $snowshot_release always;", updated)
                atomic(path, updated, 0o644)
            require(originals, "No nginx configuration selected")
            atomic(include, self.upstream(release), 0o644)
            self.nginx("-t")
            expanded = self.nginx("-T").stdout.decode()
            require(not re.search(r"(?m)^\s*proxy_pass\s+http://(?:localhost|127\.0\.0\.1):5000\s*;", expanded),
                    "An nginx location still targets port 5000 directly; include every API proxy file")
            self.nginx("-s", "reload")
            self.wait_route(release_id)
        except Exception:
            for path, content in originals.items():
                atomic(path, content, 0o644)
            include.unlink(missing_ok=True)
            self.nginx("-t")
            self.nginx("-s", "reload")
            self.state["pending"] = {"operation": "adopt_failed", "release": release_id}
            self.save("adoption reverted")
            raise
        self.state.update(active=release_id, pending=None)
        self.save("legacy release adopted without restart")
        return self.public_status()

    def wait_route(self, release_id):
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            try:
                if self.routed_release() == release_id:
                    return
            except Exception:
                pass
            time.sleep(1)
        raise DeploymentError("nginx did not confirm the selected release")

    def public_status(self):
        routed = None
        with contextlib.suppress(Exception):
            routed = self.routed_release()
        if routed in self.state["releases"] and routed != self.state["active"]:
            former = self.state["active"]
            self.state.update(active=routed, previous=former, pending={"operation": "recovered_switch", "release": routed})
            self.save("reconciled routing after interrupted switch")
        return {"active": self.state["active"], "previous": self.state["previous"], "routed": routed,
                "pending": self.state["pending"], "releases": copy.deepcopy(self.state["releases"])}

    def prepare(self, manifest, workers):
        active = self.release(self.state["active"])
        require(self.routed_release() == active["id"], "Live routing is uncertain; inspect status first")
        require(self.probe(active["port"]) == active["policy"], "Active policy changed")
        self.check_worker_ports(workers)
        release_id = manifest["id"]
        require(release_id not in self.state["releases"], "Release ID is already registered")
        slot = "green" if active["slot"] == "blue" else "blue"
        port = SLOTS[slot]["api"]
        require(port_free(port), "Candidate API port is occupied")
        for prior in self.state["releases"].values():
            require(prior["slot"] != slot or prior["status"] == "stopped", "Candidate API slot is retained; cleanup first")
        capacity(384 * 1024**2, 256 * 1024**2)
        require(shutil.disk_usage(self.root).free >= 512 * 1024**2, "Insufficient API disk space")
        base = read_json(self.directory(active["id"]) / "template.json")
        template = self.snapshot(release_id, base)
        spec = manifest.get("api", {})
        require(set(spec) <= {"legacy_clone", "image", "settings_file", "environment_file"}, "Unknown API manifest fields")
        legacy = bool(spec.get("legacy_clone"))
        if legacy:
            require(set(spec) <= {"legacy_clone"}, "Legacy adoption cannot change the image or configuration")
            require(active.get("legacy_inputs_hash"), "Legacy cloning requires an adopted legacy lineage; use normal preflight for new images")
        else:
            image = spec.get("image", active["image"])
            require(IMAGE_REF.fullmatch(image), "Use an immutable API image ID or digest")
            template["Image"] = run(["docker", "image", "inspect", "--format", "{{.Id}}", image]).stdout.decode().strip()
            if spec.get("settings_file"):
                mount = next((m for m in template["Mounts"] if m["Destination"] == "/app/appsettings.Production.json"), None)
                require(mount is not None, "No production settings mount to replace")
                shutil.copy2(spec["settings_file"], mount["Source"])
            if spec.get("environment_file"):
                for key, value in environment(Path(spec["environment_file"]).read_text()).items():
                    self.set_option(template, key, value)
        self.set_option(template, "ASPNETCORE_URLS", f"http://127.0.0.1:{port}")
        for kind, worker in workers.items():
            key = f"Providers__{WORKER_OPTIONS[kind]}__BaseUrl"
            before = self.effective_option(template, key)
            require(before and re.fullmatch(r"https://[a-zA-Z0-9.-]+:\d+/?", before), "Worker URL must use verified HTTPS host identity")
            self.set_option(template, key, re.sub(r":\d+(/?)$", f":{worker['port']}\\1", before))
        release = {"id": release_id, "slot": slot, "port": port, "container": f"snowshot-api-{release_id}",
                   "image": template["Image"], "policy": active["policy"], "workers": workers,
                   "status": "prepared", "legacy": legacy, "created_at": time.time(), "drain_generations": []}
        write_json(self.directory(release_id) / "template.json", template)
        self.make_compose(release, template)
        release["configuration_hash"] = self.configuration_identity(template)
        if legacy:
            require(self.configuration_identity(template, legacy=True) == active["legacy_inputs_hash"], "Legacy inputs differ from the frozen live release")
            release["legacy_inputs_hash"] = active["legacy_inputs_hash"]
        else:
            document = read_json(self.directory(release_id) / "compose.json")
            document["services"]["api"]["network_mode"] = "none"
            write_json(self.directory(release_id) / "describe.json", document)
            output = run(["docker", "compose", "-p", f"snowshot-describe-{release_id}", "-f",
                          str(self.directory(release_id) / "describe.json"), "run", "--rm", "--no-deps", "api", "--describe-deployment"]).stdout
            description = json.loads(output)
            require({"revision": description["policy_revision"], "fingerprint": description["policy_fingerprint"]} == active["policy"],
                    "Policy mismatch: use the separate maintenance procedure")
            require(description["drain_timeout_seconds"] <= 360 and description["shutdown_timeout_seconds"] >= 360,
                    "Candidate timing exceeds the supported drain budget")
        self.state["releases"][release_id] = release
        self.save("candidate configuration verified")
        return copy.deepcopy(release)

    def check_worker_ports(self, workers):
        referenced = {worker["id"] for release in self.state["releases"].values()
                      if release["status"] in ("running", "starting") for worker in release["workers"].values()}
        for worker in workers.values():
            require(worker["id"] in referenced or port_free(worker["port"]),
                    "Candidate reverse-forward port is occupied on the API host")
        return {"available": True}

    def start(self, release_id):
        release = self.release(release_id)
        template = read_json(self.directory(release_id) / "template.json")
        require(self.configuration_identity(template) == release["configuration_hash"], "Frozen configuration was modified")
        active = self.release(self.state["active"])
        require(self.probe(active["port"]) == release["policy"], "Policy changed before candidate startup")
        require(port_free(release["port"]), "API listener is occupied; inspect status before retrying")
        capacity(384 * 1024**2, 256 * 1024**2)
        release["status"] = "starting"
        self.save("candidate API starting")
        if release["legacy"] and release["container"] == self.settings["legacy_container"]:
            original = json.loads(run(["docker", "inspect", release["container"]]).stdout)[0]
            require(self.configuration_identity(original) == release["configuration_hash"],
                    "Legacy container inputs drifted from the adopted snapshot")
            run(["docker", "start", release["container"]])
        else:
            self.compose(release, "up", "-d", "--no-deps", "api")
        deadline = time.monotonic() + 180
        successes = 0
        while time.monotonic() < deadline:
            capacity(0, 256 * 1024**2)
            status, restarts = run(["docker", "inspect", "--format", "{{.State.Status}} {{.RestartCount}}",
                                    release["container"]]).stdout.decode().split()
            require(status not in ("dead", "exited", "restarting") and restarts == "0",
                    "Candidate API exited or restarted during warmup; inspect its logs")
            try:
                require(self.probe(release["port"]) == release["policy"], "Candidate policy changed")
                successes += 1
                if successes == 3:
                    release["status"] = "running"
                    self.save("candidate API ready")
                    return {"ready": True}
            except (OSError, ValueError, DeploymentError):
                successes = 0
            time.sleep(2)
        raise DeploymentError("Candidate API did not become ready; live routing is unchanged")

    def switch(self, release_id):
        release = self.release(release_id)
        active = self.release(self.state["active"])
        require(release_id != active["id"], "Release is already active")
        require(release["status"] == "running", "Candidate is not running")
        require(self.probe(release["port"]) == active["policy"] == release["policy"], "Policy mismatch at switch")
        require(self.routed_release() == active["id"], "Unexpected live routing")
        include = Path(self.settings["nginx_include"])
        original = include.read_text()
        identities = self.nginx_workers()
        require(identities, "Cannot establish nginx drain identities")
        self.state["pending"] = {"operation": "switch", "from": active["id"], "to": release_id,
                                  "workers": identities, "at": time.time()}
        active["drain_generations"].extend(identities)
        active["drain_started_at"] = time.time()
        self.save("switch prepared")
        try:
            atomic(include, self.upstream(release), 0o644)
            self.nginx("-t")
            self.nginx("-s", "reload")
            self.wait_route(release_id)
        except Exception:
            # Whether reload failed before or after signalling, restore verified routing.
            atomic(include, original, 0o644)
            self.nginx("-t")
            self.nginx("-s", "reload")
            self.wait_route(active["id"])
            self.state["pending"] = {"operation": "switch_reverted", "release": release_id}
            self.save("switch reverted")
            raise
        self.state.update(active=release_id, previous=active["id"], pending=None)
        self.save("traffic switched")
        return {"active": release_id, "previous": active["id"]}

    def check(self, release_id):
        release = self.release(release_id)
        require(self.routed_release() == release_id, "nginx readiness or release identity failed")
        require(self.probe(release["port"]) == release["policy"], "Active readiness or policy identity failed")
        capacity(0, 256 * 1024**2)
        return {"ready": True}

    def cleanup(self):
        require(self.routed_release() == self.state["active"], "Cannot cleanup with uncertain routing")
        pending = []
        for release in self.state["releases"].values():
            if release["id"] == self.state["active"] or release["status"] == "stopped":
                continue
            remaining = [identity for identity in release["drain_generations"] if alive(identity)]
            if remaining:
                pending.append({"release": release["id"], "remaining_workers": len(remaining),
                                "deadline": release.get("drain_started_at", time.time()) + 360})
                continue
            # Unswitched candidates have no nginx references and may also be retired.
            if release["container"] == self.settings["legacy_container"]:
                run(["docker", "stop", "--time", "390", release["container"]], timeout=400)
            else:
                self.compose(release, "stop", "api", timeout=400)
            release["status"] = "stopped"
        running_workers = {worker["id"] for release in self.state["releases"].values()
                           if release["status"] != "stopped" for worker in release["workers"].values()}
        retired = {worker["id"]: worker for release in self.state["releases"].values()
                   for worker in release["workers"].values() if worker["id"] not in running_workers}
        self.save("cleanup checked")
        return {"pending": pending, "retirable_workers": list(retired.values())}

    def migration(self, release_id, image, reviewed, secrets_directory):
        require(reviewed is True, "Migrations require explicit backward-compatibility review")
        release = self.release(release_id)
        require(release["status"] == "prepared", "Run migration before candidate startup")
        require(IMAGE_REF.fullmatch(image), "Use an immutable migrator image")
        require(Path(secrets_directory).is_dir(), "Migrator secrets directory is missing")
        run(["docker", "run", "--rm", "--network", "host", "-e", "DOTNET_ENVIRONMENT=Production",
             "-e", "SNOWSHOT_SECRETS_DIRECTORY=/run/secrets/snowshot", "--mount",
             f"type=bind,source={secrets_directory},target=/run/secrets/snowshot,readonly", image], timeout=600)
        self.save("reviewed additive migrations completed")
        return {"applied": True}

    @contextlib.contextmanager
    def probe_credentials(self, release_id, kind):
        template = read_json(self.directory(release_id) / "template.json")
        prefix = f"Providers__{WORKER_OPTIONS[kind]}__"
        pfx = self.host_path(template, self.effective_option(template, prefix + "ClientCertificatePath"))
        ca = self.host_path(template, self.effective_option(template, prefix + "ServerCaCertificatePath"))
        password = self.effective_option(template, prefix + "ClientCertificatePassword") or ""
        with tempfile.TemporaryDirectory(prefix="snowshot-probe-") as temporary:
            directory = Path(temporary)
            atomic(directory / "password", str(password) + "\n")
            for name, options in (("cert.pem", ["-clcerts", "-nokeys"]), ("key.pem", ["-nocerts", "-noenc"])):
                run(["openssl", "pkcs12", "-in", str(pfx), "-passin", f"file:{directory / 'password'}",
                     *options, "-out", str(directory / name)])
            yield {"certificate": (directory / "cert.pem").read_text(), "key": (directory / "key.pem").read_text(),
                   "ca": ca.read_text()}

    def worker_credentials(self, release_id, kind):
        # This response is consumed in memory by the CLI and is never printed.
        with self.probe_credentials(release_id, kind) as credentials:
            return credentials

    def worker_probe(self, release_id, kind, worker, fixture, expected):
        with self.probe_credentials(release_id, kind) as credentials:
            return probe_worker(kind, worker["port"], credentials, fixture, expected)


def probe_worker(kind, port, credentials, fixture, expected):
    host = f"snowshot-{'table' if kind == 'table' else 'latex'}-rec"
    with tempfile.TemporaryDirectory(prefix="snowshot-mtls-") as temporary:
        directory = Path(temporary)
        for name in ("certificate", "key", "ca"):
            atomic(directory / name, credentials[name])
        common = ["curl", "--silent", "--show-error", "--connect-timeout", "5", "--max-time", "70",
                  "--cacert", str(directory / "ca"), "--resolve", f"{host}:{port}:127.0.0.1"]
        authenticated = common + ["--cert", str(directory / "certificate"), "--key", str(directory / "key")]
        url = f"https://{host}:{port}"
        response = run([*authenticated, "--fail", url + "/health/ready"]).stdout
        require(json.loads(response)["status"] == "ready", "Worker is not ready")
        for version in ("1.2", "1.3"):
            protocol = ["--tlsv" + version, "--tls-max", version]
            response = run([*authenticated, *protocol, "--fail", url + "/health/ready"]).stdout
            require(json.loads(response)["status"] == "ready", "Authenticated TLS control failed")
            negative = run([*common, *protocol, "--write-out", "%{http_code}", url + "/health/ready"], check=False)
            # Uvicorn/OpenSSL can close TLS 1.3 without an HTTP response after a
            # missing client certificate; curl reports that EOF as 52, not 56.
            rejected = (35, 56) if version == "1.2" else (35, 52, 56)
            require(negative.returncode in rejected and negative.stdout.strip() == b"000",
                    f"Worker did not reject the unauthenticated TLS {version} client (curl={negative.returncode})")
            response = run([*authenticated, *protocol, "--fail", url + "/health/ready"]).stdout
            require(json.loads(response)["status"] == "ready", "Authenticated control failed after negative probe")
        atomic(directory / "fixture.webp", base64.b64decode(fixture, validate=True))
        response = run([*authenticated, "--fail", "-H", "Content-Type: image/webp", "-H", "X-Request-ID: deployment-probe",
                        "--data-binary", "@" + str(directory / "fixture.webp"), url + f"/v2/{kind}/extract"]).stdout
        value = json.loads(response)["html" if kind == "table" else "latex"]
        require(expected in value, "Recognition fixture did not match its reviewed expectation")
        return {"ready": True, "recognition": True, "mtls_enforced": True}


class WorkerAgent:
    def __init__(self, settings):
        self.settings = settings
        self.root = Path(settings["state_root"])

    def inventory(self):
        workers = {}
        for kind in WORKER_OPTIONS:
            unit = f"snowshot-cpu-worker@{kind}.service"
            info = properties(unit)
            require(info["ActiveState"] == "active", "Legacy worker is not active")
            runner = re.search(r"argv\[\]=(.*?)\s*;", info["ExecStart"])
            require(runner, "Cannot identify legacy worker runner")
            arguments = shlex.split(runner.group(1))
            files = re.findall(r"(\S+) \(ignore_errors=(?:yes|no)\)", info["EnvironmentFiles"])
            values = dict(item.split("=", 1) for item in shlex.split(info["Environment"]))
            for path in files:
                values.update(environment(Path(path).read_text()))
            tunnel_file = Path(f"/etc/snowshot/{kind}-tunnel.env")
            record = {"id": f"legacy-{kind}", "kind": kind, "port": SLOTS["blue"][kind],
                      "unit": unit, "tunnel_unit": f"snowshot-cpu-tunnel@{kind}.service",
                      "arguments": arguments, "environment_files": files, "affinity": info["CPUAffinity"],
                      "memory": int(info["MemoryCurrent"]), "legacy": True,
                      "tunnel_file": str(tunnel_file), "source_hash": self.source_hash(kind, arguments, values)}
            private = {"record": record, "environment": values, "tunnel": environment(tunnel_file.read_text())}
            write_json(self.root / "legacy" / f"{kind}.json", private)
            workers[kind] = record
        return workers

    def source_hash(self, kind, arguments, values):
        executable = Path(arguments[0]).resolve()
        allowed = Path(self.settings["release_root"]).resolve()
        require(executable.is_relative_to(allowed), "Worker runner is outside the permitted release root")
        prefix = "TABLE_REC" if kind == "table" else "LATEX_REC"
        model = Path(values[prefix + "_MODEL_DIR"]).resolve()
        require(model.is_relative_to(allowed), "Worker models are outside the permitted release root")
        service = "TableStructureRec" if kind == "table" else "FormulaRecognition"
        source = executable.parent / "services" / service
        require(source.is_dir(), "Worker runner must belong to a complete staged release")
        paths = [executable, source, model]
        runtime = executable.parent / f"{kind}-venv"
        if runtime.is_dir():
            paths.append(runtime)
        return hashlib.sha256("\n".join(tree_hash(path) for path in paths).encode()).hexdigest()

    def private(self, record):
        require(record.get("kind") in WORKER_OPTIONS, "Unknown worker kind")
        if record.get("legacy"):
            require(record["id"] == "legacy-" + record["kind"], "Invalid legacy worker identity")
            path = self.root / "legacy" / f"{record['kind']}.json"
        else:
            require(WORKER_ID.fullmatch(record["id"]), "Invalid worker slot")
            path = self.root / "slots" / record["id"] / "private.json"
        result = read_json(path)
        require(result["record"] == record, "Worker identity changed")
        return result

    def prepare(self, kind, base, spec, release_id):
        require(kind in WORKER_OPTIONS, "Unknown worker kind")
        require(RELEASE_ID.fullmatch(release_id), "Invalid release ID")
        slot = "green" if base["port"] == SLOTS["blue"][kind] else "blue"
        worker_id = f"{kind}-{slot}-{release_id}"
        port = SLOTS[slot][kind]
        require(port_free(port), "Candidate worker port is occupied")
        capacity(base["memory"], 1024**3)
        require(shutil.disk_usage(self.root).free > 512 * 1024**2, "Insufficient worker disk space")
        private = self.private(base)
        values = dict(private["environment"])
        arguments = private["record"]["arguments"]
        require(set(spec) <= {"clone", "runner", "environment_files", "affinity"}, "Unknown worker manifest fields")
        if not spec.get("clone"):
            require(spec.get("runner") and spec.get("environment_files"), "New worker needs a staged runner and environment files")
            arguments = [spec["runner"]]
            for path in spec["environment_files"]:
                resolved = Path(path).resolve()
                require(resolved.is_relative_to(Path(self.settings["release_root"]).resolve()) or
                        resolved.is_relative_to(Path("/etc/snowshot")), "Worker environment file is outside permitted roots")
                values.update(environment(resolved.read_text()))
        else:
            require(set(spec) == {"clone"}, "Cloning cannot also change worker settings")
        prefix = "TABLE_REC" if kind == "table" else "LATEX_REC"
        values.update({prefix + "_HOST": "127.0.0.1", prefix + "_PORT": str(port)})
        source_hash = self.source_hash(kind, arguments, values)
        record = {"id": worker_id, "kind": kind, "port": port,
                  "unit": f"snowshot-deploy-worker@{worker_id}.service",
                  "tunnel_unit": f"snowshot-deploy-tunnel@{worker_id}.service", "arguments": arguments,
                  "affinity": spec.get("affinity", base["affinity"]), "memory": base["memory"],
                  "legacy": False, "source_hash": source_hash}
        require(re.fullmatch(r"[0-9 ,\-]+", record["affinity"]), "Invalid CPU affinity")
        directory = self.root / "slots" / worker_id
        require(not directory.exists(), "Worker release is already prepared")
        directory.mkdir(parents=True, mode=0o750)
        gid = self.settings["gid"]
        os.chown(directory, 0, gid)
        os.chmod(directory, 0o750)
        # JSON avoids systemd/shell interpolation of environment values and runner arguments.
        launch = {"arguments": arguments, "environment": values}
        atomic(directory / "launch.json", json.dumps(launch), 0o640)
        os.chown(directory / "launch.json", 0, gid)
        tunnel = dict(private["tunnel"])
        tunnel.update(LOCAL_PORT=str(port), REMOTE_PORT=str(port))
        key = f"/etc/snowshot/ssh/{kind}-ed25519"
        tunnel_arguments = ["/usr/bin/ssh", "-N", "-T", "-i", key, "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes",
                            "-o", "StrictHostKeyChecking=yes", "-o", "UserKnownHostsFile=/etc/snowshot/ssh/known_hosts",
                            "-o", "ExitOnForwardFailure=yes", "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=30",
                            "-o", "ServerAliveCountMax=3", "-R", f"127.0.0.1:{port}:127.0.0.1:{port}",
                            f"{tunnel['SSH_USER']}@{tunnel['SSH_HOST']}"]
        atomic(directory / "tunnel.json", json.dumps({"arguments": tunnel_arguments, "environment": {}}), 0o640)
        os.chown(directory / "tunnel.json", 0, gid)
        dropin = Path(f"/etc/systemd/system/{record['unit']}.d")
        dropin.mkdir(parents=True, exist_ok=True)
        atomic(dropin / "affinity.conf", f"[Service]\nCPUAffinity={record['affinity']}\n", 0o644)
        write_json(directory / "private.json", {"record": record, "environment": values, "tunnel": tunnel})
        run(["systemctl", "daemon-reload"])
        run(["systemd-analyze", "verify", record["unit"], record["tunnel_unit"]])
        return record

    def start(self, record):
        private = self.private(record)
        require(self.source_hash(record["kind"], record["arguments"], private["environment"]) == record["source_hash"],
                "Staged worker source or model files changed")
        if properties(record["unit"])["ActiveState"] == "active":
            run(["systemctl", "enable", "--now", record["unit"], record["tunnel_unit"]])
            return {"started": True}
        require(port_free(record["port"]), "Worker listener is occupied")
        capacity(record["memory"], 1024**3)
        run(["systemctl", "enable", "--now", record["unit"]])
        run(["systemctl", "enable", "--now", record["tunnel_unit"]])
        return {"started": True}

    def probe(self, record, credentials, fixture, expected):
        self.private(record)
        initial_restarts = int(properties(record["unit"]).get("NRestarts", "0"))
        deadline = time.monotonic() + 180
        while time.monotonic() < deadline:
            capacity(0, 1024**3)
            require(int(properties(record["unit"]).get("NRestarts", "0")) == initial_restarts,
                    "Candidate worker restarted during warmup; inspect its journal")
            try:
                require(properties(record["tunnel_unit"])["ActiveState"] == "active", "Candidate tunnel is not active")
                return probe_worker(record["kind"], record["port"], credentials, fixture, expected)
            except (OSError, ValueError, DeploymentError):
                time.sleep(2)
        raise DeploymentError("Candidate worker did not pass authenticated recognition checks")

    def stop(self, record):
        self.private(record)
        before = properties(record["unit"])
        # The controller only sends workers no longer referenced by a running API.
        run(["systemctl", "disable", "--now", record["tunnel_unit"]], timeout=100)
        run(["systemctl", "disable", "--now", record["unit"]], timeout=70)
        state = run(["systemctl", "show", record["unit"], "-p", "Result", "--value"]).stdout.decode().strip()
        require(state == "success" or int(before["MainPID"]) == 0, "Worker stop was not graceful; inspect its journal")
        return {"stopped": True, "clean": state == "success"}


def serve(agent, lock_file):
    import fcntl
    Path(lock_file).parent.mkdir(parents=True, exist_ok=True)
    lock = open(lock_file, "a")
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        lock.close()
        print(json.dumps({"ok": False, "error": "Another deployment holds the server lock"}), flush=True)
        return 1
    # Construction can race a controller that is just releasing the lock. Read
    # persisted state again only after this session owns the deployment lock.
    if isinstance(agent, ApiAgent) and agent.state_file.exists():
        agent.state = read_json(agent.state_file)
    print(json.dumps({"ok": True, "result": "locked"}), flush=True)
    allowed = ({"adopt", "public_status", "prepare", "start", "switch", "check", "cleanup", "migration",
                "worker_credentials", "worker_probe", "check_worker_ports"} if isinstance(agent, ApiAgent)
               else {"inventory", "prepare", "start", "probe", "stop"})
    for line in sys.stdin:
        try:
            request = json.loads(line)
            require(request["operation"] in allowed, "Unsupported deployment operation")
            result = getattr(agent, request["operation"])(**request.get("arguments", {}))
            response = {"ok": True, "result": result}
        except Exception as exception:
            # Unknown exception strings may contain request data or secrets.
            message = str(exception) if isinstance(exception, DeploymentError) else type(exception).__name__
            response = {"ok": False, "error": message}
        print(json.dumps(response), flush=True)
    lock.close()
    return 0


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser()
    parser.add_argument("--api-root")
    arguments = parser.parse_args()
    if arguments.api_root:
        root = Path(arguments.api_root)
        settings = read_json(root / "target.json")
        return serve(ApiAgent(root, settings), root / "deployment.lock")
    settings = read_json("/etc/snowshot/deployment.json")
    return serve(WorkerAgent(settings), "/run/lock/snowshot-deployment.lock")


if __name__ == "__main__":
    sys.exit(main())
