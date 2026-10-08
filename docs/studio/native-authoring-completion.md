# Native authoring completion ledger

This ledger tracks the approved native authoring workflow implementation. An
implemented contract is not evidence of Explorer, installer, or human acceptance.
The README remains the product scope authority. Each row stays open until its
implementation and the relevant qualification evidence are recorded here.

For end-user startup, existing-Shell replacement, backup, and rollback, follow
[Install and use Shell Studio](using-shell-studio.md). This ledger remains the
engineering completion record rather than an installation guide.

The 2026-09-12 source checkpoint below supersedes the older package checkpoint
for implementation status. It records source, fixture, native-process, and
offscreen evidence; it does not turn those checks into Explorer, installer, or
human acceptance.

## Preserved baseline

- Repository HEAD: `65df0e4f2ad4590c9964b9ab810bc094797b81af`.
- Locally recorded upstream main: `81ec1a410d1277efa58aff52be912a254f66e5a3`.
- SetFolderType: `cdde0ee160494a820e520b47a6d135562397e965`.
- WinSetView: `fc4051c35cd5295ab6f7922539d930c0309638d3`.
- Existing dirty source and artifacts were copied and hashed before edits under
  the ignored task evidence directory. Existing local and Sandbox verification
  records describe their original package hashes, not this implementation.

## Requirements and gates

| Area | Required behavior | Implementation | Qualification |
| --- | --- | --- | --- |
| Language authority | Shared native grammar, lossless source, semantic queries; no managed interpreter | Implemented in shared native front end | 66 Core/parser and 75 native preview-semantic checks pass in the current source; complete runtime equivalence remains open |
| Imports | Ordered scoped repeated occurrences; localization/configuration roles; unsaved refresh and detached dirty retention | Implemented | Current source import/edit/undo and source-association checks pass; live cross-workspace acceptance remains open |
| Identity | Revision-aware edits, expressions, layouts, captures, source spans, hashes, and worker responses | Implemented | Local stale-result and association checks pass; human workflow open |
| Compatibility | Explicit fixes for parser/verifier inconsistencies | Implemented and inventoried | Current source passes 75 semantic and 20 construction checks; the historical package's 14 adversarial checks must be repeated on a current package |
| Worker | Versioned bounded native analysis/evaluation/rendering with cancellation | Implemented | Current source passes 19 managed protocol/lifecycle checks; the historical three-case real-native worker run must be repeated on a current package |
| Effects | Transitive evaluation restrictions and scoped brokered read snapshots | Implemented fail-closed | Current broker checks cover distinct function identities and exact argument shapes for `reg.exists`, `reg.get`, and bare `reg(...)`; live reads and the adversarial matrix remain separate gates |
| Rendering | Shared measurement/layout/painting; offscreen and composed native preview | Implemented | Catppuccin/state/DPI/composition automation passes; physical monitor and human appearance open |
| Capture preview | Apply unsaved configuration over captured original structure; automatically materialize retained submenu semantics; keep incomplete branches explicit and appearance separate | Implemented | Current construction/serialization matrices pass; final-package Explorer apply/recapture remains open |
| Visual coverage | Context-bound controls/nodes for every supported construct | Implemented for advertised constructs | 58 current-source offscreen UI checks pass; keyboard, UI Automation/Narrator, and human authoring open |
| Templates | Complete dependency export, compatible replacement, scoped conflicts, durable layouts | Implemented | Six focused template checks and Core coverage pass; broader cross-workspace acceptance open |
| Recovery | Pending recovery detected before editable workspace; dependency-version validation | Implemented, including typed thumbnail dispatch | Two transaction and 69 tool checks pass; final-package interruption matrix open |
| Verification | Expected-versus-observed comparisons distinct from generation loading; source, rule, property, settings, and completeness evidence remain inspectable | Implemented with revision-bound native expectations | Core state/key/owner-draw/partial-capture and evidence checks pass; Explorer appearance acceptance open |
| Tool actions | Saved parameters, complete selection handling, catalog eligibility | Implemented | 69 integrated fixture checks pass; installed GUI/menu equivalence open |
| Donor additions | Typed registry import/export; explicit non-elevated-host script launch | Implemented as bounded operations | Fixture/source checks pass; remaining actual Windows effects open |
| Donor parity | Every pinned donor behavior implemented and attributed | Source ledger complete for supported/unsupported rows | Four-ledger final installed effects/recovery run remains open |
| Package and UI | Exact final package lifecycle, keyboard, assistive technology and physical DPI | Current source compiles and publishes Studio/ToolHost; the last exact Release/x64 MSI is historical | Synchronize and hash a current package, then run installed lifecycle, physical DPI, assistive technology, and human acceptance |

