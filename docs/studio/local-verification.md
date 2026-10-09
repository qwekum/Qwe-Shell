# Local verification — 2026-09-12

## Source-backed actual-menu editing and automatic semantic capture — 2026-09-12 ET

The current source replaces the decorative entry relationship view with entry
details bound to real configuration properties and expression spans. Captured
native entries expose evaluated rule, property, setting, source, and
completeness evidence. Quick native edits create or reuse a durable,
context-scoped managed rule; source-linked shared rules remain a separate,
explicit edit action. Disabled `settings.modify` gates suppress the affected
edit actions, and hidden or removed entries remain available through the
evidence toggle.

The native extension now uses its normal popup-entry constructor to materialize
retained submenu definitions for capture. Commands are never invoked during
this traversal. Assignments, variable-mutating loops, and functions outside a
positive read-only allowlist stop the affected branch and publish an explicit
incomplete diagnostic. Depth, item, evaluation, time, transport, and evidence
limits likewise fail closed. Imported declarations for which the legacy parser
cannot retain an exact import occurrence are shown but are not exposed as
editable source locations.

Current local checks passed:

| Check | Result | Boundary |
| --- | --- | --- |
| Core and exported native parser | 66 passed | Model/file fixtures, source association, settings gates, durable rules, native syntax |
| WPF | 58 passed | Offscreen interaction, evidence, source-bound editing, hidden/import rows, preview-read request coverage, layout and themes |
| Integrated tools | Passed | In-memory providers and task-owned fixtures |
| Native resources | 4 passed | Windows resource APIs on a task-owned PE copy |
| Preview worker protocol and lifecycle | 19 passed | Managed framing, read isolation, policy, cancellation, and owned fixture processes |
| Native preview semantics | 75 passed | Native language evaluation with supplied facts, canonical registry aliases, and exact brokered read identities; no live registry dependency |
| Native capture serialization | Passed | C++ wire contract, evidence limits, source ambiguity, and automatic traversal coordinator |
| Native construction probes | 20 passed | Offscreen native-language construction through the explicit language DLL |

The Release/x64/v145 build compiled `shell.dll`, `shell.exe`, the language DLL,
the preview worker, Studio, and ToolHost. Final package synchronization stopped
when it reached the repository's existing `bin/shell.dll`, which was loaded by
Explorer. Explorer was left running and that loaded file was not replaced.
Tests used the newly compiled artifacts under `src/studio/artifacts/native/`.
This is therefore compilation evidence, not a completed package or installer
build for these changes.
The default developer output at
`src/studio/native/bin/Release/x64/ShellStudio.Language.dll` was refreshed
separately and passed the same 75 native preview semantic checks.

The final offscreen render matrix is retained in
`src/studio/artifacts/checks/ui-capture-source-20260912-final-r2/`, including light,
dark, and minimum-size entry-detail views. The generated language inventories
record 1,605 functions (452 callables and 1,153 values), 78 menu-property forms,
and 172 theme/settings paths against the current native sources.

No installation, registration, Explorer restart, live context-menu capture,
system mutation, commit, push, or GitHub Actions run was performed. Automatic
third-party provider materialization, exact popup appearance, physical-monitor
scaling, assistive technology, and human acceptance remain unqualified.

The prior 2026-09-11 Windows x64 application and MSI were rebuilt after the
actual-menu, localization, and folder-thumbnail changes below. They predate the
2026-09-12 source changes above. The approved plan is **not fully qualified**:
Explorer, remaining installer cases, system-operation parity, runtime-language
equivalence, and human acceptance remain pending in [acceptance.md](acceptance.md).

For portable use, replacement of an existing normal Shell installation, backup,
and rollback, follow [Install and use Shell Studio](using-shell-studio.md). The
most recently produced MSI is an unsigned historical development artifact;
verify its hash below before running it. It predates the current source changes,
and its older-version upgrade path has not been qualified.

