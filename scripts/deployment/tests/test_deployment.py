from __future__ import annotations

import copy
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import agent
import deploy

POLICY = {"revision": 13, "fingerprint": "a" * 64}


class Fixture(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.include = self.root / "upstream.conf"
        self.settings = {"public_host": "example.test", "nginx_include": str(self.include),
                         "nginx_files": [], "legacy_container": "legacy-api"}
        self.api = agent.ApiAgent(self.root, self.settings)
        for name, slot, port in (("old", "blue", 5000), ("new", "green", 5001)):
            self.api.state["releases"][name] = {"id": name, "slot": slot, "port": port,
                "container": name, "image": "sha256:" + "b" * 64, "policy": POLICY,
                "status": "running", "legacy": False, "workers": {}, "drain_generations": []}
        self.api.state["active"] = "old"
        self.include.write_text(self.api.upstream(self.api.release("old")))

    def switch_context(self, nginx=None):
        stack = __import__("contextlib").ExitStack()
        stack.enter_context(patch.object(self.api, "probe", return_value=POLICY))
        stack.enter_context(patch.object(self.api, "routed_release", return_value="old"))
        stack.enter_context(patch.object(self.api, "nginx_workers", return_value=[{"pid": 1, "start": "42"}]))
        stack.enter_context(patch.object(self.api, "nginx", side_effect=nginx))
        stack.enter_context(patch.object(self.api, "wait_route"))
        return stack


class SwitchTests(Fixture):
    def test_switch_writes_ahead_and_remembers_process_identity(self):
        observed = []
        def nginx(*arguments):
            state = json.loads(self.api.state_file.read_text())
            observed.append(state["pending"]["operation"])
        with self.switch_context(nginx):
            result = self.api.switch("new")
        self.assertEqual(result["active"], "new")
        self.assertEqual(observed, ["switch", "switch"])
        self.assertEqual(self.api.release("old")["drain_generations"], [{"pid": 1, "start": "42"}])

    def test_invalid_nginx_configuration_restores_previous_include(self):
        original = self.include.read_text()
        with self.switch_context([agent.DeploymentError("invalid"), None, None]):
            with self.assertRaises(agent.DeploymentError):
                self.api.switch("new")
        self.assertEqual(self.include.read_text(), original)
        self.assertEqual(self.api.state["active"], "old")

    def test_reload_failure_restores_previous_include(self):
        original = self.include.read_text()
        with self.switch_context([None, agent.DeploymentError("reload failed"), None, None]):
            with self.assertRaises(agent.DeploymentError):
                self.api.switch("new")
        self.assertEqual(self.include.read_text(), original)

    def test_policy_mismatch_never_touches_ingress(self):
        original = self.include.read_text()
        with self.switch_context(), patch.object(self.api, "probe", return_value={**POLICY, "revision": 14}):
            with self.assertRaises(agent.DeploymentError):
                self.api.switch("new")
        self.assertEqual(self.include.read_text(), original)

    def test_rollback_can_leave_an_unhealthy_active_backend(self):
        with self.switch_context(), patch.object(self.api, "probe", side_effect=lambda port:
                POLICY if port == 5001 else (_ for _ in ()).throw(OSError("dead"))):
            self.api.switch("new")
        self.assertEqual(self.api.state["active"], "new")

    def test_interrupted_switch_recovers_from_live_header(self):
        self.api.state["pending"] = {"operation": "switch", "to": "new"}
        with patch.object(self.api, "routed_release", return_value="new"):
            result = self.api.public_status()
        self.assertEqual(result["active"], "new")
        self.assertEqual(result["previous"], "old")


class CleanupTests(Fixture):
    def test_waits_for_old_nginx_workers_without_killing(self):
        self.api.state["active"] = "new"
        self.api.release("old")["drain_generations"] = [{"pid": 1, "start": "42"}]
        with patch.object(self.api, "routed_release", return_value="new"), \
                patch.object(agent, "alive", return_value=True), patch.object(self.api, "compose") as compose:
            result = self.api.cleanup()
        compose.assert_not_called()
        self.assertTrue(result["pending"])
        self.assertEqual(self.api.release("old")["status"], "running")

    def test_shared_worker_remains_referenced_until_last_api_stops(self):
        shared = {"id": "shared-table"}
        retired = {"id": "retired-latex"}
        self.api.state["active"] = "new"
        self.api.release("old")["workers"] = {"table": shared, "latex": retired}
        self.api.release("new")["workers"] = {"table": shared}
        with patch.object(self.api, "routed_release", return_value="new"), patch.object(self.api, "compose"):
            result = self.api.cleanup()
        self.assertEqual(result["retirable_workers"], [retired])

    def test_uncertain_routing_never_stops_a_container(self):
        with patch.object(self.api, "routed_release", return_value=None), patch.object(self.api, "compose") as compose:
            with self.assertRaises(agent.DeploymentError):
                self.api.cleanup()
        compose.assert_not_called()


class ConfigurationTests(Fixture):
    def template(self):
        secrets = self.root / "secrets"
        secrets.mkdir()
        (secrets / "Providers__Table__BaseUrl").write_text("https://snowshot-table-rec:18080/\n")
        return {"Image": "sha256:fixed", "Config": {"Env": ["SNOWSHOT_SECRETS_DIRECTORY=/run/secrets",
                "ASPNETCORE_URLS=http://127.0.0.1:5000"]},
                "Mounts": [{"Destination": "/run/secrets", "Source": str(secrets)}]}

    def test_candidate_updates_mounted_secret_with_higher_precedence(self):
        template = self.template()
        self.api.set_option(template, "Providers__Table__BaseUrl", "https://snowshot-table-rec:28080/")
        self.assertEqual(self.api.effective_option(template, "Providers__Table__BaseUrl"), "https://snowshot-table-rec:28080/")

    def test_legacy_identity_allows_only_listener_and_worker_urls(self):
        template = self.template()
        before = self.api.configuration_identity(template, legacy=True)
        self.api.set_option(template, "ASPNETCORE_URLS", "http://127.0.0.1:5001")
        self.api.set_option(template, "Providers__Table__BaseUrl", "https://snowshot-table-rec:28080/")
        self.assertEqual(before, self.api.configuration_identity(template, legacy=True))
        self.api.set_option(template, "Policy__Revision", "14")
        self.assertNotEqual(before, self.api.configuration_identity(template, legacy=True))

    def test_docker_mount_order_does_not_change_configuration_identity(self):
        template = self.template()
        other = self.root / "settings.json"
        other.write_text('{}')
        template["Mounts"].append({"Source": str(other), "Destination": "/app/appsettings.Production.json"})
        before = self.api.configuration_identity(template)
        template["Mounts"].reverse()
        self.assertEqual(before, self.api.configuration_identity(template))

    def test_resource_refusal(self):
        with patch.object(agent, "free_memory", return_value=100):
            with self.assertRaises(agent.DeploymentError):
                agent.capacity(50, 100)

    def test_candidate_forward_must_not_reuse_an_unknown_api_host_listener(self):
        with patch.object(agent, "port_free", return_value=False):
            with self.assertRaisesRegex(agent.DeploymentError, "reverse-forward port"):
                self.api.check_worker_ports({"table": {"id": "candidate", "port": 28080}})

    def test_shared_worker_forward_is_preserved(self):
        worker = {"id": "shared", "port": 18080}
        self.api.release("old")["workers"] = {"table": worker}
        with patch.object(agent, "port_free", return_value=False):
            self.assertTrue(self.api.check_worker_ports({"table": worker})["available"])

    def test_pid_reuse_does_not_hold_or_kill_an_unrelated_process(self):
        with patch.object(agent, "process_identity", return_value={"pid": 1, "start": "new"}):
            self.assertFalse(agent.alive({"pid": 1, "start": "old"}))

    def test_environment_is_parsed_without_shell_execution(self):
        self.assertEqual(agent.environment('VALUE="$(touch /tmp/never)"\n'), {"VALUE": "$(touch /tmp/never)"})

    def test_all_systemd_environment_files_are_retained_in_order(self):
        output = b"EnvironmentFiles=/etc/base.env (ignore_errors=no)\nEnvironmentFiles=/etc/override.env (ignore_errors=no)\n"
        with patch.object(agent, "run", return_value=subprocess.CompletedProcess([], 0, output)):
            values = agent.properties("worker.service")
        self.assertLess(values["EnvironmentFiles"].index("base.env"), values["EnvironmentFiles"].index("override.env"))

    def test_policy_mismatch_is_rejected_before_starting_a_candidate_server(self):
        template = self.template()
        template.update(HostConfig={}, Image="sha256:" + "b" * 64)
        template["Config"].update(Entrypoint=["dotnet", "SnowShotApi.dll"], User="1654")
        template["Mounts"][0].update(Type="bind", RW=False)
        directory = self.api.directory("old")
        agent.write_json(directory / "template.json", template)
        description = {"policy_revision": 14, "policy_fingerprint": "different"}
        calls = []
        def execute(arguments, **_):
            calls.append(arguments)
            data = (template["Image"] + "\n").encode() if "inspect" in arguments else json.dumps(description).encode()
            return subprocess.CompletedProcess(arguments, 0, data)
        with patch.object(self.api, "routed_release", return_value="old"), \
                patch.object(self.api, "probe", return_value=POLICY), patch.object(agent, "capacity"), \
                patch.object(agent, "port_free", return_value=True), patch.object(agent, "run", side_effect=execute):
            # The previous fixture occupies this slot in memory; it has no process.
            self.api.release("new")["status"] = "stopped"
            with self.assertRaisesRegex(agent.DeploymentError, "Policy mismatch"):
                self.api.prepare({"id": "candidate", "api": {"image": template["Image"]}}, {})
        self.assertFalse(any("up" in call or "start" in call for call in calls))


class ControllerTests(unittest.TestCase):
    def test_misspelled_worker_assignment_is_rejected_before_server_calls(self):
        controller = self.controller()
        with self.assertRaises(agent.DeploymentError):
            controller.stage({"id": "new", "worker": {"latex": {"clone": True}}})
        self.api.call.assert_not_called()
        self.worker.call.assert_not_called()

    def controller(self):
        from unittest.mock import Mock
        self.api, self.worker = Mock(), Mock()
        return deploy.Controller(self.api, self.worker, {}, Path("."))

    def test_startup_failure_cleans_only_candidate_references(self):
        controller = self.controller()
        self.api.call.side_effect = [{"active": "old", "releases": {"old": {"workers": {}}}}, {},
                                    agent.DeploymentError("startup failed"), {"pending": [], "retirable_workers": []}]
        with patch.object(controller, "verify_workers"):
            with self.assertRaises(agent.DeploymentError):
                controller.stage({"id": "new"})
        self.assertEqual(self.api.call.call_args.args[0], "cleanup")

    def test_missing_tunnel_prevents_candidate_api_start(self):
        controller = self.controller()
        self.api.call.side_effect = [{"active": "old", "releases": {"old": {"workers": {}}}}, {},
                                    {"pending": [], "retirable_workers": []}]
        with patch.object(controller, "verify_workers", side_effect=agent.DeploymentError("tunnel unavailable")):
            with self.assertRaises(agent.DeploymentError):
                controller.stage({"id": "new"})
        self.assertNotIn("start", [call.args[0] for call in self.api.call.call_args_list])

    def test_failed_observation_rolls_back(self):
        controller = self.controller()
        self.api.call.side_effect = [{"active": "old", "releases": {"new": {"status": "running"}}}, {},
                                    agent.DeploymentError("candidate unhealthy"), {},
                                    {"pending": [], "retirable_workers": []}]
        with self.assertRaises(agent.DeploymentError):
            controller.switch("new", observation=0)
        self.assertEqual(self.api.call.call_args_list[-2].args[0], "switch")
        self.assertEqual(self.api.call.call_args_list[-2].kwargs["release_id"], "old")
        self.assertEqual(self.api.call.call_args_list[-1].args[0], "cleanup")


class SecurityTests(Fixture):
    def test_tls13_eof_requires_authenticated_controls_and_no_http_response(self):
        ready = subprocess.CompletedProcess([], 0, b'{"status":"ready"}')
        tls12 = subprocess.CompletedProcess([], 35, b'000')
        tls13 = subprocess.CompletedProcess([], 52, b'000')
        extraction = subprocess.CompletedProcess([], 0, b'{"latex":"x"}')
        with patch.object(agent, "run", side_effect=[ready, ready, tls12, ready, ready, tls13, ready, extraction]) as execute:
            result = agent.probe_worker("latex", 28081, {"certificate": "cert", "key": "key", "ca": "ca"}, "eA==", "x")
        self.assertTrue(result["mtls_enforced"])
        self.assertEqual(execute.call_count, 8)

    def test_unauthenticated_http_error_is_not_a_tls_rejection(self):
        ready = subprocess.CompletedProcess([], 0, b'{"status":"ready"}')
        application_rejection = subprocess.CompletedProcess([], 0, b'401')
        with patch.object(agent, "run", side_effect=[ready, ready, application_rejection]):
            with self.assertRaises(agent.DeploymentError):
                agent.probe_worker("latex", 28081, {"certificate": "cert", "key": "key", "ca": "ca"}, "eA==", "x")

    def test_worker_record_cannot_substitute_an_arbitrary_systemd_unit(self):
        settings = {"state_root": str(self.root)}
        worker = agent.WorkerAgent(settings)
        original = {"id": "legacy-table", "kind": "table", "source_hash": "known", "legacy": True, "unit": "table.service"}
        agent.write_json(self.root / "legacy" / "table.json", {"record": original})
        with self.assertRaises(agent.DeploymentError):
            worker.private({**original, "unit": "ssh.service"})

    @unittest.skipIf(sys.platform == "win32", "flock is a Linux server primitive")
    def test_concurrent_server_session_cannot_acquire_lock(self):
        import fcntl
        lock_path = self.root / "lock"
        with lock_path.open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with patch("sys.stdout", new_callable=io.StringIO) as output:
                result = agent.serve(self.api, lock_path)
        self.assertEqual(result, 1)
        self.assertIn("Another deployment", output.getvalue())

    @unittest.skipIf(sys.platform == "win32", "flock is a Linux server primitive")
    def test_server_reloads_state_after_it_acquires_the_lock(self):
        persisted = copy.deepcopy(self.api.state)
        persisted["active"] = "new"
        agent.write_json(self.api.state_file, persisted)
        self.assertEqual(self.api.state["active"], "old")
        with patch("sys.stdin", io.StringIO("")), patch("sys.stdout", io.StringIO()):
            self.assertEqual(agent.serve(self.api, self.root / "lock"), 0)
        self.assertEqual(self.api.state["active"], "new")


class WorkerTests(Fixture):
    def test_healthy_shared_worker_with_historical_restart_can_be_verified(self):
        worker = agent.WorkerAgent({"state_root": str(self.root)})
        record = {"kind": "table", "port": 18080, "unit": "worker", "tunnel_unit": "tunnel"}
        with patch.object(worker, "private"), patch.object(agent, "capacity"), \
                patch.object(agent, "properties", return_value={"NRestarts": "3", "ActiveState": "active"}), \
                patch.object(agent, "probe_worker", return_value={"ready": True}):
            self.assertTrue(worker.probe(record, {}, "fixture", "expected")["ready"])

    def test_worker_restart_during_verification_fails_before_forward_probe(self):
        worker = agent.WorkerAgent({"state_root": str(self.root)})
        record = {"kind": "table", "port": 18080, "unit": "worker", "tunnel_unit": "tunnel"}
        with patch.object(worker, "private"), patch.object(agent, "capacity"), \
                patch.object(agent, "properties", side_effect=[{"NRestarts": "3"}, {"NRestarts": "4"}]), \
                patch.object(agent, "probe_worker") as probe:
            with self.assertRaises(agent.DeploymentError):
                worker.probe(record, {}, "fixture", "expected")
        probe.assert_not_called()

    @unittest.skipIf(sys.platform == "win32" or getattr(os, "geteuid", lambda: 1)() != 0,
                     "Container/root server bind-mount ownership")
    def test_configuration_snapshot_preserves_nonroot_secret_owner_and_mode(self):
        source = self.root / "original-secrets"
        source.mkdir(mode=0o700)
        secret = source / "ConnectionStrings__SnowShot"
        secret.write_text("fixture")
        secret.chmod(0o600)
        os.chown(secret, 1654, 1654)
        os.chown(source, 1654, 1654)
        inspected = {"Mounts": [{"Type": "bind", "RW": False, "Source": str(source), "Destination": "/run/secrets"}]}
        template = self.api.snapshot("snapshot", inspected)
        copied = Path(template["Mounts"][0]["Source"])
        self.assertEqual(copied.stat().st_uid, 1654)
        self.assertEqual((copied / secret.name).stat().st_uid, 1654)
        self.assertEqual((copied / secret.name).stat().st_mode & 0o777, 0o600)

    @unittest.skipIf(sys.platform == "win32", "POSIX service permissions")
    def test_private_umask_does_not_hide_launch_files_from_service_user(self):
        worker = agent.WorkerAgent({"state_root": str(self.root), "gid": os.getgid()})
        (self.root / "slots").mkdir(mode=0o750)
        base = {"port": 18080, "memory": 1, "affinity": "0-1"}
        private = {"environment": {}, "record": {"arguments": ["/runner"]},
                   "tunnel": {"SSH_USER": "tunnel", "SSH_HOST": "api"}}
        previous = os.umask(0o077)
        try:
            with patch.object(worker, "private", return_value=private), \
                    patch.object(worker, "source_hash", return_value="hash"), \
                    patch.object(agent, "capacity"), patch.object(agent, "port_free", return_value=True), \
                    patch.object(agent, "run"), patch.object(os, "chown"):
                record = worker.prepare("table", base, {"clone": True}, "candidate")
        finally:
            os.umask(previous)
        directory = self.root / "slots" / record["id"]
        self.assertEqual(directory.stat().st_mode & 0o777, 0o750)
        self.assertEqual((directory / "launch.json").stat().st_mode & 0o777, 0o640)
        self.assertEqual((directory / "tunnel.json").stat().st_mode & 0o777, 0o640)

    def test_active_worker_is_enabled_without_restart(self):
        worker = agent.WorkerAgent({"state_root": str(self.root)})
        record = {"kind": "table", "arguments": ["/runner"], "source_hash": "hash",
                  "unit": "worker", "tunnel_unit": "tunnel"}
        with patch.object(worker, "private", return_value={"environment": {}}), \
                patch.object(worker, "source_hash", return_value="hash"), \
                patch.object(agent, "properties", return_value={"ActiveState": "active"}), \
                patch.object(agent, "run") as execute:
            worker.start(record)
        execute.assert_called_once_with(["systemctl", "enable", "--now", "worker", "tunnel"])


if __name__ == "__main__":
    unittest.main()
