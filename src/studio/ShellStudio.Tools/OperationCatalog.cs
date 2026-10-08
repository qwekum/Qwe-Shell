namespace ShellStudio.Tools;

/// <summary>
/// The single operation catalog consumed by the Studio and ToolHost.  IDs are
/// stable protocol identifiers; donor names live in the parity ledger rather
/// than leaking into the GUI contract.
/// </summary>
public static class OperationCatalog
{
    public static readonly IReadOnlyList<string> FolderTypes =
    [
        // The first entries are the editable donor set from SetFolderType. The
        // remaining entries are the complete Windows 11 FolderTypes canonical
        // names observed by WinSetView's registry discovery path. Keeping the
        // union allows an offline fixture to expose the same choices while a
        // live registry remains the authority for installed types.
        "NotSpecified", "AccountPictures", "Contacts", "Contacts.Library", "Contacts.SearchResults",
        "Documents", "Documents.Library", "Documents.LibraryFolder", "Documents.SearchResults", "Downloads",
        "Downloads.SearchResults", "FileItemAPIs", "Generic", "Generic.Library", "Generic.LibraryFolder",
        "Generic.SearchResults", "HomeFolder", "Internet", "Music", "Music.Library", "Music.LibraryFolder",
        "Music.SearchResults", "OpenSearch", "OtherUsers", "OtherUsers.SearchResults", "Pictures",
        "Pictures.Library", "Pictures.LibraryFolder", "Pictures.SearchResults", "PublishedItems",
        "PublishedItems.SearchResults", "SearchConnector", "Searches", "StorageProviderDocuments",
        "StorageProviderGeneric", "StorageProviderMusic", "StorageProviderPictures", "StorageProviderVideos",
        "UserFiles", "UserFiles.SearchResults", "UsersLibraries", "UsersLibraries.SearchResults", "Videos",
        "Videos.Library", "Videos.LibraryFolder", "Videos.SearchResults", "Communications",
        "Communications.SearchResults", "CompressedFolder", "ControlPanelAllItems", "ControlPanelCategory",
        "Gallery", "Printers", "Programs", "RestrictedNonIndexed", "SearchHome", "StartMenu", "Sync",
        "VersionControl"
    ];

    private static OperationField Path(string name = "path", string label = "Path", string defaultValue = "")
        => new(name, label, "path", defaultValue);

    private static OperationField Choice(string name, string label, IEnumerable<string> choices, string defaultValue = "")
        => new(name, label, "choice", defaultValue, choices.ToArray());

    private static OperationField Text(string name, string label, string defaultValue = "")
        => new(name, label, "text", defaultValue);

    private static OperationField Bool(string name, string label, bool defaultValue = false)
        => new(name, label, "bool", defaultValue ? "true" : "false");

