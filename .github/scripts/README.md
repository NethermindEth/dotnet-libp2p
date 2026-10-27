# CI test discovery

From `libp2p/`, run `python3 ../.github/scripts/discover_test_projects.py`.
Both Linux workflows use this list before restore/build and execute every listed
suite once, stopping on the first failure. Test steps have a 30-minute deadline;
there are no retries or ignored failures.
Any failed suite, including a transient failure, still blocks publication; the
deadline only bounds hangs.

Both loops retain `--minimum-expected-tests 1` for intent and parity. The pinned
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

Run helper and workflow regression checks with:

```sh
python3 -m unittest discover -s .github/scripts -p 'test_*.py'
```
