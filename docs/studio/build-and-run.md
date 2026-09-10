# Build and run Shell Studio

These instructions target Windows 11 x64. Run repository commands from the
repository root in PowerShell. Use a checkout containing `src/studio`; the
current local implementation must be committed and made available before a
fresh remote clone can reproduce it.

The application and MSI have passed the [recorded local checks](local-verification.md).
Live Explorer, installer lifecycle, system-operation parity, and human
acceptance are still pending. Use a disposable Windows 11 x64 VM for that
qualification work.

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
| `bin/studio/ShellStudio.Language.dll` | Shared native syntax service |
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

Open the native management window or its Customize entry point with:

```powershell
.\bin\shell.exe
.\bin\shell.exe -customize
```

`-customize` only launches the adjacent Studio application; it does not
register Shell or restart Explorer.

## Install and use live capture

1. In the disposable Windows 11 x64 test VM, preserve the existing Shell
   configuration and take a VM snapshot. Copy the built MSI to the VM.
2. Double-click `setup-x64.msi`, complete the interactive installer, and accept
   its elevation prompt. The installer invokes registration; this is a system
   change and can refresh Explorer. The local build procedure above does not
   execute this step.
3. Start **Nilesoft Shell Studio** from the Start Menu, or open **Customize** in
   the installed `shell.exe`. The default installation folder is
   `%ProgramFiles%\Nilesoft Shell`; use your selected folder if you changed it.
4. Open the configuration used by that installed Shell runtime. For the default
   installation, this is `shell.nss` in the installation folder.
5. Select **Capture menu**, then right-click a file, folder, selection, or
   background in Explorer. Use the Shell/classic menu; Windows 11's separate
   modern menu is outside this editor's scope. Open lazy submenus while capture
   is active so their children can be collected.
6. Select entries to change properties. Drag above/below to reorder; hold Shift
   while dropping onto a submenu to move inside it. Alt+Up/Down provide keyboard
   movement. Check the scope selector before creating native-entry rules.
7. Use **Review & apply** to inspect the exact changes, then apply them. Protected
   configuration writes request elevation for the reviewed operation. New
   definitions go into `imports/studio.nss`; existing source edits preserve
   unrelated content and use conflict checks/backups.
8. Capture again after applying. A configuration preview is not proof of the
   menu Explorer displayed. Record the cases in [acceptance.md](acceptance.md).

Integrated tools have separate preview/Apply controls. Selecting a tool does
not execute it. Record system-operation results and restoration behavior in the
test VM; the fixture tests below do not establish those results.

## 6. Run the local verification suites

After a complete package build, use the packaged language DLL explicitly:

```powershell
$languageDll = (Resolve-Path .\bin\studio\ShellStudio.Language.dll).Path

dotnet run --project .\src\studio\ShellStudio.Tests\ShellStudio.Tests.csproj -c Release --no-launch-profile "-p:NativeLanguagePath=$languageDll" -- --native
dotnet run --project .\src\studio\ShellStudio.UiTests\ShellStudio.UiTests.csproj -c Release --no-launch-profile "-p:NativeLanguagePath=$languageDll"
dotnet run --project .\src\studio\ShellStudio.Tools\Tests\ShellStudio.Tools.Tests.csproj -c Release --no-launch-profile
dotnet run --project .\src\studio\ShellStudio.NativeTests\ShellStudio.NativeTests.csproj -c Release --no-launch-profile
```

The recorded result is 39 Core/native, 16 WPF, 34 tool-fixture, and 4 native
resource checks passing. Run these sequentially: several projects share build
outputs. The WPF suite uses an offscreen window; the native resource suite only
changes its own temporary PE copy.

To retain offscreen UI images, use the WPF command with these application
arguments:

```powershell
dotnet run --project .\src\studio\ShellStudio.UiTests\ShellStudio.UiTests.csproj -c Release --no-launch-profile "-p:NativeLanguagePath=$languageDll" -- --render-artifacts .\src\studio\artifacts\checks\ui
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
| Capture waits or has no entries | Confirm that the matching extension is loaded for the same user/session, click Capture before opening the classic menu, and open lazy submenus during capture. An upstream/unmodified Shell DLL cannot supply this capture protocol. |
| Configuration differs from the captured runtime | Open the installed runtime's actual configuration and capture again; do not apply a staged sample to an unrelated installation. |
| Apply reports an external-file conflict | Reopen/reconcile the changed files, review the new diff, and retry. Do not discard the journal or overwrite newer content. |

Python is needed only when regenerating the source coverage inventories, not
for ordinary builds or application use. That contributor workflow is described
in [language-coverage.md](language-coverage.md).