## Capture-listener diagnosis — 2026-09-11 ET

A deliberately occupied current-user/session pipe reproduced a lifecycle defect:
listener creation failed, but the previous client inferred active state from a
non-null cancellation source and the window could overwrite the failure with
**Stop capture** and a waiting message. Current source creates the first pipe
synchronously, exposes listener-task completion through `IsListening`, returns
the start result to the window, and reports `CAPTURE_LISTENER` with single-
instance recovery guidance.

The focused Release build passed with zero warnings and errors. The offscreen WPF
suite passed **49 of 49**, including the new listener-collision regression and
the existing managed original/final capture exchange. The native capture
serialization executable passed, including a real native worker handshake and
snapshot frame. In a live local diagnostic, the registered extension completed
the ready/original/final exchange with a bounded server, and the real Studio
window separately accepted a valid native-protocol final snapshot and changed to
captured state.

These checks isolate listener acquisition, framing, native publication, managed
validation, and WPF receipt, but they do not record a post-fix human-driven
Explorer-to-Studio capture. The combined package/MSI was not rebuilt after this
focused source fix, so the artifact hashes and full-build counts below describe
the immediately preceding package and must not be cited as containing the
listener change.

A read-only host check at 2026-09-11 22:29 ET found Defender antivirus,
real-time, behavior, IOAV, NIS, on-access, and tamper protection enabled,
signatures current, and no Defender Operational event 5001 in the preceding
three days. No Defender setting, service, task, or protection-history data was
changed by that diagnosis. This is a time-bounded host observation, not general
Windows Security qualification.

## Native-authoring integrated package — 2026-09-11 UTC

The current implementation source passed the full Release/x64
`src/studio/build.ps1` pipeline in **64.31 seconds** of whole build-command wall
time, excluding the
test runs below. The final WiX build reported zero warnings and errors.

- Installer: `bin/setup-x64.msi` (54,926,640 bytes), SHA-256
  `9ED834C1B0133689F09D443017EEA1C5554BA7D1272BC98F938A03F4F3D0461A`.
- Native Shell DLL: SHA-256
  `308B8BC9AE1576F4B0EBF561732D9C75791898378054612D09DA17ED583F3F9E`.
- Studio assembly: SHA-256
  `2C46327668153AD185B4BF4A29DA0213BA49D6E8D04B114128FA753258320F82`.
- Packaged language DLL: SHA-256
  `F6A9FCAF3631AC198F79838F6F6FD7941E7D9928AC70BF54EF1BE892A035BF22`.
- Packaged preview worker: SHA-256
  `6DDDF1BD437B25A10D77C336D4718DC76FF7103E2DB583D6B0A87C561BC96DE9`.

The directly relevant integrated checks all pass: 57 Core, 48 offscreen UI,
69 Tools, 11 preview protocol/broker, seven preview-worker lifecycle, six
template, four native-resource, and two transaction checks. Against the exact
built language DLL, 70 semantic, 18 construction, eight rendering, and 14
adversarial checks pass. The real native worker passes three cases, including
composed-window ownership/transparency and owner-pipe shutdown. Focused native
executables for capture serialization, row/state rendering, resources,
selection, language JSON, and Debug/Release expression syntax also pass.

These results supersede the historical checkpoint failures in the completion
ledger. They establish compilation and deterministic local/native process
contracts, not installed-package lifecycle, Explorer apply/recapture, physical
monitor DPI, assistive-technology, donor Windows effects, CI, release, or human
acceptance. The exact-package disposable-guest run is recorded separately when
complete; no package was installed or registered on the host.

## MSI rebuild — 2026-09-11

The full `src/studio/build.ps1` pipeline passed with zero warnings and errors
in 55.07 seconds of build-command wall time. `bin/setup-x64.msi` is 51.96 MiB.
WiX decompilation and extraction verified all 635 installed payload files
against the current build outputs by SHA-256, including Studio, ToolHost,
native libraries, runtimes and license notices. The embedded custom-action
binary also matches the rebuilt `ca.dll`.

