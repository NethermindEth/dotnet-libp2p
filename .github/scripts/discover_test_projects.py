# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: MIT

"""List CI test runners and reject omissions from the solutions/naming contract.

This checks filenames and explicit project declarations, not evaluated MSBuild.
Imported or alternate runner declarations require updating this contract.
"""

import argparse
import configparser
import os
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


class DiscoveryError(ValueError):
    pass


def read_xml(path):
    try:
        return ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        raise DiscoveryError(f"Cannot read {path}: {error}") from error


def is_test_name(path):
    return path.name.endswith((".Tests.csproj", ".E2eTests.csproj"))


def walk_error(error):
    raise DiscoveryError(f"Cannot inspect repository projects: {error}") from error


def solution_paths(repo, solutions=None):
    if solutions is None:
        solutions = [repo / "Libp2p.slnx", *sorted((repo / "solutions").glob("*.slnx"))]
    elif isinstance(solutions, Path):
        solutions = [solutions]
    return tuple(Path(solution).resolve() for solution in solutions)


def solution_members(repo, solutions):
    members = set()
    for solution in solutions:
        root = read_xml(solution)
        if root.tag != "Solution":
            raise DiscoveryError(f"Expected Solution root in {solution}")
        for project in root.iter("Project"):
            value = project.get("Path")
            if not value or "\n" in value or "\r" in value:
                raise DiscoveryError(f"Missing or invalid project Path in {solution}")
            path = (solution.parent / value.replace("\\", "/")).resolve()
            if not path.is_relative_to(repo) or not path.is_file():
                raise DiscoveryError(f"Solution project is missing or outside repository: {value}")
            members.add(path)
    return members


def discover(repo, solution=None):
    repo = repo.resolve()
    solutions = solution_paths(repo, solution)
    members = solution_members(repo, solutions)
    names = ", ".join(solution.name for solution in solutions)

    # Do not inspect generated output or third-party submodule projects.
    submodules = set()
    manifest = repo / ".gitmodules"
    if manifest.exists():
        config = configparser.ConfigParser(interpolation=None)
        try:
            config.read_string(manifest.read_text())
            submodules = {(repo / config[section]["path"]).resolve() for section in config.sections()}
        except (OSError, configparser.Error, KeyError) as error:
            raise DiscoveryError(f"Cannot read {manifest}: {error}") from error

    candidates = set()
    errors = []
    for directory, children, files in os.walk(repo, onerror=walk_error):
        # A nested .git entry marks a separate checkout, including linked worktrees.
        children[:] = sorted(child for child in children
                             if child not in {".git", "bin", "obj"}
                             and not (Path(directory) / child / ".git").exists()
                             and (Path(directory) / child).resolve() not in submodules)
        for filename in sorted(files):
            if not filename.endswith(".csproj"):
                continue
            path = (Path(directory) / filename).resolve()
            if not path.is_relative_to(repo):
                raise DiscoveryError(f"Project is outside repository: {path}")
            project = read_xml(path)
            runner = any(element.tag.rsplit("}", 1)[-1] in {"EnableNUnitRunner", "IsTestProject"}
                         and (element.text or "").strip().lower() == "true"
                         for element in project.iter())
            named = is_test_name(path)
            if not named and not runner:
                continue
            label = path.relative_to(repo).as_posix()
            if "\n" in label or "\r" in label:
                errors.append(f"Project path cannot contain a newline: {label!r}")
            if runner and not named:
                errors.append(f"Test runner has unsupported name: {label}; use .Tests.csproj or .E2eTests.csproj")
            if path not in members:
                errors.append(f"Test project missing from {names}: {label}")
            candidates.add(path)

    selected = {path for path in members if is_test_name(path)}
    if selected != candidates:
        for path in sorted(selected - candidates):
            errors.append(f"Solution test project is outside the repository inventory: {path}")
    if not selected:
        errors.append(f"No test projects selected from {names}")
    if errors:
        raise DiscoveryError("\n".join(errors))
    return sorted(path.relative_to(repo).as_posix() for path in selected)


def ci_projects(repo, solutions=None):
    repo = repo.resolve()
    solutions = solution_paths(repo, solutions)
    # The build list must enforce the same runner inventory before restore/build.
    discover(repo, solutions)
    return sorted(path.relative_to(repo).as_posix() for path in solution_members(repo, solutions)
                  if path.relative_to(repo).as_posix() != "samples/maui-chat/MauiChat.csproj")


def main():
    repo = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("solution", nargs="*", type=Path,
                        help="Solutions to inspect; defaults to Libp2p.slnx and solutions/*.slnx")
    parser.add_argument("--all-projects", action="store_true",
                        help="List CI build projects (excluding MAUI) instead of test runners")
    args = parser.parse_args()
    try:
        projects = (ci_projects if args.all_projects else discover)(repo, args.solution or None)
    except DiscoveryError as error:
        print(error, file=sys.stderr)
        return 1
    print("\n".join(projects))
    return 0


if __name__ == "__main__":
    sys.exit(main())