    private static OperationField Integer(string name, string label, int defaultValue = 0)
        => new(name, label, "integer", defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static OperationField FeatureChoice(string name, string label)
        => Choice(name, label, ["Unchanged", "Enabled", "Disabled"], "Unchanged");

    public static IReadOnlyList<OperationDescriptor> All { get; } =
    [
        new("folder.thumbnail.set", "Folder thumbnail style", "Choose full-size folder thumbnails or the default half-covered style. The current Windows resource is inspected before each change.", [Choice("style", "Thumbnail style", ["Full size", "Default (half-covered)"], "Full size"), Bool("refreshExplorer", "Restart Explorer and reset thumbnail cache", true)], true, true, "Folder thumbnails"),
        // The inspect/apply/restore primitives remain addressable to the
        // backend and ToolHost, while the GUI exposes the consolidated setting
        // below as the single user-facing thumbnail workflow.
        new("folder.thumbnail.inspect", "Inspect folder thumbnail", "Inspect the current Windows folder-thumbnail icon state and resource metadata.", [Path()], false, false, "Folder thumbnails", ShowInCatalog: false),
        new("folder.thumbnail.apply", "Apply folder thumbnail", "Update the folder thumbnail resource through the native Windows resource API and record a rollback backup.", [Path("iconPath", "Icon file"), Path("resourcePath", "Resource file")], true, true, "Folder thumbnails", ShowInCatalog: false),
        new("folder.thumbnail.restore", "Restore folder thumbnail", "Restore a previously journaled folder-thumbnail resource update.", [Path("journalPath", "Recovery journal")], true, true, "Folder thumbnails", ShowInCatalog: false),

        new("folder.type.inspect", "Inspect folder type", "Read FolderType, desktop.ini attributes, and automatic folder-type discovery state.", [Path()], false, false, "Folder types"),
        new("folder.type.set", "Set folder type", "Set a FolderType entry while preserving unrelated desktop.ini content.", [Path(), Choice("folderType", "Folder type", FolderTypes, "Generic"), Bool("recursive", "Include subfolders")], false, false, "Folder types"),
        new("folder.type.remove", "Remove folder type", "Remove only the FolderType entry and retain meaningful desktop.ini settings.", [Path(), Bool("recursive", "Include subfolders"), Bool("forceDelete", "Delete desktop.ini when empty")], false, true, "Folder types"),
        new("folder.type.discover", "Discover folder types", "Inspect directory metadata and report likely Windows folder-type assignments.", [Path(), Bool("recursive", "Include subfolders")], false, false, "Folder types"),

        new("shell.history.clear", "Clear shell histories", "Preview and selectively clear Explorer and shell histories with an irreversible-operation warning.", [Choice("scope", "History scope", ["Recent", "JumpLists", "RunMRU", "TypedPaths", "Temp", "RecycleBin", "Defender", "SpecifiedFolders", "All"], "Recent"), Text("specificPaths", "Specified folders"), Text("cleanupFolders", "Cleanup folders"), Bool("rebootAfter", "Restart after Defender task")], false, true, "Shell utilities"),
        new("files.unblock", "Unblock files", "Remove Zone.Identifier streams from selected files with per-file results.", [Path(), Bool("recursive", "Include subfolders")], false, true, "Shell utilities"),
        new("security.take-ownership", "Take ownership and access", "Apply an explicitly reviewed owner and access change using native Windows security APIs.", [Path(), Bool("recursive", "Include subfolders"), Text("account", "Account", "CURRENT_USER")], true, true, "Security"),
        new("environment.path", "Edit PATH", "Preview a user or machine PATH edit without invoking a shell script.", [Choice("scope", "Scope", ["User", "Machine"], "User"), Choice("action", "Action", ["Add", "Remove", "Normalize"], "Add"), Text("entry", "Entry")], false, true, "Shell utilities"),
        new("shell.visibility", "Toggle hidden items", "Read or set Explorer hidden-file and protected-file visibility policy.", [Choice("value", "Visibility", ["Show", "Hide"], "Show"), Bool("protected", "Show protected operating-system files")], false, true, "Explorer"),
        new("explorer.refresh", "Refresh Explorer", "Apply a reviewed Explorer shell restart using exact current-session ownership, scoped window-close requests, and an optional cache reset.", [Bool("resetThumbs", "Reset thumbnail cache"), Bool("resetIcons", "Reset icon cache")], false, true, "Explorer"),
        new("explorer.options", "Explorer options", "Set supported Explorer behavior options represented by the WinSetView donor.", [Bool("showExtensions", "Show file extensions", true), Bool("showHidden", "Show hidden files"), Bool("compactMode", "Use compact mode"), Bool("showProtected", "Show protected files")], false, true, "Explorer"),

        new("shortcut.convert-url", "Convert URL shortcuts", "Convert URL shortcut files to native links after previewing each target and retaining source recovery data.", [Path(), Bool("recursive", "Include subfolders"), Bool("removeSource", "Remove source after successful conversion")], false, true, "File utilities"),
        new("metadata.photo-date", "Apply photo date", "Copy Date taken metadata to creation time, optionally updating modified time.", [Path(), Bool("recursive", "Include subfolders"), Bool("setModified", "Also set modified time")], false, true, "File utilities"),
        new("capture.window", "Capture window with border", "Capture a selected window through the Windows graphics API without changing the system cursor persistently.", [Text("windowId", "Window identifier"), Integer("borderWidth", "Border width", 2)], false, false, "Capture"),

        new("launch.terminal", "Open terminal here", "Open a selected terminal at a reviewed directory; external launching is the operation itself.", [Path(), Choice("terminal", "Terminal", ["CommandPrompt", "PowerShell", "PowerShellCore"], "PowerShell")], false, false, "Launch"),
        new("launch.registry", "Open Registry Editor", "Open Registry Editor at the selected scope without a generic command runner.", [Text("key", "Registry key")], false, false, "Launch"),
        new("launch.file-manager", "Open file manager here", "Open the configured file manager at a reviewed path.", [Path(), Text("executable", "File manager executable")], false, false, "Launch"),
        new("launch.search", "Search here", "Open a configured search provider for a reviewed directory.", [Path(), Text("executable", "Search executable"), Text("arguments", "Arguments")], false, false, "Launch"),
        new("launch.custom", "Launch configured tool", "Run one explicitly configured executable with a typed JSON argument vector and optional selection, working directory, and administrator elevation.", [Path("path", "Selection path"), Text("executable", "Executable"), Text("arguments", "Arguments (JSON array)", "[]"), Path("workingDirectory", "Working directory"), Bool("elevate", "Run as administrator")], false, false, "Launch"),
        new("launch.user-script", "Run user script", "Run one explicitly selected user script with a typed argument vector in the calling user session. Scripts are never handed to the elevated operation host.", [Path("scriptPath", "Script file"), Text("arguments", "Arguments (JSON array)", "[]"), Path("workingDirectory", "Working directory")], false, false, "Launch"),

        new("registry.import-reg", "Import typed registry file", "Parse a Windows Registry Editor Version 5 .reg file, show a typed before/after diff, and apply only the declared values with journaled recovery.", [Path("regPath", "Registry file"), Bool("allowDeletes", "Allow declared key/value deletes")], false, true, "Registry"),
        new("registry.export-reg", "Export typed registry file", "Export a selected registry key and its values to a deterministic Windows Registry Editor Version 5 .reg file.", [Choice("hive", "Registry hive", ["HKCU", "HKLM", "HKCR", "HKU"], "HKCU"), Text("keyPath", "Registry key"), Path("destination", "Destination .reg file"), Bool("includeSubkeys", "Include subkeys", true)], false, false, "Registry"),

        new("views.inspect", "Inspect Explorer views", "Read current global, per-folder-type, virtual-folder, and dialog view settings.", [Choice("scope", "Scope", ["Global", "FolderType", "Virtual", "Dialogs", "All"], "All"), Text("folderType", "Folder type", "")], false, false, "Explorer views"),
        new("views.apply", "Apply Explorer views", "Apply WinSetView view, column, grouping, sorting, and inheritance settings through one registry transaction.", [Choice("scope", "Scope", ["Global", "FolderType", "Virtual", "Dialogs"], "Global"), Text("folderType", "Folder type", "Generic"), Text("viewGuid", "View identifier"), Choice("viewMode", "View mode", ["Icons", "SmallIcons", "List", "Details", "Tiles", "Content"], "Details"), Integer("iconSize", "Icon size", 32), Text("columns", "Columns"), Text("sortProperty", "Sort property", "System.ItemNameDisplay"), Choice("sortDirection", "Sort direction", ["Ascending", "Descending"], "Ascending"), Text("groupProperty", "Group property"), Choice("groupDirection", "Group direction", ["Ascending", "Descending"], "Ascending"), Bool("autoArrange", "Auto arrange icons"), Bool("alignToGrid", "Align icons to grid"), Bool("inherit", "Use folder-type inheritance")], false, true, "Explorer views"),
        new("views.options", "Apply Explorer view options", "Apply supported WinSetView global and dialog options without importing arbitrary registry scripts.", [Bool("showExtensions", "Show file extensions", true), Bool("showHidden", "Show hidden files"), Bool("compactMode", "Use compact mode"), Bool("noFullRowSelect", "Disable full row select"), Bool("legacySpacing", "Use legacy spacing"), Bool("autoArrange", "Auto arrange icons"), Bool("alignToGrid", "Align icons to grid"), Text("systemTextColor", "System text color", "0 0 0"), Bool("classicContextMenu", "Use classic context menu"), Bool("copyMoveInMenu", "Show Copy and Move commands"), Bool("searchInternet", "Search the internet"), Bool("searchHighlights", "Search highlights"), Bool("classicSearch", "Use classic search"), Bool("noNumericalSort", "Disable numerical sort"), Bool("removeHome", "Remove Home"), Bool("removeGallery", "Remove Gallery"), Bool("legacyPlacesBar", "Apply legacy PlacesBar"), Bool("noSuggestions", "Disable suggestions"), Bool("legacyDialogFix", "Apply legacy dialog fix"), Bool("unhideAppData", "Unhide AppData"), Bool("unhidePublicDesktop", "Unhide Public Desktop"), Bool("win10Explorer", "Use legacy Explorer"), FeatureChoice("win10Search", "Windows 10 search feature"), FeatureChoice("win11Explorer", "Windows 11 Explorer feature"), Bool("automaticFolderTypeDiscovery", "Use automatic folder-type discovery"), Bool("alwaysShowIcons", "Always show icons, never thumbnails"), Bool("genericDefaults", "Set Generic folder defaults"), Bool("searchOnly", "Use search-only columns"), Bool("setVirtualFolders", "Set virtual-folder defaults"), Bool("virtualFolderColumns", "Set virtual-folder columns"), Bool("homeGrouping", "Group Home items"), Bool("libraryGrouping", "Group library items"), Bool("noFolderThumbs", "Disable folder thumbnails"), Bool("thisPc", "Configure This PC view"), Integer("thisPcView", "This PC view", 3), Bool("thisPcNoGrouping", "Disable This PC grouping"), Bool("explorerStart", "Set Explorer start location"), Choice("explorerStartOption", "Explorer start location", ["ThisPC", "Home", "Downloads", "Custom"], "Home"), Choice("launchFolder", "Explorer launch folder", ["Home", "ThisPC"], "Home"), Text("launchPath", "Custom launch path")], false, true, "Explorer views"),
        new("views.backup", "Back up Explorer views", "Export selected view and option keys into a journaled, versioned JSON backup.", [Choice("scope", "Scope", ["Global", "FolderType", "Virtual", "Dialogs", "All"], "All"), Path("destination", "Backup destination")], false, false, "Explorer views"),
        new("views.restore", "Restore Explorer views", "Restore a validated Studio view backup with hash and schema checks.", [Path("backupPath", "Backup file")], false, true, "Explorer views"),
        new("views.import-ini", "Import WinSetView settings", "Import a WinSetView settings INI through typed Explorer option and view operations.", [Path("iniPath", "WinSetView settings")], false, true, "Explorer views"),
        new("views.reset", "Reset Explorer views", "Remove selected user view state after creating a recovery journal.", [Choice("scope", "Scope", ["Global", "FolderType", "Virtual", "Dialogs", "All"], "All"), Bool("resetThumbs", "Reset thumbnail cache")], false, true, "Explorer views")
    ];

    private static readonly IReadOnlyDictionary<string, OperationDescriptor> ById =
        All.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["registry.import"] = "registry.import-reg",
            ["registry.export"] = "registry.export-reg",
            ["views.import-reg"] = "registry.import-reg",
            ["views.export-reg"] = "registry.export-reg"
        };