MSI SHA-256: `AFBC97309A521D1F5233F4E1A3F2026E8A3503525B3B34A163F5CDC349020C6A`.
Logs, extracted payload verification and the previous MSI are preserved under
`src/studio/artifacts/checks/msi-rebuild-20260911-185051/`.
No installation, registration, upgrade, repair or uninstall was run; this
package check does not establish installer lifecycle or human acceptance.

## Folder thumbnail setting

The portable build now includes **Integrated tools → Folder thumbnail style**,
with Full size / Default choices, actual resource-state inspection, built-in
attributed masks, stale-preview rejection, and guarded protected-file recovery.
`build.ps1 -SkipInstaller` passed in 13.65 seconds of build-command wall time;
this did not rebuild the MSI or change the host desktop.

- 49 tool checks passed, including actual Windows-resource copies, unchanged
  non-target resources, hardlink sibling bytes, exact recovery, stale recovery,
  locked targets, cancellation, no-op permissions, and the setting service.
- 45 offscreen WPF checks passed, including the single setting, retained drafts,
  late inspection responses, and visible inspection errors.
- In the disposable guest, Full size, Default, and repeated application passed.
  The protected file's owner, group and DACL were preserved exactly. A no-delete
  sharing lock returned Windows error 32 without changing bytes or security.
  Protected recovery restored the exact pre-operation bytes and DACL.
- The final optional Explorer refresh was blocked by the existing process
  ancestry verification guard. The mask remained applied and the error was
  reported separately. This run does not qualify automatic Explorer restart;
  leave refresh unchecked and sign out/in when that guard cannot verify the shell.
- Creating a second hardlink to the guest's protected resource was denied.
  Hardlink preservation is therefore established by the copied-file test and
  source review, not a protected guest hardlink fixture.

Evidence is retained under `Sandbox/20260911-thumbnail-setting/evidence/`,
including failed experiments and their recovery, final result JSON, journals,
and the staged input manifest. Build and managed logs are under
`src/studio/artifacts/checks/folder-thumbnail-*`. The final resource writer does
not take ownership or temporarily grant access to the original file or its
directory. Its metadata snapshot covers owner/group/DACL, attributes and
creation/write times; SACL and last-access preservation are not claimed.

These checks do not establish installer lifecycle, Windows-update lifecycle,
human thumbnail appearance, or release qualification.

## Native-rendered captured previews — 2026-09-11 UTC

The portable Release/x64 build now publishes appearance v2 from Shell's native
row, background, frame, shadow, and scroll-arrow painters. Production appearance
capture no longer copies desktop pixels, waits for the compositor, or replays
foreign owner-draw callbacks. Studio displays premultiplied pixels with mapped
row hit targets, labels them **Native-rendered preview**, and keeps unsaved edits
on the structural fallback. Mnemonic display removes markers and preserves
escaped ampersands without changing raw titles or source.

Capture epochs guard both successful publication and failure reporting across
cancellation/reconnection. The bitmap surface preserves skipped disabled-row
redraws. Visible-row composition converts the measured client origin to window
coordinates before clipping; a live Sandbox comparison caught and corrected
a 14-pixel scroll offset that the initial bounds-only assertions missed.

