"""Regression coverage for candidate and retained deployment policy validation."""
import copy
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
PWSH = shutil.which("pwsh")


@unittest.skipUnless(PWSH, "PowerShell 7 is required")
class DeploymentConfigurationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.candidate = json.loads((ROOT / "src/SnowShotApi/appsettings.json").read_text(encoding="utf8"))
        self.retained = json.loads((ROOT / "deployment/policy/policy-revision-15.json").read_text(encoding="utf8"))

    def verify(self, candidate=None, retained=None, *arguments):
        command = [PWSH, "-NoLogo", "-NoProfile", "-File", str(ROOT / "scripts/Verify-DeploymentConfiguration.ps1")]
        for value, parameter, filename in (
            (candidate, "-ApplicationSettingsPath", "candidate.json"),
            (retained, "-RestorePolicyPath", "retained.json"),
        ):
            if value is not None:
                path = self.directory / filename
                path.write_text(json.dumps(value), encoding="utf8")
                command.extend((parameter, str(path)))
        return subprocess.run([*command, *arguments], cwd=ROOT, capture_output=True, text=True, encoding="utf8", timeout=30)

    def assert_valid(self, result):
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def assert_invalid(self, result, message):
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(message, result.stdout + result.stderr)

    def test_default_candidate_and_retained_policy_are_validated_independently(self):
        self.assert_valid(self.verify())

    def test_explicit_higher_revision_keeps_the_candidate_admission_contract(self):
        self.candidate["Policy"]["Revision"] += 1
        self.assert_valid(self.verify(self.candidate, None, "-ExpectedPolicyRevision", str(self.candidate["Policy"]["Revision"])))

    def test_revision_mismatch_is_rejected(self):
        self.candidate["Policy"]["Revision"] -= 1
        self.assert_invalid(self.verify(self.candidate), "expected revision")

    def test_old_admission_cannot_be_relabelled_as_the_candidate(self):
        self.candidate["Policy"]["Resources"]["ai-model"].update(RequestsPerMinute=60, PerPrincipalConcurrency=4)
        self.assert_invalid(self.verify(self.candidate), "AI Model admission")

    def test_explicit_queue_override_cannot_silently_change_the_candidate(self):
        self.candidate["Policy"]["Resources"]["ai-model"]["PerPrincipalQueueLength"] = 4
        self.assert_invalid(self.verify(self.candidate), "AI Model admission")

    def test_explicit_equivalent_queue_limit_is_accepted(self):
        self.candidate["Policy"]["Resources"]["ai-model"]["PerPrincipalQueueLength"] = 8
        self.assert_valid(self.verify(self.candidate))

    def test_higher_retained_revision_preserves_the_historical_profile(self):
        self.retained["Policy"]["Revision"] = 16
        self.assert_valid(self.verify(None, self.retained))

    def test_retained_policy_does_not_inherit_the_candidate_limits(self):
        self.retained["Policy"]["Resources"]["ai-model"].update(RequestsPerMinute=600, PerPrincipalConcurrency=8)
        self.assert_invalid(self.verify(None, self.retained), "AI Model admission")

    def test_historical_candidate_requires_an_explicit_policy_reference(self):
        historical = copy.deepcopy(self.candidate)
        historical["Policy"] = copy.deepcopy(self.retained["Policy"])
        historical["Policy"]["Revision"] = 16
        reference = str(ROOT / "deployment/policy/policy-revision-15.json")
        self.assert_valid(self.verify(historical, None, "-PolicyReferencePath", reference, "-ExpectedPolicyRevision", "16"))

    def test_current_profile_can_be_selected_for_a_new_recovery_configuration(self):
        recovery = copy.deepcopy(self.candidate)
        recovery["Policy"]["Revision"] += 1
        reference = str(ROOT / "deployment/policy/policy-revision-17.json")
        self.assert_valid(self.verify(None, recovery, "-RestorePolicyReferencePath", reference))

    def test_explicit_revision_cannot_downgrade_the_selected_profile(self):
        self.candidate["Policy"]["Revision"] = 16
        self.assert_invalid(self.verify(self.candidate, None, "-ExpectedPolicyRevision", "16"), "reference revision")

    def test_common_accounting_and_resource_checks_remain_enforced(self):
        invalid = copy.deepcopy(self.candidate)
        invalid["Policy"]["PrincipalDailyAllowanceNanoYuan"] = 1
        self.assert_invalid(self.verify(invalid), "allowances and budgets")
        invalid = copy.deepcopy(self.candidate)
        invalid["Policy"]["Resources"]["qwen-mt-flash"] = {}
        self.assert_invalid(self.verify(invalid), "only the four public resources")


if __name__ == "__main__":
    unittest.main()
