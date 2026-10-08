# Folder thumbnail style

In **Integrated tools → Folder thumbnail style**, choose **Full size** or
**Default (half-covered)**, preview the change, then apply the reviewed operation.
Only a resource change requests administrator access. The Windows resource is the
authority; a saved checkbox does not stand in for the installed mask.

The setting replaces mask icon group 6, language 1033, in the current Windows
`SystemResources/imageres.dll.mun`. Its two built-in masks come from
FolderThumbnailFix 1.0.9; [asset provenance](../../src/studio/ShellStudio.Tools/Assets/FolderThumbnails/README.md)
and the accompanying MIT notice are retained. No donor executable or Resource
Hacker is required. An unrecognized mask is identified as such in the preview
and backed up before replacement.

The resource hash is part of the reviewed preview. A resource changed since
review must be reviewed again. Selecting the already installed mask skips the
resource write and elevation. If the Explorer refresh option remains checked,
that explicitly selected refresh still runs. Selecting Default patches the current resource with the
half-cover mask; it never overwrites a newer Windows file with an older system
file just to change the style.

**Restart Explorer and reset thumbnail cache** is enabled by default. It closes
Explorer windows and restarts only the verified shell in the current session.
The screen may temporarily go black; Windows can take 30–60 seconds to restart
Explorer. If it has not restarted after a minute, use Ctrl+Alt+Del to open Task
Manager and run `explorer.exe`, or sign out and sign back in.
If Studio cannot verify the current shell and blocks the refresh, leave this
option unchecked and sign out/in after applying the style.

If thumbnails have not changed, sign out and sign back in. Restarting Windows
is not a substitute for this troubleshooting step. If Windows refuses the
resource write because a process has it locked, retry the setting from Safe Mode
Command Prompt with the portable ToolHost. Run ToolHost from an administrator
command prompt:

```powershell
.\ShellStudio.ToolHost.exe --allow-system --operation folder.thumbnail.set --style "Full size" --refreshExplorer false --execute
```

Use `--style "Default (half-covered)"` to return to the half-cover mask. Omit
`--execute` to print the preview without applying it. In Safe Mode Command Prompt,
leave `refreshExplorer` false because there may be no shell to restart.

A major Windows update, Windows repair, or `SFC /scannow` can reset the mask.
The setting reads the actual resource again when inspected. Reapply the desired
style and reset the thumbnail cache if old folders still show cached thumbnails.
An Explorer refresh failure after a successful mask write is reported separately;
the mask remains applied and its recovery data remains available.

Local tests and disposable Sandbox checks do not establish installer, Windows
update lifecycle, or human visual acceptance on every Windows 11 build.
