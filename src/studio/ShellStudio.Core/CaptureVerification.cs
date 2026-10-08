namespace ShellStudio.Core;

public enum CaptureVerificationStatus
{
    Passed,
    Failed,
    Inconclusive,
}

public sealed record CaptureVerificationDifference(
    string Code,
    string Message,
    string? ExpectedEntryId = null,
    string? ActualEntryId = null);

/// <summary>
/// The structural claims recorded when an edited preview is applied. It is a
/// small immutable value so a later capture can be compared without reusing a
/// mutable WPF tree or re-evaluating the source expressions.
/// </summary>
public sealed record CaptureExpectation(
    string Context,
    string[] Paths,
    long WorkspaceRevision,
    IReadOnlyList<CaptureExpectedEntry> Entries,
    bool CompleteStructure,
    string? RuntimeGeneration = null)
{
    public static CaptureExpectation FromSnapshot(MenuSnapshot snapshot, long workspaceRevision = 0)
    {
        var entries = Flatten(snapshot.Entries).Select(item => new CaptureExpectedEntry(
            item.Entry.Id,
            Identity(item.Entry),
            item.ParentPath,
            item.Index,
            item.Entry.Title,
            item.Entry.Kind,
            item.Entry.Disabled,
            item.Entry.Checked,
            item.Entry.Radio,
            item.Entry.IsDefault,
            item.Entry.Keys,
            item.Entry.OwnerDraw,
            item.Entry.ChildrenCaptured)).ToArray();
        return new(snapshot.Context, snapshot.Paths.ToArray(), workspaceRevision, entries,
            entries.All(entry => entry.ChildrenCaptured), snapshot.RuntimeGeneration);
    }

    internal static string? Identity(MenuEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.StableId)) return "stable:" + entry.StableId;
        if (!string.IsNullOrWhiteSpace(entry.SourceFile) && !string.IsNullOrWhiteSpace(entry.SourceNodeId))
            return "source:" + entry.SourceFile + "#" + entry.SourceNodeId;
        string selector = entry.MatchTitle ?? entry.Title;
        return selector.Length == 0 ? null : "title:" + entry.Kind + ":" + selector;
    }

    private static IEnumerable<(MenuEntry Entry, string ParentPath, int Index)> Flatten(
        IEnumerable<MenuEntry> entries, string parentPath = "")
    {
        int index = 0;
        foreach (var entry in entries)
        {
            yield return (entry, parentPath, index++);
            string childPath = parentPath.Length == 0 ? entry.Title : parentPath + "/" + entry.Title;
            foreach (var child in Flatten(entry.Children, childPath)) yield return child;
        }
    }
}

public sealed record CaptureExpectedEntry(
    string EntryId,
    string? Identity,
    string ParentPath,
    int Index,
    string Title,
    string Kind,
    bool Disabled,
    bool Checked,
    bool Radio,
    bool IsDefault,
    string Keys,
    bool OwnerDraw,
    bool ChildrenCaptured);

public sealed record CaptureVerificationResult(
    CaptureVerificationStatus Status,
    IReadOnlyList<CaptureVerificationDifference> Differences)
{
    public bool IsPassed => Status == CaptureVerificationStatus.Passed;
    public bool IsConclusive => Status != CaptureVerificationStatus.Inconclusive;
}

