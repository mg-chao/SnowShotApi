import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("prepare_policy", HERE / "Prepare-AiModelPolicy.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
OVERLAY = json.loads((HERE.parent / "deployment/policy/policy-revision-15.json").read_text(encoding="utf8"))
CURRENT_OVERLAY_PATH = HERE.parent / "deployment/policy/policy-revision-17.json"
CURRENT_OVERLAY = json.loads(CURRENT_OVERLAY_PATH.read_text(encoding="utf8"))


class AiModelPolicyTests(unittest.TestCase):
    def test_removes_owned_keys_preserves_secrets_and_unrelated_settings(self):
        original = {
            "Policy": {"Revision": 13, "Resources": {"qwen-mt-flash": {}, "qwen3-vl-flash": {}},
                       "Pricing": {"qwen3.8-flash": {}}},
            "Providers": {
                "CloudProviders": {"aliyun": {"ApiKey": "fixture-secret"}},
                "Models": {"qwen-mt-flash": {}, "qwen3.8-flash": {"NativeTranslationOptions": True}},
                "Translation": {"LogicalModels": ["qwen3.8-flash", "qwen-mt-flash"], "AttemptTimeoutSeconds": 90},
            },
            "Other": {"preserved": True},
        }
        result = MODULE.prepare(original, OVERLAY, 14, 15)
        self.assertEqual(result["Policy"]["Revision"], 15)
        self.assertEqual(set(result["Policy"]["Resources"]),
                         {"ai-model", "translation", "table-extraction", "latex-extraction"})
        self.assertEqual(set(result["Providers"]["Models"]), {"qwen3.7-flash", "qwen3.8-flash"})
        self.assertFalse(result["Providers"]["Models"]["qwen3.8-flash"].get("NativeTranslationOptions", False))
        self.assertEqual(result["Providers"]["Translation"]["LogicalModels"], ["qwen3.7-flash"])
        self.assertEqual(result["Providers"]["Translation"]["AttemptTimeoutSeconds"], 90)
        self.assertEqual(result["Providers"]["CloudProviders"], original["Providers"]["CloudProviders"])
        self.assertEqual(result["Other"], original["Other"])
        self.assertEqual(original["Policy"]["Revision"], 13)

    def test_revision_must_exceed_active_and_configured_including_recovery(self):
        for active, configured, proposed in [(15, 13, 15), (13, 16, 15), (13, 13, 14)]:
            with self.assertRaises(ValueError):
                MODULE.prepare({"Policy": {"Revision": configured}}, OVERLAY, active, proposed)
        self.assertEqual(MODULE.prepare({"Policy": {"Revision": 15}}, OVERLAY, 15, 16)["Policy"]["Revision"], 16)

    def test_current_overlay_preserves_current_settings_and_advances_past_retained_recovery(self):
        settings = json.loads((HERE.parent / "src/SnowShotApi/appsettings.json").read_text(encoding="utf8"))
        prepared = MODULE.prepare(settings, CURRENT_OVERLAY, 16, CURRENT_OVERLAY["Policy"]["Revision"] + 1)
        expected = json.loads(json.dumps(settings))
        expected["Policy"]["Revision"] = prepared["Policy"]["Revision"]
        self.assertEqual(prepared, expected)

    def test_current_overlay_cannot_be_prepared_under_an_older_revision(self):
        for revision in (15, 16):
            with self.subTest(revision=revision), self.assertRaisesRegex(ValueError, "selected overlay"):
                MODULE.prepare({"Policy": {"Revision": 13}}, CURRENT_OVERLAY, 13, revision)

    def test_cli_selects_matching_overlay_and_revision_by_default(self):
        with tempfile.TemporaryDirectory() as temporary:
            source, output = Path(temporary) / "source.json", Path(temporary) / "output.json"
            source.write_text(json.dumps({"Policy": {"Revision": 16}, "Secret": "fixture-secret"}), encoding="utf8")
            result = subprocess.run([sys.executable, str(HERE / "Prepare-AiModelPolicy.py"),
                                     "--settings", str(source), "--output", str(output), "--active-revision", "15"],
                                    capture_output=True, text=True, encoding="utf8", timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)
            prepared = json.loads(output.read_text(encoding="utf8"))
            self.assertEqual(prepared["Policy"], CURRENT_OVERLAY["Policy"])
            self.assertEqual(prepared["Secret"], "fixture-secret")
            self.assertNotIn("fixture-secret", result.stdout + result.stderr)

    def test_cli_can_explicitly_prepare_a_historical_profile_at_a_higher_revision(self):
        with tempfile.TemporaryDirectory() as temporary:
            source, output = Path(temporary) / "source.json", Path(temporary) / "output.json"
            source.write_text(json.dumps({"Policy": {"Revision": 15}}), encoding="utf8")
            result = subprocess.run([sys.executable, str(HERE / "Prepare-AiModelPolicy.py"),
                                     "--settings", str(source), "--output", str(output), "--active-revision", "15",
                                     "--overlay", str(HERE.parent / "deployment/policy/policy-revision-15.json"),
                                     "--revision", "16"], capture_output=True, text=True, encoding="utf8", timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)
            prepared = json.loads(output.read_text(encoding="utf8"))
            expected = json.loads(json.dumps(OVERLAY["Policy"]))
            expected["Revision"] = 16
            self.assertEqual(prepared["Policy"], expected)


if __name__ == "__main__":
    unittest.main()
