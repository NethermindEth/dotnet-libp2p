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
        # Pin today's twenty-one suites while allowing additional suites to be discovered automatically.
        names = ["Core.Tests", "E2eTests", "Samples.Chat.Tests", "Samples.Perf.Tests"]
        names += ["Protocols." + name + ".Tests" for name in (
            "AutoTls", "IpTcp", "KadDht", "Mplex", "Multistream", "Noise", "Ping", "Plaintext",
            "Pubsub", "Quic", "RequestResponse", "Tls", "WebRtc", "WebSockets", "Yamux")]
        names += ["Protocols.Pubsub.E2eTests", "Protocols.PubsubPeerDiscovery.E2eTests"]
        expected = {f"Libp2p.{name}/Libp2p.{name}.csproj" for name in names}
        self.assertEqual(21, len(expected))
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
    def run_suites(self, workflow, project, exits):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "test-projects.txt").write_text(project + "\nsecond.Tests.csproj\n")
            command = root / "dotnet"
            command.write_text(textwrap.dedent('''\
                #!/usr/bin/env python3
                import os
                from pathlib import Path
                import sys
                root = Path(os.environ["RUNNER_TEMP"])
                trace = root / "trace"
                previous = trace.read_text().splitlines() if trace.exists() else []
                project = sys.argv[2]
                attempt = previous.count(project)
                with trace.open("a") as output:
                    output.write(project + "\\n")
                print(f"runner output: {project}, attempt {attempt + 1}")
                codes = os.environ["EXIT_CODES"].split(",")
                sys.exit(int(codes[min(attempt, len(codes) - 1)]) if project != "second.Tests.csproj" else 0)
                '''))
            command.chmod(0o755)
            script = workflow_step(workflow, "Test")[1] + '\necho publish >> "$RUNNER_TEMP/trace"\n'
            env = dict(os.environ, RUNNER_TEMP=directory, BUILD_CONFIG="Release",
                       EXIT_CODES=",".join(map(str, exits)), GITHUB_STEP_SUMMARY=str(root / "summary"),
                       PATH=directory + os.pathsep + os.environ["PATH"])
            result = subprocess.run(["bash", "-c", script], cwd=REPO / "libp2p", env=env,
                                    capture_output=True, text=True)
            trace = (root / "trace").read_text().splitlines()
            logs = {p.relative_to(root).as_posix(): p.read_text() for p in root.rglob("*.log")}
            summary = (root / "summary").read_text() if (root / "summary").exists() else ""
            return result, trace, logs, summary

    def test_e2e_failure_recovers_once_with_visible_original_failure(self):
        project = "first suite.E2eTests.csproj"
        for workflow in ("test.yml", "publish.yml"):
            with self.subTest(workflow=workflow):
                result, trace, logs, summary = self.run_suites(workflow, project, [2, 0])
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual([project, project, "second.Tests.csproj", "publish"], trace)
                self.assertIn("::warning::", result.stdout)
                self.assertIn("exit 2", summary)
                self.assertIn("passed on retry", summary)
                first = "test-results/1-first suite.E2eTests.csproj/attempt-1.log"
                second = "test-results/1-first suite.E2eTests.csproj/attempt-2.log"
                self.assertIn("attempt 1", logs[first])
                self.assertIn("attempt 2", logs[second])

    def test_persistent_e2e_failure_remains_fatal_after_one_retry(self):
        project = "first.E2eTests.csproj"
        for workflow in ("test.yml", "publish.yml"):
            with self.subTest(workflow=workflow):
                result, trace, logs, summary = self.run_suites(workflow, project, [2, 2])
                self.assertEqual(2, result.returncode, result.stderr)
                self.assertEqual([project, project], trace)
                self.assertEqual(2, len(logs))
                self.assertIn("failed after retry", summary)

    def test_only_e2e_test_failures_are_retried(self):
        for workflow in ("test.yml", "publish.yml"):
            for project, code in [("first.Tests.csproj", 2)] + [
                    ("first.E2eTests.csproj", code) for code in (1, 3, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 124)]:
                with self.subTest(workflow=workflow, project=project, code=code):
                    result, trace, _, _ = self.run_suites(workflow, project, [code, 0])
                    self.assertEqual(code, result.returncode, result.stderr)
                    self.assertEqual([project], trace)

    def test_retry_with_runner_error_remains_fatal(self):
        for workflow in ("test.yml", "publish.yml"):
            with self.subTest(workflow=workflow):
                result, trace, _, _ = self.run_suites(workflow, "first.E2eTests.csproj", [2, 8])
                self.assertEqual(8, result.returncode, result.stderr)
                self.assertEqual(["first.E2eTests.csproj"] * 2, trace)

    def test_shared_discovery_timeouts_and_shell_syntax(self):
        for workflow in ("test.yml", "publish.yml"):
            with self.subTest(workflow=workflow):
                content = (REPO / ".github/workflows" / workflow).read_text()
                self.assertIn('python3 ../.github/scripts/discover_test_projects.py > "$RUNNER_TEMP/test-projects.txt"', content)
                step, script = workflow_step(workflow, "Test")
                self.assertIn("timeout-minutes: 30", step)
                self.assertIn('bash ../.github/scripts/run_test_projects.sh "$RUNNER_TEMP/test-projects.txt"', script)
                runner = (REPO / ".github/scripts/run_test_projects.sh").read_text()
                self.assertIn("--minimum-expected-tests 1", runner)
                self.assertIn("Zero tests already fail with exit 8", runner)
                subprocess.run(["bash", "-n"], input=script, text=True, check=True)
                subprocess.run(["bash", "-n"], input=runner, text=True, check=True)
                upload = re.search(r"^      - name: Upload test logs\n(.*?)(?=^      - name: |^  [\w-]+:|\Z)",
                                   content, re.M | re.S)[1]
                self.assertIn("if: always()", upload)
                self.assertIn("path: ${{ runner.temp }}/test-results", upload)
                self.assertNotIn("continue-on-error", content)
                if workflow == "publish.yml":
                    for name in ("NuGet login", "Publish"):
                        step = re.search(r"^      - name: " + name + r"\n(.*?)(?=^      - name: |\Z)",
                                         content, re.M | re.S)[1]
                        self.assertNotIn("if:", step)  # Preserve the implicit success gate after artifact upload.

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
                    result = subprocess.run(["bash", "-c", script], cwd=REPO / "libp2p", env=env,
                                            capture_output=True, text=True)
                    self.assertEqual(23 if fail else 0, result.returncode, result.stderr)
                    expected = ["first suite.Tests.csproj"] if fail else ["first suite.Tests.csproj", "second.Tests.csproj", "publish"]
                    self.assertEqual(expected, (root / "trace").read_text().splitlines())

    def test_empty_list_and_log_write_failures_are_fatal(self):
        runner = REPO / ".github/scripts/run_test_projects.sh"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            projects = root / "test-projects.txt"
            env = dict(os.environ, RUNNER_TEMP=directory, BUILD_CONFIG="Release",
                       PATH=directory + os.pathsep + os.environ["PATH"])
            for content in ("", "\n"):
                with self.subTest(content=content):
                    projects.write_text(content)
                    result = subprocess.run(["bash", str(runner), str(projects)], env=env,
                                            capture_output=True, text=True)
                    self.assertEqual(1, result.returncode)
            projects.write_text("first.E2eTests.csproj\n")
            (root / "dotnet").write_text("#!/bin/bash\necho run >> \"$RUNNER_TEMP/trace\"\nexit 0\n")
            (root / "tee").write_text("#!/bin/bash\ncat >/dev/null\nexit 28\n")
            (root / "dotnet").chmod(0o755)
            (root / "tee").chmod(0o755)
            result = subprocess.run(["bash", str(runner), str(projects)], env=env,
                                    capture_output=True, text=True)
            self.assertEqual(28, result.returncode)
            self.assertEqual(["run"], (root / "trace").read_text().splitlines())

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
