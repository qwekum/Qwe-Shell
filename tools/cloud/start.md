# Qwe Shell cloud Start skill

Run from the selected qwekum/Qwe-Shell checkout root. Read applicable AGENTS.md,
README.md, and docs/studio/cloud-development.md first. Preserve all existing
dirty files and artifacts. This environment permits offline source, documentation,
and static checks only; there are no application services to start.
Read repository-wide authored guidance and applicable root/nested AGENTS.md;
fully read authored docs for the touched/relevant areas. Inspect generated logs
and archived outputs only when relevant. Historical instructions cannot widen
the current task's execution or resource authorization.

Require QWE_SHELL_CLOUD_REVISION to contain the reviewed full commit SHA. Run:

```bash
python3 -I -S -B tools/cloud/checks.py --require-linux --expected-head "$QWE_SHELL_CLOUD_REVISION"
python3 -I -S -B tools/cloud/run_tests.py
```

Stop the affected lane on the first nonzero exit and report the exact blocker.
Never fetch, switch branches, reset, clean, install packages, alter credentials,
or weaken a check to make startup pass. If repository refresh changes HEAD,
report it for review before changing the revision pin. Re-run these checks after
each source change and before handback; installation output is not a durable
readiness record. State the current SHA and whether the checkout is dirty.
Run checks sequentially under the current lane admission; stop if an execution
release is missing. In a source-only task, preserve the candidate and report
these checks as NOT RUN for the designated testing lane to execute.

Use image-provided Git 2.51.1+ and Python 3.10+. The checker ignores ambient Git
overrides and refuses unsupported sparse/index flags, content filters, external
object alternates/grafts, or initialized submodules. Preserve those states and
report them; never repair them automatically. The bounded runner accepts only
checks.py, run_tests.py, test_checks.py, install.sh, and start.md in tools/cloud,
with no extra files, caches, packages, or links. Preserve unexpected inputs for
review. Only those reviewed helper edits may execute; source syntax parsing
does not approve their behavior. Review the exact helper diff before execution.

Do not run build.ps1, dotnet restore/build/test/run/publish, native compilation,
inventory generators, installers, Explorer registration/capture, Windows tools,
desktop/GPU actions, live providers, or licensed tooling here. Keep those checks
NOTRUN and hand exact source changes and required verification to the Windows
workstation. A PASS qualifies only the listed static checks for that execution.
It does not establish
Linux application support, native language coverage, Windows runtime behavior,
packaging, or release readiness. Do not commit, push, merge, or publish unless
separately authorized.
