"""Root-only integration check in disposable network namespaces; no live link changes."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time


def execute(*arguments):
    return subprocess.run(arguments, check=True, capture_output=True, text=True, timeout=20).stdout


def main():
    if os.geteuid() != 0:
        raise SystemExit("Run with sudo on Linux")
    suffix = str(os.getpid())
    client, router = "snowshot-wifi-c-" + suffix, "snowshot-wifi-r-" + suffix
    created = []
    server = None
    try:
        for name in (client, router):
            execute("ip", "netns", "add", name)
            created.append(name)
            execute("ip", "-n", name, "link", "set", "lo", "up")
        for local, remote, prefix in (("wired", "rwired", "192.0.2"), ("wifi", "rwifi", "198.51.100")):
            execute("ip", "-n", client, "link", "add", local, "type", "veth", "peer", "name", remote)
            execute("ip", "-n", client, "link", "set", remote, "netns", router)
            for namespace, device, address in ((client, local, prefix + ".2/24"), (router, remote, prefix + ".1/24")):
                execute("ip", "-n", namespace, "addr", "add", address, "dev", device)
                execute("ip", "-n", namespace, "link", "set", device, "up")
            execute("ip", "-n", client, "route", "add", "default", "via", prefix + ".1", "dev", local,
                    "proto", "dhcp", "metric", "100" if local == "wired" else "600")
        execute("ip", "-n", router, "addr", "add", "203.0.113.10/32", "dev", "lo")
        server_code = ("import socket\ns=socket.socket();s.bind(('203.0.113.10',2222));s.listen()\n"
                       "print('ready',flush=True)\nwhile True:\n c,_=s.accept();c.sendall(b'SSH-2.0-test\\r\\n');c.close()\n")
        server = subprocess.Popen(["ip", "netns", "exec", router, "python3", "-u", "-c", server_code],
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        assert server.stdout.readline().strip() == "ready"
        source = Path(__file__).resolve().parents[1] / "backup_wifi.py"
        if not source.exists():
            source = Path("/usr/local/sbin/snowshot-backup-wifi")
        with tempfile.TemporaryDirectory(prefix="snowshot-wifi-test-") as temporary:
            script = Path(temporary) / "client.py"
            script.write_text("""
import json, pathlib, sys
namespace = {'__name__':'network_test','__file__':sys.argv[1]}
exec(compile(pathlib.Path(sys.argv[1]).read_text(), sys.argv[1], 'exec'), namespace)
namespace['ensure_wifi'] = lambda *args: None
state_path = pathlib.Path(sys.argv[2])
state = json.loads(state_path.read_text()) if state_path.exists() else {}
config = {'wired':'wired','wifi':'wifi','probe_host':'203.0.113.10','probe_port':2222}
result = namespace['check'](config, state)
state_path.write_text(json.dumps(result))
route = json.loads(namespace['run'](['ip','-j','route','get','203.0.113.10']).stdout)[0]
print(json.dumps({'state':result,'selected_device':route['dev']}))
""")
            def check(expected, wired_ok):
                result = json.loads(execute("ip", "netns", "exec", client, "python3", str(script),
                                            str(source), str(Path(temporary) / "state.json")))
                assert result["selected_device"] == expected, result
                assert result["state"]["wired_cloud_reachable"] == wired_ok, result
                assert result["state"]["wifi_cloud_reachable"], result
                return result
            check("wired", True)
            # Remove the upstream gateway address while keeping the client's wired link UP.
            execute("ip", "-n", router, "addr", "del", "192.0.2.1/24", "dev", "rwired")
            execute("ip", "-n", client, "neigh", "flush", "dev", "wired")
            link = json.loads(execute("ip", "-n", client, "-j", "link", "show", "wired"))[0]
            assert "UP" in link["flags"]
            check("wired", False)
            check("wifi", False)
            execute("ip", "-n", router, "addr", "add", "192.0.2.1/24", "dev", "rwired")
            check("wifi", True)
            check("wifi", True)
            check("wired", True)
            print(json.dumps({"wired_carrier_stayed_up": True, "automatic_wifi_failover": True,
                              "automatic_wired_recovery": True, "live_network_untouched": True}))
    finally:
        if server is not None:
            server.terminate()
            try:
                server.communicate(timeout=5)
            except subprocess.TimeoutExpired:
                server.kill()
                server.communicate(timeout=5)
        for name in reversed(created):
            execute("ip", "netns", "del", name)


if __name__ == "__main__":
    main()
