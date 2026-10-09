# Install and use Shell Studio

Shell Studio can be used in two different modes. Choose the mode before changing
an existing Nilesoft Shell installation.

> **Current status:** this repository produces a local development build, not a
> signed release. The current source has passed local deterministic checks and
> compiled through the self-contained Studio and ToolHost publications, but
> package synchronization stopped at the Explorer-loaded `bin\shell.dll`. The
> repository's existing MSI predates the 2026-09-12 source-backed editing and
> automatic semantic-capture changes. Build a complete current-source package
> in a disposable Windows 11 x64 VM before testing those live features. Record
> the rebuilt MSI's SHA-256 in a new package receipt before accepting an
> unknown-publisher warning. The hash in
> [local verification](local-verification.md) identifies the older package only.

## Choose a mode

| Goal | What to run | Effect on the installed Shell |
| --- | --- | --- |
| Inspect or edit a copied configuration without live capture | `bin\studio\ShellStudio.exe` | None. Studio does not install or register the extension. |
| Capture the actual Explorer menu, apply to the live configuration, and recapture | `bin\setup-x64.msi`, then the installed **Nilesoft Shell Studio** shortcut | Replaces the registered normal Shell build with this fork. |

An upstream or older `shell.dll` cannot send Studio's capture protocol. Portable
Studio can still inspect definitions, edit expressions, use templates, and
preview reviewed operations, but **Actual menu** capture requires this fork's
matching registered native extension.

## Do I have to uninstall normal Shell?

Do not try to keep both versions registered. Windows Explorer has one active
Shell registration, and this fork deliberately retains Nilesoft Shell's MSI
upgrade identity and default `%ProgramFiles%\Nilesoft Shell` directory.

- An older MSI installation is intended to be detected and upgraded in place.
  A manual uninstall is not inherently required by the MSI design.
- For this development build, the safe recommendation is nevertheless to back
  up first and test in a VM. The exact older-version upgrade has not passed the
  final-package acceptance matrix, and older upstream installers contain broad
  uninstall cleanup that must be tested during an upgrade.
- On an active PC, use a backed-up clean replacement: uninstall normal Shell,
  verify that it is no longer active, and then install this package. This avoids
  relying on the presently unqualified upgrade path.
- If the installed version is equal to or newer than this package, side-by-side
  installation is not supported. Setup may enter maintenance mode or reject the
  downgrade; back up and remove the existing product first.
- If normal Shell was installed through WinGet, Scoop, Chocolatey, or portable
  registration, use that method's documented uninstall or unregister step. Do
  not register two different `shell.dll` files.

