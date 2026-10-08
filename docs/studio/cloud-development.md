# Linux cloud source and static checks

This candidate adds an offline source and documentation lane. Shell Studio is
still a Windows 11 x64 WPF/.NET 10 application. Its native language DLL, Explorer
extension, tool operations, UI, and installer require verification on the Windows
workstation. No supported Linux application or portable managed test suite is claimed.
The existing [Windows build instructions](build-and-run.md) remain authoritative.

## Source selection and review status

Prepared from `codex/add-context-menu-customization-gui` at
`65df0e4f2ad4590c9964b9ab810bc094797b81af`, verified by a normal origin fetch on
2026-10-01. It was five commits ahead and three behind main at
`2f288a9c1df9ed1d8f99c4dac76bc68c39a911db`; no merge is part of this setup.
Use a reviewed descendant of this selected feature tip, and pin its full SHA.
The checks reject a different origin, missing ancestry, moved HEAD, wrong working
directory, missing sources, and a dirty installation checkout.

The pre-existing local feature checkout has unpushed commit `0231dbf` and extensive
dirty Studio changes. Its build script adds a native PreviewWorker and its build
documentation records a stopped package synchronization. Those unique changes
are excluded from this candidate and must not be overwritten or silently copied
into the remote feature checkout. Static results here apply only to this line.

## Dependencies and network

Use a Linux image already containing Bash, Git 2.51.1 or newer, and Python 3.10
or newer. Git 2.51.1 is the conservative reviewed documentation baseline for
the offline controls, not a claim about the earliest compatible release. Record
the actual image identity and versions during Linux validation. Python uses only
the standard library; no pip, apt, npm, NuGet, .NET SDK, Visual Studio, WiX,
submodule initialization, downloads, external services, secrets, or credentials
are required by these scripts. Missing tools are blockers to image provisioning,
not invitations to modify the host. There are no services, ports, background
processes, or persistent readiness files.

Set agent internet access off with no package-manager or additional-domain
allowlist. The platform's authorized GitHub repository checkout/refresh may need
`github.com` separately; the setup itself performs no network calls. Do not add
NuGet/Microsoft feeds, donor sites, package registries, or broad network access
for this static lane. Network enforcement belongs to the environment controls;
these scripts do not change firewall, proxy, credential, or security settings.

## Exact manual application

Planned owner application: Sunday, 2026-10-04 at 5:00 PM EDT (21:00 UTC).
First obtain independent candidate review and actual Linux validation. This
Windows-prepared candidate is not ready to publish. It has not been committed,
pushed, merged, or applied to cloud settings. Make the reviewed files available
on the selected feature line only through separately authorized publication.
Do not merge them into main to make setup available.

In the current Cloud UI select only `qwekum/Qwe-Shell` and the approved feature
line. Keep the environment private. Confirm that the checkout contains this
candidate. Set the nonsecret environment variable `QWE_SHELL_CLOUD_REVISION` to
the full reviewed commit SHA that contains these files, not a branch name or an
automatically computed HEAD. All 40 lowercase hex characters are required.
The original source SHA above does not contain the new tooling until it is
reviewed and published; do not paste that old SHA as the final application pin.

Paste this exact **Install script**, executed from the selected checkout root:

```bash
bash tools/cloud/install.sh
```

