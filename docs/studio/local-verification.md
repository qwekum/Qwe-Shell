# Local verification — 2026-09-10

The Windows x64 implementation and MSI are built. The approved plan is **not
fully qualified**: Explorer, installer lifecycle, system-operation parity,
runtime-language equivalence, and human acceptance remain pending in
[acceptance.md](acceptance.md).

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
