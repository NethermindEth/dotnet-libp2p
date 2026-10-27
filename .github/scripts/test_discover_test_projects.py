# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: MIT

import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap
import unittest

from discover_test_projects import DiscoveryError, discover


REPO = Path(__file__).resolve().parents[2]


class DiscoveryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        self.solution = self.repo / "libp2p/Libp2p.slnx"
        self.solution.parent.mkdir()

    def project(self, name="Future.Tests", declaration="<EnableNUnitRunner>true</EnableNUnitRunner>"):
        path = self.solution.parent / name / (name + ".csproj")
        path.parent.mkdir(parents=True)
        path.write_text(f"<Project><PropertyGroup>{declaration}</PropertyGroup></Project>")
        return path.relative_to(self.solution.parent).as_posix()

    def solution_with(self, *projects):
        self.solution.write_text("<Solution>" + "".join(f'<Project Path="{p}" />' for p in projects) + "</Solution>")

    def test_current_inventory_and_support_library_exclusion(self):
        paths = discover(REPO, REPO / "libp2p/Libp2p.slnx")
        self.assertEqual(sorted(set(paths)), paths)
        self.assertFalse(any("TestsBase" in p for p in paths))
        # Pin today's twenty suites while allowing additional suites to be discovered automatically.
        names = ["Core.Tests", "E2eTests", "Samples.Perf.Tests"]
        names += ["Protocols." + name + ".Tests" for name in (
            "AutoTls", "IpTcp", "KadDht", "Mplex", "Multistream", "Noise", "Ping", "Plaintext",
            "Pubsub", "Quic", "RequestResponse", "Tls", "WebRtc", "WebSockets", "Yamux")]
        names += ["Protocols.Pubsub.E2eTests", "Protocols.PubsubPeerDiscovery.E2eTests"]
        expected = {f"Libp2p.{name}/Libp2p.{name}.csproj" for name in names}
        self.assertEqual(20, len(expected))
        self.assertTrue(expected.issubset(paths), expected - set(paths))

    def test_future_suite_spaces_duplicate_and_sorted_paths(self):
        second = self.project("Z suite.E2eTests", "<IsTestProject>True</IsTestProject>")
        first = self.project()
        self.solution_with(second, first, second)
        self.assertEqual([first, second], discover(self.repo, self.solution))

    def test_support_generated_output_and_submodules_excluded(self):
        suite = self.project()
        support = self.project("Support.TestsBase", "")
        for folder in ("bin", "obj", "vendor"):
            path = self.repo / folder / "Omitted.Tests.csproj"
            path.parent.mkdir()
            path.write_text("<Project><IsTestProject>true</IsTestProject></Project>")
        (self.repo / ".gitmodules").write_text('[submodule "vendor"]\npath = vendor\n')
        self.solution_with(suite, support)
        self.assertEqual([suite], discover(self.repo, self.solution))

    def test_nested_git_worktree_and_checkout_excluded(self):
        suite = self.project()
        self.solution_with(suite)
        for marker in ("file", "directory"):
            with self.subTest(marker=marker):
                checkout = self.repo / ".claude/worktrees" / marker
                project = checkout / "libp2p/Fake.Tests/Fake.Tests.csproj"
                project.parent.mkdir(parents=True)
                project.write_text("<Project><IsTestProject>true</IsTestProject></Project>")
                if marker == "file":
                    (checkout / ".git").write_text("gitdir: elsewhere\n")
                else:
                    (checkout / ".git").mkdir()
                self.assertEqual([suite], discover(self.repo, self.solution))

    def test_missing_solution(self):
        with self.assertRaisesRegex(DiscoveryError, "Cannot read"):
            discover(self.repo, self.solution)

    def test_malformed_solution(self):
        for content in ("<Solution>", "<Project />", '<Solution><Project /></Solution>'):
            with self.subTest(content=content):
                self.solution.write_text(content)
                with self.assertRaises(DiscoveryError):
                    discover(self.repo, self.solution)

    def test_empty_selection(self):
        self.solution_with()
        with self.assertRaisesRegex(DiscoveryError, "No test projects"):
            discover(self.repo, self.solution)

    def test_missing_project(self):
        self.solution_with("Missing.Tests.csproj")
        with self.assertRaisesRegex(DiscoveryError, "missing.*Missing.Tests.csproj"):
            discover(self.repo, self.solution)

    def test_malformed_project(self):
        suite = self.project()
        (self.solution.parent / suite).write_text("<Project>")
        self.solution_with(suite)
        with self.assertRaisesRegex(DiscoveryError, "Cannot read"):
            discover(self.repo, self.solution)

    def test_misnamed_runner(self):
        suite = self.project("WrongName")
        self.solution_with(suite)
        with self.assertRaisesRegex(DiscoveryError, "unsupported name.*WrongName"):
            discover(self.repo, self.solution)

    def test_omitted_solution_membership(self):
        self.project()
        self.solution_with()
        with self.assertRaisesRegex(DiscoveryError, "missing from Libp2p.slnx.*Future.Tests"):
            discover(self.repo, self.solution)


