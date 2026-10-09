using System.Text.Json;
using ShellStudio.Core;
using ShellStudio.Tools;

internal static class NativeSelectionFixtureTests
{
    private sealed record Expected(string Context, string[] Paths, string[] Types, bool Background, bool Desktop, string Supported, string Rejected);

    public static void Check(string manifestPath)
    {
        Require(Path.IsPathFullyQualified(manifestPath), "fixture manifest path must be absolute");
        using var manifestStream = File.OpenRead(manifestPath);
        Require(manifestStream.Length <= SelectionSnapshotStore.MaxSnapshotBytes, "fixture manifest is oversized");
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestStream, Protocol.Json)
            ?? throw new InvalidDataException("native fixture manifest is empty");
        Require(manifest.Version == 1 && manifest.Producer == "SelectionSnapshot::TryWrite", "native fixture manifest producer/version mismatch");
        // Expectations are independent of the producer's serializer and context
        // resolver. Both the exported manifest and actual snapshots must agree.
        const string parent = @"C:\SnapshotFixture";
        const string folder = parent + @"\folder 例子";
        const string file = parent + @"\file 例.txt";
        var expected = new Dictionary<string, Expected>(StringComparer.Ordinal)
        {
            ["folder-one"] = new("explorer.folder", [folder], ["directory"], false, false, "folder.type.set", "capture.window"),
            ["folder-many"] = new("explorer.selection", [folder, parent + @"\second folder"], ["directory", "directory"], false, false, "files.unblock", "folder.type.set"),
            ["file-one"] = new("explorer.selection", [file], ["file"], false, false, "files.unblock", "folder.type.set"),
            ["file-many"] = new("explorer.selection", [file, parent + @"\second 😀.txt"], ["file", "file"], false, false, "launch.user-script", "launch.terminal"),
            ["mixed"] = new("explorer.selection", [file, folder], ["file", "directory"], false, false, "launch.custom", "folder.type.set"),
            ["desktop"] = new("desktop", [parent + @"\Desktop"], ["directory"], true, true, "launch.user-script", "files.unblock"),
            ["explorer-background"] = new("explorer.background", [parent + @"\Background"], ["directory"], true, false, "launch.terminal", "folder.type.set")
        };
        Require(manifest.Fixtures is not null && manifest.Fixtures.Length == expected.Count, "native fixture manifest must contain all seven cases");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fixture in manifest.Fixtures!)
        {
            Require(fixture is not null && ids.Add(fixture.Id) && expected.ContainsKey(fixture.Id), "duplicate/unknown native fixture");
            var contract = expected[fixture!.Id];
            Require(fixture.ExpectedContext == contract.Context && fixture.ExpectedParentPath == parent
                && fixture.ExpectedIsBackground == contract.Background && fixture.ExpectedIsDesktop == contract.Desktop
                && fixture.ExpectedPaths is not null && fixture.ExpectedPaths.SequenceEqual(contract.Paths)
                && fixture.ExpectedTypes is not null && fixture.ExpectedTypes.SequenceEqual(contract.Types)
                && fixture.SupportedOperation == contract.Supported && fixture.RejectedOperation == contract.Rejected,
                $"native fixture source contract changed for {fixture.Id}");
            Require(Path.IsPathFullyQualified(fixture.SnapshotPath) && paths.Add(fixture.SnapshotPath), "fixture snapshot paths must be unique and absolute");
            var before = File.ReadAllBytes(fixture.SnapshotPath);
            using (var snapshotJson = JsonDocument.Parse(before))
                Require(snapshotJson.RootElement.GetProperty("version").GetInt32() == 1, "emitter did not produce schema 1");
            var selection = SelectionSnapshotStore.Load(fixture.SnapshotPath);
            Require(File.ReadAllBytes(fixture.SnapshotPath).SequenceEqual(before), "managed Load changed native snapshot bytes");
            Require(selection.Context == contract.Context && selection.ParentPath == parent
                && selection.IsBackground == contract.Background && selection.IsDesktop == contract.Desktop
                && selection.Paths.SequenceEqual(contract.Paths) && selection.IsMultiple == (contract.Paths.Length > 1),
                $"native-to-managed envelope/order mismatch for {fixture.Id}");
            Require(OperationCatalog.TryGet(contract.Supported, out _) && OperationCatalog.TryGet(contract.Rejected, out _), "fixture operations are absent from catalog");
            Require(OperationCatalog.IsSelectionEligible(contract.Supported, selection, out var supportedReason),
                $"catalog rejected supported native fixture {fixture.Id}: {supportedReason}");
            Require(!OperationCatalog.IsSelectionEligible(contract.Rejected, selection, out var rejectedReason)
                && !string.IsNullOrWhiteSpace(rejectedReason), $"catalog accepted incompatible native fixture {fixture.Id}");
            var eligible = OperationCatalog.GetEligibleOperations(selection);
            Require(eligible.Any(operation => operation.Id == contract.Supported)
                && eligible.All(operation => operation.Id != contract.Rejected), $"catalog operation filtering disagreed for {fixture.Id}");
        }
        // Consume only after every contract check succeeds. Failed runs preserve
        // the native outputs and manifest for diagnosis instead of hiding them.
        foreach (var fixture in manifest.Fixtures!)
        {
            _ = SelectionSnapshotStore.ConsumeNative(fixture.SnapshotPath, out var warning);
            Require(warning is null && !File.Exists(fixture.SnapshotPath), $"native fixture cleanup failed for {fixture.Id}: {warning?.Message}");
            Console.WriteLine($"PASS native_selection_fixture_{fixture.Id}");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
    private sealed class Manifest
    {
        public int Version { get; set; }
        public string Producer { get; set; } = "";
        public Fixture[]? Fixtures { get; set; }
    }
    private sealed class Fixture
    {
        public string Id { get; set; } = "";
        public string SnapshotPath { get; set; } = "";
        public string ExpectedContext { get; set; } = "";
        public string[]? ExpectedPaths { get; set; }
        public string[]? ExpectedTypes { get; set; }
        public string ExpectedParentPath { get; set; } = "";
        public bool ExpectedIsBackground { get; set; }
        public bool ExpectedIsDesktop { get; set; }
        public string SupportedOperation { get; set; } = "";
        public string RejectedOperation { get; set; } = "";
    }
}