    /// <summary>
    /// Selection eligibility is explicit because a profile or generated command
    /// must not guess whether a tool understands a background, folder, or
    /// multi-item selection. Operations absent from this map accept only an
    /// empty selection object.
    /// </summary>
    public static IReadOnlyDictionary<string, OperationSelectionEligibility> SelectionEligibility { get; } =
        new Dictionary<string, OperationSelectionEligibility>(StringComparer.OrdinalIgnoreCase)
        {
            ["folder.type.set"] = new(["explorer.folder"], AllowsMultiple: false),
            ["folder.type.remove"] = new(["explorer.folder"], AllowsMultiple: false),
            ["folder.type.discover"] = new(["explorer.folder"], AllowsMultiple: false),
            ["files.unblock"] = new(["explorer.selection", "explorer.folder"], AllowsMultiple: true),
            ["shortcut.convert-url"] = new(["explorer.selection", "explorer.folder"], AllowsMultiple: true),
            ["metadata.photo-date"] = new(["explorer.selection", "explorer.folder"], AllowsMultiple: true),
            ["launch.terminal"] = new(["explorer.folder", "explorer.background"], AllowsMultiple: false),
            ["launch.file-manager"] = new(["explorer.folder", "explorer.background"], AllowsMultiple: false),
            ["launch.search"] = new(["explorer.folder", "explorer.background"], AllowsMultiple: false),
            ["launch.custom"] = new(["explorer.selection", "explorer.folder", "explorer.background", "desktop"], AllowsMultiple: true),
            ["launch.user-script"] = new(["explorer.selection", "explorer.folder", "explorer.background", "desktop"], AllowsMultiple: true),
            ["capture.window"] = new(["window"], AllowsMultiple: false)
        };

