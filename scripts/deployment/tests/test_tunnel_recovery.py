import subprocess
import sys
import tempfile
from pathlib import Path
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import tunnel_recovery
import deploy


class TunnelRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "recovery.conf"
        self.run = Mock(side_effect=self.command)

    def command(self, arguments, **kwargs):
        return subprocess.CompletedProcess(arguments, 0,
            stdout="clientaliveinterval 30\nclientalivecountmax 3\n", stderr="")

    def test_validates_effective_keepalive_before_reloading(self):
        result = tunnel_recovery.configure(self.path, run=self.run)
        self.assertTrue(result["changed"])
        self.assertEqual(self.path.read_text(), tunnel_recovery.CONFIGURATION)
        self.assertEqual([call.args[0] for call in self.run.call_args_list], [
            ["/usr/sbin/sshd", "-t"],
            ["/usr/sbin/sshd", "-T", "-C", "user=root,host=localhost,addr=127.0.0.1"],
            ["systemctl", "reload", "ssh"]])

    def test_syntax_failure_preserves_previous_config_without_reloading(self):
        self.path.write_bytes(b"original configuration\n")
        self.run.side_effect = subprocess.CalledProcessError(1, "sshd")
        with self.assertRaises(subprocess.CalledProcessError):
            tunnel_recovery.configure(self.path, run=self.run)
        self.assertEqual(self.path.read_bytes(), b"original configuration\n")
        self.run.assert_called_once()

    def test_ignored_dropin_is_rejected_without_reloading(self):
        self.run.return_value = subprocess.CompletedProcess([], 0,
            stdout="clientaliveinterval 0\nclientalivecountmax 3\n")
        self.run.side_effect = None
        with self.assertRaisesRegex(RuntimeError, "did not enable"):
            tunnel_recovery.configure(self.path, run=self.run)
        self.assertFalse(self.path.exists())
        self.assertEqual(self.run.call_count, 2)

    def test_reload_failure_restores_config_and_reloads_previous_settings(self):
        self.path.write_bytes(b"previous configuration\n")
        def command(arguments, **kwargs):
            if arguments[0] == "systemctl" and kwargs["check"]:
                raise subprocess.CalledProcessError(1, arguments)
            return self.command(arguments, **kwargs)
        self.run.side_effect = command
        with self.assertRaises(subprocess.CalledProcessError):
            tunnel_recovery.configure(self.path, run=self.run)
        self.assertEqual(self.path.read_bytes(), b"previous configuration\n")
        self.assertEqual(self.run.call_args.args[0], ["systemctl", "reload", "ssh"])
        self.assertFalse(self.run.call_args.kwargs["check"])

    def test_unchanged_settings_are_validated_without_reload(self):
        self.path.write_bytes(tunnel_recovery.CONFIGURATION.encode())
        self.assertFalse(tunnel_recovery.configure(self.path, run=self.run)["changed"])
        self.assertEqual(self.run.call_count, 2)

    def test_bootstrap_includes_the_recovery_helper(self):
        import base64
        import json
        target = {"api_host": "api.test", "worker_host": "worker.test"}
        with patch.object(deploy.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)) as execute, \
                patch("builtins.print"):
            deploy.bootstrap(target, password="test-only")
        self.assertEqual(execute.call_count, 2)
        payload = json.loads(execute.call_args.kwargs["input"])
        self.assertEqual(payload["role"], "api")
        self.assertEqual(base64.b64decode(payload["files"]["tunnel_recovery.py"]),
                         Path(tunnel_recovery.__file__).read_bytes())


if __name__ == "__main__":
    unittest.main()
