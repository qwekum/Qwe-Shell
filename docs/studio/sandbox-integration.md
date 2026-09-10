# Windows Sandbox integration — 2026-09-10

Status: **installer checkpoint passed; overall acceptance incomplete**.
Testing used fresh Windows 11 Enterprise x64 Sandbox guests, build
`26100.9445`, with networking, clipboard, audio, and video input disabled.
Only a read-only package/script directory and a writable evidence directory
were mapped. Installation and registration affected the guest only.

The fixed MSI is 54,385,858 bytes, SHA-256
`66D05AEE47EBC2FCFF0B6DEC0A6C8E52DC9B68C83982E2D2D370B9F3FDA88557`.
[Local verification](local-verification.md) records its binary hashes and build.

## Observed results

| Case | Result and evidence |
| --- | --- |
| Clean starting state | No installed registrar; guest verified the package hash before installation |
| Silent clean install | MSI exit `0`; installed files and both Shell CLSIDs present; approximately 126 seconds including guest installer startup |
| Successful registrar | `shell.exe -r -s -t` returned `0`; registered DLL points to the installed native binary |
| Registrar without adjacent DLL | Isolated copy returned `1`; no false success |
| Packaged Studio startup | Resolved the installed Start Menu shortcut, launched its target with `--render-to`, loaded the shipped configuration, produced a PNG, and exited `0` |
| Silent repair | `/fvomus` returned `0`; seven seeded user files retained their exact SHA-256 hashes |
| Silent uninstall | `/x` returned `0`; all seven seeded files remained byte-identical |
| Removal state | Registrar, Studio executable, Start Menu shortcut, and both Shell CLSIDs absent; remaining configuration files exported |

The seven preservation fixtures cover the root configuration, a handwritten
import, `imports/studio.nss`, a modified shipped theme import, an asset, a
template, and a recovery backup. These test preservation of file contents;
they do not establish semantic template import or recovery execution.
Repair tested an intact installation with edited user files, not restoration
of deliberately deleted application files or registration.

The startup check uses the real installed self-contained application and native
parser. It is an offscreen render, not a Start Menu click, management-window
Customize check, or actual Explorer capture. No Computer Use was performed
after the user's request to continue without it.

## Defect and regression coverage

The previous package
`C8CA55391534E7B9F47C911727B8553F19C5CCEAEC2EA641EAB0343FB4A3700A`
failed MSI installation with `1603` after registration succeeded. Its direct
registrar probe returned `1` on success and `0` when its DLL was absent.
`wWinMain` returned the Boolean result of `Register` as a process exit code.
The fix explicitly maps `true` to `0` and `false` to `1`; the MSI's checked
custom-action failure contract is preserved.

The first guest wrapper timed out at 120 seconds; the MSI itself finished
roughly five seconds later. Subsequent wrappers allowed 300 seconds. The
MSI log, successful registration record, and direct process probes establish
the exit-code defect independently of that wrapper timeout.

The reusable regression test is
`src/studio/tools/Test-SandboxRegistrar.ps1`. Copy it into a disposable Sandbox
with the installed package and run it **inside that guest**, providing the
installed directory and writable evidence directory as parameters. It refuses
accounts other than the Sandbox guest account, checks success and missing-DLL
failure, and records bounded process results. It changes guest registration;
do not adapt it to run against the active host installation. The final test
run followed completed repair and passed both cases.

## Retained evidence

Machine-specific configurations and logs stay in the ignored `Sandbox/` tree:

- `20260910-160034-6adec5fe/`: original package, failed-install log, red
  registrar probes, and fixed-build command log/result.
- `20260910-161300-fixed/`: fixed package, manifest, Sandbox ownership/config,
  guest scripts, and `evidence/` with `Preflight.json`, `Install.json`,
  `Inspect.json`, `RegisterProbe.json`, `RegistrarMissingDll.json`,
  `registrar-exit-codes.json`, `Render.json`, `installed-studio.png`,
  `Seed.json`, `Repair.json`, `Uninstall.json`, `Retained.json`,
  `FinalExport.json`, MSI logs, and exported retained files.

The pre-fix and post-fix guest lifetimes are separate. Exported results are
retained outside the guest; the Sandbox instance is disposable. No host
installation, host registration, publication, or GitHub Actions run is part
of this checkpoint.

## Pending

Upgrade from a distinct earlier product, damaged-install repair, injected
registration failure propagated through MSI, interactive/UAC installation,
Start Menu and both Customize entry points, live Explorer capture/edit/apply/
recapture, runtime reload and interruption, donor system effects, and human
UI/accessibility/DPI checks remain unqualified. Continue with the
[roadmap](roadmap.md) and full [acceptance matrix](acceptance.md).
