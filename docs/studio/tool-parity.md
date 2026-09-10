# Managed Studio tool parity

This ledger records the source evidence used to consolidate the four pinned
donors into `ShellStudio.Tools`.  Donor identifiers are evidence, not runtime
dependencies.  The managed implementation targets Windows 11 x64 and routes
all mutations through a reviewed `OperationPlan`, a bounded environment seam,
and a recovery journal.

Build and launch instructions are in [build-and-run.md](build-and-run.md).
The [local verification record](local-verification.md) includes 34 managed tool
fixture checks and four task-owned native resource checks. The rows marked
Windows-only still require the disposable-VM acceptance matrix.

## Pinned donor evidence

| Donor | Pinned commit | Source evidence | Consolidated operation IDs |
| --- | --- | --- | --- |
| FolderThumbnailFix | `7f845506` (2026-01-04) | `Program.cs:43-94,193-284` checks Windows 11, restarts Explorer, clears thumbnail databases, and replaces resource icon group 6/1033 through Resource Hacker. | `folder.thumbnail.inspect`, `folder.thumbnail.apply`, `folder.thumbnail.restore`, `explorer.refresh` |
| SetFolderType | `cdde0ee` (2026-02-05) | `Program.cs:349-429` recursively writes `[ViewState] FolderType` in `desktop.ini`, preserves other entries, and optionally removes the file. | `folder.type.inspect`, `folder.type.set`, `folder.type.remove`, `folder.type.discover` |
| RightClickTools | `f68de3f` (2026-09-02) | `Program.cs:250-482,1009-1211,1318-1401,1582-1598,1854-2050,3341-3382` covers shell histories, PATH, ownership, ADS, visibility, Explorer restart, launches, shortcut conversion, photo dates, and the privileged Defender startup task. | `shell.history.clear`, `files.unblock`, `security.take-ownership`, `environment.path`, `shell.visibility`, `explorer.refresh`, `shortcut.convert-url`, `metadata.photo-date`, `launch.*` |
| WinSetView | `fc4051c` (2026-08-30) | `src/WinSetView.ps1:102-160,407-638,495-525,649-890` covers Explorer options, FolderTypes, global/virtual/dialog bags, per-type TopViews, columns, grouping, sorting, icon sizes, reset, backup flow, and feature IDs `18755234`/`40729001`. | `views.inspect`, `views.apply`, `views.options`, `views.backup`, `views.restore`, `views.import-ini`, `views.reset` |

The exact source-to-operation mapping is intentionally kept here instead of
embedding donor names in the GUI contract.  The runtime contract is the
stable catalog in `src/studio/ShellStudio.Tools/OperationCatalog.cs`.

## Implemented behavior

- `desktop.ini` edits preserve the detected BOM, encoding, newline style,
  unrelated entries, and existing attributes. Recursive walks have depth,
  item, reparse-point, and cancellation bounds.
- Folder thumbnails use `BeginUpdateResource`, `UpdateResource`, and
  `EndUpdateResource` with a managed ICO parser. A journal stores the original
  resource bytes and hash before the update.
- URL files become COM `IShellLinkW` links through `IPersistFile`; no
  `WScript.Shell` automation or donor launcher is required.
- Registry changes use the 64-bit view and are snapshotted before writes.
  Explorer views write the donor's `LogicalViewMode`, `IconSize`, `Mode`,
  `FFlags`, `GroupView`, `GroupBy`, `GroupAscending`, `SortByList`, and
  `ColumnList` values, including existing per-type `TopViews` child keys.
- `views.import-ini` accepts the bounded settings file emitted by WinSetView's
  `Var2Ini` function. It applies typed options, per-folder-type views, up to
  three sort levels, search-only column filtering, file-dialog variants,
  virtual-folder defaults, and This PC settings. It requires each imported
  folder section to resolve to an installed FolderTypes GUID and reports
  unsupported keys before execution. The two donor feature IDs are queried and
  written through the native ntdll feature-store API when the donor's build and
  UBR gates match; no ViVeTool binary or GPL library is bundled.
- `shell.history.clear` accepts both `specificPaths` and the explicit
  `cleanupFolders` field. A path without a trailing separator is emptied while
  its root remains; a path with a trailing separator is removed recursively.
  Paths that normalize to a filesystem root are rejected before preview can be
  executed, including root aliases that use `..` segments.
  The Temp scope removes top-level files and directories recursively while
  preserving the Temp root. File and directory metadata are journaled before
  deletion, and the active journal location is rejected as a target.