Paste the complete contents of [Start skill](../../tools/cloud/start.md) into
the **Start skill** field. No standalone skill installation is required. Use
these current fields rather than legacy setup/maintenance script fields. The
[official Cloud environments guide](https://learn.chatgpt.com/docs/environments/cloud-environments#customize-installation-and-startup)
describes Install script and Start skill and explains that repository refresh
does not rerun installation/startup. Therefore run the Start skill checks at each
task start and after refresh; a cached installation PASS cannot validate a new
checkout. Stop and review an unexpected SHA instead of repinning automatically.

Do not publish until both commands succeed on actual Linux at the reviewed
revision and the offline boundary is verified. Save the logs and source diff.
New tasks must recheck their own checkout. Existing tasks retain their own files;
preserve them and do not use republishing as an excuse to discard their state.

## Commands and gate limits

For development, including existing dirty files, run from the checkout root:

```bash
python3 -I -S -B tools/cloud/checks.py --require-linux --expected-head "$QWE_SHELL_CLOUD_REVISION"
python3 -I -S -B tools/cloud/run_tests.py
```

Installation adds `--require-clean` and runs the same regression suite. There is
no package installation. For Windows preparation use `python` and omit
`--require-linux`; the JSON must label `linuxStaticExecution` as `NOTRUN`.
`linuxEnvironmentQualification` remains `NOTRUN` on every host: the static helper
cannot establish the image, Install script, regression execution, or enforced
network boundary by itself. Python must use `-I -S -B`: no checkout/environment
module shadowing, site initialization, or bytecode-cache writes. The runner
loads only the reviewed checks.py and test_checks.py source bytes, without
adding checkout paths to imports or discovering modules. It requires exactly
38 tests, stops on the first failure, and refuses skips or an incomplete count.
Only these reviewed helper edits may execute; an AST result does not approve
dirty helper behavior. Review the exact helper diff before admitting execution.

An explicitly admitted Windows preparation lane may instead run
`python -I -S -B tools/cloud/run_tests.py --prepare-only`. This executes exactly
20 mocked/pure-Python guard tests, with no Git subprocess or child interpreter.
Its receipt labels the full Linux helper suite and Linux qualification NOTRUN.
It cannot replace the full 38-test Linux suite or the Install/Start checks.
The full runner refuses non-Linux execution. Keep preparation runs sequential
with an outer 30-second deadline; obtain current lane admission first.

The runner checks the complete tools/cloud directory before loading helpers.
Only checks.py, run_tests.py, test_checks.py, install.sh, and start.md are
allowed. Additional files (including ignored Python files or caches), package
directories, file links, and directory links are blockers. Preserve unexpected
inputs and request source review; do not delete them to make admission pass.
The trusted entry points themselves must be the reviewed regular source files.
The frozen-source/one-writer contract applies throughout each command; these
checks do not protect against a concurrent adversary replacing reviewed files.

Every Git subprocess ignores ambient `GIT_*` overrides, including repository,
index, object, configuration-injection, replacement, and tracing settings. Its
process-local controls disable lazy fetch, object replacement, optional index
writes, filesystem monitoring, protocol access, and submodule recursion. It
does not read system/global Git config and never adds safe.directory exceptions.
Git metadata must match the root's .git directory or a verified linked-worktree
gitfile/backpointer/common directory. Separate external Git directories,
nonempty object alternates, and grafts are unsupported. Normal linked worktrees
use their own index and the legitimate common object store.
Both default absolute links and Git 2.51.1 `worktree add --relative-paths` links
are supported by the binding logic. A relative administrative `gitdir`
backpointer resolves from its containing directory; a mismatched pointer remains
a blocker. The checker never repairs linking metadata.

The checker refuses configured clean/smudge/process filters before status or
diff can execute them. Sparse checkout/index configuration, assume-unchanged,
and skip-worktree flags are refused in both development and installation so a
dirty receipt cannot silently rely on suppressed verification. It never clears
flags or refreshes/writes an index. Initialized submodules are refused; all
status calls explicitly ignore submodules and no donor/library command runs.
Uninitialized gitlinks remain excluded and unqualified. Missing objects/history
fail offline without fetch, repair, or configuration changes. For clean Install,
the three Python helper files must also match raw pinned Git blob bytes exactly.
The whitespace gate retains external-diff/text-conversion protection. Bash files
are pinned to LF line endings without changing Windows build files.

The regression suite creates and mutates only its own temporary files and Git
repositories, then removes those owned fixtures. It never modifies the admitted
checkout. Fixture origin/base overrides are scoped to regression tests and do
not supply production identity or Linux acceptance evidence.

| Check | Evidence it supplies | Limitation |
| --- | --- | --- |
| Exact checkout, origin, feature ancestry, required files | Scope and checkout identity | Does not verify remote freshness or approve dirty edits |
| Python AST in Studio/cloud tooling | Syntax without importing/running tooling | No native DLL load or script behavior qualification |
| Studio inventory JSON version/object shape | Parseable version 1 inventory objects | No source provenance, grammar acceptance, or coverage qualification |
| Studio/WiX project and XAML XML parsing | Well-formed XML | No MSBuild, XAML compiler, native, or installer validation |
| `git diff --check HEAD` | Whitespace check of tracked staged/unstaged edits | Untracked files are syntax-checked when in the listed groups |
| Bounded 38-test helper suite | Parser/refusal receipts, real-Git hook/transport/import/index regressions and fixture byte preservation | Actual Linux execution remains required; temporary fixture identities never qualify the feature checkout |

Inventory provenance is deliberately unqualified: at the selected source tip,
the recorded hashes for `Verification.cpp` and `Parser.cpp` in property coverage
do not match the corresponding source bytes, including an LF-normalized check.
Retain the recorded inventory hashes as evidence. Do not refresh them merely to
make comparisons pass, regenerate the catalogs in cloud, or weaken the existing
native grammar checks. A hash mismatch requires source review and native corpus
qualification on Windows; changing a provenance hash cannot qualify coverage.
Byte hashes from a Windows CRLF checkout and Git's LF blob can differ even when
the source text is unchanged; record both representations when diagnosing drift.
Do not mistake that line-ending difference for a semantic source change. The
`Verification.cpp` and `Parser.cpp` discrepancy above is not resolved by LF
normalization alone.

The repository lacks `docs/agents/issue-tracker.md`, which blocks the issue
skill's tracker routing; report that discrepancy without publishing an issue.

The nominal `net10.0` Core tests instantiate `NativeLanguage` and directly import
`ShellStudio.Language.dll`. Other tests target `net10.0-windows`, Windows native
tools, or WPF. The function-inventory generator loads that same DLL using ctypes
and writes inventories; other generators also overwrite source artifacts. None
is invoked by cloud preparation. Any future CPU-only managed test lane needs
independent portability evidence and a separately reviewed setup update.

## Required validation and Windows handback

Historical v4b preparation: after the parent confirmed shared-resource tests settled,
the admitted Windows `--prepare-only` subset passed **20 tests, zero skips**, exit
0, at 2026-10-01 05:32 EDT. Python 3.14.7 ran it under `-I -S -B`, with a
30-second outer deadline. The process settled; recorded source, index/config,
and owner-file bytes were unchanged. This is mocked/pure-Python guard evidence
for v4b only; it does not qualify the subsequent R1 source correction. The
independent reviewer held v4b for incorrect relative-backpointer resolution.
After the correction, an admitted focused Windows test passed **one test with
absolute, relative, and mismatched-relative pointer subcases; zero skips**, exit
0, at 2026-10-01 06:08 EDT. Python 3.14.7 ran it under `-I -S -B`, with a
30-second outer deadline; it settled in 0.22 seconds. Git queries were mocked,
and the owned plain-file fixture, source/metadata, prior packets, and 181 recorded
owner hashes remained unchanged. The earlier 20-test subset was not rerun.
The expanded real-Git `--relative-paths` test remains NOT RUN, the full authored
count remains 38, and the same reviewer's new source re-review is still required.
The actual-checkout static helper, full 38-test Linux suite, Linux setup, and
enforced offline Linux qualification remain **NOT RUN**. Windows app/runtime,
build, and installer gates remain **NOT RUN**. Independent acceptance is pending.
The parent must allocate a separate actual Linux testing/reviewer lane. Record subsequent
results against the exact candidate patch and file manifest, with commands,
versions, source SHA, and the testing lane identity. Do not convert Windows
results to Linux PASS or treat a source handoff as independent acceptance.

During uncommitted candidate review, bind evidence to the base SHA plus the exact
patch and file-manifest digests. The development helper permits this dirty
checkout; the Install script must reject it. That expected refusal is not a
successful installation. After candidate review and separate authorization to
make the changes available, validate the clean reviewed descendant using its
new full SHA before qualifying or publishing the cloud environment.

Read repository-wide authored guidance, applicable root/nested AGENTS.md, and
authored docs for the touched/relevant areas. Inspect generated and archived
evidence only when relevant. Keep one writer per file and run checks sequentially
under the current execution-lane admission. The existing Windows guidance also
requires sequential test suites because projects share outputs. No numeric
cloud CPU/RAM budget is established by this candidate, and none is inferred from
the native Windows build's `/m` option. Obtain the applicable execution release
before starting checks; the Windows preparation release above permits only its
bounded pure-Python subset and does not admit application or Linux qualification.

On actual Linux, use a clean reviewed checkout with the feature base in history.
With internet disabled, run Install script and Start skill. Confirm missing or
wrong revision, wrong origin, moved HEAD, dirty install, absent feature history,
and wrong root fail without touching checkout files; run the bounded suite.
Exercise F1-F4's real-Git fsmonitor/missing-promisor-object fixtures, ambient
repository/index/config redirection, legitimate absolute and `--relative-paths`
linked worktrees and a mismatched relative backpointer, modified
assume-unchanged helpers, skip-worktree/sparse states, content filters, and
initialized submodules. Check that markers remain absent and source/index/config
bytes remain unchanged. Exercise root/environment module shadowing, extra and
ignored Python inputs, package initializers, external file/directory links, and
the expected nonzero full test count. Confirm host receipt labels and nonzero
failure exits. These are authored requirements, not observed test results.
Record image digest, Git/Python/Bash versions, full SHA, command exits, output,
before/after file hashes, and enforced network settings. No Linux result is valid
for a later checkout automatically. Keep Windows runtime checks **NOTRUN** there.

Hand source changes back to the Windows workstation for the existing Release/x64
combined native, self-contained Studio/ToolHost, and pinned `WixToolset.Sdk/5.0.2` build using
Visual Studio v145 and .NET 10. Native DLL/Core, tool, WPF, Explorer, installer,
and human acceptance remain the existing Windows gates. Follow their current
authorization and disposable-environment rules; this cloud setup neither runs
nor authorizes those actions. Windows workflow files are unchanged.

Primary evidence retrieved 2026-10-01: [WPF platform boundary](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/),
[Windows targeting error](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1100),
[selected test project](https://github.com/qwekum/Qwe-Shell/blob/65df0e4f2ad4590c9964b9ab810bc094797b81af/src/studio/ShellStudio.Tests/ShellStudio.Tests.csproj),
[native imports](https://github.com/qwekum/Qwe-Shell/blob/65df0e4f2ad4590c9964b9ab810bc094797b81af/src/studio/ShellStudio.Core/NativeLanguage.cs#L105),
and [test calls](https://github.com/qwekum/Qwe-Shell/blob/65df0e4f2ad4590c9964b9ab810bc094797b81af/src/studio/ShellStudio.Tests/Program.cs#L255).

Reviewer-remediation primary evidence retrieved 2026-10-01:
[Git 2.51.1 process-local options/environment](https://raw.githubusercontent.com/git/git/v2.51.1/Documentation/git.adoc),
[filesystem monitor configuration](https://raw.githubusercontent.com/git/git/v2.51.1/Documentation/config/core.adoc),
[index flag tags](https://raw.githubusercontent.com/git/git/v2.51.1/Documentation/git-ls-files.adoc),
[submodule status controls](https://raw.githubusercontent.com/git/git/v2.51.1/Documentation/git-status.adoc),
and [CPython 3.10 isolation/site/bytecode controls](https://raw.githubusercontent.com/python/cpython/3.10/Doc/using/cmdline.rst).

R1 evidence retrieved 2026-10-01: [Git 2.51.1 relative worktree option](https://raw.githubusercontent.com/git/git/v2.51.1/Documentation/git-worktree.adoc)
and [relative administrative-backpointer resolution](https://raw.githubusercontent.com/git/git/v2.51.1/worktree.c#L908).
