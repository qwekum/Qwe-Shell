using System.Text.Json;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ShellStudio.Core;

public enum RuleScope { Category, AllMatching, ExactPath }

/// <summary>One configuration rule that can affect a captured entry.</summary>
public sealed class RuleAssociation
{
    public string RuleId { get; init; } = "";
    public string? Marker { get; init; }
    public string Kind { get; init; } = "modify";
    public string Outcome { get; init; } = "unknown";
    public string? Reason { get; init; }
    public SourceReference? Source { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool IsGenerated => !string.IsNullOrWhiteSpace(Marker);
    public bool IsValid { get; init; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    // The syntax node is an in-memory binding and must never cross a capture
    // or template serialization boundary.
    [System.Text.Json.Serialization.JsonIgnore]
    public SyntaxNode? Node { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public SourceFile? File { get; init; }
}

/// <summary>A settings gate that can explain why a property change is blocked.</summary>
public sealed record SettingGate(string Property, bool? Enabled, string? Value,
    SourceReference? Source, string? Reason = null);

public static partial class MenuEditing
{
    /// <summary>
    /// Prefix used by generated scoped rules.  It is deliberately a comment so
    /// the marker is harmless to older Shell parsers and remains next to the
    /// rule when unrelated properties are edited.
    /// </summary>
    public const string GeneratedRuleMarkerPrefix = "shell-studio-rule:";
    private const string GeneratedRuleMarkerCommentPrefix = "// " + GeneratedRuleMarkerPrefix;

    [GeneratedRegex(@"(?im)^\s*//\s*shell-studio-rule:(?<id>[a-z0-9][a-z0-9-]{7,63})(?:\s+(?<fingerprint>[a-f0-9]{64}))?(?:\s+scope=(?<scope>category|allmatching|exactpath))?\s*$")]
    private static partial Regex GeneratedRuleMarker();

    private sealed record MarkerInfo(string Id, string? Fingerprint, RuleScope? Scope);

    public static IEnumerable<MenuEntry> Descendants(IEnumerable<MenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in Descendants(entry.Children)) yield return child;
        }
    }
    public static MenuSnapshot Clone(MenuSnapshot snapshot) => JsonSerializer.Deserialize<MenuSnapshot>(JsonSerializer.Serialize(snapshot, Protocol.Json), Protocol.Json)!;

    public static MenuSnapshot FromConfiguration(Workspace workspace)
    {
        var snapshot = new MenuSnapshot { Phase = "configuration", ConfigPath = workspace.RootPath, Context = "Configuration (not captured)" };
        if (!workspace.Files.TryGetValue(workspace.RootPath, out var root))
        {
            snapshot.Diagnostics.Add(new("WORKSPACE_ROOT", "The root configuration could not be loaded.", "error", workspace.RootPath));
            return snapshot;
        }

        // Expand through each import occurrence in source order.  Imported
        // files are intentionally not iterated as an independent inventory:
        // the same file may occur more than once and each occurrence can have
        // a different parent scope and visible bindings.
        snapshot.Entries.AddRange(BuildScope(workspace, root, root.Syntax.Nodes,
            "", "", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), [], null));
        return snapshot;
    }

    private static IEnumerable<MenuEntry> BuildScope(Workspace workspace, SourceFile file,
        IEnumerable<SyntaxNode> nodes, string scopeId, string scopePath,
        IReadOnlyDictionary<string, string> bindings, IReadOnlyList<string> importChain,
        string? containingOccurrenceId)
    {
        foreach (var element in workspace.ExpandConfigurationScope(file, nodes, scopeId, scopePath,
            bindings, importChain, containingOccurrenceId))
        {
            if (element.IsImport)
            {
                // Localization imports supply labels rather than menu
                // declarations. Their graph occurrences remain available in
                // Workspace.ImportOccurrences and diagnostics; unresolved
                // configuration imports get an explicit visual placeholder.
                if (element.Import!.ParseRole == SourceParseRole.Localization) continue;
                yield return UnresolvedImport(element);
                continue;
            }

            var node = element.Node!;
            if (node.Kind is not ("item" or "menu" or "separator" or "sep")) continue;
            var titleProperty = Property(node, "title");
            string raw = titleProperty is null ? "" : element.File.Value(titleProperty);
            bool titleIsLiteral = Expressions.TryLiteral(titleProperty?.Expression, out var literal);
            string title = titleIsLiteral ? literal : (raw.Length == 0 ? node.Kind : "ƒ " + raw);
            string? configurationLabel = null;
            if (!titleIsLiteral && titleProperty?.Expression is { } expression &&
                workspace.TryResolveSourceString(element.File, expression, element.VisibleBindings,
                    element.ImportChain, element.ScopePath, node.Id, element.ContainingOccurrenceId, out var resolved))
                configurationLabel = resolved;
            var keysProperty = Property(node, "keys");
            string keys = Expressions.TryLiteral(keysProperty?.Expression, out var literalKeys) ? literalKeys : "";
            bool checkedValue = false;
            bool radio = false;
            if (TryStaticInteger(element.File, Property(node, "checked"), out var checkedKind))
            {
                checkedValue = checkedKind is 1 or 2;
                radio = checkedKind == 2;
            }
            yield return new MenuEntry
            {
                Id = element.File.Path + "#" + node.Id + "@" + (element.ContainingOccurrenceId ?? "root"), Title = title, Kind = node.Kind == "sep" ? "separator" : node.Kind,
                Origin = "custom", SourceFile = element.File.Path, SourceNodeId = node.Id,
                SourceStart = node.Start, SourceEnd = node.Start + node.Length,
                SourceHash = element.File.CurrentHash,
                Source = new SourceReference
                {
                    File = element.File.Path,
                    Start = node.Start,
                    End = node.Start + node.Length,
                    NodeId = node.Id,
                    Hash = element.File.CurrentHash,
                    OccurrenceId = element.ContainingOccurrenceId,
                },
                ConfigurationLabel = configurationLabel,
                Keys = keys,
                Checked = checkedValue,
                Radio = radio,
                IsDefault = TryStaticBoolean(element.File, Property(node, "default"), out var isDefault) && isDefault,
                Disabled = TryStaticDisabled(element.File, Property(node, "vis") ?? Property(node, "visibility")),
                Trace = Trace(element),
                Children = BuildScope(workspace, element.File, node.Children, node.Id,
                    element.ScopePath.Length == 0 ? node.Id : element.ScopePath + "/" + node.Id,
                    element.VisibleBindings, element.ImportChain, element.ContainingOccurrenceId).ToList()
            };
        }
    }

