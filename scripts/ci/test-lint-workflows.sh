#!/usr/bin/env bash
# Self-test of the workflow linter: a good workflow passes, and each rule rejects a workflow that breaks only that rule.
set -euo pipefail
L="$(dirname "$0")/lint-workflows.rb"
D=$(mktemp -d); trap 'rm -rf "$D"' EXIT
SHA=0123456789abcdef0123456789abcdef01234567
good() { cat <<Y
name: t
on: { push: { branches: [main] } }
permissions: { contents: read }
jobs:
  a:
    runs-on: ubuntu-24.04
    timeout-minutes: 5
    steps:
      - uses: actions/checkout@$SHA # v4
      - run: echo hello
Y
}
expect() { # description, expected exit, perl expression applied to the good workflow
  mkdir -p "$D/w"; rm -f "$D/w"/*.yml; good | perl -pe "$3" > "$D/w/t.yml"
  set +e; "$L" "$D/w" >/dev/null 2>&1; local rc=$?; set -e
  [[ $rc -eq $2 ]] || { echo "FAIL linter: $1 (exit $rc, wanted $2)" >&2; exit 1; }
  echo "ok   linter: $1"
}
expect "a good workflow passes" 0 's/x/x/'
expect "unpinned action is refused" 1 "s/\\@$SHA # v4/\\@v4/"
expect "missing timeout is refused" 1 '$_="" if /timeout-minutes/'
expect "missing permissions is refused" 1 '$_="" if /^permissions/'
expect "pull_request_target is refused" 1 's/push: \{ branches: \[main\] \}/pull_request_target: {}/'
expect "script injection through the event payload is refused" 1 's/echo hello/echo \${{ github.event.pull_request.title }}/'
expect "missing script is refused" 1 's#echo hello#scripts/does-not-exist.sh#'
expect "unknown needs is refused" 1 's/runs-on: ubuntu-24.04/runs-on: ubuntu-24.04\n    needs: [ghost]/'
