import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("backup_wifi", Path(__file__).resolve().parents[1] / "backup_wifi.py")
wifi = importlib.util.module_from_spec(spec)
spec.loader.exec_module(wifi)
CONFIG = {"wired": "ethernet", "wifi": "wlan", "probe_host": "192.0.2.10", "probe_port": 22}
WIRED_ROUTE = {"dev": "ethernet", "gateway": "192.0.2.1", "metric": 100, "protocol": "dhcp"}
WIFI_ROUTE = {"dev": "wlan", "gateway": "198.51.100.1", "metric": 600, "protocol": "dhcp"}
PREFERRED = {**WIFI_ROUTE, "metric": 50, "protocol": "static"}


class FailoverTests(unittest.TestCase):
    def test_switches_after_two_failed_wired_probes_with_working_wifi(self):
        state = {}
        self.assertEqual(wifi.choose(False, True, state, False), "wired")
        self.assertEqual(wifi.choose(False, True, state, False), "wifi")

    def test_does_not_promote_unreachable_wifi(self):
        state = {}
        for _ in range(4):
            self.assertEqual(wifi.choose(False, False, state, False), "wired")

    def test_restores_wired_only_after_three_consecutive_successes(self):
        state = {}
        self.assertEqual(wifi.choose(True, True, state, True), "wifi")
        self.assertEqual(wifi.choose(True, True, state, True), "wifi")
        self.assertEqual(wifi.choose(True, True, state, True), "wired")

    def test_interrupted_recovery_resets_the_success_counter(self):
        state = {}
        wifi.choose(True, True, state, True)
        wifi.choose(False, True, state, True)
        self.assertEqual(wifi.choose(True, True, state, True), "wifi")

    def test_falls_back_immediately_when_preferred_wifi_fails(self):
        self.assertEqual(wifi.choose(True, False, {}, True), "wired")

    def test_promotion_preserves_dhcp_routes(self):
        with patch.object(wifi, "run") as execute:
            wifi.apply_route("wifi", CONFIG, [WIRED_ROUTE, WIFI_ROUTE])
        execute.assert_called_once_with(["ip", "-4", "route", "add", "default", "via", "198.51.100.1",
                                         "dev", "wlan", "proto", "static", "metric", "50"])

    def test_recovery_removes_only_the_owned_preference(self):
        with patch.object(wifi, "run") as execute:
            wifi.apply_route("wired", CONFIG, [WIRED_ROUTE, WIFI_ROUTE, PREFERRED])
        execute.assert_called_once_with(["ip", "-4", "route", "del", "default", "via", "198.51.100.1",
                                         "dev", "wlan", "proto", "static", "metric", "50"])

    def test_unchanged_wifi_gateway_does_not_rewrite_routes(self):
        with patch.object(wifi, "run") as execute:
            wifi.apply_route("wifi", CONFIG, [WIRED_ROUTE, WIFI_ROUTE, PREFERRED])
        execute.assert_not_called()

    def test_gateway_change_removes_stale_preference_before_adding_new_one(self):
        with patch.object(wifi, "run") as execute:
            wifi.apply_route("wifi", CONFIG, [WIRED_ROUTE, {**WIFI_ROUTE, "gateway": "198.51.100.2"}, PREFERRED])
        self.assertEqual(execute.call_count, 2)
        self.assertIn("198.51.100.1", execute.call_args_list[0].args[0])
        self.assertIn("198.51.100.2", execute.call_args_list[1].args[0])

    def test_unrelated_metric_collision_is_rejected(self):
        with patch.object(wifi, "run") as execute:
            with self.assertRaisesRegex(RuntimeError, "already used"):
                wifi.apply_route("wifi", CONFIG, [WIRED_ROUTE, WIFI_ROUTE, {**WIRED_ROUTE, "metric": 50}])
        execute.assert_not_called()

    def test_check_probes_both_devices_even_when_wired_has_an_address(self):
        state = {"wired_failures": 1}
        with patch.object(wifi, "ensure_wifi"), patch.object(wifi, "cloud_reachable", side_effect=[False, True]) as probe, \
                patch.object(wifi, "routes", return_value=[WIRED_ROUTE, WIFI_ROUTE]), patch.object(wifi, "apply_route") as apply:
            result = wifi.check(CONFIG, state)
        self.assertEqual([call.args[0] for call in probe.call_args_list], ["ethernet", "wlan"])
        self.assertEqual(result["path"], "wifi")
        apply.assert_called_once_with("wifi", CONFIG, [WIRED_ROUTE, WIFI_ROUTE])

    def test_manual_connect_retries_even_with_an_existing_wifi_address(self):
        with patch.object(wifi, "radio_on"), patch.object(wifi, "addresses", return_value=["198.51.100.10"]), \
                patch.object(wifi, "run") as execute:
            wifi.ensure_wifi(CONFIG, {}, force=True)
        self.assertEqual(execute.call_args.args[0], ["wpa_cli", "-i", "wlan", "reconnect"])


if __name__ == "__main__":
    unittest.main()
