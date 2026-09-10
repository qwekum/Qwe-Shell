# Windows 11 x64 acceptance record

Status: **pending**. Local parser, file, provider-fixture, offscreen WPF, and build checks do not qualify these cases. Run against a recorded disposable VM snapshot with the exact MSI and native/managed binary hashes. Do not run system-changing cases against the developer's active desktop.

The current artifact hashes and 93 passing local checks are in
[local-verification.md](local-verification.md). Follow
[build-and-run.md](build-and-run.md) to rebuild, launch the application, and
prepare the separate installation/capture stage. Retain the current package
receipt with each VM run; a later rebuild requires a new receipt.

For each case retain the OS build, starting state, steps, expected and observed result, diagnostics, and relevant before/after files or registry values. Mark failures explicitly. Restore the VM snapshot between cases that modify system resources or security.

| Area | Required cases and evidence |
|---|---|
| Installation | Clean install; Start Menu launch; management-window Customize; `shell.exe -customize`; registration failure surfaced to MSI; repair; upgrade retaining handwritten imports and Studio assets; uninstall retaining configuration, templates, and recovery backups. |
| Capture | Files, mixed multiple selections, folder, folder background, desktop, drive, and recycle-bin contexts. Compare original evidence and final displayed hierarchy against Explorer. Open lazy third-party submenus and confirm uncaptured state changes only after capture. Cancel capture, close the menu, and disconnect Studio without blocking Explorer. |
| Direct editing | Reorder and nest mixed native/custom entries; move out of a submenu; separators; duplicate and localized titles; entries with and without stable identifiers; category and exact-path rules; custom-source edits and import scope restrictions. Compare the saved configuration and a subsequent actual menu. An unrepresentable move must produce a diagnostic. |
| Runtime reload | Apply root-only and import-only edits. Confirm capture reports the saved generation. Inject an invalid replacement and verify the previous valid runtime configuration remains active. Interrupt a multi-file publication while the marker exists and prove Explorer never loads the partial generation. |
| Recovery/elevation | Deny access to a target; cancel UAC; interrupt after the first file replacement; change a file externally before apply and before recovery; lock a restore target. Verify original backups, actionable diagnostics, and no overwrite of newer edits. |
| Tools | Validate every row in `tool-parity.md` against the pinned donor source. Exercise registry and view defaults, recursive folder types, INI preservation, file attributes, ADS, owner/DACL changes and recovery, resource groups, cache resets, launch privilege levels, metadata, shortcuts, and feature flags. Compare exact before/after state, including unrelated values/resources. |
| Partial operations | Cancel a recursive operation; make one child inaccessible; exceed the configured item/depth limit; include reparse points; fail the provider after some changes; fail a restore. Verify per-item results and recovery information accurately distinguish completed, skipped, failed, and unattempted targets. |
| Templates | Save/reload a submenu and customization with icons; merge into another workspace; replace only the selected customization; duplicate variables; unavailable executable/assets; machine-specific paths; package version mismatch. Verify exact preview and absence of execution during load. |
| Diagnostics | Compare Explain this entry with actual rule evaluation for displayed, hidden, renamed, moved, disabled, and checked entries. Ensure inspecting the trace causes no second evaluation. Review diagnostic-report content before export. |
| Human/UI | Keyboard-only entry selection, move, editing, canvas, dialogs, and cancellation; screen-reader names and focus order; 100/150/200% DPI and monitor transitions; light/dark themes; large menus and graphs; selection persistence; drag insertion indicators; capture/preview/saved/verified distinctions. Record human acceptance separately from automated checks. |

The full plan remains unqualified until these cases and the source-based language/tool coverage inventories pass. A build artifact alone is not completion evidence.