    private static MenuEntry UnresolvedImport(ConfigurationElement element)
    {
        var occurrence = element.Import!;
        string expression = string.IsNullOrWhiteSpace(occurrence.ExpressionText) ? "(runtime expression)" : occurrence.ExpressionText!;
        string status = occurrence.Status switch
        {
            ImportResolutionStatus.Missing => "missing",
            ImportResolutionStatus.Remote => "remote",
            ImportResolutionStatus.Cycle => "cycle",
            ImportResolutionStatus.RoleConflict => "role conflict",
            ImportResolutionStatus.Limit => "limit",
            _ => "unresolved",
        };
        var trace = new List<string>
        {
            "Import occurrence in " + element.File.Path,
            "Status: " + status,
            "Scope: " + (string.IsNullOrEmpty(element.ScopePath) ? "root" : element.ScopePath),
        };
        if (occurrence.ImportChain.Count > 0) trace.Add("Import chain: " + string.Join(" -> ", occurrence.ImportChain));
        trace.Add("Runtime-dependent imports remain visible but are excluded from preview evaluation.");
        return new MenuEntry
        {
            Id = "import:" + occurrence.OccurrenceId,
            Title = "↪ import " + expression + " [" + status + "]",
            Kind = "import",
            Origin = "import",
            SourceFile = element.File.Path,
            SourceNodeId = occurrence.SourceNodeId,
            ChildrenCaptured = false,
            Trace = trace,
            Diagnostics = occurrence.Diagnostics.ToList(),
        };
    }

    private static List<string> Trace(ConfigurationElement element)
    {
        var trace = new List<string> { "Defined in " + element.File.Path };
        if (element.ImportChain.Count > 1)
            trace.Add("Imported at: " + string.Join(" -> ", element.ImportChain));
        trace.Add("Runtime conditions have not been evaluated.");
        return trace;
    }

