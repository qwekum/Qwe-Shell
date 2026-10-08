#!/usr/bin/env bash
# Offline static lane; regressions use only owned temporary fixtures. No downloads.
set -euo pipefail
command -v git >/dev/null || { echo 'BLOCKED: Git 2.51.1+ is required.' >&2; exit 1; }
command -v python3 >/dev/null || { echo 'BLOCKED: Python 3.10+ is required.' >&2; exit 1; }
: "${QWE_SHELL_CLOUD_REVISION:?Set the reviewed full checkout commit SHA.}"
python3 -I -S -B tools/cloud/checks.py --require-linux --require-clean \
  --expected-head "$QWE_SHELL_CLOUD_REVISION"
python3 -I -S -B tools/cloud/run_tests.py
