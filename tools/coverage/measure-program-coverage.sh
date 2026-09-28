#!/usr/bin/env bash
# SPEC-3.1.0.md F7-5: Program.cs (all partial files) line coverage >= 65%, measured
# at the class level (see measure-program-coverage.py) so splitting the type across
# files cannot game the number.
#
# Runs the same test filter as the interim gate (R11: Kabul!=Regresyon&Kabul!=RetrieveSet
# &Category!=IntentionalRed) with coverage collection on, then parses the Cobertura
# output and exits nonzero below the threshold. Coverlet's collector propagates
# instrumentation to child oom.exe processes started by the acceptance harness (measured:
# this is why the total is far above what tests/Oom.Tests/Kabul/ProgramKapsamKabul.cs's
# in-process calls alone reach), so this always runs the whole filtered suite, not one
# class, to get an honest number.
#
# Usage: tools/coverage/measure-program-coverage.sh [threshold]
#   threshold defaults to 65 (SPEC-3.1.0.md F7-5). Never lower this to make a run pass;
#   report a red result instead (Binding rules: changing an acceptance threshold is
#   forbidden).
set -euo pipefail

THRESHOLD="${1:-65}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RESULTS_DIR="$(mktemp -d)"
FILTER='Kabul!=Regresyon&Kabul!=RetrieveSet&Category!=IntentionalRed'

echo "+ dotnet test Oom.sln -c Release --filter \"$FILTER\" --collect:\"XPlat Code Coverage\" --results-directory \"$RESULTS_DIR\""
(cd "$REPO_ROOT" && dotnet test Oom.sln -c Release --filter "$FILTER" --collect:"XPlat Code Coverage" --results-directory "$RESULTS_DIR")

COBERTURA="$(find "$RESULTS_DIR" -maxdepth 3 -name 'coverage.cobertura.xml' -print -quit)"
if [[ -z "$COBERTURA" ]]; then
    echo "kapsama: coverage.cobertura.xml uretilmedi ($RESULTS_DIR altinda)" >&2
    exit 1
fi
echo "+ python \"$REPO_ROOT/tools/coverage/measure-program-coverage.py\" \"$COBERTURA\" --threshold $THRESHOLD --class-name Oom.Program"
python "$REPO_ROOT/tools/coverage/measure-program-coverage.py" "$COBERTURA" --threshold "$THRESHOLD" --class-name Oom.Program
