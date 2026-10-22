"""Configure server-side SSH liveness checks for the root-owned worker tunnels."""
from __future__ import annotations

import os
from pathlib import Path
import subprocess
import tempfile


CONFIGURATION = ("# Reclaim recognition forwarding ports after a lost worker connection.\n"
                 "Match User root\n"
                 "    ClientAliveInterval 30\n"
                 "    ClientAliveCountMax 3\n"
                 "Match all\n")


def atomic_write(path, content, mode):
    descriptor, temporary = tempfile.mkstemp(dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def configure(path=Path("/etc/ssh/sshd_config.d/60-snowshot-tunnel-recovery.conf"), *, run=subprocess.run):
    path = Path(path)
    original = path.read_bytes() if path.exists() else None
    original_mode = path.stat().st_mode & 0o777 if original is not None else 0o644
    desired = CONFIGURATION.encode()
    changed = original != desired
    reload_attempted = False
    path.parent.mkdir(parents=True, exist_ok=True)
    try:
        if changed:
            atomic_write(path, desired, 0o644)
        run(["/usr/sbin/sshd", "-t"], check=True, capture_output=True, text=True)
        result = run(["/usr/sbin/sshd", "-T", "-C", "user=root,host=localhost,addr=127.0.0.1"],
                     check=True, capture_output=True, text=True)
        effective = dict(line.split(None, 1) for line in result.stdout.splitlines() if line.strip())
        if effective.get("clientaliveinterval") != "30" or effective.get("clientalivecountmax") != "3":
            raise RuntimeError("SSH configuration did not enable the recognition tunnel liveness checks")
        if changed:
            reload_attempted = True
            run(["systemctl", "reload", "ssh"], check=True, capture_output=True, text=True)
    except Exception:
        if changed:
            if original is None:
                path.unlink(missing_ok=True)
            else:
                atomic_write(path, original, original_mode)
            if reload_attempted:
                run(["systemctl", "reload", "ssh"], check=False, capture_output=True, text=True)
        raise
    return {"changed": changed, "client_alive_interval": 30, "client_alive_count_max": 3}


if __name__ == "__main__":
    import json
    print(json.dumps(configure()))
