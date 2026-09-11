# Qwe Shell and Shell Studio

This fork of [Nilesoft Shell](https://nilesoft.org) extends the native Windows Explorer context-menu manager. Shell Studio now has a Windows x64 implementation and a locally built MSI, including the visual editor and consolidated tool services. The [local verification record](docs/studio/local-verification.md) records the checks and their limits. Explorer, installer lifecycle, runtime parity, and human acceptance remain pending; this is not a qualified release.

See [Build and run Shell Studio](docs/studio/build-and-run.md) for prerequisites, complete build/run instructions, live capture setup, verification, and troubleshooting. The approved plan below remains the acceptance contract.

The [Studio interface design](docs/studio/design-system.md) records the shared visual system, independent review gates, and the limits of local UI verification.

## Recommended companion: Microsoft PowerToys

We highly recommend downloading and installing [Microsoft PowerToys](https://learn.microsoft.com/windows/powertoys/install) alongside Qwe Shell. Its Windows productivity and customization utilities make it a useful companion for everyday desktop tasks.

## Existing Shell

The C++ extension customizes the classic context menu with commands, submenus, modification rules, expressions, themes, and icons. Configuration files remain the runtime authority. The existing native targets include x64, x86, and ARM64; Studio targets Windows 11 x64 only. Windows 11's separate modern menu is outside the Studio scope.

- `src/dll/`: native Explorer extension, menu construction, parser, and expression runtime.
- `src/exe/`: native management executable.
- `src/shared/`: shared native support.
- `src/setup/`: WiX installer and custom actions.
- `templates/` and `docs/`: configuration examples and documentation.
- `src/studio/`: WPF Studio, shared configuration and operation services, native language service, tests, and build entry point.
- Three retained donor submodules: `SetFolderType-main/`, `WinSetView-main/`, and `FolderThumbnailFix-main/`. The historical RightClickTools source mapping and all donor qualification limits remain in the [tool parity ledger](docs/studio/tool-parity.md).

## Build and run

Install Visual Studio C++ build tools with a Windows SDK and the .NET 10 x64 SDK. From this repository's root in PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\studio\build.ps1
.\bin\studio\ShellStudio.exe
```

The combined build produces `bin/setup-x64.msi`, `bin/shell.exe`, `bin/shell.dll`, and the self-contained Studio application in `bin/studio/`. It restores the pinned `WixToolset.Sdk/5.0.2` through NuGet; a globally installed `wix.exe` is not required. `src/setup/build.cmd x64` delegates to this same pipeline.

The default toolset is `v145`, tested with Visual Studio 18. Use `-PlatformToolset v143` when building with an installed v143 toolset. The combined pipeline supports Release; the native solution has no Debug configuration. Use `-SkipInstaller` to omit the MSI. Existing x86/ARM64 native builds remain separate and do not include Studio.

Opening Studio allows configuration inspection and editing. Actual menu capture requires the matching native extension to be installed and loaded in Explorer; see the [installation and capture steps](docs/studio/build-and-run.md#install-and-use-live-capture). Building or opening Studio does not register the extension.

## Upstream and attribution

See the [Shell documentation](https://nilesoft.org/docs), [upstream downloads](https://nilesoft.org/download), and [repository license](LICENSE). Upstream downloads and existing screenshots describe Shell, not a completed Studio release. Preserve upstream and donor attribution and third-party notices when integrating functionality.

## Shell Studio plan

### Summary and user experience

Build **Shell Studio**, a modern Windows 11 x64 application integrated into this Shell fork’s build, installer, and management window.

The main editing surface will show the **actual Shell/classic context menu for the selected context**, including existing Windows entries, third-party entries, and custom Shell commands.

The primary workflow will be:

1. Choose **Capture menu**, then right-click the relevant file, selection, folder, background, desktop, or other Shell-supported context.
2. Edit the captured menu directly: drag to reorder, move entries into or out of submenus, and add, remove, or edit entries.
3. Select an entry to inspect its properties and visibility conditions. Open the node canvas for advanced expressions.
4. Review diagnostics and the proposed configuration changes.
5. Apply, then capture the menu again to verify the actual result.

Default changes to the captured **context category**, such as folders or a file extension. Keep a visible scope selector for broader rules, narrower conditions, and exact paths.

Windows 11’s separate modern menu is outside this plan. Existing native x86/ARM64 targets remain separate; the new editor and consolidated tools target x64.

### Architecture and editing behavior

#### Desktop application and shared components

- Use **WPF on .NET 10**, published as a self-contained Windows x64 application. Implement the menu surface and node canvas with native desktop controls, without a browser runtime.
- Keep the editor and managed runtime outside Explorer. Retain the existing C++ extension and control executable.
- Add a **Customize** entry point to the management window and `shell.exe -customize`, plus a Start Menu shortcut.
- Separate configuration/language services, menu capture, tool operations, and UI state. Share the same operation implementations between GUI controls and generated context-menu actions.
- Use an on-demand operation host for actions requiring another privilege level. Do not make the entire editor permanently elevated.

#### Actual-menu capture and direct manipulation

- Add opt-in capture hooks to Shell’s existing native menu enumeration and construction paths. Capture original entries before filtering/modification and the final displayed menu.
- Transfer immutable snapshots through bounded, versioned, current-user/session IPC. Do not expose native handles or block Explorer while waiting for the editor.
- Include context, hierarchy, state, available Shell identifiers, matching information, and configuration provenance. Capture lazily populated submenus when opened and mark uncaptured content.
- Use stable Shell identifiers where available. Otherwise generate context-constrained title/path matching rules and show their potential ambiguity; never persist transient Windows command IDs.
- Convert drag operations into persistent configuration edits. Extend rule handling where necessary to express deterministic ordering and nesting while preserving existing rule behavior.
- Distinguish **hide an existing system entry** from **delete a custom definition**. Removing an entry must not silently uninstall its application.
- Provide undo/redo, keyboard move commands, drag insertion indicators, and visible rule scope. A proposed rearrangement that cannot be represented must produce a diagnostic rather than a misleading saved preview.
- Clearly distinguish the captured state, the edited preview, and the verified state after application.

#### Full visual configuration language

The current parser is tied to extension state and evaluates import expressions. Refactor its syntax front end into a reusable native component before using it in the editor.

- Retain tokens, comments, encoding, whitespace, and source locations in a lossless document model; lower that model into runtime objects.
- Share grammar and validation metadata between the runtime and editor instead of maintaining a second independent parser.
- Provide visual controls for every configuration construct supported by this fork: menus, commands, modification rules, settings, themes, images, variables, localization, and imports.
- Provide node representations for all accepted expression constructs, including functions, operators, arrays, interpolation, assignments, statements, conditions, and loops.
- Preserve evaluation order, variable scope, short-circuiting, and side effects. Graph layout must not introduce repeated evaluation or implicit caching.
- Represent loops and ordered statements explicitly; arbitrary graph cycles are invalid.
- Opening, editing, validating, or previewing a document must not execute its commands. Resolve imports using a restricted read-only evaluation policy; show unresolved runtime-dependent imports explicitly.
- Maintain a coverage inventory connecting every supported property/function/construct to its visual representation and tests. A raw-code placeholder does not satisfy “everything visual.”

#### Configuration preservation and application

- Continue using Shell configuration files as the authoritative runtime format.
- Put newly authored content in a dedicated `imports/studio.nss` file, adding its import once to the selected root configuration.
- Allow targeted edits to existing handwritten definitions when needed. Preserve unrelated text and show the exact diff before application.
- Detect external changes using file hashes and source identities; require reconciliation rather than overwriting newer edits.
- Store editor layouts and workspace metadata separately under the user profile. Losing layout metadata must not lose menu configuration.
- Apply multi-file changes through a journaled transaction with backups, staged writes, and recovery. Coordinate runtime loading so it never consumes a partially committed configuration.
- Reload through an explicit generation notification, including changes confined to imported files. Retain the last successfully loaded runtime configuration when a replacement fails.
- Elevate only the reviewed write operation when configuration files reside in a protected directory.

### Templates, diagnostics, and consolidated tools

#### Reusable templates

Templates contain **menu structure, rules, expressions, icons, and appearance settings**. They do not automatically apply registry, folder, or thumbnail changes.

- Support saving an entire menu customization or selected reusable submenu/rule groups.
- Use a versioned template package containing declarative configuration, asset files, metadata, and optional node layouts.
- Provide explicit **merge into this workspace** and **replace the selected customization** choices, both with preview and conflict reporting.
- Detect missing programs, absolute machine-specific paths, conflicting variables, unavailable assets, and incompatible template versions.
- Import templates without executing content. Validate archive paths and package size limits.
- Include starter templates built from the fork’s existing examples and the consolidated tool catalog.

#### Debugging and error system

Provide a persistent diagnostics panel and inline indicators on entries, properties, and nodes.

Each diagnostic should identify:

- Severity, stable error code, and a plain-language explanation.
- Affected file/location and visual object.
- Relevant context and import chain.
- A corrective action where one can be determined reliably.

Include syntax and semantic errors, invalid graph connections, unresolved imports, ambiguous menu matches, conflicting rules, missing executables/assets, permission failures, and operation-specific failures.

Add an **Explain this entry** view showing its source, applicable rules, and why it was displayed, hidden, disabled, renamed, or moved. Runtime traces must describe actual evaluation without re-executing expressions for debugging.

Offer an exportable diagnostic report with a preview of included paths and configuration content.

#### Full consolidation of the four submodules

Port their functionality into shared components and unified Studio pages. Separate donor launchers do not satisfy completion. Preserve attribution and retain the pinned donor source until parity is verified.

| Source | Integrated capabilities |
|---|---|
| **RightClickTools** | Command launch and privilege selection, terminals, file manager/search, registry tools, history cleanup, unblocking, ownership/access, PATH editing, visibility toggles, folder icons/options, shell refresh, shortcuts, date/time utilities, capture utilities, and remaining shipped actions. |
| **SetFolderType** | Folder type assignment/removal, single-folder and recursive operation, prerequisite detection, and preservation of unrelated `desktop.ini` data. |
| **WinSetView** | Global and per-type views, columns, widths, sorting/grouping, inheritance, Explorer and dialog options, supported feature settings, backup, restore, and reset behavior. |
| **FolderThumbnailFix** | Inspect, apply, and restore folder thumbnail-mask changes with Windows-version checks and recoverable resource updates. |

Implementation requirements:

- Establish a source-based parity ledger for every operation and setting at the four currently pinned commits. Overlapping features appear once in the GUI and call one implementation.
- Port donor behavior into managed services with native interop where required. Replace donor executable/script chains and binary-only helpers with source-backed implementations.
- Specifically replace Resource Hacker-dependent resource editing and SetACL-dependent ownership changes. Resolve remaining dependency provenance and licenses before packaging; preserve third-party notices.
- Model view defaults, folder types, and folder icons separately. Explain the conflict between disabling automatic folder-type discovery and using SetFolderType.
- Give recursive operations preview, progress, cancellation, and per-item results. Preserve unrelated INI content, registry values, attributes, and security information.
- Give system changes separate Apply controls. Record recovery information where restoration is possible; do not promise undo for irreversible cleanup.
- Replace broad process termination and swallowed errors with explicit restart handling and actionable results. Show restart/sign-out requirements before execution.
- Keep external application launching where it is the function itself—such as opening a chosen terminal—but consolidate the four donor applications’ own behavior.

#### Shared interfaces

Introduce versioned contracts for:

- **Configuration service:** parse, describe capabilities, validate, compute edits, and return source-linked diagnostics.
- **Menu capture:** request/cancel capture and return contextual original/final snapshots.
- **Tool operations:** inspect, preview, execute, cancel, and report recovery capabilities.
- **Apply transactions:** reviewed changes, expected file versions, commit/recovery status, and reload results.

Use typed requests and structured results. Validate requests at process and privilege boundaries; avoid a generic elevated script-execution interface.

### Delivery sequence and acceptance

1. **Baseline and contracts:** record donor versions, language/tool parity inventories, representative configurations, and existing runtime behavior. Preserve the dirty `.gitignore` and donor checkouts.
2. **Core foundations:** extract the lossless syntax service; implement operation contracts, diagnostics, transaction recovery, and bounded IPC.
3. **Menu editor:** implement actual capture, direct manipulation, context scopes, provenance, property controls, undo/redo, and preview/apply verification.
4. **Complete visual authoring:** implement the full node language, remaining settings/theme controls, templates, and debugging views.
5. **Tool consolidation:** complete each donor’s parity ledger and shared implementation, including overlapping capabilities and recovery behavior.
6. **Integration and qualification:** package the complete application, verify upgrade/uninstall behavior, and perform Windows 11 x64 acceptance testing.

Required validation:

- **Language:** no-op byte preservation, surgical edits, import handling, graph/config round trips, and runtime semantic equivalence—including ordering and side effects.
- **Editing:** reorder/nesting across mixed native/custom entries, duplicate/localized titles, multiple selections, context restrictions, dynamic submenus, and external file conflicts.
- **Templates:** reuse across workspaces, merge/replace conflicts, missing dependencies, malicious archive paths, and backward compatibility.
- **Recovery:** interrupted multi-file writes, failed reloads, access denial, elevation cancellation, partial recursive failures, and failed restores.
- **Tools:** source-based parity fixtures and disposable Windows VM tests for registry, filesystem, ACL, resource, and Explorer effects.
- **UI:** keyboard navigation, accessibility, DPI scaling, light/dark themes, large menus/graphs, and human acceptance of the direct-editing workflow.
- **Packaging:** clean installation, upgrade, repair, and uninstall without deleting user configurations, templates, or backups. Replace the existing wildcard uninstall cleanup and surface registration failures.
- **Build:** native Release x64, self-contained Studio publication, and the pinned WiX build. Provision missing WiX tooling explicitly.

Local tests do not establish Explorer, installer, or human acceptance. Intermediate milestones are checkpoints; completion requires the visual-language coverage and all four tool parity ledgers to pass.

Implementation does not include publishing, enabling GitHub Actions, or applying system changes to the user’s active desktop without separate authorization.
