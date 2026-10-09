# Shell Studio implementation and verification

The root README contains the approved product scope. This directory describes the implementation and its evidence. A successful build or local test run does not establish the Windows Explorer, installer, system-operation, or human acceptance gates in that scope.

Start with [Install and use](using-shell-studio.md) when the goal is to run the
application or replace an existing normal Shell installation. [Build and run](build-and-run.md)
contains contributor prerequisites and commands. See [the local verification
record](local-verification.md) for built artifact hashes and check results.

See the [current roadmap](roadmap.md) for delivery status and the
[Sandbox integration record](sandbox-integration.md) for real guest installer
checks and their remaining qualification boundaries.

## Working with a configuration

Open a root `.nss` configuration in Studio. The configuration view shows custom definitions without evaluating their commands or runtime conditions. Read-only import resolution loads supported deterministic paths and reports unresolved imports. **Capture menu** requests the actual classic menu from the matching native extension. Once capture is armed, the extension reuses the live `construct_popup_entries` popup-construction path to materialize retained semantic submenu definitions into a bounded tree; opening, hovering, or scrolling every submenu is unnecessary. Automatic materialization uses a positive allowlist of read-only literal, control/math, string, selection, path, color, theme, view, and `this` reads. `cmd` and `args` remain syntax evidence: commands, assignments, mutating loops, unknown or unsupported functions, and unsafe providers are never executed and leave an explicit incomplete branch. An observed nested popup can add native-rendered appearance evidence without determining semantic child availability. Run one Studio instance per Windows user/session. A second instance cannot own the single capture endpoint and reports `CAPTURE_LISTENER` instead of presenting a false active state.

Select a menu entry to edit its properties. Drag above or below an entry to reorder, or hold Shift while dropping on a submenu to move inside it. Alt+Up and Alt+Down provide keyboard movement. The scope selector controls generated native-entry matching rules. Removing a native entry creates a hide rule; removing a custom entry deletes its source definition. Entry details expose source, rule-outcome, property-effect, and effective-settings evidence when it was published. `settings.modify` gates changes to existing entries and `settings.new` gates custom definitions. Ordinary property controls create or update one durable scoped quick rule; **Open shared rule** is required before changing a broader handwritten rule. Ambiguous destinations, duplicate native matches, and unsupported moves produce diagnostics.

Property expressions open an ordered tree on the Expression canvas. Card movement and zoom affect layout only. Edits are parsed without executing expressions. Save expression updates the workspace; Review & apply remains a separate action. Appearance & settings exposes configuration declarations and their value canvases.

The function/value picker includes source-derived entries and their accepted argument counts. **Named member or imported function** collects identifiers and call shapes for localization, named images/SVGs, runtime menu IDs, and imported symbols. Syntax acceptance does not imply that a runtime-dependent symbol will resolve in every context.

Edits remain in memory until Review & apply. Studio shows the exact proposed changes, checks the original file hashes, stages files, retains backups, and writes a recovery journal. New declarations use `imports/studio.nss`. Imported source files retain unrelated bytes, comments, encoding, and whitespace. External edits stop application rather than being overwritten. A saved generation requires a matching subsequent capture before the UI labels it captured after apply.

Templates contain configuration, assets, metadata, and optional canvas layouts. Merge and replace are separate reviewed operations. Loading a template never executes its commands or applies system settings. Integrated tools have their own preview and Apply controls; opening a tool or generating its context-menu entry does not execute it.

## Components

| Component | Responsibility |
|---|---|
| `src/studio/ShellStudio` | WPF menu editor, expression canvas, capture client, template UI, operation pages, reviewed elevation |
| `src/studio/ShellStudio.Core` | Source identities and preservation, restricted imports, configuration edits, templates, transaction/recovery contracts |
| `src/studio/native` and native Parser sources | Syntax-only native validation, source-mapped document tree, language catalogue |
| `src/studio/ShellStudio.Tools` | Shared donor operation implementations and preview/recovery models |
| `src/studio/ShellStudio.ToolHost` | Bounded operation-host protocol |
| Native capture and initializer sources | Classic-menu snapshots, provenance, transaction exclusion, generation reload |