    public static bool TryGetSelectionEligibility(string id, out OperationSelectionEligibility eligibility)
        => SelectionEligibility.TryGetValue(id ?? string.Empty, out eligibility!)
            || (Aliases.TryGetValue(id ?? string.Empty, out var canonical)
                && SelectionEligibility.TryGetValue(canonical, out eligibility!));

    public static bool IsSelectionEligible(string id, OperationSelection? selection, out string reason)
    {
        selection ??= OperationSelection.Empty;
        if (selection.Paths is null)
        {
            reason = "The operation selection path list is null.";
            return false;
        }
        if (!TryGetSelectionEligibility(id, out var eligibility))
        {
            if (selection.Paths.Count == 0)
            {
                reason = string.Empty;
                return true;
            }
            reason = $"Catalog operation '{id}' does not declare eligibility for an external selection.";
            return false;
        }
        if (eligibility.RequiresSelection && selection.Paths.Count == 0)
        {
            reason = $"Catalog operation '{id}' requires a selection in context {string.Join(" or ", eligibility.Contexts)}.";
            return false;
        }
        if (selection.Paths.Count > 0 && !eligibility.Contexts.Contains(selection.Context, StringComparer.OrdinalIgnoreCase))
        {
            reason = $"Catalog operation '{id}' does not accept selection context '{selection.Context}'.";
            return false;
        }
        if (!eligibility.AllowsMultiple && selection.Paths.Count > 1)
        {
            reason = $"Catalog operation '{id}' accepts only one selected path.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    public static IReadOnlyList<OperationDescriptor> GetEligibleOperations(OperationSelection? selection)
        => All.Where(descriptor => IsSelectionEligible(descriptor.Id, selection, out _)).ToArray();

    public static bool TryGet(string id, out OperationDescriptor descriptor)
        => ById.TryGetValue(id ?? string.Empty, out descriptor!)
            || (Aliases.TryGetValue(id ?? string.Empty, out var canonical)
                && ById.TryGetValue(canonical, out descriptor!));
}

public sealed record OperationSelectionEligibility(
    IReadOnlyList<string> Contexts,
    bool AllowsMultiple,
    bool RequiresSelection = false);