    private static SyntaxProperty? Property(SyntaxNode node, string name) =>
        node.Properties.FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool TryStaticInteger(SourceFile file, SyntaxProperty? property, out int value)
    {
        value = 0;
        return property is not null && property.ValueLength > 0 &&
            int.TryParse(file.Value(property).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryStaticBoolean(SourceFile file, SyntaxProperty? property, out bool value)
    {
        value = false;
        if (property is null) return false;
        // Native boolean properties also allow a flag without an assignment;
        // the presence of `default` is therefore a known true value.
        if (property.ValueLength == 0) { value = true; return true; }
        return bool.TryParse(file.Value(property).Trim(), out value);
    }

    private static bool TryStaticDisabled(SourceFile file, SyntaxProperty? property)
    {
        if (property is null || property.ValueLength == 0) return false;
        string raw = file.Value(property).Trim();
        if (raw.Equals("disable", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("vis.disable", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("vis.disabled", StringComparison.OrdinalIgnoreCase)) return true;
        return Expressions.TryLiteral(property?.Expression, out var literal) &&
            (literal.Equals("disable", StringComparison.OrdinalIgnoreCase) || literal.Equals("disabled", StringComparison.OrdinalIgnoreCase));
    }

    public static (SourceFile File, SyntaxNode Node)? Resolve(Workspace workspace, MenuEntry entry)
    {
        var reference = SourceReferenceFor(entry);
        if (reference is null || !workspace.TryResolveSource(reference, out var binding)) return null;
        if (!string.IsNullOrWhiteSpace(entry.Kind) &&
            !binding.Node.Kind.Equals(entry.Kind, StringComparison.OrdinalIgnoreCase) &&
            !(entry.Kind.Equals("separator", StringComparison.OrdinalIgnoreCase) && binding.Node.Kind.Equals("sep", StringComparison.OrdinalIgnoreCase)))
            return null;
        return (binding.File, binding.Node);
    }

    public static void BindCaptureSources(Workspace workspace, MenuSnapshot snapshot)
    {
        BindEvidenceSource(workspace, snapshot, snapshot.Source, null);
        foreach (var entry in Descendants(snapshot.Entries).Concat(Descendants(snapshot.Original)).Distinct())
        {
            BindEntrySource(workspace, snapshot, entry);
            BindEvidenceSource(workspace, snapshot, entry.Source, entry);
            if (entry.RuleOutcomes is not null)
                foreach (var outcome in entry.RuleOutcomes)
                    BindEvidenceSource(workspace, snapshot, outcome.Source, entry);
            if (entry.PropertyEffects is not null)
                foreach (var effect in entry.PropertyEffects)
                    BindEvidenceSource(workspace, snapshot, effect.Source, entry);
            if (entry.EffectiveSettings?.SettingSources is not null)
                foreach (var setting in entry.EffectiveSettings.SettingSources)
                    BindEvidenceSource(workspace, snapshot, setting.Source, entry);
            if (entry.Origin.Equals("custom", StringComparison.OrdinalIgnoreCase)) continue;

            // Parser node IDs are session-scoped. A legacy capture can carry
            // only that transient ID; never leave it looking editable after a
            // recapture when no durable marker is available.
            if (string.IsNullOrWhiteSpace(entry.GeneratedRuleMarker))
                entry.GeneratedRuleId = null;

            // A parser node ID is session-scoped.  Keep it when it still
            // resolves, otherwise rediscover the durable marker in the
            // managed source.  This is what makes recapture after reopening
            // update the existing generated rule instead of appending another.
            var candidates = FindGeneratedAssociations(workspace, snapshot, entry);
            if (!string.IsNullOrWhiteSpace(entry.GeneratedRuleMarker))
                candidates = candidates.Where(candidate =>
                    candidate.Marker!.Equals(entry.GeneratedRuleMarker, StringComparison.Ordinal)).ToArray();

            if (candidates.Length == 1 && candidates[0].IsValid)
            {
                entry.GeneratedRuleMarker = candidates[0].Marker;
                entry.GeneratedRuleId = candidates[0].Node?.Id;
            }
            else if (candidates.Length > 1)
            {
                entry.GeneratedRuleId = null;
                AddEntryDiagnostic(snapshot, entry, new("RULE_ASSOCIATION_AMBIGUOUS",
                    "More than one generated rule matches this captured entry. The rules must be reconciled before editing.",
                    "warning", entry.SourceFile));
            }
            else if (!string.IsNullOrWhiteSpace(entry.GeneratedRuleMarker))
            {
                entry.GeneratedRuleId = null;
                AddEntryDiagnostic(snapshot, entry, new("RULE_ASSOCIATION_STALE",
                    "The generated rule marker could not be matched to a valid selector and scope in the opened source.",
                    "warning", entry.SourceFile));
            }
        }

        if (snapshot.RuleOutcomes is not null)
            foreach (var outcome in snapshot.RuleOutcomes)
                BindEvidenceSource(workspace, snapshot, outcome.Source, null);
        if (snapshot.PropertyEffects is not null)
            foreach (var effect in snapshot.PropertyEffects)
                BindEvidenceSource(workspace, snapshot, effect.Source, null);
        if (snapshot.EffectiveSettings?.SettingSources is not null)
            foreach (var setting in snapshot.EffectiveSettings.SettingSources)
                BindEvidenceSource(workspace, snapshot, setting.Source, null);
    }

    private static void BindEvidenceSource(Workspace workspace, MenuSnapshot snapshot,
        SourceReference? reference, MenuEntry? entry)
    {
        // A provider may intentionally omit provenance in a legacy capture.
        // When it does publish a complete source identity, verify it against
        // the exact workspace bytes and parser identity before exposing the
        // evidence as an edit-capable association.
        if (reference is null || !reference.HasFileHash) return;
        if (workspace.TryResolveSource(reference, out _)) return;
        var diagnostic = new Diagnostic("CAPTURE_EVIDENCE_SOURCE_VERSION",
            "The captured evidence source does not match the opened workspace bytes, source identity, or import occurrence. The evidence remains viewable, but source edits are disabled.",
            "warning", reference.File, reference.Start ?? 0,
            reference.Start.HasValue && reference.End.HasValue && reference.End >= reference.Start
                ? reference.End.Value - reference.Start.Value : 0, reference.NodeId);
        if (entry is not null) AddEntryDiagnostic(snapshot, entry, diagnostic);
        else if (!snapshot.Diagnostics.Any(existing => existing.Code == diagnostic.Code &&
            string.Equals(existing.File, diagnostic.File, StringComparison.OrdinalIgnoreCase)))
            snapshot.Diagnostics.Add(diagnostic);
    }

    private static void BindEntrySource(Workspace workspace, MenuSnapshot snapshot, MenuEntry entry)
    {
        var reference = SourceReferenceFor(entry);
        if (reference is null) return;
        if (!workspace.TryResolveSource(reference, entry.Kind, out var binding))
        {
            // Keep the captured file/hash/location as evidence, but clear the
            // session binding so source edits are disabled until the source is
            // reopened or recaptured from matching bytes.
            entry.SourceNodeId = null;
            entry.SourceEnd = reference.End;
            if (entry.Source is not null) entry.Source.NodeId = null;
            AddEntryDiagnostic(snapshot, entry, new("CAPTURE_SOURCE_VERSION",
                "The captured source does not match the opened workspace bytes or import occurrence. Source edits are disabled until the matching source is reopened and captured.",
                "warning", reference.File));
            return;
        }

        var bound = new SourceReference
        {
            File = binding.File.Path,
            Start = binding.Node.Start,
            End = binding.Node.Start + binding.Node.Length,
            NodeId = binding.Node.Id,
            Hash = binding.File.CurrentHash,
            OccurrenceId = reference.OccurrenceId,
        };
        ApplySourceReference(entry, bound);
    }

    private static void AddEntryDiagnostic(MenuSnapshot snapshot, MenuEntry entry, Diagnostic diagnostic)
    {
        if (!entry.Diagnostics.Any(existing => existing.Code == diagnostic.Code &&
            string.Equals(existing.File, diagnostic.File, StringComparison.OrdinalIgnoreCase)))
            entry.Diagnostics.Add(diagnostic);
        if (!snapshot.Diagnostics.Any(existing => existing.Code == diagnostic.Code &&
            string.Equals(existing.File, diagnostic.File, StringComparison.OrdinalIgnoreCase)))
            snapshot.Diagnostics.Add(diagnostic);
    }

    public static void Remove(Workspace workspace, MenuSnapshot snapshot, MenuEntry entry, RuleScope scope)
    {
        if (entry.Origin == "custom")
        {
            var resolved = Resolve(workspace, entry) ?? throw new InvalidDataException("The custom definition could not be located. Reopen the configuration and capture again.");
            workspace.Checkpoint();
            resolved.File.Replace(resolved.Node.Start, resolved.Node.Length, "");
        }
        else SetNativeProperties(workspace, snapshot, entry, scope, new() { ["vis"] = "vis.remove" });
    }

    public static void Rename(Workspace workspace, MenuSnapshot snapshot, MenuEntry entry, string title, RuleScope scope)
    {
        if (entry.Origin == "custom")
        {
            var resolved = Resolve(workspace, entry) ?? throw new InvalidDataException("The custom definition could not be located.");
            workspace.SetProperty(resolved.File, resolved.Node, "title", Expressions.Quote(title));
        }
        else SetNativeProperties(workspace, snapshot, entry, scope, new() { ["title"] = Expressions.Quote(title) });
    }

    public static void Move(Workspace workspace, MenuSnapshot snapshot, MenuEntry entry, MenuEntry? parent, int index, RuleScope scope)
    {
        if (index < 0) throw new InvalidDataException("Invalid insertion position.");
        if (parent?.Id == entry.Id || (parent is not null && Descendants(entry.Children).Any(e => e.Id == parent.Id)))
            throw new InvalidDataException("A menu cannot be moved inside itself.");
        if (parent is not null && parent.Kind != "menu") throw new InvalidDataException("Choose a submenu as the destination.");
        if (parent?.ChildrenCaptured == false) throw new InvalidDataException("Open and capture the destination submenu before moving entries into it.");
        if (entry.Kind == "separator" && entry.Origin != "custom") throw new InvalidDataException("Native separators do not have stable identities. Hide separator groups through appearance settings instead.");

        if (entry.Origin == "custom" && snapshot.Phase == "configuration")
        {
            var source = Resolve(workspace, entry) ?? throw new InvalidDataException("The custom definition could not be located.");
            var target = parent is null ? null : Resolve(workspace, parent);
            var siblings = parent?.Children ?? snapshot.Entries;
            MenuEntry? sibling = siblings.Where(e => e.Id != entry.Id).ElementAtOrDefault(index);
            var neighbor = sibling is null ? null : Resolve(workspace, sibling);
            SourceFile destination = target?.File ?? neighbor?.File ?? source.File;
            int offset = neighbor?.Node.Start ?? target?.Node.ChildInsert ?? destination.Text.Length;
            if (offset < 0) throw new InvalidDataException("This destination has no editable body.");
            string declaration = source.File.Slice(source.Node.Start, source.Node.Length);
            var movedIdentities = source.File.AllNodes().Where(n => n.Start >= source.Node.Start && n.Start + n.Length <= source.Node.Start + source.Node.Length)
                .Select(n => new NodeIdentity(n.Start - source.Node.Start, n.Kind, n.Id)).ToArray();
            // Moving between scopes can change variable bindings. Keep scope-changing edits explicit.
            if (source.File != destination)
                throw new InvalidDataException("Moving a definition across imported scopes can change variable bindings. Create it in the destination scope explicitly.");
            workspace.Checkpoint();
            source.File.Replace(source.Node.Start, source.Node.Length, "");
            if (source.File == destination && offset > source.Node.Start) offset -= source.Node.Length;
            destination.Replace(offset, 0, declaration + destination.NewLine);
            foreach (var identity in movedIdentities)
            {
                var moved = destination.AllNodes().FirstOrDefault(n => n.Start == offset + identity.Start && n.Kind == identity.Kind);
                if (moved is not null) moved.Id = identity.Id;
            }
            entry.SourceNodeId = destination.AllNodes().FirstOrDefault(n => n.Start == offset && n.Kind == source.Node.Kind)?.Id
                ?? throw new InvalidDataException("The moved definition could not be resolved. Undo the move and review diagnostics.");
            return;
        }
        string menu = parent is null ? "" : ParentName(snapshot, parent);
        if (entry.Origin == "custom")
        {
            var resolved = Resolve(workspace, entry) ?? throw new InvalidDataException("The custom definition has no source identity in this snapshot.");
            // Two span changes are applied from right to left as one undo operation.
            var patches = new List<(int Start, int Length, string Text)>();
            foreach (var (name, value) in new[] { ("pos", index.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("menu", Expressions.Quote(menu)) })
            {
                var property = resolved.Node.Properties.FirstOrDefault(p => p.Name == name);
                if (property is not null) patches.Add((property.ValueStart, property.ValueLength, value));
                else if (resolved.Node.PropertyInsert >= 0) patches.Add((resolved.Node.PropertyInsert, 0, " " + name + "=" + value + " "));
                else throw new InvalidDataException("The custom definition cannot accept placement properties.");
            }
            workspace.Checkpoint();
            foreach (var patch in patches.OrderByDescending(p => p.Start)) resolved.File.Replace(patch.Start, patch.Length, patch.Text);
        }
        else SetNativeProperties(workspace, snapshot, entry, scope, new() { ["pos"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture), ["menu"] = Expressions.Quote(menu) });
    }

    public static void SetNativeProperties(Workspace workspace, MenuSnapshot snapshot, MenuEntry entry, RuleScope scope, Dictionary<string, string> changes)
    {
        if (entry.Origin == "custom") throw new InvalidDataException("Custom definitions must be edited through their source.");
        foreach (var change in changes)
        {
            if (change.Key is not ("title" or "vis" or "pos" or "menu" or "image" or "checked" or "tip"))
                throw new InvalidDataException("This generated rule property is not supported.");
            if (!IsPropertyEditAllowed(snapshot, change.Key) ||
                (entry.EffectiveSettings is not null &&
                 !IsPropertyEditAllowed(new MenuSnapshot { EffectiveSettings = entry.EffectiveSettings }, change.Key)))
            {
                string reason = FindSettingGates(snapshot, change.Key)
                    .Concat(entry.EffectiveSettings is null
                        ? []
                        : FindSettingGates(new MenuSnapshot { EffectiveSettings = entry.EffectiveSettings }, change.Key))
                    .FirstOrDefault(gate => gate.Enabled == false)?.Reason
                    ?? $"The captured settings disable {NormalizeSettingProperty(change.Key)} changes.";
                throw new InvalidDataException("NATIVE_PROPERTY_DISABLED: " + reason);
            }
        }

        // Keep the original captured title as the selector identity.  The UI
        // may update entry.Title after an edit; MatchTitle must remain stable so
        // the same rule continues to target the original native item.
        if (string.IsNullOrWhiteSpace(entry.MatchTitle)) entry.MatchTitle = entry.Title;
        string match = Match(snapshot, entry, scope);
        var existing = FindGeneratedRuleForEdit(workspace, snapshot, entry, scope, match);
        if (existing is not null)
        {
            var file = existing.File!;
            var rule = existing.Node!;
            var patches = new List<(int Start, int Length, string Text)>();
            foreach (var change in changes)
            {
                var property = rule.Properties.FirstOrDefault(p =>
                    p.Name.Equals(change.Key, StringComparison.OrdinalIgnoreCase));
                if (property is null)
                {
                    if (rule.PropertyInsert < 0)
                        throw new InvalidDataException("The generated rule cannot accept this property.");
                    patches.Add((rule.PropertyInsert, 0, " " + change.Key + "=" + change.Value + " "));
                }
                else
                {
                    patches.Add((property.ValueStart, property.ValueLength, change.Value));
                }
            }
            // Apply right-to-left so all offsets refer to the original source
            // declaration. Unknown properties, comments, aliases, and source
            // formatting remain untouched.
            workspace.Checkpoint();
            foreach (var patch in patches.OrderByDescending(patch => patch.Start))
                file.Replace(patch.Start, patch.Length, patch.Text);
            var rebound = FindRuleByMarker(workspace, existing.Marker!);
            entry.GeneratedRuleMarker = existing.Marker;
            entry.GeneratedRuleId = rebound?.Node?.Id;
            if (rebound is null)
                throw new InvalidDataException("The generated modification rule could not be rebound after editing. Undo this change and review its diagnostics.");
        }
        else
        {
            string marker = Guid.NewGuid().ToString("N");
            string declaration = "modify(" + match + " " + string.Join(" ", changes.Select(change => change.Key + "=" + change.Value)) + ")";
            // The marker is a source comment, not a parser property.  Include a
            // fingerprint of the exact selector and scope so a later recapture
            // can validate that an apparently matching rule still targets the
            // same entry before it is edited.
            string markerLine = GeneratedRuleMarkerCommentPrefix + marker + " " + Fingerprint(match) +
                " scope=" + scope.ToString().ToLowerInvariant();
            // Do not materialize the managed source before Append creates its
            // checkpoint.  The first generated edit may need to undo both the
            // new file and the root import that EnsureManaged adds; creating
            // either one before the checkpoint would leave an empty draft or
            // import behind after Undo.
            string newline = workspace.Files.TryGetValue(workspace.ManagedPath, out var managedSource)
                ? managedSource.NewLine
                : workspace.Files.TryGetValue(workspace.RootPath, out var rootSource)
                    ? rootSource.NewLine
                    : "\n";
            workspace.Append(markerLine + newline + declaration);
            var managed = workspace.Files[workspace.ManagedPath];
            var rebound = FindRuleByMarker(workspace, marker);
            if (rebound is null)
                throw new InvalidDataException("The generated modification rule did not parse or could not be located. Undo this change and review its diagnostics.");
            entry.GeneratedRuleMarker = marker;
            entry.GeneratedRuleId = rebound.Node?.Id;
        }
    }

    /// <summary>
    /// Returns source-backed rules that could have affected <paramref name="entry"/>.
    /// The result includes unmarked shared rules and durable generated rules;
    /// callers can inspect <see cref="RuleAssociation.IsGenerated"/> when they
    /// need to offer a scoped edit versus a shared-rule action.
    /// </summary>
    public static IReadOnlyList<RuleAssociation> FindMatchingRules(
        Workspace workspace, MenuSnapshot snapshot, MenuEntry entry)
    {
        var result = new List<RuleAssociation>();
        var managed = workspace.Files.GetValueOrDefault(workspace.ManagedPath);
        var seenDeclarations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<(SourceFile File, string? OccurrenceId)>();
        if (workspace.Files.TryGetValue(workspace.RootPath, out var root))
            sources.Add((root, null));
        foreach (var occurrence in workspace.ImportOccurrences.Where(occurrence =>
            occurrence.ParseRole == SourceParseRole.Configuration && occurrence.ResolvedPath is not null))
        {
            if (!workspace.Files.TryGetValue(occurrence.ResolvedPath!, out var imported) ||
                workspace.DetachedFiles.Contains(imported.Path)) continue;
            sources.Add((imported, occurrence.OccurrenceId));
        }
        foreach (var file in workspace.EffectiveFiles)
            if (!sources.Any(source => string.Equals(source.File.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                sources.Add((file, null));

        foreach (var (file, occurrenceId) in sources)
        {
            bool markerAllowed = managed is not null &&
                string.Equals(file.Path, managed.Path, StringComparison.OrdinalIgnoreCase);
            foreach (var node in file.AllNodes().Where(node =>
                node.Kind.Equals("modify", StringComparison.OrdinalIgnoreCase) ||
                node.Kind.Equals("remove", StringComparison.OrdinalIgnoreCase)))
            {
                // One imported declaration can be evaluated more than once in
                // distinct import scopes. Keep each occurrence so its source
                // evidence and effective bindings are not silently collapsed.
                string declarationKey = file.Path + "#" + node.Id + "@" + (occurrenceId ?? "root");
                if (!seenDeclarations.Add(declarationKey)) continue;
                var marker = markerAllowed ? ReadMarker(file, node) : null;
                var evidence = EvidenceFor(workspace, snapshot, entry, file, node, marker, occurrenceId);
                var properties = RuleProperties(file, node);
                bool sourceCurrent = EvidenceSourceMatches(workspace, file, evidence);
                bool targetMatch = RuleCanAffect(snapshot, entry, file, node, marker, evidence is not null);
                if (!targetMatch && evidence is null) continue;

                var diagnostics = new List<Diagnostic>();
                bool valid = sourceCurrent && targetMatch;
                if (!sourceCurrent)
                    diagnostics.Add(new("RULE_SOURCE_VERSION", "The rule source changed after this capture and cannot be edited from the captured association.", "warning", file.Path, node.Start, node.Length, node.Id));
                if (marker is not null && !MarkerMatchesPossibleScope(snapshot, entry, marker))
                {
                    valid = false;
                    diagnostics.Add(new("RULE_ASSOCIATION_STALE", "The generated rule marker no longer matches this entry's selector or scope.", "warning", file.Path, node.Start, node.Length, node.Id));
                }
                result.Add(new RuleAssociation
                {
                    RuleId = marker?.Id ?? node.Id,
                    Marker = marker?.Id,
                    Kind = node.Kind,
                    Outcome = evidence?.Outcome ?? "unknown",
                    Reason = evidence?.Reason,
                    Source = RuleSource(file, node, occurrenceId),
                    Properties = properties,
                    IsValid = valid,
                    Diagnostics = diagnostics,
                    Node = node,
                    File = file,
                });
            }
        }

        // Keep evidence for a removed/overwritten rule visible even when its
        // source node is unavailable or the entry disappeared from final menu
        // construction.  An evidence record never authorizes an edit by itself.
        foreach (var evidence in EvidenceFor(snapshot, entry))
        {
            if (result.Any(candidate =>
                candidate.RuleId.Equals(evidence.RuleId, StringComparison.OrdinalIgnoreCase) ||
                (candidate.File is not null && candidate.Source is not null && evidence.Source is not null &&
                 candidate.Source.Start == evidence.Source.Start && candidate.Source.End == evidence.Source.End &&
                 EvidenceSourceMatches(workspace, evidence.Source, candidate.File,
                     candidate.Source.OccurrenceId)))) continue;
            result.Add(new RuleAssociation
            {
                RuleId = evidence.RuleId,
                // RuleOutcome.RuleId is the native evaluation step (such as
                // `static.title`), not the durable Studio marker. An
                // unresolved evidence record must stay read-only and must not
                // be mistaken for a generated rule by the UI or edit path.
                Marker = null,
                Kind = "modify",
                Outcome = evidence.Outcome,
                Reason = evidence.Reason,
                Source = evidence.Source,
                IsValid = false,
                Diagnostics = [new("RULE_EVIDENCE_UNRESOLVED", "The captured rule outcome has no matching source declaration in this workspace.", "warning", evidence.Source?.File)],
            });
        }
        return result;
    }

    private static bool EvidenceSourceMatches(Workspace workspace, SourceFile managed,
        RuleOutcome? evidence)
    {
        if (evidence is null) return true;
        var reference = evidence?.Source;
        if (reference is null || !reference.HasFileHash) return false;

        string? path = reference.File;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                string fullPath = Path.IsPathFullyQualified(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(path, Path.GetDirectoryName(workspace.RootPath)!);
                if (!string.Equals(fullPath, managed.Path, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        // A native provider may publish a source node ID from a different
        // parser session (for example `n<offset>`), while SourceFile assigns a
        // fresh in-memory identity when it opens the same bytes. Resolve the
        // complete reference through Workspace so its unique span fallback,
        // hash, and import occurrence checks are applied together.
        if (!string.Equals(reference.Hash, managed.CurrentHash, StringComparison.OrdinalIgnoreCase))
            return false;
        return workspace.TryResolveSource(reference, out var binding) &&
            string.Equals(binding.File.Path, managed.Path, StringComparison.OrdinalIgnoreCase);
    }

    // Short aliases keep the Core surface easy to discover from the UI and
    // preserve a descriptive name for callers that already use the noun.
    public static IReadOnlyList<RuleAssociation> MatchingRules(
        Workspace workspace, MenuSnapshot snapshot, MenuEntry entry) =>
        FindMatchingRules(workspace, snapshot, entry);

    public static IReadOnlyList<RuleAssociation> FindRuleAssociations(
        Workspace workspace, MenuSnapshot snapshot, MenuEntry entry) =>
        FindMatchingRules(workspace, snapshot, entry);

    /// <summary>
    /// Returns effective settings evidence that governs a property.  An empty
    /// result means that the capture did not publish a gate, so callers must
    /// present the setting as unknown rather than assuming it is enabled.
    /// </summary>
    public static IReadOnlyList<SettingGate> FindSettingGates(MenuSnapshot snapshot, string property)
    {
        if (snapshot.EffectiveSettings is null || string.IsNullOrWhiteSpace(property)) return [];
        string normalized = NormalizeSettingProperty(property);
        var gates = new List<SettingGate>();
        // Existing native entries are controlled by settings.modify. The
        // other transport groups describe separator cleanup and settings.new;
        // neither is a gate for a quick edit to an existing entry.
        var keys = new List<string> { "enabled" };
        string? specific = normalized switch
        {
            "title" => "title",
            "vis" => "visibility",
            "menu" => "parent",
            "pos" => "position",
            "image" => "image",
            "separator" => "separator",
            "keys" => "keys",
            _ => null,
        };
        if (specific is not null) keys.Add(specific);

        foreach (string key in keys)
        {
            string canonical = "modifyitems." + key;
            var sourceEvidence = snapshot.EffectiveSettings.SettingSources?
                .FirstOrDefault(candidate => CanonicalSettingPath(candidate.Property)
                    .Equals(canonical, StringComparison.OrdinalIgnoreCase));

            bool? enabled = null;
            string? raw = null;
            bool found = snapshot.EffectiveSettings.ModifyItems.HasValue &&
                (key == "enabled" || snapshot.EffectiveSettings.ModifyItems.Value.ValueKind == JsonValueKind.Object) &&
                TryReadSetting(snapshot.EffectiveSettings.ModifyItems.Value, key,
                    out enabled, out raw);
            if (!found && sourceEvidence is not null)
            {
                raw = sourceEvidence.Value;
                enabled = ParseBoolean(raw);
                found = true;
            }
            if (!found) continue;

            string authored = key == "enabled" ? "settings.modify.enabled" : "settings.modify." + key;
            string? reason = enabled == false ? $"{authored} disables {normalized} changes." : null;
            gates.Add(new SettingGate(authored, enabled, raw, sourceEvidence?.Source, reason));
        }
        return gates;
    }

    public static IReadOnlyList<SettingGate> SettingGates(MenuSnapshot snapshot, string property) =>
        FindSettingGates(snapshot, property);

    /// <summary>
    /// Returns false only when native evidence proves a gate is disabled. A
    /// missing or non-boolean setting remains unknown and therefore returns
    /// true for compatibility with existing editing flows.
    /// </summary>
    public static bool IsPropertyEditAllowed(MenuSnapshot snapshot, string property) =>
        FindSettingGates(snapshot, property).All(gate => gate.Enabled != false);

    private static string NormalizeSettingProperty(string property)
    {
        string value = property.Trim().ToLowerInvariant();
        return value switch
        {
            "visibility" => "vis",
            "position" => "pos",
            "parent" => "menu",
            "icon" => "image",
            _ => value,
        };
    }

    private static string CanonicalSettingPath(string property)
    {
        string value = property.Trim().ToLowerInvariant().Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);
        if (value.StartsWith("settings.modify.", StringComparison.Ordinal))
            value = "modifyitems." + value["settings.modify.".Length..];
        else if (value.Equals("settings.modify", StringComparison.Ordinal) ||
                 value.Equals("modifyitems", StringComparison.Ordinal))
            value = "modifyitems.enabled";

        int separator = value.LastIndexOf('.');
        if (separator >= 0)
        {
            string prefix = value[..(separator + 1)];
            string leaf = NormalizeSettingProperty(value[(separator + 1)..]) switch
            {
                "vis" => "visibility",
                "pos" => "position",
                "menu" => "parent",
                var current => current,
            };
            value = prefix + leaf;
        }
        return value;
    }

    private static bool TryReadSetting(JsonElement value, string property, out bool? enabled, out string? raw)
    {
        enabled = null;
        raw = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in new[] { property, property == "vis" ? "visibility" : property == "pos" ? "position" : property == "menu" ? "parent" : property })
            {
                if (value.TryGetProperty(key, out var child))
                {
                    raw = child.ToString();
                    enabled = ParseBoolean(child.ToString());
                    return true;
                }
            }
            return false;
        }
        enabled = ParseBoolean(raw);
        return enabled.HasValue;
    }

    private static bool? ParseBoolean(string? value)
    {
        if (value is null) return null;
        if (bool.TryParse(value, out var result)) return result;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return number != 0;
        return null;
    }

    private static IReadOnlyList<RuleOutcome> EvidenceFor(MenuSnapshot snapshot, MenuEntry entry)
    {
        var values = new List<RuleOutcome>();
        if (entry.RuleOutcomes is not null) values.AddRange(entry.RuleOutcomes);
        if (snapshot.RuleOutcomes is not null)
            values.AddRange(snapshot.RuleOutcomes.Where(outcome =>
                string.IsNullOrWhiteSpace(outcome.EntryId) || outcome.EntryId.Equals(entry.Id, StringComparison.Ordinal)));
        return values;
    }

    private static RuleOutcome? EvidenceFor(Workspace workspace, MenuSnapshot snapshot,
        MenuEntry entry, SourceFile file, SyntaxNode node, MarkerInfo? marker,
        string? occurrenceId)
    {
        string nodeId = node.Id;
        return EvidenceFor(snapshot, entry).FirstOrDefault(outcome =>
        {
            if (!EvidenceSourceMatches(workspace, outcome.Source, file, occurrenceId)) return false;
            // Native rule IDs describe the evaluation step (for example
            // `static.title`), not the parser node ID. A complete source span
            // is therefore also a declaration identity after Workspace has
            // verified its hash and occurrence.
            bool sourceIdentified = outcome.Source is not null &&
                (outcome.Source.NodeId is not null || outcome.Source.Start.HasValue || outcome.Source.End.HasValue);
            return sourceIdentified ||
                (!string.IsNullOrWhiteSpace(marker?.Id) && outcome.RuleId.Equals(marker!.Id, StringComparison.OrdinalIgnoreCase)) ||
                outcome.RuleId.Equals(nodeId, StringComparison.OrdinalIgnoreCase) ||
                (outcome.Source?.NodeId is not null && outcome.Source.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
        });
    }

    private static bool EvidenceSourceMatches(Workspace workspace, SourceReference? reference,
        SourceFile file, string? occurrenceId)
    {
        if (reference is null || !reference.HasFileHash) return false;
        if (!string.Equals(reference.OccurrenceId, occurrenceId, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(reference.File))
        {
            try
            {
                string fullPath = Path.IsPathFullyQualified(reference.File)
                    ? Path.GetFullPath(reference.File)
                    : Path.GetFullPath(reference.File, Path.GetDirectoryName(workspace.RootPath)!);
                if (!string.Equals(fullPath, file.Path, StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return false;
            }
        }
        // Native capture IDs are not required to equal the managed parser's
        // session IDs. When a hash is available, Workspace performs the
        // authoritative identity/span/occurrence check and can safely fall
        // back to a unique source offset.
        if (!workspace.TryResolveSource(reference, out var binding)) return false;
        return string.Equals(binding.File.Path, file.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static SourceReference? SourceReferenceFor(MenuEntry entry)
    {
        var source = entry.Source;
        if (source is null && entry.SourceFile is null && entry.SourceHash is null &&
            entry.SourceNodeId is null && !entry.SourceStart.HasValue && !entry.SourceEnd.HasValue) return null;
        return new SourceReference
        {
            File = entry.SourceFile ?? source?.File,
            Start = entry.SourceStart ?? source?.Start,
            End = entry.SourceEnd ?? source?.End,
            NodeId = entry.SourceNodeId ?? source?.NodeId,
            Hash = entry.SourceHash ?? source?.Hash,
            OccurrenceId = source?.OccurrenceId,
        };
    }

    private static void ApplySourceReference(MenuEntry entry, SourceReference reference)
    {
        entry.SourceFile = reference.File;
        entry.SourceStart = reference.Start;
        entry.SourceEnd = reference.End;
        entry.SourceNodeId = reference.NodeId;
        entry.SourceHash = reference.Hash;
        entry.Source = new SourceReference
        {
            File = reference.File,
            Start = reference.Start,
            End = reference.End,
            NodeId = reference.NodeId,
            Hash = reference.Hash,
            OccurrenceId = reference.OccurrenceId,
        };
    }

    private static SourceReference RuleSource(SourceFile file, SyntaxNode node, string? occurrenceId = null) => new()
    {
        File = file.Path,
        Start = node.Start,
        End = node.Start + node.Length,
        NodeId = node.Id,
        Hash = file.CurrentHash,
        OccurrenceId = occurrenceId,
    };

    private static Dictionary<string, string> RuleProperties(SourceFile file, SyntaxNode node) =>
        node.Properties.GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => file.Value(group.First()), StringComparer.OrdinalIgnoreCase);

    private static bool RuleCanAffect(MenuSnapshot snapshot, MenuEntry entry, SourceFile file,
        SyntaxNode node, MarkerInfo? marker, bool hasEvidence)
    {
        if (marker is not null && MarkerMatchesPossibleScope(snapshot, entry, marker)) return true;
        if (hasEvidence) return true;

        string? find = PropertyLiteral(file, node, "find");
        string selector = entry.MatchTitle ?? entry.Title;
        if (find is not null && selector.Length > 0 && !find.Equals(selector, StringComparison.OrdinalIgnoreCase)) return false;

        string? parent = PropertyLiteral(file, node, "in") ?? PropertyLiteral(file, node, "menu") ?? PropertyLiteral(file, node, "parent");
        if (parent is not null && !parent.Equals(entry.ParentPath ?? "", StringComparison.OrdinalIgnoreCase)) return false;

        string where = PropertyText(file, node, "where") ?? "";
        if (where.Length > 0)
        {
            if (where.Contains("this.name", StringComparison.OrdinalIgnoreCase) && selector.Length > 0 &&
                !where.Contains(selector, StringComparison.OrdinalIgnoreCase)) return false;
            if (entry.StableId is not null && where.Contains("this.id", StringComparison.OrdinalIgnoreCase) &&
                !where.Contains(entry.StableId, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return find is not null || parent is not null || where.Length > 0 || node.Kind.Equals("remove", StringComparison.OrdinalIgnoreCase);
    }

    private static string? PropertyText(SourceFile file, SyntaxNode node, string name)
    {
        var property = node.Properties.FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return property is null ? null : file.Value(property).Trim();
    }

    private static string? PropertyLiteral(SourceFile file, SyntaxNode node, string name)
    {
        var property = node.Properties.FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (property is null) return null;
        if (Expressions.TryLiteral(property.Expression, out var literal)) return literal;
        string value = file.Value(property).Trim();
        return Expressions.TryLiteral(value, out literal) ? literal : value;
    }

    private static bool MarkerMatchesPossibleScope(MenuSnapshot snapshot, MenuEntry entry, MarkerInfo marker)
    {
        if (string.IsNullOrWhiteSpace(marker.Fingerprint)) return true;
        if (marker.Scope is { } recordedScope)
        {
            try { return Fingerprint(Match(snapshot, entry, recordedScope)).Equals(marker.Fingerprint, StringComparison.OrdinalIgnoreCase); }
            catch (InvalidDataException) { return false; }
        }
        foreach (var scope in Enum.GetValues<RuleScope>())
        {
            try
            {
                if (Fingerprint(Match(snapshot, entry, scope)).Equals(marker.Fingerprint, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (InvalidDataException) { }
        }
        return false;
    }

    private static bool MarkerMatchesScope(MenuSnapshot snapshot, MenuEntry entry,
        MarkerInfo marker, RuleScope scope, string match)
    {
        return (marker.Scope is null || marker.Scope == scope) &&
            (string.IsNullOrWhiteSpace(marker.Fingerprint) ||
            Fingerprint(match).Equals(marker.Fingerprint, StringComparison.OrdinalIgnoreCase));
    }

    private static string Fingerprint(string value) =>
        SourceFile.Hash(Encoding.UTF8.GetBytes(value));

    private static MarkerInfo? ReadMarker(SourceFile file, SyntaxNode node)
    {
        int begin = Math.Max(0, node.Start - 8192);
        var matches = GeneratedRuleMarker().Matches(file.Text[begin..node.Start]);
        if (matches.Count == 0) return null;
        var match = matches[^1];
        string trailing = file.Text[(begin + match.Index + match.Length)..node.Start];
        // A marker belongs to one rule only when no other source text appears
        // between the comment and declaration. This prevents a stale marker
        // above an unrelated rule from being adopted during recapture.
        if (trailing.Any(character => !char.IsWhiteSpace(character))) return null;
        RuleScope? scope = null;
        if (match.Groups["scope"].Success && Enum.TryParse<RuleScope>(match.Groups["scope"].Value, ignoreCase: true, out var parsed))
            scope = parsed;
        return new MarkerInfo(match.Groups["id"].Value,
            match.Groups["fingerprint"].Success ? match.Groups["fingerprint"].Value : null, scope);
    }

    private static RuleAssociation? FindGeneratedRuleForEdit(Workspace workspace,
        MenuSnapshot snapshot, MenuEntry entry, RuleScope scope, string match)
    {
        var associations = FindMatchingRules(workspace, snapshot, entry)
            .Where(candidate => candidate.IsGenerated).ToArray();
        if (!string.IsNullOrWhiteSpace(entry.GeneratedRuleMarker))
            associations = associations.Where(candidate => candidate.Marker!.Equals(entry.GeneratedRuleMarker, StringComparison.Ordinal)).ToArray();
        else if (!string.IsNullOrWhiteSpace(entry.GeneratedRuleId))
            associations = associations.Where(candidate => candidate.Node?.Id.Equals(entry.GeneratedRuleId, StringComparison.Ordinal) == true).ToArray();

        if (associations.Length > 1)
            throw new InvalidDataException("RULE_ASSOCIATION_AMBIGUOUS: more than one generated rule matches this entry; reconcile the managed source before editing.");
        if (associations.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(entry.GeneratedRuleMarker))
                throw new InvalidDataException("RULE_ASSOCIATION_STALE: the generated rule marker is missing or no longer matches the requested selector and scope.");
            return null;
        }

        var association = associations[0];
        if (!association.IsValid || association.Node is null || association.File is null ||
            association.Marker is null || !MarkerMatchesScope(snapshot, entry,
                ReadMarker(association.File, association.Node)!, scope, match))
            throw new InvalidDataException("RULE_ASSOCIATION_STALE: the generated rule selector or scope changed outside Studio.");
        return association;
    }

    private static RuleAssociation[] FindGeneratedAssociations(Workspace workspace,
        MenuSnapshot snapshot, MenuEntry entry) =>
        FindMatchingRules(workspace, snapshot, entry).Where(candidate => candidate.IsGenerated).ToArray();

    private static RuleAssociation? FindRuleByMarker(Workspace workspace, string marker)
    {
        var managed = workspace.Files.GetValueOrDefault(workspace.ManagedPath);
        if (managed is null) return null;
        var matches = managed.AllNodes()
            .Where(node => node.Kind.Equals("modify", StringComparison.OrdinalIgnoreCase))
            .Select(node => (Node: node, Marker: ReadMarker(managed, node)))
            .Where(pair => pair.Marker?.Id.Equals(marker, StringComparison.Ordinal) == true)
            .ToArray();
        if (matches.Length != 1) return null;
        return new RuleAssociation
        {
            RuleId = marker,
            Marker = marker,
            Kind = matches[0].Node.Kind,
            Source = RuleSource(managed, matches[0].Node),
            Properties = RuleProperties(managed, matches[0].Node),
            IsValid = true,
            Node = matches[0].Node,
            File = managed,
        };
    }

    private static string ParentName(MenuSnapshot snapshot, MenuEntry parent)
    {
        string name = DestinationTitle(parent.Title);
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/')) throw new InvalidDataException("The destination title cannot be represented as an unambiguous menu path.");
        if (Descendants(snapshot.Entries).Count(e => e.Kind == "menu" && DestinationTitle(e.Title).Equals(name, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidDataException("More than one submenu has this title. Rename the destination before moving entries.");
        var owner = Descendants(snapshot.Entries).FirstOrDefault(e => e.Children.Any(child => child.Id == parent.Id));
        return owner is null ? name : ParentName(snapshot, owner) + "/" + name;
    }

    // MenuItemInfo::normalize removes mnemonic markers and the shortcut suffix.
    // MatchTitle remains the original selector; destinations use the edited title.
    private static string DestinationTitle(string title)
    {
        string value = title.TrimEnd('.').Trim(Enumerable.Range(0, 32).Select(i => (char)i).Append((char)127).ToArray());
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < value.Length && value[i] is not ('\0' or '\t' or '\b'); i++)
        {
            if (value[i] == '&')
            {
                if (i + 1 >= value.Length || value[i + 1] != '&') continue;
                i++;
            }
            result.Append(value[i]);
        }
        return result.ToString();
    }

    public static string Match(MenuSnapshot snapshot, MenuEntry entry, RuleScope scope)
    {
        if (snapshot.Phase == "configuration") throw new InvalidDataException("Capture an actual menu before editing native entries.");
        string selector;
        if (entry.StableId?.StartsWith("shell.muid:", StringComparison.Ordinal) == true &&
            uint.TryParse(entry.StableId.AsSpan(11), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint muid) && muid != 0)
            selector = "this.id==0x" + muid.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        else if (!string.IsNullOrEmpty(entry.StableId) && StableIdentifier().IsMatch(entry.StableId)) selector = "this.id==" + entry.StableId;
        else
        {
            string name = entry.MatchTitle ?? entry.Title;
            if (name.Length == 0) throw new InvalidDataException("This native entry has no persistent matching information.");
            bool Duplicate(IEnumerable<MenuEntry> entries) => Descendants(entries).Count(e =>
                (e.MatchTitle ?? e.Title).Equals(name, StringComparison.OrdinalIgnoreCase) && (string.IsNullOrEmpty(entry.ParentPath) || e.ParentPath == entry.ParentPath)) > 1;
            if (Duplicate(snapshot.Original) || Duplicate(snapshot.Entries))
                throw new InvalidDataException("Multiple native entries match this title and parent. A safe persistent rule cannot be generated.");
            selector = "this.name==" + Expressions.Quote(name);
        }
        var (type, conditions) = ScopeConditions(snapshot, scope);
        conditions.Insert(0, selector);
        return (type.Length > 0 ? "type=" + Expressions.Quote(type) + " " : "") +
            (!string.IsNullOrEmpty(entry.ParentPath) ? "in=" + Expressions.Quote(entry.ParentPath) + " " : "") +
            "where=(" + string.Join(" && ", conditions) + ")";
    }

    private static (string Type, List<string> Conditions) ScopeConditions(MenuSnapshot snapshot, RuleScope scope)
    {
        var conditions = new List<string>();
        string type = "";
        if (scope != RuleScope.AllMatching)
        {
            type = (string.IsNullOrEmpty(snapshot.ContextCategory) ? snapshot.Context : snapshot.ContextCategory).ToLowerInvariant() switch
            {
                "file" or "files" => "file", "folder" or "directory" or "dir" => "dir",
                "folderbackground" or "dir.back" => "dir.back", "drive" => "drive", "drive.back" => "drive.back",
                "desktop" => "desktop", "taskbar" => "taskbar", "recyclebin" => "recyclebin", _ => ""
            };
            if (type.Length == 0) throw new InvalidDataException("The captured context cannot be scoped automatically. Select all matching menus or capture a supported context.");
            if (scope == RuleScope.Category && type == "file" && snapshot.Paths.Length > 0)
            {
                var extensions = snapshot.Paths.Select(path => Path.GetExtension(path) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (extensions.Any(extension => string.IsNullOrWhiteSpace(extension)))
                    throw new InvalidDataException("Category scope requires every captured file to have a concrete extension. Select all matching menus or use an explicit supported scope.");
                string predicate = string.Join(" || ", extensions.Select(extension => "path.ext(sel.path)==" + Expressions.Quote(extension)));
                conditions.Add(extensions.Length == 1 ? predicate : "(" + predicate + ")");
            }
            else if (scope == RuleScope.Category && type == "file")
                throw new InvalidDataException("Category scope requires captured file paths. Select all matching menus or use an explicit supported scope.");
        }
        if (scope == RuleScope.ExactPath)
        {
            if (snapshot.Paths.Length != 1) throw new InvalidDataException("Exact-path scope requires one captured path.");
            conditions.Add("sel.path==" + Expressions.Quote(snapshot.Paths[0]));
        }
        return (type, conditions);
    }

    public static string ScopeProperties(MenuSnapshot snapshot, RuleScope scope)
    {
        if (scope == RuleScope.AllMatching || snapshot.Phase == "configuration") return "";
        var (type, conditions) = ScopeConditions(snapshot, scope);
        return ((type.Length > 0 ? "type=" + Expressions.Quote(type) + " " : "") +
            (conditions.Count > 0 ? "where=(" + string.Join(" && ", conditions) + ")" : "")).Trim();
    }

    [GeneratedRegex(@"^id\.[a-zA-Z_][a-zA-Z_0-9]*$")]
    private static partial Regex StableIdentifier();
}
