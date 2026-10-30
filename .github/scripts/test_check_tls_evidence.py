# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: MIT

import json
from pathlib import Path
import tempfile
import unittest

from check_tls_evidence import ARCHIVES, EVIDENCE, EvidenceError, REPO, input_digest, inspect_evidence, sha256, summary


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        (self.repo / EVIDENCE).mkdir(parents=True)
        self.source = self.repo / "source.cs"
        self.source.write_text("original source")
        self.record = {"inputs": {"source.cs": sha256(self.source.read_bytes())}}
        self.record["input_digest"] = {"file_count": 1, "sha256": input_digest(self.record["inputs"])}

    def archive(self, record=None, name="snapshot.json"):
        raw = json.dumps(self.record if record is None else record).encode()
        (self.repo / EVIDENCE / name).write_bytes(raw)
        return {name: sha256(raw)}

    def test_real_archive_inventory_and_manifests(self):
        reports = inspect_evidence(REPO)
        self.assertEqual(10, len(reports))
        self.assertEqual(set(ARCHIVES), {r["archive"] for r in reports})
        self.assertTrue(all(r["historical_only"] for r in reports))
        self.assertEqual(9, sum(r["manifest_present"] for r in reports))

    def test_matching_inputs_still_labelled_historical(self):
        report, = inspect_evidence(self.repo, self.archive())
        self.assertEqual(["source.cs"], report["matched_inputs"])
        self.assertEqual([], report["changed_inputs"])
        self.assertTrue(report["historical_only"])
        self.assertIn("not validation of this revision", summary([report]))

    def test_source_drift_and_deletion_are_visible(self):
        catalog = self.archive()
        self.source.write_text("changed source")
        report, = inspect_evidence(self.repo, catalog)
        self.assertEqual(["source.cs"], report["changed_inputs"])
        self.assertEqual([], report["matched_inputs"])
        self.assertIn("| snapshot.json | 0 | 1 | 0 |", summary([report]))
        self.source.unlink()
        report, = inspect_evidence(self.repo, catalog)
        self.assertEqual(["source.cs"], report["missing_inputs"])
        self.assertIn("| snapshot.json | 0 | 0 | 1 |", summary([report]))

    def test_added_and_removed_archives_require_catalog_change(self):
        catalog = self.archive()
        extra = self.repo / EVIDENCE / "extra.json"
        extra.write_text("{}")
        with self.assertRaisesRegex(EvidenceError, "unlisted=.*extra.json"):
            inspect_evidence(self.repo, catalog)
        extra.unlink()
        (self.repo / EVIDENCE / "snapshot.json").unlink()
        with self.assertRaisesRegex(EvidenceError, "missing=.*snapshot.json"):
            inspect_evidence(self.repo, catalog)

    def test_modified_archive_fails_integrity(self):
        catalog = self.archive()
        path = self.repo / EVIDENCE / "snapshot.json"
        path.write_bytes(path.read_bytes() + b"\n")
        with self.assertRaisesRegex(EvidenceError, "integrity mismatch"):
            inspect_evidence(self.repo, catalog)

    def test_invalid_manifests_cannot_be_catalogued_as_valid(self):
        for record in ({}, {"inputs": {}}, {"inputs": []}, {"inputs": {"source.cs": "bad hash"}},
                       dict(self.record, input_digest={"file_count": 2, "sha256": "0" * 64}),
                       dict(self.record, input_digest={"file_count": 1, "sha256": "0" * 64})):
            with self.subTest(record=record), self.assertRaises(EvidenceError):
                inspect_evidence(self.repo, self.archive(record))

    def test_unsafe_manifest_paths_are_rejected(self):
        for name in ("../outside", "/absolute", "nested/../../outside", "a\\b", "./source.cs", ""):
            with self.subTest(name=name), self.assertRaises(EvidenceError):
                inspect_evidence(self.repo, self.archive({"inputs": {name: "0" * 64}}))
        (self.repo / "link").symlink_to(self.repo.parent, target_is_directory=True)
        with self.assertRaisesRegex(EvidenceError, "escapes repository"):
            inspect_evidence(self.repo, self.archive({"inputs": {"link/outside": "0" * 64}}))

    def test_parent_without_manifest_is_explicitly_unusable_for_reuse(self):
        report, = inspect_evidence(self.repo, self.archive({}, "windows-secure-transport-results.json"))
        self.assertFalse(report["manifest_present"])
        self.assertIn("not recorded", summary([report]))

    def test_baseline_test_inputs_are_checked(self):
        report, = inspect_evidence(self.repo, self.archive({"test_inputs_sha256": self.record["inputs"]}))
        self.assertEqual(["source.cs"], report["matched_inputs"])

    def test_malformed_json_fails_even_with_matching_archive_hash(self):
        path = self.repo / EVIDENCE / "snapshot.json"
        path.write_bytes(b"{")
        with self.assertRaises(json.JSONDecodeError):
            inspect_evidence(self.repo, {path.name: sha256(path.read_bytes())})

    def test_ci_checks_archives_and_executes_both_interop_roles(self):
        workflow = (REPO / ".github/workflows/test.yml").read_text()
        job = workflow.split("  tls-evidence-and-interop:\n", 1)[1].split("  windows-quic-tls:\n", 1)[0]
        for command in ("check_tls_evidence.py", "go build -mod=readonly", "go test -mod=readonly",
                        "dotnet build samples/transport-interop/TransportInterop.csproj",
                        "python3 tests/tls-interop/run.py", "--dotnet-dll", "--go-peer", "--output"):
            self.assertIn(command, job)
        self.assertIn("if: always()", job)
        self.assertNotIn("continue-on-error", job)


if __name__ == "__main__":
    unittest.main()
