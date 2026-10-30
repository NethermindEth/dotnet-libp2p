# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: MIT

"""Check historical TLS evidence integrity and report drift from recorded inputs."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re


REPO = Path(__file__).resolve().parents[2]
EVIDENCE = "docs/development/evidence"
# These are immutable historical observations, including failed checkpoints.
# Updating or adding an archive requires an explicit catalog change in review.
# A matching hash establishes integrity, not the truth of a recorded outcome.
ARCHIVES = {
    "linux-continuation-tls-cardinality-candidate.json": "4f33507368f3f0a1225bc3a9f27eb66515c33d715fe5405054ed0e5e4dcca5cc",
    "linux-retry-tls-cardinality-candidate.json": "c478a7498e1d13d59cc7fb87baf19171cff71e0345483b5b4a73c7be12301d52",
    "linux-shutdown-tls-cardinality-candidate.json": "e5c8ab48b09e00931b7909dcc55ecea579cd23f333647a8e3abd93a6789036ab",
    "linux-tls-cardinality-baseline.json": "0b18c3fa4d881e73f91a41e07f8b0ebd712bca4bd56ad0b0f938633e4540a859",
    "linux-tls-cardinality-candidate.json": "c093090517a600a3d7aee33cde4b3dca50070e371e67cebdcbe98aa65de5a4f2",
    "windows-065245c-tls-cardinality-candidate.json": "86106f60db409eaad623cd0f9d67d20914f4c71902bb086cbb7ce9a4b3bcbcbf",
    "windows-retry-b295980-tls-cardinality-candidate.json": "3a68364021e191e47762e9ff12d186d45260ced25edd6bf1b2112c468132728d",
    "windows-secure-transport-results.json": "0e7756045f627d9507c874a46df5e0f4814004d327df74bd656d0b23712ee43e",
    "windows-tls-cardinality-baseline.json": "04dc1e3b1b85a80c5bc9650a13a6360ab4e262f9b267c881f2bb4ef76b74c179",
    "windows-tls-cardinality-candidate.json": "b13a8069610daec6ae6c971b888026b5e28f2f6f63ab2bdf8485ed4ce2059f1c",
}


class EvidenceError(ValueError):
    pass


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def input_digest(inputs):
    return sha256("".join(f"{path}\0{digest}\n" for path, digest in sorted(inputs.items())).encode())


def input_path(repo, name):
    if not isinstance(name, str) or not name or "\\" in name:
        raise EvidenceError(f"Invalid input path: {name!r}")
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts or path.as_posix() != name:
        raise EvidenceError(f"Invalid input path: {name!r}")
    resolved = (repo / name).resolve()
    if not resolved.is_relative_to(repo.resolve()):
        raise EvidenceError(f"Input escapes repository: {name}")
    return resolved


def inspect_evidence(repo, catalog=ARCHIVES):
    folder = repo / EVIDENCE
    actual = {p.name for p in folder.glob("*.json")}
    if actual != set(catalog):
        raise EvidenceError(f"Archive inventory differs: missing={sorted(set(catalog) - actual)}, unlisted={sorted(actual - set(catalog))}")
    reports = []
    for name, expected in sorted(catalog.items()):
        raw = (folder / name).read_bytes()
        if sha256(raw) != expected:
            raise EvidenceError(f"Archive integrity mismatch: {name}")
        record = json.loads(raw)
        if not isinstance(record, dict):
            raise EvidenceError(f"Archive must be an object: {name}")
        inputs = record.get("inputs", record.get("test_inputs_sha256"))
        report = {"archive": name, "historical_only": True, "archive_sha256": expected,
                  "manifest_present": inputs is not None, "matched_inputs": [], "changed_inputs": [], "missing_inputs": []}
        if inputs is None:
            # This parent snapshot predates input manifests. Never infer reuse.
            if name != "windows-secure-transport-results.json":
                raise EvidenceError(f"Missing input manifest: {name}")
        else:
            if not isinstance(inputs, dict) or not inputs:
                raise EvidenceError(f"Empty or invalid input manifest: {name}")
            for path, digest in sorted(inputs.items()):
                source = input_path(repo, path)
                if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest):
                    raise EvidenceError(f"Invalid input SHA256: {name}: {path}")
                if not source.is_file():
                    report["missing_inputs"].append(path)
                elif sha256(source.read_bytes()) != digest:
                    report["changed_inputs"].append(path)
                else:
                    report["matched_inputs"].append(path)
            if "input_digest" in record:
                manifest = record["input_digest"]
                if not isinstance(manifest, dict) or manifest.get("file_count") != len(inputs) or manifest.get("sha256") != input_digest(inputs):
                    raise EvidenceError(f"Input manifest digest mismatch: {name}")
        reports.append(report)
    return reports


def summary(reports):
    lines = ["## Historical TLS evidence", "",
             "Archive integrity and recorded input digests checked. These are historical observations, not validation of this revision.",
             "Changed or missing inputs prevent direct reuse; even matching recorded inputs do not cover new files or environment changes.", "",
             "| Archive | Matching inputs | Changed inputs | Missing inputs |", "| --- | ---: | ---: | ---: |"]
    for report in reports:
        counts = [str(len(report[key])) for key in ("matched_inputs", "changed_inputs", "missing_inputs")]
        if not report["manifest_present"]:
            counts = ["not recorded", "not recorded", "not recorded"]
        lines.append(f"| {report['archive']} | {' | '.join(counts)} |")
    lines += ["", "The CI job separately runs current Go TLS/Yamux interoperability and uploads its logs and summary with this drift report.", ""]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--summary", type=Path)
    args = parser.parse_args()
    reports = inspect_evidence(REPO)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({"archives": reports}, indent=2) + "\n")
    message = summary(reports)
    print(message)
    if args.summary:
        with args.summary.open("a") as output:
            output.write(message)


if __name__ == "__main__":
    main()