- Defender cleanup now mirrors the donor's one-shot `DWDH` task: a fixed native
  `schtasks.exe` command registers a SYSTEM `ONSTART` task that removes the
  Service history, Quarantine, and `mpenginedb.db*` locations, then removes
  itself. The preview requires `AllowSystem` and states whether a manual or
  requested reboot is needed; task and UAC failures are returned explicitly.
- ACL, resource, Explorer, shell, process, metadata, registry, and file
  effects are interfaces. Fixtures use temporary files and in-memory stores;
  the implementation does not mutate the active desktop during build or test.
  The native Explorer provider selects the current-session shell through
  `GetShellWindow`/`GetWindowThreadProcessId`, retains its process handle and
  creation identity, verifies the exact `%WINDIR%\\explorer.exe` image and
  current-user token, and refuses the operation when any ownership check is
  unavailable.
- ToolHost uses compact newline-delimited JSON, caps each UTF-8 frame at
  `Protocol.MaxMessageBytes`, preserves extra frames read in one buffer,
  supports concurrent previews, serializes execution, and cancels by request
  ID. A shutdown waits for active requests after cancellation.

## Per-setting parity ledger

Each row is an individual source behavior or setting. “Fixture check” names the
deterministic test that exercises the shared contract; “Windows-only” means the
provider is implemented but cannot be established by the local fixture run.

### FolderThumbnailFix (`7f845506`)

| Source behavior | Catalog/backend | State | Fixture check |
| --- | --- | --- | --- |
| Windows 11 build gate | `IToolEnvironment.IsWindows11X64` | Implemented | Windows-only |
| Inspect resource bytes and hash | `folder.thumbnail.inspect` | Implemented | Windows-only resource seam |
| Replace icon group 6/1033 | `folder.thumbnail.apply` | Implemented with native resource API | Windows-only resource seam |
| Restore prior resource | `folder.thumbnail.restore` and journal | Implemented | `photo_date_journal_restores_file_metadata` covers journal protocol |
| Close Explorer and restart | `explorer.refresh` | Implemented through exact current-session shell ownership, scoped window-close requests, journal-aware cache reset, and an absolute `%WINDIR%\\explorer.exe` restart | Windows-only |
| Reset thumbnail cache | `explorer.refresh.resetThumbs` | Implemented as a journaled provider option | `explorer_refresh_forwards_cache_options_and_reports_failure` (option forwarding); Windows-only cache files |
| `/install` and `/remove` shell registration | Combined MSI and native `shell.exe` | Studio uses the shared Shell installation instead of registering a donor launcher | MSI database inspection; live installer behavior pending |
| Donor bundled Resource Hacker | No dependency | Replaced by native `BeginUpdateResource` path | Windows-only |

### SetFolderType (`cdde0ee`)

| Source behavior | Catalog/backend | State | Fixture check |
| --- | --- | --- | --- |
| Set `[ViewState] FolderType` | `folder.type.set` | Implemented | `desktop_ini_round_trip_preserves_unrelated_entries` |
| Recursive set | `folder.type.set.recursive` | Implemented with depth/item/cancellation bounds | `recursive_folder_type_is_journaled_and_reports_progress` |
| Remove only FolderType | `folder.type.remove` | Implemented | `desktop_ini_remove_uses_forward_section_context` |
| Delete empty desktop.ini | `folder.type.remove.forceDelete` | Implemented with warning | Windows-only edge case |
| Preserve unrelated sections and encoding | `DesktopIniDocument` | Implemented | `desktop_ini_round_trip_preserves_unrelated_entries`, `desktop_ini_remove_uses_forward_section_context` |
| Auto folder-type discovery | `folder.type.inspect` / `folder.type.discover` | Implemented from live HKCU/HKLM FolderTypes | Windows-only registry |
| NTFS/fixed-drive validation | Environment path diagnostics | Implemented as path/permission boundary | Windows-only |
| Context-menu registration and MUI labels | Studio-generated tool entries and native Shell installation | Generated entries replace donor-specific registration; exact donor MUI behavior is not claimed | Live menu/localization parity pending |

### RightClickTools (`f68de3f`)

