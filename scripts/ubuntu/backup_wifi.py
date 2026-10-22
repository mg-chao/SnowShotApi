#!/usr/bin/env python3
"""Warm Wi-Fi standby and IPv4 failover for a networkd recognition host."""
from __future__ import annotations

import argparse
import getpass
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time

CONFIG = Path("/etc/snowshot/backup-network.json")
STATE = Path("/run/snowshot-backup-network.json")
PREFERRED_METRIC = 50


def run(arguments, **kwargs):
    return subprocess.run(arguments, check=True, capture_output=True, text=True, timeout=10, **kwargs)


def atomic(path, content, mode=0o600):
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


def cloud_reachable(interface, config):
    # Binding the probe to each device detects upstream failure with carrier still up.
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as connection:
            connection.settimeout(2)
            connection.setsockopt(socket.SOL_SOCKET, socket.SO_BINDTODEVICE, interface.encode() + b"\0")
            connection.connect((config["probe_host"], config["probe_port"]))
            return connection.recv(256).startswith(b"SSH-")
    except OSError:
        return False


def routes():
    return json.loads(run(["ip", "-j", "-4", "route", "show", "default"]).stdout)


def addresses(interface):
    records = json.loads(run(["ip", "-j", "-4", "address", "show", "dev", interface]).stdout)
    return [address["local"] for record in records for address in record["addr_info"]
            if address["family"] == "inet" and address.get("scope") == "global"]


def radio_on():
    for device in Path("/sys/class/rfkill").glob("rfkill*"):
        if (device / "type").read_text().strip() != "wlan":
            continue
        if (device / "hard").read_text().strip() == "1":
            raise RuntimeError("Wi-Fi is disabled by the hardware switch")
        if (device / "soft").read_text().strip() == "1":
            (device / "soft").write_text("0\n")


def ensure_wifi(config, state, *, force=False):
    radio_on()
    if not force and addresses(config["wifi"]):
        return
    now = time.monotonic()
    if not force and now - state.get("last_connect_attempt", -30) < 30:
        return
    run(["ip", "link", "set", "dev", config["wifi"], "up"])
    run(["systemctl", "start", f"netplan-wpa-{config['wifi']}.service"])
    run(["networkctl", "up", config["wifi"]])
    run(["wpa_cli", "-i", config["wifi"], "reconnect"])
    state["last_connect_attempt"] = now


def preferred(route, config):
    return (route.get("dev") == config["wifi"] and route.get("metric") == PREFERRED_METRIC
            and route.get("protocol") == "static")


def choose(wired_ok, wifi_ok, state, active):
    state["wired_failures"] = 0 if wired_ok else state.get("wired_failures", 0) + 1
    state["wired_successes"] = state.get("wired_successes", 0) + 1 if wired_ok else 0
    if active and not wifi_ok:
        return "wired"
    if active and wired_ok and state["wired_successes"] >= 3:
        return "wired"
    if not wired_ok and wifi_ok and state["wired_failures"] >= 2:
        return "wifi"
    return "wifi" if active else "wired"


def apply_route(choice, config, current):
    active = [route for route in current if preferred(route, config)]
    if choice == "wired":
        for route in active:
            run(["ip", "-4", "route", "del", "default", "via", route["gateway"],
                 "dev", config["wifi"], "proto", "static", "metric", str(PREFERRED_METRIC)])
        return
    gateways = [route for route in current if route.get("dev") == config["wifi"]
                and route.get("gateway") and not preferred(route, config)]
    if not gateways:
        raise RuntimeError("Wi-Fi has no DHCP default gateway")
    gateway = min(gateways, key=lambda route: route.get("metric", 0))["gateway"]
    if any(route.get("metric") == PREFERRED_METRIC and not preferred(route, config) for route in current):
        raise RuntimeError("Failover route metric is already used by another route")
    if len(active) == 1 and active[0].get("gateway") == gateway:
        return
    for route in active:
        run(["ip", "-4", "route", "del", "default", "via", route["gateway"],
             "dev", config["wifi"], "proto", "static", "metric", str(PREFERRED_METRIC)])
    run(["ip", "-4", "route", "add", "default", "via", gateway, "dev", config["wifi"],
         "proto", "static", "metric", str(PREFERRED_METRIC)])


def check(config, state):
    ensure_wifi(config, state)
    wired_ok = cloud_reachable(config["wired"], config)
    wifi_ok = cloud_reachable(config["wifi"], config)
    current = routes()
    active = any(preferred(route, config) for route in current)
    choice = choose(wired_ok, wifi_ok, state, active)
    apply_route(choice, config, current)
    state.update(path=choice, wired_cloud_reachable=wired_ok, wifi_cloud_reachable=wifi_ok)
    return state