| Check | Result | Boundary |
| --- | --- | --- |
| Final portable build, `-SkipInstaller` | Passed; 15.16 seconds | Whole build-command wall time; excludes tests |
| Core and exported native parser | 46 passed, 0 failed | Local model/file/parser checks |
| WPF capture and preview | 43 passed, 0 failed | Protocol fixtures, alpha/provenance, hit targets, stale imagery and mnemonic handling |
| Native row state/effect tests | 10 passed | Shared state planning and unchanged item storage |
| Native row DIB tests | 3 passed | Original callback/sink, translated presentation, skipped redraw, oversize fallback and GDI cleanup |
| Five consecutive root captures at 96 DPI | 5 passed | Final build, real guest Explorer, 23 fixture root rows, native pixels and Studio hit selection |
| Shipped submenu at 96 DPI | Passed | Pin/Unpin native image, Studio navigation, and child hit selection |
| Rendering-state fixture at 96 DPI | Passed | Normal, checked, disabled, icon, separator, mnemonic and Unicode source; preview command sentinel absent |
| Scrolling fixture at 96 DPI | Passed | 90 entries; 39 clipped hit rows, full first row, final 4-pixel partial row, arrow-gutter exclusion and child selection |
| Configuration preservation | 24 of 24 hashes unchanged | Host package configuration compared with the saved baseline |

The final Sandbox input contained 648 hash-verified package files. Fixture imports
were added only to the disposable guest. The root, state-matrix, and scrolling
PNG exports were inspected alongside guest desktop evidence. Japanese text was
preserved in the snapshot; this clean guest displayed the same missing-glyph
fallback in both Explorer and the native image, so complete Japanese font
coverage is not claimed.

The five-run 144-DPI gate remains pending. Earlier development captures reported
144 DPI, but the final guest reported 96 DPI. No host display setting was changed
by this work. The 96-DPI series does not substitute for that gate, host third-party
extension coverage, human acceptance, DWM blur parity, or installer qualification.
Late arming without a subsequent original paint can still return the explicit
appearance-unavailable structural fallback.

Evidence is retained under the ignored
`Sandbox/20260911-native-renderer-cli/evidence/`: final root runs `checked96-1`
through `checked96-5`, `checked96-submenu`, `viewport96-matrix`, and `viewport96-scroll`. Earlier
`final96-scroll` demonstrates the rejected viewport defect. Two subsequent
root attempts failed in the guest automation's folder lookup despite successful
capture payloads; they were excluded from the final consecutive series. The
runner now performs a bounded enumeration of the exact folder accessibility
item rather than relying on one immediate lookup.

Build/test logs, source/binary hashes, configuration preservation, and before/after
viewport receipts are in `src/studio/artifacts/checks/`, including
`native-renderer-viewport-build-result.json`, `native-renderer-core-final.log`,
`native-renderer-ui-final.log`, `native-row-surface-final.log`,
`renderer-state-tests/results.log`, `native-renderer-final-hashes.json`, and
`viewport-regression-{before,after}.json`. The reusable harness and instructions
are in [ShellStudio.SandboxTests](../../src/studio/ShellStudio.SandboxTests/README.md).

No host installation, registration, system-tool operation, commit, push, or
GitHub Actions run was performed. The existing MSI remains older than this
portable build. All earlier verification sections below describe historical
builds and do not supersede this checkpoint. All task-owned Sandbox guests were
stopped; the final CLI inventory contained no running environments.

## Actual-menu and localization corrections — 2026-09-10

### Windows Sandbox CLI verification — 2026-09-11 UTC

The current portable build was copied into a fresh Windows Sandbox with
networking disabled and read-only input mapping; 643 staged files passed
SHA-256 verification. Guest-only native registration succeeded. The actual
Studio executable rendered its configuration successfully, and a bounded
WPF harness using the same built assemblies observed zero startup errors and
the expected `LANG_IMPORT_DYNAMIC` warning.

A real selected-folder Explorer menu reached Studio through the native capture
protocol: 20 final root entries, 23 original root entries, Windows/system and
custom entries both present. The Studio render visibly shows Actual capture
and the corresponding live entries in its left pane. This verifies root-menu
structure in the clean guest; it does not qualify host third-party extensions,
unopened submenu contents, installer behavior, or human acceptance.