def workflow_step(workflow, name):
    content = (REPO / ".github/workflows" / workflow).read_text()
    step = re.search(r"^      - name: " + re.escape(name) + r"\n(.*?)(?=^      - name: |^  [\w-]+:|\Z)", content, re.M | re.S)[1]
    return step, textwrap.dedent(step.split("        run: |\n", 1)[1])


class WorkflowTests(unittest.TestCase):
    def test_shared_discovery_timeouts_and_shell_syntax(self):
        for workflow in ("test.yml", "publish.yml"):
            with self.subTest(workflow=workflow):
                content = (REPO / ".github/workflows" / workflow).read_text()
                self.assertIn('python3 ../.github/scripts/discover_test_projects.py > "$RUNNER_TEMP/test-projects.txt"', content)
                step, script = workflow_step(workflow, "Test")
                self.assertIn("timeout-minutes: 30", step)
                self.assertIn("--minimum-expected-tests 1", script)
                subprocess.run(["bash", "-n"], input=script, text=True, check=True)

    def test_actual_loops_stop_before_next_suite_and_publication(self):
        for workflow in ("test.yml", "publish.yml"):
            for fail in ("first suite.Tests.csproj", ""):
                with self.subTest(workflow=workflow, fail=fail), tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    (root / "test-projects.txt").write_text("first suite.Tests.csproj\nsecond.Tests.csproj\n")
                    command = root / "dotnet"
                    command.write_text('#!/bin/bash\nprintf "%s\\n" "$2" >> "$RUNNER_TEMP/trace"\nif [[ "$2" == "$FAIL_PROJECT" ]]; then exit 23; fi\n')
                    command.chmod(0o755)
                    script = workflow_step(workflow, "Test")[1] + '\necho publish >> "$RUNNER_TEMP/trace"\n'
                    env = dict(os.environ, RUNNER_TEMP=directory, BUILD_CONFIG="Release", FAIL_PROJECT=fail,
                               PATH=directory + os.pathsep + os.environ["PATH"])
                    result = subprocess.run(["bash", "-c", script], env=env, capture_output=True, text=True)
                    self.assertEqual(23 if fail else 0, result.returncode, result.stderr)
                    expected = ["first suite.Tests.csproj"] if fail else ["first suite.Tests.csproj", "second.Tests.csproj", "publish"]
                    self.assertEqual(expected, (root / "trace").read_text().splitlines())

    def test_failed_discovery_stops_before_later_steps(self):
        for workflow in ("test.yml", "publish.yml"):
            with self.subTest(workflow=workflow), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                command = root / "python3"
                command.write_text("#!/bin/bash\nexit 17\n")
                command.chmod(0o755)
                script = workflow_step(workflow, "List test projects")[1] + '\ntouch "$RUNNER_TEMP/publish"\n'
                env = dict(os.environ, RUNNER_TEMP=directory, PATH=directory + os.pathsep + os.environ["PATH"])
                result = subprocess.run(["bash", "-c", script], env=env, capture_output=True, text=True)
                self.assertEqual(17, result.returncode)
                self.assertFalse((root / "publish").exists())


if __name__ == "__main__":
    unittest.main()