/// <summary>
/// Compares the structural part of an expected preview with a later native
/// capture. Appearance pixels and human visual acceptance remain separate
/// evidence. Ambiguous or incomplete captures produce Inconclusive, never a
/// passing verification claim.
/// </summary>
public static class CaptureVerification
{
    public static CaptureVerificationResult Compare(CaptureExpectation expected, MenuSnapshot? actual)
    {
        var differences = new List<CaptureVerificationDifference>();
        if (actual is null)
            return Inconclusive("VERIFY_NO_CAPTURE", "No subsequent capture was returned.", differences);
        if (!string.IsNullOrWhiteSpace(expected.Context) && !string.IsNullOrWhiteSpace(actual.Context) &&
            !ContextMatches(expected.Context, actual.Context))
            differences.Add(new("VERIFY_CONTEXT", "The subsequent capture belongs to a different context.", null, null));
        if (expected.Paths.Length > 0 && actual.Paths.Length > 0 &&
            !expected.Paths.SequenceEqual(actual.Paths, StringComparer.OrdinalIgnoreCase))
            differences.Add(new("VERIFY_PATHS", "The subsequent capture used different selected paths.", null, null));

        if (actual.Entries is null)
            return Inconclusive("VERIFY_ENTRIES_UNAVAILABLE", "The subsequent capture did not provide menu entries.", differences);

        var actualRows = Flatten(actual.Entries).ToArray();
        var byIdentity = actualRows.Where(row => CaptureExpectation.Identity(row.Entry) is not null)
            .GroupBy(row => CaptureExpectation.Identity(row.Entry)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in expected.Entries)
        {
            if (item.Identity is null)
            {
                differences.Add(new("VERIFY_NO_IDENTITY", "An expected entry has no stable or unambiguous identity.", item.EntryId));
                continue;
            }
            if (!byIdentity.TryGetValue(item.Identity, out var matches) || matches.Length == 0)
            {
                differences.Add(new("VERIFY_MISSING_ENTRY", "The expected entry was not present in the subsequent capture.", item.EntryId));
                continue;
            }
            if (matches.Length != 1)
            {
                differences.Add(new("VERIFY_AMBIGUOUS_MATCH", "The expected entry matched more than one captured entry; verification is inconclusive.", item.EntryId));
                continue;
            }

            var actualRow = matches[0];
            matched.Add(actualRow.Entry.Id);
            CompareEntry(item, actualRow, differences);
        }

        if (expected.CompleteStructure)
        {
            foreach (var row in actualRows.Where(row => !matched.Contains(row.Entry.Id)))
                differences.Add(new("VERIFY_UNEXPECTED_ENTRY", "The subsequent capture contains an entry that was not in the expected structure.", null, row.Entry.Id));
        }
        else
        {
            differences.Add(new("VERIFY_PARTIAL_CAPTURE", "The expected or subsequent capture contains uncaptured popup descendants, so the complete structure cannot be verified."));
        }

        if (differences.Any(d => d.Code is "VERIFY_CONTEXT" or "VERIFY_PATHS" or "VERIFY_MISSING_ENTRY" or "VERIFY_UNEXPECTED_ENTRY" or "VERIFY_TITLE" or "VERIFY_KIND" or "VERIFY_PARENT" or "VERIFY_ORDER" or "VERIFY_STATE"))
            return new(CaptureVerificationStatus.Failed, differences);
        if (differences.Count > 0)
            return new(CaptureVerificationStatus.Inconclusive, differences);
        return new(CaptureVerificationStatus.Passed, differences);
    }

    private static void CompareEntry(CaptureExpectedEntry expected,
        (MenuEntry Entry, string ParentPath, int Index) actual,
        List<CaptureVerificationDifference> differences)
    {
        if (!string.Equals(expected.Kind, actual.Entry.Kind, StringComparison.OrdinalIgnoreCase))
            differences.Add(new("VERIFY_KIND", "The captured entry kind changed.", expected.EntryId, actual.Entry.Id));
        if (!string.Equals(expected.Title, actual.Entry.Title, StringComparison.Ordinal))
            differences.Add(new("VERIFY_TITLE", "The captured entry title does not match the expected title.", expected.EntryId, actual.Entry.Id));
        if (!string.Equals(expected.ParentPath, actual.ParentPath, StringComparison.Ordinal))
            differences.Add(new("VERIFY_PARENT", "The captured entry is in a different submenu.", expected.EntryId, actual.Entry.Id));
        if (expected.Index != actual.Index)
            differences.Add(new("VERIFY_ORDER", "The captured entry has a different sibling position.", expected.EntryId, actual.Entry.Id));
        if (expected.Disabled != actual.Entry.Disabled || expected.Checked != actual.Entry.Checked ||
            expected.Radio != actual.Entry.Radio || expected.IsDefault != actual.Entry.IsDefault ||
            !string.Equals(expected.Keys, actual.Entry.Keys, StringComparison.Ordinal) ||
            expected.OwnerDraw != actual.Entry.OwnerDraw)
            differences.Add(new("VERIFY_STATE", "The captured entry state does not match the expected state.", expected.EntryId, actual.Entry.Id));
        if (expected.ChildrenCaptured && !actual.Entry.ChildrenCaptured)
            differences.Add(new("VERIFY_SUBMENU_UNAVAILABLE", "The expected submenu was not captured deeply enough to verify its children.", expected.EntryId, actual.Entry.Id));
    }

    private static CaptureVerificationResult Inconclusive(string code, string message,
        List<CaptureVerificationDifference> differences)
    {
        differences.Add(new(code, message));
        return new(CaptureVerificationStatus.Inconclusive, differences);
    }

    private static bool ContextMatches(string expected, string actual) =>
        expected.Equals(actual, StringComparison.OrdinalIgnoreCase) ||
        expected.Contains(actual, StringComparison.OrdinalIgnoreCase) ||
        actual.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(MenuEntry Entry, string ParentPath, int Index)> Flatten(
        IEnumerable<MenuEntry> entries, string parentPath = "")
    {
        int index = 0;
        foreach (var entry in entries)
        {
            yield return (entry, parentPath, index++);
            string childPath = parentPath.Length == 0 ? entry.Title : parentPath + "/" + entry.Title;
            foreach (var child in Flatten(entry.Children, childPath)) yield return child;
        }
    }
}