Exact popup appearance remains unverified: the capture reports
`CAPTURE_APPEARANCE_UNAVAILABLE` after bounded retries. The fallback renders
structure and currently displays mnemonic ampersands literally. The separate
guest desktop screenshot confirms that Explorer displayed the customized menu.
Evidence is retained in the ignored
`Sandbox/20260911-actual-menu-cli/evidence/` directory, including `startup.png`,
`desktop.png`, and `capture/{result.json,snapshot.json,studio.png}`.
All 643 host build files remained unchanged. Host registration still points to
the existing installation. The task-owned Sandbox was stopped after export;
the CLI reported no running guests. Evidence hashes and the ownership receipt
are retained alongside the evidence directory.

### Source corrections and preceding local checks

The default Actual menu pane no longer substitutes unevaluated configuration
definitions for Explorer entries. Startup listens for capture, context changes
hide incompatible captures and clear their inspector selection, and the
automatic sample workspace can follow a valid capture's configuration without
overriding explicit configuration choices or pending edits. Configuration
definitions remain available in Arrange entries.

Explicit localization imports now retain a native localization parser role
through source edits and undo/redo. Native tokens identify import qualifiers
across comments and whitespace. Ordinary configuration parsing still rejects
bare localization assignments, and conflicting import roles are diagnosed.
The real `bin/shell.nss` startup reproduction now reports zero errors; its
runtime-dependent import warning remains intentional.

The Release x64 native/application build passed with `-SkipInstaller`, preserving
all 24 local configuration files by SHA-256 comparison. Final checks passed:
46 Core/native tests and 42 offscreen WPF tests. The GUI correction also passed
an independent 42-test WPF run with 42 render artifacts. The main agent reran
the WPF suite against the final language DLL and reviewed the integrated change.
The property inventory was regenerated to retain current source hashes and
line mappings; its native acceptance checks passed.

Pre-Sandbox logs and the independent review are retained under
`src/studio/artifacts/checks/actual-menu-*`. The registered Explorer extension
is an older installation without the Studio capture protocol. No installation,
registration, or Explorer restart was performed. Installer rebuilding was
blocked by automatic approval review pending separate authorization. Actual
Explorer capture was subsequently checked as described above; installer
acceptance remains unverified for these corrections.

## Workspace redesign — 2026-09-10

The menu preview, then-current property map, context picker, configurable file type groups,
and revised supporting screens are built locally. The current independent
design gates are recorded in [the redesign record](redesign-2026-09-10.md);
earlier interface scores below do not qualify this redesign.

The replacement Release/x64/v145 package passed in **63.097 seconds** of whole
build-command wall time, excluding tests. The MSI was written at
**2026-09-10 22:36:36 UTC**. The final WiX result had zero warnings and errors.

- Installer: `bin/setup-x64.msi` (54,435,010 bytes)
- MSI SHA-256: `F9CA84DFE1F6153AD93A6886DEBBE0868E3FDAD63C558E6FCA94873652E54430`
- Native Shell DLL SHA-256: `08EF118077F1D4296A533E7F52295626144416679ED3D229EEF1FF1115664A62`
- Studio assembly SHA-256: `0611A9551E3039955F98B90E18DD46976BD2818CF32D0B253B314698ED2D9049`
- Language DLL SHA-256: `C6AA045CFA46A73113FDDAD5794691A4444380FC7DB68B6B80210D724CFA0D6E`

The main-agent checks passed **45 Core checks** and **39 offscreen WPF checks**,
including capture filtering, settings persistence, mixed-extension rule scope,
pixel/hit-target validation, late root/submenu evidence, and minimum-window
layout. The integrated-tools and native-resource counts below are historical;
they were not rerun for this redesign. The independent critic separately ran
and passed the same **45 Core** and **39 WPF** checks against the final packaged
language library, produced **42 render artifacts**, and scored the workspace
gate **8.5/10**. The supporting-screen gate also passed at **8.5/10**, reusing
that unchanged-source verification. Both reviews reused the main agent's final
full-build result; no independent second full-package build is claimed.

