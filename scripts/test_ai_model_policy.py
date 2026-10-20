import importlib.util
import json
from pathlib import Path
import unittest

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("prepare_policy", HERE / "Prepare-AiModelPolicy.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
OVERLAY = json.loads((HERE.parent / "deployment/policy/policy-revision-15.json").read_text(encoding="utf8"))


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


if __name__ == "__main__":
    unittest.main()
