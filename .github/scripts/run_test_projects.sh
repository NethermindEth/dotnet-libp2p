#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: MIT

set -euo pipefail

if [[ $# -ne 1 || ! -s "$1" ]]; then
  printf '%s\n' 'Expected a nonempty test-project list.' >&2
  exit 1
fi

results="$RUNNER_TEMP/test-results"
mkdir -p "$results"

report() {
  printf '%s\n' "$1" | tee -a "$results/summary.md"
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    printf '%s\n' "$1" >> "$GITHUB_STEP_SUMMARY"
  fi
}

suite=0
while IFS= read -r project || [[ -n "$project" ]]; do
  if [[ -z "$project" ]]; then
    printf '%s\n' 'Empty test-project entry.' >&2
    exit 1
  fi
  suite=$((suite + 1))
  directory="$results/$suite-${project##*/}"
  mkdir -p "$directory"
  printf '%s\n' "$project" > "$directory/project.txt"
  for attempt in 1 2; do
    # Capture both statuses: a failed log write must not make a failed run look successful.
    set +e
    # Zero tests already fail with exit 8; minimum 1 records intent and parity.
    dotnet test "$project" -c "$BUILD_CONFIG" --no-build -- --minimum-expected-tests 1 \
      2>&1 | tee "$directory/attempt-$attempt.log"
    statuses=("${PIPESTATUS[@]}")
    set -e
    if [[ "${statuses[1]}" -ne 0 ]]; then
      exit "${statuses[1]}"
    fi
    status="${statuses[0]}"
    if [[ "$status" -eq 0 ]]; then
      if [[ "$attempt" -eq 2 ]]; then
        report "- $project passed on retry; the original failed attempt is retained in the test logs."
      fi
      break
    fi
    # MTP exit 2 means test failures. Do not retry discovery, runner, minimum-count or timeout errors.
    if [[ "$project" == *.E2eTests.csproj && "$status" -eq 2 && "$attempt" -eq 1 ]]; then
      printf '::warning::%s failed with exit 2; retrying the complete E2E suite once.\n' "$project"
      report "- $project failed with exit 2 on attempt 1; retrying once."
    else
      report "- $project failed $([[ "$attempt" -eq 2 ]] && printf 'after retry' || printf 'without retry') (exit $status)."
      exit "$status"
    fi
  done
done < "$1"
