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