The native appearance path was compiled and source-reviewed. No live Explorer,
DWM composition, physical DPI, active desktop registration, or installation of
this replacement package was performed. The earlier guest installer results
remain evidence for their recorded package, not for these new capture changes.
Build and rendering evidence is under `src/studio/artifacts/checks/`.

## Sandbox registration fix — 2026-09-10

Real guest installation exposed an inverted process-exit contract in
`src/exe/src/Main.cpp`: `Register` returns a Boolean, while the MSI custom action
requires zero on success. The executable now maps success to `0` and failure to
`1`. Clean installation and both registrar outcomes passed in Windows Sandbox.
See [the integration record](sandbox-integration.md) for lifecycle results,
evidence, and limitations; [the roadmap](roadmap.md) lists the remaining work.

The replacement Release/x64/v145 build completed in **66.26 seconds** at
**2026-09-10 16:11:35 UTC**, with zero final WiX warnings or errors. This is whole
build-command wall time, excluding tests.

- Installer: `bin/setup-x64.msi` (54,385,858 bytes)
- MSI SHA-256: `66D05AEE47EBC2FCFF0B6DEC0A6C8E52DC9B68C83982E2D2D370B9F3FDA88557`
- Registrar SHA-256: `920CFD487FFD9FE3A8F7138AB334FBE6DAE10FF068DD6430B88FE40E3E04E754`
- Native Shell DLL SHA-256: `420D3BA46BB9846AF088B882B3CE614FD2E6ACE742D92F2901F184FE045E462C`
- Studio assembly SHA-256: `15273DBCA6F8EC26107E74A8038FD99D561EF48D0CE6ADE71D30A8CBB68ABB75`
- Language DLL SHA-256: `E2F24F01FCA644D21494EF2C0D5686D9273EA2D64D315DD3ACFB6F48A2B5B916`

The 107 checks below belong to the preceding interface verification. They were
not rerun for this registrar-only change; guest process-exit probes and real MSI
execution provide the targeted regression coverage. The prior MSI hash below
is historical and is superseded by this receipt.

## Interface update — 2026-09-10

The shared visual system now covers the menu editor, expression canvas,
settings, tools, templates, and dialogs. The independent design gates both
finished at **9/10**; the [design record](design-system.md) records each score,
refinements, and remaining limitations.

The complete Release/x64/v145 package build passed in **55.02 seconds**,
finishing at **2026-09-10 12:09:43 UTC**. This is whole build-command wall time,
excluding tests. The final WiX build reported zero warnings and zero errors.
The sandbox attempt failed in Visual C++ FileTracker with access denied;
the same local build succeeded outside that restriction.

- Application: `bin/studio/ShellStudio.exe`
- Installer: `bin/setup-x64.msi` (54,389,954 bytes)
- MSI SHA-256: `C8CA55391534E7B9F47C911727B8553F19C5CCEAEC2EA641EAB0343FB4A3700A`
- Packaged language DLL SHA-256: `E2F24F01FCA644D21494EF2C0D5686D9273EA2D64D315DD3ACFB6F48A2B5B916`

All **107 local checks** passed after packaging:

| Check | Result | Boundary |
| --- | --- | --- |
| Core and exported native parser | 39 passed | Model/file fixtures and source-preserving transactions |
| WPF | 30 passed | Offscreen interaction, keyboard, layout, themes, drafts, cancellation, dialogs |
| Integrated tools | 34 passed | In-memory providers and task-owned fixtures |
| Native resources | 4 passed | Windows resource APIs on a task-owned PE copy |

The independent critic also rebuilt and passed the 30 WPF checks against the
unchanged reviewed source. The main agent verified the combined diff and
confirmed all 12 reviewed application/test source hashes remained unchanged
through the package build. Packaged and UI-test language DLL hashes match.

Evidence under `src/studio/artifacts/checks/`:

- `ui-design-build.log`, `ui-design-build-result.json`, and
  `ui-design-package-receipt.json` record the new package build and hashes.