def install(ssid, password, wired, wifi, probe_host):
    import yaml
    if not (1 <= len(ssid.encode()) <= 32 and 8 <= len(password) <= 63):
        raise ValueError("Invalid SSID or WPA passphrase length")
    for interface in (wired, wifi):
        if not (Path("/sys/class/net") / interface).exists():
            raise ValueError("Configured network device does not exist")
    config = {"wired": wired, "wifi": wifi, "ssid": ssid, "probe_host": probe_host, "probe_port": 22}
    netplan = Path("/etc/netplan/90-snowshot-backup-wifi.yaml")
    settings = {"network": {"version": 2, "renderer": "networkd", "wifis": {wifi: {
        "dhcp4": True, "dhcp6": False, "optional": True,
        "dhcp4-overrides": {"route-metric": 600}, "access-points": {ssid: {"password": password}}}}}}
    original = netplan.read_bytes() if netplan.exists() else None
    try:
        atomic(netplan, yaml.safe_dump(settings, sort_keys=False))
        run(["netplan", "generate"])
    except Exception:
        if original is None:
            netplan.unlink(missing_ok=True)
        else:
            atomic(netplan, original)
        run(["netplan", "generate"])
        raise
    atomic(CONFIG, json.dumps(config))
    atomic(Path("/usr/local/sbin/snowshot-backup-wifi"), Path(__file__).read_bytes(), 0o755)
    atomic(Path("/usr/local/sbin/snowshot-connect-backup-wifi"),
           '#!/bin/sh\nexec /usr/local/sbin/snowshot-backup-wifi connect "$@"\n', 0o755)
    atomic(Path("/etc/systemd/system/snowshot-network-failover.service"),
           "[Unit]\nDescription=SnowShot warm Wi-Fi standby and network failover\n"
           "After=systemd-networkd.service\nWants=systemd-networkd.service\n"
           "[Service]\nType=oneshot\nExecStart=/usr/local/sbin/snowshot-backup-wifi check\n"
           "TimeoutStartSec=25\nUMask=0077\n", 0o644)
    atomic(Path("/etc/systemd/system/snowshot-network-failover.timer"),
           "[Unit]\nDescription=Check SnowShot uplinks every ten seconds\n[Timer]\n"
           "OnBootSec=10\nOnUnitActiveSec=10\nAccuracySec=1\n"
           "[Install]\nWantedBy=timers.target\n", 0o644)
    run(["systemctl", "daemon-reload"])
    radio_on()
    run(["networkctl", "reload"])
    run(["networkctl", "reconfigure", wifi])
    run(["systemctl", "start", f"netplan-wpa-{wifi}.service"])
    run(["systemctl", "enable", "--now", "snowshot-network-failover.timer"])
    return {"installed": True, "ssid": ssid, "wifi": wifi, "wired": wired}


def main():
    import fcntl
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("install", "connect", "check", "status"))
    parser.add_argument("--credentials-stdin", action="store_true")
    parser.add_argument("--ssid")
    parser.add_argument("--wired", default="enx207bd235d107")
    parser.add_argument("--wifi", default="wlp1s0")
    parser.add_argument("--probe-host", default="120.79.232.67")
    options = parser.parse_args()
    if os.geteuid() != 0:
        parser.error("Run with sudo")
    if options.action == "install":
        credentials = json.load(sys.stdin) if options.credentials_stdin else {
            "ssid": options.ssid or input("Wi-Fi SSID: "), "password": getpass.getpass("Wi-Fi password: ")}
        print(json.dumps(install(credentials["ssid"], credentials["password"],
                                 options.wired, options.wifi, options.probe_host)))
        return
    config = json.loads(CONFIG.read_text())
    with open("/run/snowshot-backup-network.lock", "w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        state = json.loads(STATE.read_text()) if STATE.exists() else {}
        if options.action == "connect":
            ensure_wifi(config, state, force=True)
            deadline = time.monotonic() + 35
            while time.monotonic() < deadline:
                if addresses(config["wifi"]) and cloud_reachable(config["wifi"], config):
                    break
                time.sleep(1)
            else:
                raise RuntimeError("Wi-Fi did not obtain a working cloud connection")
            state.update(wifi_cloud_reachable=True, wifi_addresses=addresses(config["wifi"]))
        elif options.action == "check":
            check(config, state)
        else:
            state.update(wifi_addresses=addresses(config["wifi"]), default_routes=routes())
        atomic(STATE, json.dumps(state))
        print(json.dumps(state))


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as error:
        sys.exit(f"{error.cmd[0]} failed (exit {error.returncode}); inspect the system journal")