## Decisions

### Current source checkpoint — 2026-09-12 ET

The current source replaces the decorative entry relationship view with entry
details bound to real configuration properties and expression spans. Captured
native entries retain structured source, rule, property-effect, effective
`settings.modify`, and completeness evidence. Quick native edits create or
reuse a durable context-scoped managed rule; **Open shared rule** is the
explicit route to a wider handwritten rule. A stale hash, missing import
occurrence, duplicate marker, or ambiguous selector keeps the evidence
readable and disables source editing. Disabled effective settings disable the
affected action with a diagnostic, and hidden or removed entries remain
available through the evidence toggle.

Native capture now reuses the popup-entry constructor to materialize retained
submenu definitions automatically. Commands are never invoked during this
traversal. Assignment, variable-mutating loop, unsupported-function, provider,
cycle, policy, and bound failures leave the affected branch explicitly
incomplete with a diagnostic. Native-rendered appearance is separate from the
semantic hierarchy, so unavailable or partial pixels do not discard semantic
children. Imported declarations without an exact retained occurrence remain
inspectable but are not exposed as editable source locations.

The current local checks recorded in [local verification](local-verification.md)
include 66 Core/exported-parser checks, 58 WPF checks, 75 native preview
semantic checks, 20 native construction checks, 19 preview-worker protocol and
lifecycle checks, four native-resource checks, and the native capture
serialization checks. The Release/x64 source build compiled the components,
but package synchronization stopped before replacing the Explorer-loaded
`bin/shell.dll`; no installation or live Explorer capture was performed.
These results establish source and local process contracts only.

### Earlier integrated package checkpoint — 2026-09-11 UTC (historical package)

This package checkpoint predates the 2026-09-12 source changes above. Its
hashes and qualification record remain useful for the exact artifact that was
tested, but they do not describe the current source or close the remaining
installer and Explorer gates.

The source corresponding to this historical package checkpoint passed the
Release/x64 native and managed build,
the exact built native-language matrices (70 semantic, 18 construction, eight
rendering, and 14 adversarial checks), and the three-case real-native worker
test. Managed checks pass in Core (57), UI (48), Tools (69), preview protocol
(11), preview-worker lifecycle (seven), templates (six), native resources
(four), and transactions (two). The focused native executables for context
painting, shared rendering, row surfaces, DPI rendering, preview resources,
selection, selection snapshots, capture serialization, language JSON, and
Debug/Release native expression syntax also pass.

The full `src/studio/build.ps1` pipeline then produced that historical MSI in
64.31 seconds with zero warnings and errors:

- `bin/setup-x64.msi`: 54,926,640 bytes,
  SHA-256 `9ED834C1B0133689F09D443017EEA1C5554BA7D1272BC98F938A03F4F3D0461A`.
- `ShellStudio.Language.dll`:
  SHA-256 `F6A9FCAF3631AC198F79838F6F6FD7941E7D9928AC70BF54EF1BE892A035BF22`.
- `ShellStudio.PreviewWorker.exe`:
  SHA-256 `6DDDF1BD437B25A10D77C336D4718DC76FF7103E2DB583D6B0A87C561BC96DE9`.
- `ShellStudio.dll`:
  SHA-256 `2C46327668153AD185B4BF4A29DA0213BA49D6E8D04B114128FA753258320F82`.

This closes the known authored-image, theme-channel, layered-window input,
parser/span, RTTI, capture-serialization, stale-apply-expectation, repeated
source-selection, and thumbnail-recovery implementation regressions. It does
not close final-package installation, Explorer apply/recapture, four-ledger
Windows effects, physical-monitor DPI, UI Automation/Narrator, or human visual
acceptance.

### Earlier implementation checkpoint (historical, not qualification)

The managed worker lifecycle suite now passes seven checks. A new fixture first
reproduced indefinite cleanup after a valid response from a worker that remains
alive. Cleanup now terminates the owned worker and bounds its exit wait; the
same fixture passes. This is local process-contract evidence, pending repetition
with the final integrated package.

The integrated native construction checkpoint passed 12 offscreen checks for
authored rows, static modifications/removal, selection conditions, lazy submenu
evaluation, stable identities after insertion, captured owner-drawn limitations,
and ambiguous submenu rejection. An added supplied-image integration regression
is still failing: decoded resource dependencies are present but the authored
image does not change the frame. Resource decoding alone does not establish
image rendering parity.

