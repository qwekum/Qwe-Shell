# Unsigned 1.9.20 candidate preparation — 2026-10-08

Status: **combined local candidate record; release acceptance blocked**.
The twelve original corrections are implemented in that combined local overlay,
but are intentionally excluded from the separate qualification draft PR3.
PR3 contains the additional runtime, build, harness and documentation work and
is stacked on the original Studio PR head. Neither PR1 nor PR2 is updated by
publishing PR3. Results below identify the combined candidate's source and
package; they do not qualify the narrower PR3 branch or its excluded fixes.

This is a local, uncommitted combined snapshot targeting main. It is not a
published release or a merged revision. Public distribution and signing are
outside this work. Persistent Windows VM and recorded human acceptance gates
are held by the owner's instruction to prepare the work and leave them blocked.

## Source and package identity

Inputs are Studio PR1 `b8ee65cc73cc1d21db76bd1c419dbe5daec3ef2c`, cloud PR2
`0cb837a5bb5d4a2ae6ec0043a861b658345e7c89`, and main
`01d9a9659dcf35fb59b496b114a41729580b2990`. The required feature ancestor is
`65df0e4f2ad4590c9964b9ab810bc094797b81af`; preserve it through the authorized
integration history. Main branding and the Studio acceptance contract are
combined. Recording the actual merge commits remains a separate approval.

Native, managed, and MSI metadata use 1.9.20. ProductCode is
`{E865DA3F-5044-424F-BC29-5E62A99ED771}`; UpgradeCode remains
`{7BCA6512-254B-456F-8F08-AAD7508BEE00}`. Release/x64 builds use .NET SDK
10.0.401, MSBuild 18.9.1, v145, and pinned WiX SDK 5.0.2. Managed restore is
sequential and NuGet audit remains enabled.

The current private Release/x64 package is build 6. Its production-source
manifest SHA-256 is
`7c7a6da31e519a62700471e5e96dd86e6bbe2570bba39df4fe3e3f6bd1a1400f`.
The later qualification freeze 9 is
`57c7bc6ed60db08803ecc89bfa3b63a9cb6b4e34c6a488b043f94d191ad30c88`;
it changes qualification fixtures and documentation, with no production payload
change. Final prose is recorded in a subsequent documentation-only manifest.
These manifests identify uncommitted bytes, not merged Git revisions.

| Private artifact | SHA-256 |
|---|---|
| Unsigned portable ZIP, 105,751,482 bytes | `ab71763678106c3e82e2eaee24e381048371c501782b2500852cf6b5e32c8446` |
| Unsigned MSI, 54,988,073 bytes | `fdf19f23452962455d8896b6314d278e115ed4ea1813af7f8c766788536d1d49` |
| Native Shell DLL | `12ac07e9e12762b7ba2438488b52f44e9fe44b7c13eafa548bfd45f35b333bc7` |

The private package manifest records all 645 ZIP payload hashes. Independent
review verified every payload, the ZIP and MSI hashes, nine principal unsigned
binaries and fourteen packaged notice copies. Against build 5, only the native
Shell DLL and MSI changed among 646 staged portable files. Build 5 and older
guest results remain historical; the changed DLL and MSI require build-6
receipts. Private preparation does not authorize distribution.

## Remediation and evidence limits

All twelve requested review fixes are implemented: typed CMD batch encoding and
bounds; complete native selection contexts; registry continuation commas, empty
keys/recovery, standard QWORD export, supported-type checks and strict Unicode;
authenticated snapshot consumption; preservation of detached source edits;
atomic workspace replacement; README LF; and separate staged/worktree checks.
The shared parser also records intermediate expression spans, fixing the
exported-parser build qualification failure.

Independent review of the first combined freeze ran eight managed suites:
Core 67, offscreen WPF 71, Tools 79, resource 4, lifecycle 8, protocol 12,
templates 7 and transactions 2. All eleven native suites and seven actual
native-emitter-to-managed/catalog cases passed. Semantic/construction/render/
adversarial/worker probes passed 75/20/8/14/3. Native x86 DLL, executable and
custom-action targets compiled. Windows cloud preparation passed 22 tests;
actual Linux helpers passed 48 tests with networking disabled and read-only
source. These are scoped results, not full language or Windows acceptance.

Further corrections address explicit snapshot ownership, missing harness worker
prerequisites, Windows PowerShell's framed-stdin BOM, and the damaged-repair
harness. Strict ownership/link cleanup assertions remain intact. Independent
affected reruns passed Core 67, WPF 72, native serialization 11 and the actual
native-to-managed boundary: eight frames, fourteen entries and 34 rejected
missing/unsupported versions. Live basic capture passed after the producer was
fixed to emit evidence versions even without source or ledger data.

Build 5 exposed a P1 construction regression: a valid dynamic-only submenu was
treated as a failure because it had no system entries. The production correction
preserves null-menu rejection and permits the empty system-entry case to reach
dynamic construction. A new suite compiles the real production constructor;
independent review passed ten cases, including ordinary/capture paths, nesting,
ordering, disabled gates, bounds and command non-evaluation. The affected x86
DLL also compiled. This adds a twelfth native suite; the prior eleven suites
retain scoped evidence through unchanged relevant source and payload hashes.