| Source behavior or setting | Catalog/backend | State | Fixture check |
| --- | --- | --- | --- |
| Recent items | `shell.history.clear:Recent` | Implemented, top-level donor scope | Windows-only |
| Automatic Destinations | `shell.history.clear:JumpLists` | Implemented, top-level donor scope | Windows-only |
| Custom Destinations | `shell.history.clear:JumpLists` | Implemented, top-level donor scope | Windows-only |
| RunMRU | `shell.history.clear:RunMRU` | Implemented | `history_clears_registry_and_recycle_through_seams` |
| TypedPaths | `shell.history.clear:TypedPaths` | Implemented | Windows-only registry |
| Temporary files | `shell.history.clear:Temp` | Implemented with recursive directory deletion | `history_cleans_configured_folders_and_full_temp_contents` exercises the same cleanup/journal path |
| Recycle Bin | `shell.history.clear:RecycleBin` | Implemented through `SHEmptyRecycleBin` | `history_clears_registry_and_recycle_through_seams` |
| Defender Protection history | `shell.history.clear:Defender` | Implemented as reviewed SYSTEM startup task | `defender_cleanup_reports_task_and_reboot_state` |
| Configured cleanup folders | `shell.history.clear.cleanupFolders` / `specificPaths` | Implemented; trailing separator controls root deletion | `history_cleans_configured_folders_and_full_temp_contents` |
| Unblock Mark of the Web | `files.unblock` | Implemented through Zone.Identifier seam | Windows-only ADS |
| Take ownership and access | `security.take-ownership` | Implemented through native ACL provider with per-item owner/DACL capture and rollback | `acl_operation_uses_journal_and_result_seam`; Windows-only ACL mutation |
| User PATH add/remove/normalize | `environment.path:User` | Implemented | Windows-only registry |
| Machine PATH add/remove/normalize | `environment.path:Machine` | Implemented with `AllowSystem` gate | Windows-only registry/UAC |
| Show/hide protected and hidden items | `shell.visibility` | Implemented through Explorer Advanced values | Windows-only |
| Quick shell refresh | `explorer.refresh` | Implemented through the reviewed current-session Explorer shell seam | Windows-only |
| Icon/thumbnail cache reset | `explorer.refresh.resetIcons/resetThumbs` | Implemented as explicit options; matching user cache files are journaled before deletion | `explorer_refresh_forwards_cache_options_and_reports_failure` (provider options) |
| Restart Explorer | `explorer.refresh` | Implemented with exact shell HWND/PID verification, scoped WM_CLOSE requests, reviewed shell-process stop, cache reset before a mandatory finally-path launch, and explicit blocking/failure results | `explorer_refresh_forwards_cache_options_and_reports_failure`, `explorer_refresh_blocks_unverified_shell`, `explorer_refresh_restarts_after_cache_failure`, and `explorer_refresh_restarts_after_cancellation`; Windows-only native window/process effect |
| Cmd Here | `launch.terminal:CommandPrompt` | Implemented with typed process spec | Windows-only |
| PowerShell Here | `launch.terminal:PowerShell` | Implemented with typed process spec | Windows-only |
| PowerShell Core Here | `launch.terminal:PowerShellCore` | Implemented with typed process spec | Windows-only |
| RegEdit | `launch.registry` | Implemented with fixed executable | Windows-only |
| Configured file manager | `launch.file-manager` | Implemented with absolute executable requirement | Windows-only |
| Configured search tool | `launch.search` | Implemented with typed arguments | Windows-only |
| Typed custom launcher | `launch.custom` | Implemented with an existing absolute executable, JSON string argument vector, optional selected path appended as a typed argument, working directory, and explicit elevation | `custom_launch_uses_typed_arguments_and_selection`, `custom_launch_rejects_invalid_argument_json` |
| URL to LNK conversion | `shortcut.convert-url` | Implemented through native `IShellLinkW` | Windows-only link provider |
| Optional source deletion after conversion | `shortcut.convert-url.removeSource` | Implemented and journaled | Windows-only link provider |
| Date modified/created from Date taken | `metadata.photo-date` | Implemented through native photo metadata provider | `photo_date_journal_restores_file_metadata` |
| Folder options: extensions/hidden/compact/protected | `explorer.options` | Implemented | Windows-only registry |
| Folder options: type/colors/icons/thumbnails | `folder.type`, `views.apply`, `views.options` | Implemented across typed operations | `views_write_typed_registry_values` |
| MoreTools arbitrary launcher and donor INI | `launch.custom` | Implemented as a bounded typed contract: existing absolute executable, JSON string argument vector, optional selected path, working directory, and explicit administrator elevation; arbitrary donor INI/script execution remains outside the contract | `custom_launch_uses_typed_arguments_and_selection`, `custom_launch_rejects_invalid_argument_json` |
| SnipWithBorder visible capture | `capture.window` in the Studio GUI | Visible capture implementation; the headless host only previews/reports the UI requirement | Human/Windows capture acceptance pending |