- `ui-design-source-receipt.json` identifies the reviewed source.
- `ui-design-core.log`, `ui-design-ui.log`, `ui-design-tools.log`, and
  `ui-design-native.log` retain the post-build checks.
- `ui-stage2-final-r3/` contains 34 PNGs and a qualification note;
  `ui-critic-final/` contains the critic's independent render run.

The render matrix includes populated, long-label, empty, diagnostic, invalid
expression, minimum-size, and dialog states; all five pages have light, dark,
and high-contrast-mapping renders. Selected images use 144/192 DPI bitmap
resolution. These are settled offscreen WPF visuals, not physical-monitor DPI,
OS text scaling, Narrator, or human acceptance. Elevated execution windows
received source review only. No installer execution, registration, Explorer
restart, live system-tool operation, commit, push, or Actions run occurred.

## Earlier implementation artifacts (historical)

The following records describe the prior implementation build. The interface
update above supersedes its application/MSI artifacts and test counts.

- Application: `bin/studio/ShellStudio.exe`
- Installer: `bin/setup-x64.msi` (54,369,474 bytes)
- MSI SHA-256: `265B98178F9523248718B52B009ED1DBA3FD505F8C6E28BFF810EC00A5BCB839`
- Packaged language DLL SHA-256: `E2F24F01FCA644D21494EF2C0D5686D9273EA2D64D315DD3ACFB6F48A2B5B916`

The final incremental `src/studio/build.ps1` command completed successfully in
56.76 seconds with Release/x64/v145. Its final WiX build reported zero warnings
and zero errors. This duration covers the whole final build command, not the
earlier implementation work or subsequent tests. Completion time was
2026-09-10 02:31:14 UTC.

Generated receipts and logs are retained under
`src/studio/artifacts/checks/`: `build-result.json`, `final-build.log`,
`package-receipt.json`, and `msi-payload.json`.

## Earlier implementation checks

| Check | Result | Boundary |
| --- | --- | --- |
| Core and exported native parser | 39 passed | Temporary files, fixtures, parser limits, transactions, templates, inventories |
| WPF | 16 passed | Offscreen controls, editing, capture protocol fixture, templates |
| Integrated tools | 34 passed | In-memory providers and task-owned fixtures |
| Native resources | 4 passed | Windows resource APIs on a task-owned PE copy |
| MSI database inspection | Passed | Payload, runtimes, licenses, preservation flags, checked custom actions, shortcut |

Core and WPF tests used the packaged language DLL; all three SHA-256 hashes
matched. The four test logs are `core-final.log`, `ui-final.log`,
`tools-final.log`, and `native-resource-final.log`. Offscreen menu and expression
canvas renders are in `ui-final/`. Visual inspection found and corrected a
low-contrast selected menu row; the replacement render was checked.

## Earlier implementation source review

The function inventory contains 451 callable entries and 1,152 values, derived
from documentation and 1,412 verifier candidates. There are no unmapped hashes
in those candidates. Every advertised insertion and all six named-family
examples pass the exported parser and produce visual expression trees.

Independent source review found no missing finite names in the requested
theme paths, all 59 key-table entries, all 149 color-table entries, and 39 string
paths. That review was read-only and reused the parent's native insertion
checks; it did not independently establish runtime evaluation equivalence.
Rejected documentation spellings and parser/runtime discrepancies are retained
in `function-coverage.json`. Overall language coverage intentionally remains
unqualified for runtime-dependent resolution and evaluation.

The four donor implementations are consolidated into managed operation
services; their detailed mapping and remaining live checks are recorded in
[tool-parity.md](tool-parity.md). No donor executable is required by those
services. The final MSI contains the donor and applicable third-party notices.

No installation, registration, Explorer restart, live tool mutation, commit,
push, or GitHub Actions run was performed for this verification. Existing local
README and root ignore-file changes were preserved.