The same checkpoint passed eight structural theme/render checks and 14 adversarial checks.
Subsequent pixel inspection found swapped red/blue channels in authored theme
colors. The expanded Catppuccin color assertion now fails and is a required
regression gate; the earlier structural checks did not prove color fidelity.
The rebuilt native worker passed three lifecycle checks, including real composed
window ownership and cleanup after its owner pipe closes. Managed Studio builds
with zero warnings and errors. Shared theme extraction and parser regression
repairs remain in progress; these results must be repeated on the final package.
An expanded window-style check also requires layered-window mouse transparency:
`HTTRANSPARENT` alone only routes within the same thread, while the preview
worker and WPF host are separate processes. See Microsoft's
[layered-window input contract](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows)
and [hit-test return values](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest).
This check currently fails on the checkpoint binary.

The memory-only resource decoder's focused checks cover PNG/ICO decoding,
premultiplied captured pixels, content hashes, size and base64 validation,
duplicate paths, and process-private font registration. Decoder and broker
bounds have been aligned; independent ICO frame-directory consistency and
PNG/font rejection regressions pass. The managed protocol/broker suite passes
11 checks, with existing Windows-platform analyzer warnings. Relative authored image paths
remain explicitly unavailable where the runtime's process-current-directory
resolution cannot be reproduced from supplied context.

The restricted native evaluator now uses the runtime parser, scope objects, and
expression evaluator with a request-owned capability policy. The native semantic
test program (`src/studio/native/tests/preview_semantics.py`) passed 50 checks
against the isolated build, covering arithmetic, native variable scope, arrays,
short-circuiting, denied nested effects, supplied environment/import values,
import cycles, aliases, empty results, declaration-time resolution, repeated
imports, and dependency fingerprints. The separate adversarial program passed
11 checks against the then-current DLL, including a transitive denied write and
an unchanged file sentinel. Later source changes require those checks to be
rerun against the final DLL. Worker/UI integration remains open.

The real-worker test (`src/studio/native/tests/preview_worker_native.py`) passed
offscreen rendering, composed-window ownership/message dispatch, owner-pipe EOF
cleanup, and failed-composition exit checks. The worker retains its native DLL
for its entire process lifetime so live window callbacks cannot target unloaded
code. These checks exercise a real native window; they do not establish visual
appearance, physical DPI behavior, or human acceptance.

The worker and Studio build together with zero warnings and errors. Native
offscreen frame tests pass at 96, 144, and 192 DPI, including frame reuse and
scrolling. These checks do not establish configured menu construction, Windows
composition, Explorer appearance, or physical monitor behavior. The UI exposes
workspace-scoped read permissions and deliberate snapshot refresh; integrated
effect and lifecycle checks remain required.

Compatibility corrections in this checkpoint:

- `if` now rejects argument counts outside 1–3, and `sel.index`/`sel.i`
  reject counts outside 1–2. Their former disjunctions accepted every count.
- Unsupported `command.random` and `package.title`/`appx.title`/`uwp.title`
  now produce native syntax diagnostics instead of silently missing a runtime
  result. The command namespace uses the same verifier in Studio and runtime.
- Preview `foreach` validates its directly inspected selection selector before
  runtime evaluation. Short-path iteration is unavailable because it performs
  an operating-system lookup; supplied-name/path iteration remains available.
- `path.ext(value)` is an alias of `path.file.ext(value)` in both verification
  and evaluation. The underlying extension helper now returns a view into the
  caller's path rather than a destroyed temporary.
- `cmds` is normalized to the existing `commands` property parser.
- Dynamic `id` expressions are retained in native menu objects; runtime identity
  construction and rendering integration are still being checked.
- Properties discarded by `remove`, including a bare separator flag, now produce
  an explicit property diagnostic.

Import occurrences, template improvements, action profiles, registry operations,
and script launch support are implemented in the current source and covered by
local checks. Their presence in source does not close the corresponding live
package, Windows-effects, or human-acceptance gates above.

Both standalone and captured-menu preview modes are required. Preview runs in a
native worker and includes a live composed native window. Live reads are opt-in,
scoped, and snapshotted per revision; network and mutation are unavailable to
preview. Templates never grant capabilities. Registry/script donor additions are
explicit tool operations, never preview effects or elevated arbitrary scripts.
Current runtime inconsistencies require explicit compatibility tests and notes.

The published Catppuccin `.nss` configurations are the integration corpus; the
`.tera` generator is not interpreted by Studio. Catppuccin acceptance is an early
integrated gate, not a substitute for the remaining completion rows.

## Authorization boundary

Implementation and local checks are authorized. Publishing, enabling GitHub
Actions, and system changes to the active desktop require separate authorization.
Use isolated Windows environments for system-changing qualification and retain
evidence. Human acceptance must be recorded by a human.