### WinSetView (`fc4051c`)

| Source setting or behavior | Catalog/backend | State | Fixture check |
| --- | --- | --- | --- |
| `ShowExt` | `views.options.showExtensions` | Implemented | `views_write_typed_registry_values` |
| `CompView` | `views.options.compactMode` | Implemented | `views_write_typed_registry_values` |
| `ShowHidden` | `views.options.showHidden` | Implemented | Windows-only registry |
| `NoFullRowSelect` | `views.options.noFullRowSelect` | Implemented | Windows-only registry |
| `LegacySpacing` | `views.options.legacySpacing` | Implemented | Windows-only registry |
| `AutoArrange` | `views.options.autoArrange` / imported FFlags | Implemented | `winsetview_import_applies_installed_folder_view` |
| `AlignToGrid` | `views.options.alignToGrid` / imported FFlags | Implemented | `winsetview_import_applies_installed_folder_view` |
| `SystemTextColor` | `views.options.systemTextColor` | Implemented | Windows-only registry |
| `NoNumericalSort` | `views.options.noNumericalSort` | Implemented | Windows-only registry |
| `ClassicContextMenu` | `views.options.classicContextMenu` | Implemented | Windows-only registry |
| `CopyMoveInMenu` | `views.options.copyMoveInMenu` | Implemented | Windows-only registry |
| `NoSearchInternet` | `views.options.searchInternet` | Implemented | Windows-only registry |
| `NoSearchHighlights` | `views.options.searchHighlights` | Implemented | Windows-only registry |
| `ClassicSearch` | `views.options.classicSearch` | Implemented | Windows-only registry |
| `RemoveHome` | `views.options.removeHome` | Implemented | Windows-only registry |
| `RemoveGallery` | `views.options.removeGallery` | Implemented | Windows-only registry |
| `LegacyDialogFix` and PlacesBar | `views.options.legacyDialogFix` / `legacyPlacesBar` | Implemented | Windows-only registry |
| `NoSuggestions` | `views.options.noSuggestions` | Implemented | Windows-only registry |
| `ExplorerStart` / `ExplorerStartOption` / `ExplorerStartPath` | `views.options.launchFolder` / `launchPath` | Implemented | Windows-only registry |
| `Win10Explorer` | `views.options.win10Explorer` | Implemented through CLSID mappings | Windows-only registry |
| `Win10Search` / feature `18755234` | `views.options.win10Search` | Implemented through native ntdll API with donor build gate | `vive_feature_flags_use_native_service_seam` |
| `Win11Explorer` / feature `40729001` | `views.options.win11Explorer` | Implemented through native ntdll API with donor build gate and donor's inverse mapping | `vive_feature_flags_use_native_service_seam` |
| `UnhideAppData` | `views.options.unhideAppData` | Implemented | Windows-only attributes |
| `UnhidePublicDesktop` | `views.options.unhidePublicDesktop` | Implemented | Windows-only attributes |
| `NoFolderThumbs` | `views.options.noFolderThumbs` | Implemented via `Logo` value | `winsetview_import_applies_installed_folder_view` |
| `Generic` | `views.options.genericDefaults` | Implemented via AllFolders Shell | `winsetview_import_applies_installed_folder_view` |
| `SetVirtualFolderColumns` | `views.options.virtualFolderColumns` | Implemented via typed virtual-folder defaults | Windows-only registry |
| `HomeGrouping` | `views.options.homeGrouping` | Implemented | Windows-only registry |
| `LibraryGrouping` | `views.options.libraryGrouping` | Implemented | Windows-only registry |
| `ThisPCoption` / `ThisPCView` / `ThisPCNG` | `views.options.thisPc` and imported This PC path | Implemented | `winsetview_import_applies_installed_folder_view` |
| Per-folder `GUID` and `Include` | `views.import-ini` | Implemented with installed GUID validation | `winsetview_import_applies_installed_folder_view` |
| Per-folder `View` and `IconSize` | `views.apply` / `views.import-ini` | Implemented | `folder_type_view_updates_existing_top_view_children`, `winsetview_import_applies_installed_folder_view` |
| Per-folder `ColumnList` | `views.apply` / `views.import-ini` | Implemented with search-only filtering | `winsetview_import_applies_installed_folder_view` |
| Per-folder `GroupBy` / `GroupByOrder` | `views.apply` / `views.import-ini` | Implemented | `winsetview_import_applies_installed_folder_view` |
| Per-folder `SortBy` | `views.apply` / `views.import-ini` | Implemented with three-level bound | `winsetview_import_applies_installed_folder_view` |
| `FileDialogOption` / `FileDialogView` / `FileDialogNG` | `views.import-ini` | Implemented for ComDlg and ComDlgLegacy variants | `winsetview_import_applies_installed_folder_view` |
| Global/FolderType/Virtual/Dialogs inspect | `views.inspect` | Implemented | Windows-only registry |
| View backup | `views.backup` | Implemented as versioned JSON and journaled output | Windows-only registry |
| View restore | `views.restore` | Implemented with HKCU allow-list and schema/size bounds | `view_restore_rejects_machine_registry_target` |
| View reset | `views.reset` | Implemented with journal | Windows-only registry |
| Arbitrary `.reg` import/export and custom script | No managed operation | Deliberately unsupported | No fixture check |
| `ViVeTool.exe` packaging | Native feature service | Deliberately not bundled | `vive_feature_flags_use_native_service_seam` |

