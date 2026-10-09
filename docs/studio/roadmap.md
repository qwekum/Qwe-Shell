# Shell Studio roadmap — 2026-09-12

The [root plan](../../README.md#shell-studio-plan) remains the scope authority.
This roadmap separates implemented checkpoints from outstanding qualification.

| Milestone | Current evidence | Remaining gate |
| --- | --- | --- |
| Baseline and contracts | Pinned donors, language inventories, versioned contracts, source coverage records | Complete source-based parity acceptance; retain donor sources |
| Core foundations | Current source: shared parser/workspace/transaction contracts; 66 Core and exported-native-parser checks, 75 native preview-semantic checks | Runtime semantic equivalence and real interruption/elevation cases |
| Menu editor | Capture client/runtime implementation, source-backed entry details, rule/property/settings/import evidence, and automatic semantic submenu materialization with explicit completeness states; listener collisions report `CAPTURE_LISTENER` | Post-fix human-driven Explorer capture, mixed-entry edits, apply and recapture matrix |
| Visual authoring | Function/value catalogue, canvases, settings, templates, diagnostics; 58 current-source WPF checks | Runtime-dependent resolution, physical DPI/accessibility and human acceptance |
| Tool consolidation | Shared services and donor ledgers; current fixture/provider result is recorded separately from the 69 checks in the historical package checkpoint; 4 native resource checks | All four ledgers verified against live Windows effects and recovery |
| Integration and qualification | Current source compiles, but package synchronization stopped before replacing the Explorer-loaded `bin/shell.dll`; the earlier MSI passed Sandbox clean install, registrar, installed startup, repair, and uninstall-preservation checks | Build a package from current source, then repeat the installer matrix; older-normal-version upgrade, damaged-install repair, interactive entry points, Explorer, system tools, and human cases |

## Next work

1. Build and synchronize a package from the current source in an isolated
   environment, record new hashes, repeat the passed historical Sandbox
   installer checkpoint, then extend it to an older normal-Shell upgrade and
   damaged-install repair.
   [The integration record](sandbox-integration.md) records the tested package
   and exact passed/pending cases.
2. Exercise the actual Explorer capture → source-backed edit → reviewed apply →
   recapture workflow with one Studio instance. Verify automatic semantic
   submenu discovery, observed appearance enrichment, settings-gated actions,
   durable rule reuse, and the duplicate-listener diagnostic, then broaden to
   the selection/context and runtime reload matrix. The post-fix human-driven
   desktop result remains pending.
3. Qualify each donor operation's real effects, cancellation, partial failure,
   and recovery in isolated Windows environments. Resolve source-ledger gaps.
4. Finish runtime-language equivalence and interrupted publication/elevation
   scenarios. Use a persistent VM where Sandbox cannot reproduce a scenario.
5. Record human keyboard, accessibility, DPI, theme, and editing acceptance.

The [acceptance matrix](acceptance.md) is the completion gate. Sandbox results,
local tests, and design scores do not replace unperformed cases. Publishing,
GitHub Actions, and changes to the active host desktop remain separately
authorized actions.

For actual first use, existing-install replacement, and rollback steps, follow
[Install and use Shell Studio](using-shell-studio.md).