Official Shell installation, uninstallation, portable registration, and
unregistration commands are documented at
[nilesoft.org/docs/installation](https://www.nilesoft.org/docs/installation).

## Back up before replacement

Preserve the whole existing directory for recovery, but restore only user data
after installing the fork—never copy old `shell.exe` or `shell.dll` files over
the new installation.

```powershell
$shellBackup = Join-Path $env:USERPROFILE ("Documents\Nilesoft-Shell-backup-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Path $shellBackup | Out-Null

$installedShell = Join-Path $env:ProgramFiles "Nilesoft Shell"
if (Test-Path $installedShell) {
    Copy-Item $installedShell (Join-Path $shellBackup "ProgramFiles") -Recurse
}

$studioState = Join-Path $env:LOCALAPPDATA "QweShell"
if (Test-Path $studioState) {
    Copy-Item $studioState (Join-Path $shellBackup "QweShell-AppData") -Recurse
}
```

Confirm that the backup contains `shell.nss`, the `imports` directory, and any
user assets or saved template packages before uninstalling anything. The custom
MSI marks its configuration/import components permanent and non-overwriting,
but that is not a substitute for a backup of data created under another build.

## Preview or edit without installing

1. Keep the entire `bin\studio` directory together.
2. Start `bin\studio\ShellStudio.exe`.
3. Select **Open configuration** and open a working copy of `shell.nss` outside
   the generated `bin` directory.
4. Use **Arrange entries**, source-backed entry details, the expression canvas, templates,
   and tool previews. These actions do not register Shell or execute menu
   commands.
5. Select **Review & apply** only when the displayed paths and exact file diff
   identify the intended working copy.

The **Actual menu** pane will wait for a compatible capture in this mode unless
the matching forked extension is already installed and registered.

The **Native preview** page starts with external reads disabled. Choose
**Preview read scopes** only when an expression needs a specific local file,
environment variable, registry key/value, or image/font/icon resource. Each
line grants one exact scope for the open workspace and is snapshotted per
revision; templates cannot add scopes. Registry lines use
`hive|key|value name`; leave the final field empty for a key/default-value
scope. Studio supplies both existence and value reads for that exact scope,
including `reg.exists`, `reg.get`, and the documented bare `reg(...)` alias.
Use **Refresh read snapshot** to read the same scopes again deliberately.
Network paths, writes, commands, and unlisted functions remain unavailable.

## Install for the full live workflow

1. Start with a disposable Windows 11 x64 VM or a restorable VM snapshot.
2. Copy `bin\setup-x64.msi` into the VM and calculate its SHA-256:

   ```powershell
   Get-FileHash .\setup-x64.msi -Algorithm SHA256
   ```

   Retain that hash with the build receipt and use it to verify the copy inside
   the VM. The hash currently listed in
   [local-verification.md](local-verification.md) belongs to the historical MSI
   and should match only when deliberately repeating that historical package's
   acceptance cases.

3. If normal Shell is installed, complete the backup above. For the safest
   current route, uninstall it through **Settings → Apps → Installed apps** and
   allow its Explorer refresh. Confirm the ordinary Shell menu is no longer
   active before continuing.
4. Double-click the verified custom MSI, accept elevation, and allow Explorer
   to refresh. The default installation directory is
   `%ProgramFiles%\Nilesoft Shell`.
5. If a clean removal took user files with it, restore only `shell.nss`,
   `imports`, user images/assets, and saved template packages. Do not restore
   executables or DLLs from the old installation.
6. Start **Nilesoft Shell Studio** from the Start Menu. It should run
   `%ProgramFiles%\Nilesoft Shell\Studio\ShellStudio.exe`. Keep exactly one
   Studio instance open for the current Windows session.
7. Open the `shell.nss` used by the installed runtime. With the default install,
   it is `%ProgramFiles%\Nilesoft Shell\shell.nss`.
8. Choose the context category or file-type group, select **Capture menu**, and
   right-click the matching file, folder, background, drive, or selection in
   Explorer's Shell/classic menu. The matching extension reuses the live
   `construct_popup_entries` popup-construction path and automatically materializes retained semantic
   submenu definitions; you do not need to open, scroll through, or hover every
   submenu. Automatic discovery uses a positive read-only allowlist for literals
   and approved control/math, string, selection, path, color, theme, view, and
   `this` reads. `cmd` and `args` remain syntax nodes and are never invoked;
   assignments, mutating loops, unknown or unsupported functions, and unsafe
   providers are denied. The current traversal bounds are depth 64, 4,096
   items, 50,000 evaluation steps, and 100 ms; trace/evidence, serialization,
   and queue limits are also explicit. A provider, cycle, expression, source,
   or bound that cannot be inspected safely remains visibly incomplete with a
   diagnostic. Opening a submenu can add observed native appearance evidence,
   but it is not required for complete semantic branches.
9. Select an entry to edit it. Use **Arrange entries** to reorder; hold Shift
   while dropping on a submenu to move inside it. Use the scope selector before
   creating a rule for an existing native entry. Entry details show source
   file/node/span, hash and import occurrence when proven, ordered `ruleOutcomes`,
   `propertyEffects`, and effective settings evidence. `settings.modify` gates
   existing native-item changes and `settings.new` gates custom definitions. A
   stale hash, missing occurrence, or ambiguous selector keeps evidence readable
   but disables direct source editing. **Open shared rule** identifies edits that
   can affect other entries; ordinary property controls create or update one
   durable scoped quick rule.
10. Select **Review & apply**, inspect every target and exact diff, and apply.
    Protected configuration files request elevation for only the reviewed
    operation. If Studio reports an external edit or stale review, reopen and
    review again instead of forcing the write.
11. Capture the same context again. Only the later Explorer capture verifies
    what the live runtime displayed; a configuration preview is not that proof.

The official Shell shortcut to reload configuration is Ctrl+right-click on the
desktop or taskbar; restarting Explorer is the fallback. Studio's post-apply
capture should still be used to compare expected and observed state.

## Capture troubleshooting

Studio has one capture endpoint per Windows user/session. When another Studio
instance already owns it, the newer instance reports `CAPTURE_LISTENER`, keeps
**Capture menu** visible, and does not claim to be listening. Use the first
instance, or close every Studio window and start exactly one.

When **Stop capture** is visible, the listener is armed; that label is not proof
that Explorer delivered a menu. If the **Actual menu** pane still says that no
menu was captured:

1. Confirm that the registered `shell.dll` is from the same fork/build as Studio,
   not an upstream or older installation.
2. Confirm that Studio and Explorer run as the same Windows user and in the same
   interactive session.
3. Choose the matching context in Studio before opening Explorer's Shell/classic
   menu. Windows 11's separate modern menu is outside this capture path.
4. Inspect any branch marked incomplete. Automatic discovery is governed by
   the positive read-only allowlist and does not execute commands, assignments,
   or mutating loops; unknown/unsupported functions, provider failures, cycles,
   source-occurrence gaps, and capture limits remain explicit. Opening that
   submenu may add observed rows or appearance, but cannot make an unavailable
   semantic branch complete or discard descendants already captured.
5. Confirm that Studio opened the configuration used by the registered runtime.

The current source passed listener-collision, managed capture, and native
transport regressions. A post-fix human-driven Explorer capture remains a
separate acceptance case; local/offscreen checks do not substitute for it.

## Windows Security notifications

Studio build, launch, capture, shutdown, and Explorer-refresh operations do not
disable Microsoft Defender or call its real-time-protection preference APIs.
A Windows Security toast appearing after an Explorer or Security Center refresh
is not proof that protection changed. Inspect the live Defender component state
and the Defender Operational log before drawing that conclusion.

The integrated **Clear shell histories** tool with **History scope: Defender**
is separate from capture. It requires explicit preview, Apply, elevation, and a
startup/reboot boundary. Its reviewed task removes Defender history, quarantine,
and engine-database data and then removes itself; it does not disable real-time
protection. Do not run it as a response to a notification unless deleting that
data is the intended action.

## Return to normal Shell

1. Back up the current `shell.nss`, `imports`, Studio-generated files, assets,
   templates, and `%LOCALAPPDATA%\QweShell` state.
2. Uninstall the custom **Nilesoft Shell** entry from Windows Settings and allow
   Explorer to refresh.
3. Reinstall normal Shell only from the
   [official download page](https://www.nilesoft.org/download).
4. Restore compatible user configuration files, not binaries, and reload the
   configuration. Keep the backup until the normal menu is verified.

Do not delete retained configuration or recovery data merely because the custom
application binaries were removed.

## What is not yet established

Local tests do not prove the active-PC install, older-version upgrade,
interactive UAC flow, live Explorer capture/apply/recapture, donor system
operations, physical-monitor DPI, assistive technology, or human acceptance.
Record those cases in [acceptance.md](acceptance.md); historical Sandbox results
for an earlier MSI are in [sandbox-integration.md](sandbox-integration.md).
