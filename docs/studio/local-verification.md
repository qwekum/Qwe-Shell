# Local verification — 2026-09-09

The Windows x64 implementation and MSI are built. The approved plan is **not
fully qualified**: Explorer, installer lifecycle, system-operation parity,
runtime-language equivalence, and human acceptance remain pending in
[acceptance.md](acceptance.md).

## Final artifacts

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

## Checks

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

## Source review

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