The current build-6 package was tested in a fresh networking-disabled Windows 11
x64 Sandbox, build 26100, instance
`af7c25ad-2b23-409b-a34f-aaf71459cb0f`, starting without the product installed.
Clean installation passed all 636 installed-payload comparisons. Scoped runs
passed Tools 79, native selection 7 plus managed routes 7, worker lifecycle 8,
two actual Registry Editor imports with eight values and two empty keys, preview 14,
registrar 2, and capture serialization 11 plus the eight-frame/fourteen-entry
reader boundary and 34 invalid-version rejections. Start Menu and
`shell.exe -customize` entry routes passed. Damaged repair restored two native
binaries and registration while preserving seven user-data categories.
Uninstall and reinstall also passed, preserving all seven categories byte for
byte; reinstall reverified all 636 installed payloads against the package and
the retained configuration hashes. The MSI processes exited 0 without timeout.

At 144 DPI, the exact current DLL passed basic capture (23 final/21 original
entries), the opened six-child matrix and the opened 90-child scrolling fixture
with 41 visible native rows. The final qualification harness separately verifies
all semantic targets, the visible native subset, exact finite geometry, viewport
and scroll gutters. Independent checks passed WPF 72 and sixteen helper cases,
including rejection of NaN, incorrect geometry and missing/extra identities.
The final harness was published without warnings and its 407 copied files were
verified before these guest runs.

These opened-menu results do not establish automatic discovery of unopened
submenus. The basic capture still records unopened fixture roots with empty
children; the full automatic semantic-capture gate remains open. A source-based
readiness/identity-order concern remains a hypothesis, not a proven root cause.
The management-window Customize route also remains unverified: UI Automation
did not expose the button and the alternative driver could not establish
foreground activation. These retained failures do not establish human or
accessibility acceptance. Earlier driver-readiness and scroll-assertion failures
are retained alongside corrected runs; passing results do not erase them.

Both review and release evidence must record the exact source manifest/commit,
package hashes, commands, versions, exits, guest starting state and process
identities. The local review reports and receipts are retained outside source
control; do not commit personal paths or raw machine logs. Draft PR descriptions
are in [PR remediation notes](pr-remediation.md).

## Remaining release gates

| Gate | Current boundary |
|---|---|
| ARM64 compilation | v145 ARM64 build tools are absent; DLL attempt stopped with MSB8020 before compilation; executable/custom action not attempted |
| Clean Linux Install/Start | Requires the approved final clean combined commit and its pinned revision; helper tests do not qualify this |
| Donor completion | All 150 recovered source files and notices were audited. Known source omissions include fixed-NTFS/AFTD gates, folder icon/LNK/date/privilege behavior and WinSetView inheritance/reseed/app-view effects; [four donor ledgers](tool-parity.md) distinguish these from unrun Windows effects. Independent full-plan disposition remains **revise** |
| Language coverage | Canonical inventories contain 452 callables, 1,153 values, 78 properties and 172 settings paths; runtime semantic completeness is not established by catalogue coverage |
| Explorer editing/recovery | Full context, interrupted transaction, denied access, reload, external-edit and recapture matrices remain governed by [acceptance](acceptance.md) |
| Templates/diagnostics | Local checks pass; cross-workspace and full malicious/dependency/asset matrix still needs recorded acceptance |
| Installer lifecycle | Build-6 scoped clean install, damaged repair, uninstall and reinstall passed, with seven preserved user-data categories. Old normal-MSI upgrade, equal/newer rejection, registration-failure recovery and failed upgrade recovery remain incomplete |
| Persistent VM/physical session | Reboot, startup cleanup, legacy upgrade/recovery and physical-session scenarios blocked by unavailable approved VM |
| Human/UI | Keyboard, Narrator, themes/high contrast, 100/150/200% DPI, monitor transitions, large graphs and direct editing acceptance blocked pending recorded human testing |
| Publication | The separate PR3 branch and scoped draft publication are authorized. Publishing the combined candidate/original fixes, changing PR1/PR2, retargeting, review publication, merges, Actions, signing and distribution still require separate authorization |

## Backup and rollback

Before any installation or apply, copy the entire configuration/import/asset
tree and retain its hashes, permissions, original package and registration
path. Keep the transaction/recovery directory, manifest and original backups
together. Compare the exact preview and external-edit checks before applying.
Use the reviewed recovery route only when it recognizes the journal and its
current post-state; never bypass conflict, backup-integrity or generation checks.

Historical journals without the required state/fingerprint are deliberately
refused without discarding backups. Preserve a separate immutable copy; inspect
each recorded original against the current target and proposed restoration,
including empty registry keys and registry view. Obtain review of that exact
restoration before applying it in the disposable guest. Do not fabricate missing
fingerprints or relabel a legacy journal as a modern one. Automatic refusal
tests establish safe preservation, not a universal legacy-journal migration.

For installer rollback, preserve user data independently of MSI. Return a
disposable VM to its recorded starting snapshot after failed lifecycle tests;
do not extrapolate rollback success from a clean install. See the
[installation and rollback guide](using-shell-studio.md) for the supported
workflow and registration constraints.
