#!/usr/bin/env bash
#
# Runs the test suite with coverage and turns the result into a map of where the untested code is.
#
# Nothing about this is part of a normal run or of the pull request build. Coverage is a way of
# finding the next thing worth testing, not a number to defend: there is no threshold here, nothing
# fails on a percentage, and `dotnet test` on its own still neither instruments nor slows down.
# Settings and exclusions are in coverlet.runsettings; read them before believing a figure.
#
# Usage:
#   scripts/coverage.sh                              the whole suite
#   scripts/coverage.sh --filter VmSignalRHandler    any further arguments go to `dotnet test`
#   TOP=50 scripts/coverage.sh                       a longer ranking; the default is 25
#
# A filtered run maps only what those tests reach, and reads as 0% for everything else.
#
# Needs what the suite already needs - a working Docker daemon for the PostgreSQL container - plus
# one local tool, ReportGenerator, pinned in .config/dotnet-tools.json.

set -euo pipefail

cd "$(dirname "$0")/.."

OUT=coverage
RAW=$OUT/raw
REPORT=$OUT/report

# A stale report is worse than none: it reads as current and the file names look right.
rm -rf "$OUT"

dotnet tool restore

# The test failure is not fatal here. A run with red tests still produces coverage, and the map of
# what nothing executed is worth having either way, so the exit code is carried to the end instead.
set +e
dotnet test src/Player.Vm.Api.Tests/Player.Vm.Api.Tests.csproj \
  --settings coverlet.runsettings \
  --collect:"XPlat Code Coverage" \
  --results-directory "$RAW" \
  "$@"
TESTS=$?
set -e

# The collector writes into a per-run guid directory whose name is not knowable in advance. All of
# them, rather than the first: a multi-targeted run would write one per framework.
mapfile -t REPORTS < <(find "$RAW" -name 'coverage.cobertura.xml')
if [ ${#REPORTS[@]} -eq 0 ]; then
  echo "coverage.sh: no coverage file under $RAW - did the test host start?" >&2
  exit 1
fi

# Html to read, TextSummary for the console, JsonSummary for the ranking below, and
# MarkdownSummaryGithub for the workflow's job summary.
dotnet reportgenerator \
  "-reports:$(IFS=';'; echo "${REPORTS[*]}")" \
  "-targetdir:$REPORT" \
  "-reporttypes:Html;TextSummary;JsonSummary;MarkdownSummaryGithub" \
  "-title:Player.Vm.Api" \
  -verbosity:Warning

sed -n '/^Summary/,/^$/p' "$REPORT/Summary.txt"

# The ranking is the point of the script, and it is not the percentage. A percentage answers "how
# much of this class is covered"; the count answers "how much untested code is in it", which is the
# question a reader of this output actually has. The two disagree about where the risk is: 17
# uncovered lines in a 94% Startup are less worth reading than the 200 in a class at 40%.
if command -v jq > /dev/null; then
  echo "Untested lines by class, most first"
  echo
  jq -r '
    .coverage.assemblies[].classesinassembly[]
    | (.coverablelines - .coveredlines) as $untested
    | select($untested > 0)
    | [$untested, .coverage, .name] | @tsv
  ' "$REPORT/Summary.json" |
    sort -rn |
    head -"${TOP:-25}" |
    awk -F'\t' '{ printf "  %5d untested  %5.1f%% covered  %s\n", $1, $2, $3 }'
  echo
  echo "  (TOP=n for more; the report below has all of them)"
  echo
else
  echo "jq is not installed, so the ranked list is skipped. The HTML report has the same figures."
  echo
fi

echo "Full report: $REPORT/index.html"

exit $TESTS
