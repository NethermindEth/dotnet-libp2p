# CI test discovery

From `libp2p/`, run `python3 ../.github/scripts/discover_test_projects.py`.
Both Linux workflows use this list before restore/build, then invoke
`bash ../.github/scripts/run_test_projects.sh "$RUNNER_TEMP/test-projects.txt"`.
Test steps have a 30-minute deadline, including any retry.

An E2E suite (`.E2eTests.csproj`) that reports test failures (MTP exit 2) is retried
once as a complete suite. A successful retry permits publication, and is reported
as a recovered failure in a workflow warning and job summary. Both attempt logs
are retained under `$RUNNER_TEMP/test-results` and uploaded even if testing fails.
A second failure still blocks publication. Unit-suite failures, discovery errors,
zero tests, unmet minimum counts, crashes and timeouts are not retried. This bounds
the effect of an intermittent E2E failure; it does not prove that a recovered
failure was harmless or remove the possibility of a release being blocked.

The shared runner retains `--minimum-expected-tests 1` for intent and parity. The pinned
test platform already returns exit 8 when zero tests run without this flag; a
positive test count below a larger requested minimum returns exit 9.

Runner projects must belong to `Libp2p.slnx` and use `.Tests.csproj` or
`.E2eTests.csproj`. Discovery cross-checks repository project filenames and explicit
`EnableNUnitRunner=true` or `IsTestProject=true` declarations, excluding `bin`,
`obj`, submodules and nested Git checkouts or worktrees containing a `.git` entry.
The root checkout remains in scope, including new untracked projects. Discovery
does not evaluate MSBuild conditions or imports. Introducing
an alternate/imported runner declaration requires updating this discovery contract.
`Libp2p.Core.TestsBase` is support infrastructure, not a runner.

Console and MAUI chat boundary tests belong to `Libp2p.Samples.Chat.Tests`, which
compiles the real sample protocol sources without requiring MAUI packaging.
Core.Tests has no sample-source compiles. AI chat tests remain in
`Libp2p.Samples.Perf.Tests`: the console and AI samples both declare a global
`ChatProtocol`, so they need separate test assemblies.

Run helper and workflow regression checks with:

```sh
python3 -m unittest discover -s .github/scripts -p 'test_*.py'
```

## TLS evidence and interoperability

The `tls-evidence-and-interop` job in `test.yml` checks all ten historical JSON
records in `docs/development/evidence/` against the archive catalog in
`check_tls_evidence.py`. It rejects unlisted, missing or modified archives and
invalid recorded input manifests. Intentional additions or corrections require
a reviewed catalog update. The check verifies integrity, not whether a recorded
test outcome was correct.

Each run compares the recorded input hashes with the checkout. The job summary
shows matching, changed and missing input counts; the uploaded `evidence.json`
lists their paths. Historical drift is reported rather than rejected. These
archives include native baselines and failed checkpoints, so they must not be
rewritten to match later source. Matching recorded inputs alone cannot establish
reuse: manifests may omit new files, and execution environments can differ. The
old parent record without a manifest is explicitly marked as such.

The same job builds `tests/tls-interop/main.go` with Go 1.23.12 and the checked-in
module versions, runs the pinned go-libp2p certificate/reference tests with
nonzero discovery checks, and invokes `tests/tls-interop/run.py`. The driver runs
the real .NET transport sample against Go over TCP with TLS and Yamux in both
roles, checking authenticated identities and traffic. It owns and stops its
Redis and peer processes. Any build, reference-test or interoperability failure
fails the job. Logs, the drift report and the interoperability summary are
uploaded even when a later step fails. This Linux job does not replace the
separate Windows QUIC/TLS controls.

Replay the archive check from the repository root:

```sh
python3 .github/scripts/check_tls_evidence.py --output "$TMPDIR/tls-evidence.json"
```

The exact build and interoperability commands are in `test.yml`; use the
session's `TMPDIR` in place of `RUNNER_TEMP` for local output.