See [capture protocol](capture-protocol.md), [language coverage](language-coverage.md), and [tool parity](tool-parity.md) for their contracts and qualification boundaries.

Ordinary build, launch, capture, and Explorer-refresh paths do not disable
Microsoft Defender. The Defender Protection-history operation is a separate,
explicitly reviewed system tool; its scope and qualification limits are recorded
in [tool parity](tool-parity.md#deliberate-gaps-and-qualification-boundaries).

## Local checks

Build the combined Windows x64 package with `./src/studio/build.ps1` from the repository root. The script uses the installed Visual Studio C++ toolchain, .NET 10, and the pinned WiX SDK to produce `bin/studio/ShellStudio.exe`, `bin/studio/ToolHost/ShellStudio.ToolHost.exe`, the native Shell binaries, and `bin/setup-x64.msi`. It does not register the extension or install the MSI. `-SkipInstaller` produces the applications without the installer.

The default native toolset is `v145`, matching Visual Studio 18 on the development host. Pass `-PlatformToolset v143` for an installation with that toolset. The requested toolset and a compatible Windows SDK must already be installed. The combined native solution supports Release only; the script rejects Debug before building.

When verifying a combined publication, pass `/p:NativeLanguagePath=<absolute path to bin/studio/ShellStudio.Language.dll>` to the managed Core and WPF test commands below, and compare the copied test DLL hash with the packaged DLL. This prevents an older standalone language build from being used as evidence for a newer package.

Run from the repository root after the combined build. These commands explicitly use the packaged language DLL. The test projects' fallback path, used only without this override, is `src/studio/native/bin/Release/x64/ShellStudio.Language.dll`.

```powershell
$languageDll = (Resolve-Path .\bin\studio\ShellStudio.Language.dll).Path
dotnet run --project src/studio/ShellStudio.Tests/ShellStudio.Tests.csproj -c Release --no-launch-profile "-p:NativeLanguagePath=$languageDll" -- --native
dotnet run --project src/studio/ShellStudio.UiTests/ShellStudio.UiTests.csproj -c Release --no-launch-profile "-p:NativeLanguagePath=$languageDll"
dotnet run --project src/studio/ShellStudio.NativeTests/ShellStudio.NativeTests.csproj -c Release --no-launch-profile
dotnet run --project src/studio/ShellStudio.Tools/Tests/ShellStudio.Tools.Tests.csproj -c Release --no-launch-profile
```

The Core suite tests byte preservation, source identity, hash conflicts, multi-file rollback and recovery, template boundaries, rule matching, and native parsing of repository fixtures. The WPF suite runs an offscreen application with temporary configuration files. It exercises selection after reorder, property controls, unified undo/redo, expression span editing, invalid-expression rejection, starter-template syntax, and operation selection without execution. It does not register the extension or mutate the active desktop.

The managed tools suite exercises the shared operation catalog, typed launches, Explorer refresh result handling, recursive folder edits, INI and registry preservation, history cleanup, feature flags, ACL journaling seams, and recovery boundaries. Its in-memory providers do not establish live Explorer, registry, ACL, resource, UAC, or installer behavior.

The native resource suite copies a PE file into its own temporary directory and calls the Windows resource APIs on that copy. It checks that replacement preserves icon images shared by other groups, other languages, and unrelated resources, and that invalid input/cancellation leaves the file unchanged. It does not modify Windows resource files or establish live thumbnail behavior.

## Acceptance still requiring an appropriate environment

The [Windows acceptance record](acceptance.md) lists the required cases and evidence to retain.

Use a disposable Windows 11 x64 VM for live Explorer capture and post-apply verification, mixed native/custom ordering and nesting, dynamic and localized menus, runtime reload failure, UAC cancellation, donor registry/ACL/resource effects, recursive cancellation and partial failure, and clean-install/upgrade/repair/uninstall behavior. Retain the original VM state and record the exact tested build. Human review must assess the editing workflow, keyboard and accessibility behavior, DPI scaling, and appearance. These checks cannot be inferred from fixture, parser, offscreen, or build results.
