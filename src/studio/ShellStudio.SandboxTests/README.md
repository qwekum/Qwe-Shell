# Native-renderer Sandbox harness

This harness starts the real portable Studio window and capture client inside a
disposable Windows Sandbox. It refuses accounts other than `WDAGUtilityAccount`.
It requires native-renderer v2 pixels, valid premultiplication, title pixels in
the shipped light-theme fixture, a hit target for every root fixture entry,
Studio's bitmap display mode, and selection of the entry behind a mapped row.
The `submenu` mode additionally requires a native submenu image, navigation to
that image in Studio, complete fixture child mappings, and child selection. These checks
do not establish human acceptance, installer behavior, or desktop blur parity.

Build the portable application first with `src/studio/build.ps1 -SkipInstaller`.
From the repository root, publish the harness into a task-owned ignored folder:

```powershell
dotnet publish src/studio/ShellStudio.SandboxTests/ShellStudio.SandboxTests.csproj -c Release -o Sandbox/<run>/input/harness
```

The project intentionally references the packaged assemblies in `bin/studio`;
it does not trigger a concurrent Studio or native build. Use a fresh guest after
changing those assemblies. Runtime/build directories remain ignored.

Stage these resources under `Sandbox/<run>/`:

- `input/app/`: an exact copy of the portable `bin/` directory.
- `input/manifest.json`: `HostComputer`, unique `RunId`, and `Files`, each with
  repository-independent relative `Path` and `SHA256` for every staged app file.
- `input/guest.ps1`: copy of `../tools/SandboxRenderer/guest.ps1`.
- `input/renderer-fixture.nss`: copy of `../tools/SandboxRenderer/renderer-fixture.nss`.
- `run-case.ps1`: copy of `../tools/SandboxRenderer/run-case.ps1`.
- `evidence/`: writable output directory.

Create a `.wsb` configuration mapping `input` read-only to `C:\QweShellInput`
and `evidence` writable to `C:\QweShellEvidence`, with networking, clipboard,
audio input, and video input disabled. Keep the machine-specific configuration
and manifest in the ignored run folder. Start and connect one task-owned guest
using `wsb`; invoke `guest.ps1 Prepare` and `guest.ps1 Register` using
`wsb exec --run-as ExistingLogin`. Both operations must succeed before capture.
Registration is confined to that disposable guest. No installer is used.

Run one case from the host with its owned Sandbox ID and a new evidence name:

```powershell
& Sandbox/<run>/run-case.ps1 -SandboxId <owned-guid> -RunName dpi144-1 -ExpectedDpi 144
& Sandbox/<run>/run-case.ps1 -SandboxId <owned-guid> -RunName dpi144-submenu -Mode submenu -ExpectedDpi 144
```

The guest script dismisses the previous menu, launches the harness, opens the
selected-folder menu, and hovers the named submenu using guest-only native
geometry. Screenshots are test evidence only. Repeat with five distinct names
for the consecutive-run gate. Use `-ExpectedDpi 96` only in a guest measured at
96 DPI; the script asserts the reported DPI and does not change host scaling.
Retain results, snapshots, screenshots, manifests and ownership records, then
stop the exact task-owned guest. Review exported PNGs as well as JSON results.

For the rendered-state matrix, prepare a separate fresh guest using
`guest.ps1 Prepare -Mode matrix`, then register and run `run-case.ps1 -Mode matrix`.
Preparation adds the supplied fixture import only to the guest configuration
after verifying the original package hashes. The fixture covers Unicode,
mnemonics, checked and disabled rows, an icon, and an inserted separator.
The harness navigates and selects within its image and checks that the command
sentinel file was not created. Inspect the exported submenu PNG for glyph and
state rendering; the automated assertions do not substitute for that inspection.

The standalone state/effect tests require no registration or Sandbox. From an
x64 Visual Studio developer prompt at the repository root:

```powershell
msbuild src/studio/native/tests/ContextMenuPaintTests.vcxproj /p:Configuration=Release /p:Platform=x64 /v:minimal
& src/studio/native/tests/bin/Release/ContextMenuPaintTests.exe
```

Those tests exercise the actual shared planning boundary and assert that item
storage remains untouched. They cover selection/tooltip/geometry receipts and
disabled-static partial redraws; they do not claim glyph rasterization coverage.

The Win32 bitmap-boundary tests exercise the real row surface separately:

```powershell
msbuild src/studio/native/tests/NativeRowSurfaceTests.vcxproj /p:Configuration=Release /p:Platform=x64 /p:TrackFileAccess=false /v:minimal
& src/studio/native/tests/bin/Release/x64/NativeRowSurfaceTests.exe
```

They check translated row pixels, one original callback and one cache sink,
preservation when presentation is skipped, bounded allocation fallback, and
GDI cleanup. Sink assertions inspect the DC while it is alive.
