# Build and run Shell Studio

This qualification draft is stacked on the original Studio head and excludes
the twelve original review fixes. The [publication scope](pr-remediation.md#pr3-scope-and-dependencies)
distinguishes its changes from the combined candidate and from PR2's cloud lane.
The candidate record's historical package results do not qualify this branch.

These instructions target Windows 11 x64. Run repository commands from the
repository root in PowerShell. Use a checkout containing `src/studio`; the
current local implementation must be committed and made available before a
fresh remote clone can reproduce it.

The combined 1.9.20 preparation has built an unsigned Release/x64 package in an
isolated worktree and passed independent local checks. See the
[current candidate record](release-candidate-1.9.20.md) for source/package
identity, exact-package Sandbox results and remaining failures. The
[older local record](local-verification.md) describes its own historical source;
it does not qualify this candidate. Full Explorer, installer, donor and human
acceptance remain incomplete. Persistent reboot, upgrade and recovery scenarios
require a disposable Windows 11 x64 VM.

## 1. Install build prerequisites

1. Install Git for Windows if the checkout or its submodules need to be fetched.
2. Install Visual Studio or Visual Studio Build Tools with **Desktop development
   with C++**, an x64 C++ toolchain, MSBuild, and a Windows SDK. The tested
   environment uses Visual Studio 18 with toolset `v145`. The build script also
   accepts `-PlatformToolset v143` for an installed v143 toolset; that alternative
   is not qualified by this run. Microsoft documents the workload in
   [Install C and C++ support](https://learn.microsoft.com/en-us/cpp/build/vscpp-step-0-installation).
3. Install the **.NET 10 SDK for Windows x64**, not only the runtime. Use the
   [official .NET 10 download page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
   A separate .NET runtime is not needed to run the published Studio folder
   because the build publishes it self-contained.
4. Allow access to NuGet feeds during the first build. The project pins
   `WixToolset.Sdk/5.0.2`; `dotnet restore` obtains it. A global `wix.exe`, WiX
   Visual Studio extension, Node.js, and Python are not prerequisites for the
   normal combined build. WiX's
   [v5 SDK documentation](https://github.com/wixtoolset/wix/blob/v5.0.2/src/wix/WixToolset.Sdk/README.md)
   describes the SDK-style MSBuild integration used here.

Open a new PowerShell window after installing prerequisites and check:

```powershell
git --version
dotnet --list-sdks
Test-Path "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
```

The SDK list must include a `10.0` SDK and the final check should return `True`.
The build script finds Visual Studio using `vswhere` and initializes its C++
environment; an ordinary PowerShell window is sufficient. Building itself does
not require registering Shell or running the editor as administrator.

## 2. Prepare the checkout

Open PowerShell in the repository root—the directory containing `README.md`
and `src`. Keep existing changes and donor checkouts intact. On a fresh checkout,
initialize the pinned submodules:

```powershell
git submodule update --init --recursive
git submodule status --recursive
Test-Path .\src\studio\build.ps1
```

Do not use `--remote`: the parity ledger refers to the committed donor versions.
The standard native build uses the checked-in Detours and PlutoSVG libraries
under `src/shared/Library`. The historical native `packages.config` files are
not the Studio package restore entry point; the combined script restores its
managed and WiX projects itself.

## 3. Build the complete package

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\studio\build.ps1
```

This uses Release, x64, and `v145`. The execution-policy switch applies only to
that PowerShell process. The script builds `shell.dll`, `shell.exe`, the native
language DLL, self-contained Studio and ToolHost publications, the installer
custom-action DLL, and the MSI. It does not install or register anything.

If the installed C++ toolset is v143:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\studio\build.ps1 -PlatformToolset v143
```

To build applications without an MSI:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\studio\build.ps1 -SkipInstaller
```

The script also accepts `-Clean`, which removes and regenerates its native and
publication directories, including `bin/studio`. Keep those directories for
build outputs only. The combined solution supports **Release only**; it does
not define native Debug configurations. The x86/ARM64 options build native
components with `-SkipInstaller` and do not publish Studio.

The legacy entry point now delegates to the same pipeline:

```powershell
.\src\setup\build.cmd x64
```

Do not substitute a whole-solution MSBuild call for the combined script: it
does not perform the Studio publication/staging sequence needed by the MSI.

## 4. Locate the output

| Path | Purpose |
| --- | --- |
| `bin/studio/ShellStudio.exe` | Main application |
| `bin/studio/ShellStudio.Language.dll` | Shared native syntax and preview service |
| `bin/studio/ShellStudio.PreviewWorker.exe` | Bounded native preview process |
| `bin/studio/ToolHost/ShellStudio.ToolHost.exe` | Operation protocol host, not the main UI |
| `bin/shell.exe` | Native management window and registration controls |
| `bin/shell.dll` | Explorer extension |
| `bin/setup-x64.msi` | Combined installer |
| `src/studio/artifacts/` | Intermediate native and self-contained publication outputs |

Keep the entire `bin/studio` directory together, including its DLLs, runtime
files, licenses, and `ToolHost` directory. Copying only `ShellStudio.exe` is not
a portable deployment. Do not store personal configuration in a generated
build directory: packaging can replace the staged configuration from `src/bin`.

## 5. Run without installing

For the end-user choice between portable editing and a full registered install,
including backup, existing-version, and rollback instructions, start with
[Install and use Shell Studio](using-shell-studio.md).

From the repository root:

```powershell
.\bin\studio\ShellStudio.exe
```

The application looks for `shell.nss` beside itself and then in its parent
directory. With a complete package build, the parent is `bin`, containing the
staged sample configuration. Use **Open configuration** to select your own
working copy. You can also pass a configuration explicitly:

```powershell
.\bin\studio\ShellStudio.exe "C:\path\to\your\shell.nss"
```

Configuration inspection, the menu-definition view, expression editing,
templates, and operation previews are available without registering the
extension. Opening Studio does not activate the fork in Explorer. **Capture
menu** requires a matching loaded native extension and a configuration that
corresponds to that runtime.

Studio listens for captures automatically on startup. The default **Actual
menu** pane waits for an Explorer capture instead of showing unevaluated
configuration definitions as menu entries. Right-click a target matching the
context picker; changing the selection hides an incompatible previous capture
while preserving its editing state. **Arrange entries** retains access to
configuration definitions before capture. **Stop capture** stops listening;
**Capture menu** or choosing another context starts it again.

Only one Studio process can listen for a given Windows user/session. Listener
ownership is established before Studio changes the button to **Stop capture**.
If another instance owns the endpoint, Studio remains inactive, keeps **Capture
menu** visible, and reports `CAPTURE_LISTENER`; use the already-open instance or
close all Studio instances and start one. An active listener is only an armed
capture state, not evidence that a menu was received.

If Studio automatically opened the adjacent sample configuration, the first
accepted capture can open Explorer's actual configuration when there are no
pending edits. An explicitly opened configuration is never switched this way.
Installing an older upstream Shell build is insufficient: it cannot send the
Studio capture protocol, even when its context menu looks correct.

The menu editor and Tools page have resizable panes. Use **Move to…** to choose
a menu destination with the keyboard, or Alt+Up/Down while the menu list has
focus to reorder entries. Diagnostics expand when a problem is reported.
The expression canvas provides a node selector, root/parent navigation, zoom,
Fit, and an exact source preview. Saving an expression updates the editing
workspace; configuration changes still pass through **Review & apply**.
Tool searches retain field drafts, and running operations expose cancellation
when supported. The theme control switches between the authored light and
dark palettes; Windows high contrast takes precedence.

Open the native management window or its Customize entry point with:

```powershell
.\bin\shell.exe
.\bin\shell.exe -customize
```

`-customize` only launches the adjacent Studio application; it does not
register Shell or restart Explorer.

## Install and use live capture

This fork shares upstream Shell's MSI upgrade identity and default installation
directory. It is a replacement for normal Shell, not a side-by-side extension.
The exact final-package older-version upgrade is still unqualified, so follow
the [existing-Shell decision and backup procedure](using-shell-studio.md#do-i-have-to-uninstall-normal-shell)
before changing a registration.

1. In the disposable Windows 11 x64 test VM, preserve the existing Shell
   configuration and take a VM snapshot. Copy the built MSI to the VM and
   verify its SHA-256 against [local verification](local-verification.md).
2. For the safest current route, remove a normal Shell installation only after
   its configuration/imports/assets are backed up. Then double-click
   `setup-x64.msi`, complete the interactive installer, and accept its elevation
   prompt. The package is a locally built, unsigned development artifact; do
   not accept an unknown-publisher warning unless the hash matches. The
   installer invokes registration and can refresh Explorer. The build procedure
   above does not execute this step.
3. Confirm that no other Studio instance is already open, then start **Nilesoft
   Shell Studio** from the Start Menu, or open **Customize** in
   the installed `shell.exe`. The default installation folder is
   `%ProgramFiles%\Nilesoft Shell`; use your selected folder if you changed it.
4. Open the configuration used by that installed Shell runtime. For the default
   installation, this is `shell.nss` in the installation folder.
5. Choose a context category or common file type group in the picker. You can
   narrow a group to one extension or use **Choose target** for a specific file
   or folder. **File type groups** opens the editable group names and extension
   lists in Studio settings. Select **Capture menu**, then right-click the
   matching file, folder, selection, or background in Explorer. A mismatched
   capture leaves the current menu and edits intact. Use the Shell/classic menu;
   Windows 11's separate modern menu is outside this editor's scope. The matching
   extension reuses the live `construct_popup_entries` popup-construction path
   to materialize retained semantic submenu definitions into the capture-owned
   tree; opening, hovering, or scrolling each submenu is unnecessary. Automatic
   materialization uses a positive read-only allowlist for literals and approved
   control/math, string, selection, path, color, theme, view, and `this` reads.
   `cmd` and `args` remain syntax evidence, while conditions are evaluated only
   through that read-only policy. Capture never invokes commands, assignments,
   mutating loops, unknown functions, or unsafe providers. Current automatic
   bounds are depth 64, 4,096 items, 50,000 evaluation steps, and a 100 ms
   wall-clock budget; trace retention is 64 entries/1,024 characters and
   structured evidence is 128 rule/property records. Serialization and queue
   limits also apply. A cycle, unavailable source/provider, or reached bound
   reports `state`, `childrenCaptured`, `complete`, and diagnostics explicitly;
   it is never represented as a silently complete hierarchy. See [capture protocol](capture-protocol.md)
   for the wire-level fields and diagnostics.
6. Select entries in the menu preview to change properties. Keep the semantic
   capture and optional `appearance` evidence separate: captured native-rendered
   pixels may be enriched by opening a popup, but missing or unavailable pixels
   do not remove semantic descendants. Configuration and edited previews are
   structural and explicitly labeled. Choose **Arrange entries** to drag
   above/below to reorder; hold Shift while dropping onto a submenu to move
   inside it. Alt+Up/Down provide keyboard movement. Check the scope selector
   before creating native-entry rules. Entry details show source file/node/span,
   hash and import occurrence when proven, ordered rule outcomes,
   property effects, and effective settings evidence. `settings.modify` gates
   existing native-item changes; `settings.new` gates custom definitions. A
   stale hash, missing occurrence, or ambiguous selector keeps evidence readable
   but disables direct source editing. The normal property edit creates or
   updates one durable scoped Studio quick rule; **Open shared rule** is required
   to change a broader handwritten `modify`/`remove` declaration.
7. Use **Review & apply** to inspect the exact changes, then apply them. Protected
   configuration writes request elevation for the reviewed operation. New
   definitions go into `imports/studio.nss`; existing source edits preserve
   unrelated content and use conflict checks/backups.
8. Capture again after applying. A configuration preview is not proof of the
   menu Explorer displayed. Record the cases in [acceptance.md](acceptance.md).

Integrated tools have separate preview/Apply controls. Selecting a tool does
not execute it. Record system-operation results and restoration behavior in the
test VM; the fixture tests below do not establish those results.

See [Return to normal Shell](using-shell-studio.md#return-to-normal-shell) before
removing the fork. Preserve user configuration and restore only data files, not
binaries from another build.

## 6. Run the local verification suites

After a complete package build, build the capture serializer fixture from the
Visual Studio x64 developer shell with the v145 tools. The UI suite passes this
native output through the managed capture reader, including ordinary entries
whose only evidence is branch completeness:

```powershell
msbuild .\src\studio\native\tests\StudioCaptureSerializationTests.vcxproj /m:1 /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v145 /v:minimal
```

Use the packaged language DLL and the exact fixture executable explicitly:

```powershell
$languageDll = (Resolve-Path .\bin\studio\ShellStudio.Language.dll).Path
$previewWorker = (Resolve-Path .\bin\studio\ShellStudio.PreviewWorker.exe).Path
$captureFixture = (Resolve-Path .\src\studio\native\tests\bin\Release\x64\StudioCaptureSerializationTests.exe).Path

dotnet run --project .\src\studio\ShellStudio.Tests\ShellStudio.Tests.csproj -c Release --no-launch-profile "-p:NativeLanguagePath=$languageDll" "-p:PreviewWorkerPath=$previewWorker" -- --native
dotnet run --project .\src\studio\ShellStudio.UiTests\ShellStudio.UiTests.csproj -c Release --no-launch-profile "-p:NativeLanguagePath=$languageDll" "-p:NativeCaptureFixturePath=$captureFixture"
dotnet run --project .\src\studio\ShellStudio.Tools\Tests\ShellStudio.Tools.Tests.csproj -c Release --no-launch-profile
dotnet run --project .\src\studio\ShellStudio.NativeTests\ShellStudio.NativeTests.csproj -c Release --no-launch-profile
dotnet run --project .\src\studio\ShellStudio.PreviewWorker.ClientTests\ShellStudio.PreviewWorker.ClientTests.csproj -c Release --no-launch-profile
```

See the [candidate record](release-candidate-1.9.20.md) for current counts,
the [historical verification record](local-verification.md) for its earlier
revision, and the [interface design record](design-system.md) for UI review gates.
Run these sequentially: several projects share build
outputs. The WPF suite uses an offscreen window; the native resource suite only
changes its own temporary PE copy.

Core tests fail before build when either explicit native prerequisite is absent;
UI tests fail before build when the capture serializer fixture is absent.
Restore each managed project sequentially with `--disable-parallel -m:1
-p:BuildInParallel=false`, then run with `--no-restore`; retain NuGet audit and
report any vulnerability-feed failure. Lifecycle fixtures synchronize on bounded
worker readiness and record process identity before asserting cleanup.

The runtime submenu regression compiles the production `ContextMenu` constructor
and its dependencies. From the same x64 developer shell, build and run it
sequentially:

```powershell
msbuild .\src\studio\native\tests\ContextMenuConstructionTests.vcxproj /m:1 /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v145 /v:minimal
.\src\studio\native\bin\Release\x64\tests\ContextMenuConstructionTests.exe
```

It checks dynamic-only construction, capture-owned construction, nested rows,
settings and disabled-parent gates, invalid null menus, bounds and command nonexecution. Serialization
fixtures with synthetic children cannot replace this runtime regression.

To retain offscreen UI images, use the WPF command with these application
arguments:

```powershell
dotnet run --project .\src\studio\ShellStudio.UiTests\ShellStudio.UiTests.csproj -c Release --no-launch-profile "-p:NativeLanguagePath=$languageDll" "-p:NativeCaptureFixturePath=$captureFixture" -- --render-artifacts .\src\studio\artifacts\checks\ui
```

## Troubleshooting

| Symptom | Next step |
| --- | --- |
| `dotnet` missing or a .NET target-framework error | Install the .NET 10 x64 SDK and open a new terminal; check `dotnet --list-sdks`. |
| Missing `vswhere`, C++ tools, or SDK | Modify the Visual Studio installation to include the C++ workload and Windows SDK. |
| `MSB8020` / requested toolset unavailable | Install the requested toolset or pass `-PlatformToolset v143` for an installed v143 toolset. |
| NuGet or WiX SDK restore fails | Check feed access/proxy settings and retry the combined build; keep the pinned WiX SDK version. |
| Native DLL unavailable in Studio/tests | Build the combined package; keep the publication folder intact and use the explicit test DLL path above. |
| Build output is locked | Close the Studio instance using that output. For an Explorer-loaded DLL, test/install a separate package in the VM rather than overwriting the loaded file. |
| `CAPTURE_LISTENER` appears, or Capture never becomes active | Another Studio process owns the single current-user/session endpoint. Use that instance, or close every Studio instance and start exactly one. Do not troubleshoot registration until this collision is cleared. |
| **Stop capture** is visible but the menu remains empty | The listener is armed but no matching menu has arrived. Confirm that the matching extension is loaded for the same user/session, open the Shell/classic menu only after capture is armed, and match the selected context. Retained submenu definitions are discovered automatically; branches that require unavailable provider state or unsafe evaluation remain explicitly incomplete. An upstream/unmodified Shell DLL cannot supply this protocol. |
| Configuration differs from the captured runtime | Open the installed runtime's actual configuration and capture again; do not apply a staged sample to an unrelated installation. |
| Apply reports an external-file conflict | Reopen/reconcile the changed files, review the new diff, and retry. Do not discard the journal or overwrite newer content. |
| Windows Security displays a virus-protection toast after an Explorer refresh | Check the live Defender component state and Defender Operational log before concluding protection changed. Studio build/capture and Explorer refresh do not change Defender preferences. **Clear shell histories** with **History scope: Defender** is separate and runs only after explicit review and elevation. |

Python is needed only when regenerating the source coverage inventories, not
for ordinary builds or application use. That contributor workflow is described
in [language-coverage.md](language-coverage.md).
