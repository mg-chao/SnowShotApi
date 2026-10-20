"""One-time privileged installer; invoked over SSH with a private stdin payload."""
import base64
import json
import os
from pathlib import Path
import pwd
import re
import subprocess
import tempfile
import time


def install(path, content, mode=0o600):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content.encode() if isinstance(content, str) else content)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


os.umask(0o077)
payload = json.load(__import__("sys").stdin)
target = payload["target"]
assert os.geteuid() == 0
if payload["role"] == "api":
    root = Path(target["api_root"]).resolve()
    assert root.is_absolute() and root.is_relative_to("/opt/snowshot-v2")
    root.mkdir(parents=True, exist_ok=True, mode=0o700)
    install(root / "agent.py", base64.b64decode(payload["files"]["agent.py"]))
    install(root / "target.json", json.dumps({key: target[key] for key in
            ("public_host", "nginx_include", "nginx_files", "legacy_container")}))
    keys_path = Path("/root/.ssh/authorized_keys")
    keys = keys_path.read_text()
    updated = []
    for line in keys.splitlines():
        for kind, old, new in (("table", 18080, 28080), ("latex", 18081, 28081)):
            if line.endswith(f"snowshot-ubuntu-{kind}-tunnel"):
                assert f'permitlisten="127.0.0.1:{old}"' in line
                addition = f'permitlisten="127.0.0.1:{new}"'
                if addition not in line:
                    line = line.replace(f'permitlisten="127.0.0.1:{old}"', f'permitlisten="127.0.0.1:{old}",{addition}')
        updated.append(line)
    assert sum(line.endswith(("snowshot-ubuntu-table-tunnel", "snowshot-ubuntu-latex-tunnel")) for line in updated) == 2
    if "\n".join(updated) + "\n" != keys:
        install(root / f"authorized-keys-before-{int(time.time())}", keys)
        install(keys_path, "\n".join(updated) + "\n")
else:
    user = target["worker_user"]
    assert re.fullmatch(r"[a-z_][a-z0-9_-]*", user)
    account = pwd.getpwnam(user)
    root = Path("/var/lib/snowshot-deploy")
    root.mkdir(mode=0o750, exist_ok=True)
    os.chown(root, 0, account.pw_gid)
    os.chmod(root, 0o750)
    slots = root / "slots"
    slots.mkdir(mode=0o750, exist_ok=True)
    os.chown(slots, 0, account.pw_gid)
    os.chmod(slots, 0o750)
    executable_root = Path("/usr/local/libexec/snowshot-deploy")
    executable_root.mkdir(mode=0o755, exist_ok=True)
    os.chmod(executable_root, 0o755)
    for name in ("agent.py", "launch.py"):
        install(executable_root / name, base64.b64decode(payload["files"][name]), 0o755)
    install("/usr/local/libexec/snowshot-deploy-worker",
            "#!/bin/sh\nexec /usr/bin/python3 /usr/local/libexec/snowshot-deploy/agent.py\n", 0o755)
    install("/etc/snowshot/deployment.json", json.dumps({"state_root": str(root),
            "release_root": target["worker_release_root"], "user": user, "uid": account.pw_uid, "gid": account.pw_gid}))
    common = (f"User={user}\nGroup={account.pw_gid}\nRestart=on-failure\nRestartSec=3\n"
              "NoNewPrivileges=true\nPrivateTmp=true\nUMask=0077\nMemoryAccounting=true\n")
    worker = ("[Unit]\nDescription=SnowShot staged recognition %i\nAfter=network-online.target snowshot-cpu-power.service\n"
              "Wants=network-online.target\n[Service]\nType=simple\n" + common +
              "ExecStart=/usr/bin/python3 /usr/local/libexec/snowshot-deploy/launch.py worker %i\nTimeoutStopSec=60\n"
              "[Install]\nWantedBy=multi-user.target\n")
    tunnel = ("[Unit]\nDescription=SnowShot staged tunnel %i\nAfter=network-online.target snowshot-deploy-worker@%i.service\n"
              "Requires=snowshot-deploy-worker@%i.service\nStartLimitIntervalSec=0\n[Service]\nType=simple\n" + common +
              "ExecStart=/usr/bin/python3 /usr/local/libexec/snowshot-deploy/launch.py tunnel %i\nTimeoutStopSec=60\n"
              "[Install]\nWantedBy=multi-user.target\n")
    install("/etc/systemd/system/snowshot-deploy-worker@.service", worker, 0o644)
    install("/etc/systemd/system/snowshot-deploy-tunnel@.service", tunnel, 0o644)
    # Empty argument specification forbids using this sudo entry to pass a different role/root.
    sudoers = f'{user} ALL=(root) NOPASSWD: /usr/local/libexec/snowshot-deploy-worker ""\n'
    install("/etc/sudoers.d/snowshot-deployment", sudoers, 0o440)
    subprocess.run(["visudo", "-cf", "/etc/sudoers.d/snowshot-deployment"], check=True, stdout=subprocess.DEVNULL)
    subprocess.run(["systemctl", "daemon-reload"], check=True)
print("installed")