## Deliberate gaps and qualification boundaries

- Defender-history cleanup uses a fixed, typed startup-task provider. It still
  requires an administrator-capable host and a reboot; local fixtures do not
  establish actual Defender ACLs, scheduled-task registration, or startup
  execution. Recycle Bin cleanup uses the native `SHEmptyRecycleBin` API after
  explicit review.
- WinSetView feature IDs `18755234` and `40729001` use the native ntdll API only
  on the donor's explicit build/UBR gates. Other Windows builds receive a
  blocking diagnostic; Studio does not invoke ViVeTool or infer feature IDs.
- Configured cleanup folders and full Temp deletion are supported through the
  typed path fields with root-preserving versus root-removing semantics. The
  active journal is excluded and every discovered file/directory is bounded and
  journaled. WinSetView INI import remains bounded and typed; arbitrary registry
  exports, custom scripts, and unknown INI keys remain unsupported.
- Window capture remains a visible Studio UI operation. The headless host
  previews it and returns a diagnostic instead of claiming clipboard or human
  presentation acceptance.
- WindowsAPICodePack metadata and the donor's DT2DC helper are not runtime
  dependencies. The Windows provider reads `System.Photo.DateTaken` through
  the native `IPropertyStore`/`SHGetPropertyStoreFromParsingName` APIs and
  the operation writes the selected file timestamps through the bounded file
  seam. The fixture provider remains deterministic for tests; files without a
  readable shell property are reported as skipped.
- Local builds and fixture tests establish compilation and deterministic
  managed behavior only. They do not establish Windows Explorer behavior,
  UAC/TrustedInstaller qualification, Defender behavior, CI, release signing,
  installer behavior, or human visual acceptance.

## Attribution and redistribution

The donor repositories are retained in this checkout as pinned source
evidence. Their repository licenses apply to their own code and must be
checked before copying implementation text. FolderThumbnailFix documents
Resource Hacker as an included third-party component, but this checkout does
not contain a redistributable Resource Hacker binary or a license grant for
it; the managed resource editor therefore does not package it. RightClickTools
expects `SetACL.exe`, `DT2DC.exe`, and other `AppParts` files that are absent
from the pinned source. WinSetView expects `ViVeTool.exe` and
`Albacore.ViVe.dll`, also absent. Windows API Code Pack packages in the donor
carry an external license URL and `requireLicenseAcceptance`; no license text
is used as a Studio runtime dependency. The exact pinned donor license texts
and the tracked WIL third-party notice are copied to
`src/studio/licenses/`; that directory is copied beside both the Studio and
ToolHost build and publish outputs. The WIL package and its listed Libc++,
Catch2, Boost, and Detours notices remain provenance material unless the
corresponding native code is actually reused.

No donor binary, donor PowerShell `ExecutionPolicy Bypass` command,
`taskkill /im explorer.exe`, global cursor replacement, or arbitrary launcher
is part of the managed backend. Defender cleanup uses the separately reviewed,
fixed `MyTasks\\DWDH` provider described above.
