"""Policy discovery regressions that do not require Docker."""
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).resolve().parent))
import run_harness as harness


class HarnessPolicyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.api = harness.HarnessAgent(Path(self.temporary.name), {})

    def test_probe_reports_the_database_active_policy_for_any_revision(self):
        for revision in (17, 18, 42):
            with self.subTest(revision=revision):
                policy = {"revision": revision, "fingerprint": "a" * 64}
                with patch.object(harness.urllib.request, "urlopen", return_value=io.BytesIO(b'{"status":"ready"}')), \
                        patch.object(harness, "sql", return_value=json.dumps(policy)):
                    self.assertEqual(self.api.probe(15101), policy)

    def test_probe_rejects_missing_database_activation(self):
        with patch.object(harness.urllib.request, "urlopen", return_value=io.BytesIO(b'{"status":"ready"}')), \
                patch.object(harness, "sql", return_value=""):
            with self.assertRaises(ValueError):
                self.api.probe(15101)

    def test_probe_rejects_a_backend_that_is_not_ready(self):
        with patch.object(harness.urllib.request, "urlopen", return_value=io.BytesIO(b'{"status":"not_ready"}')), \
                patch.object(harness, "sql") as sql:
            with self.assertRaises(AssertionError):
                self.api.probe(15101)
        sql.assert_not_called()


if __name__ == "__main__":
    unittest.main()
