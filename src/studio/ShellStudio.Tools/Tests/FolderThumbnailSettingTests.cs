using System.Security.Cryptography;
using ShellStudio.Core;
using ShellStudio.Tools;

public static class FolderThumbnailSettingTests
{
    private const string DefaultHash = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string FullHash = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string ChangedHash = "3333333333333333333333333333333333333333333333333333333333333333";

    public static Task CatalogContract()
    {
        var descriptor = OperationCatalog.All.Single(item => item.Id == "folder.thumbnail.set");
        var style = descriptor.Fields.Single(field => field.Name == "style");
        var refresh = descriptor.Fields.Single(field => field.Name == "refreshExplorer");

        Ensure(style.Kind == "choice", "thumbnail style is not a choice field");
        Ensure(style.Choices.SequenceEqual(["Full size", "Default (half-covered)"]),
            "thumbnail style choices changed");
        Ensure(refresh.Kind == "bool" && refresh.DefaultValue == "true",
            "refreshExplorer is not a true-default bool field");
        return Task.CompletedTask;
    }

    public static async Task DefaultFullDefaultIsEffective()
    {
        using var fixture = new Fixture();
        var setting = fixture.CreateSetting(FolderThumbnailResources.DefaultStyle, DefaultHash);
        var environment = fixture.CreateEnvironment(ToolMutationMode.AllowSystem);
        var service = new OperationService(environment, setting);

        var toFull = await service.PreviewAsync(Request("Full size"));
        Ensure(toFull.CanExecute, "default-to-full preview was blocked");
        var fullResult = await service.ExecuteAsync(toFull);
        Ensure(fullResult.Success, Diagnostics(fullResult.Diagnostics));
        Ensure(setting.State.Style == FolderThumbnailResources.FullSizeStyle && setting.State.Sha256 == FullHash,
            "full-size setting did not become effective");

        var toDefault = await service.PreviewAsync(Request(FolderThumbnailResources.DefaultStyle));
        Ensure(toDefault.CanExecute, "full-to-default preview was blocked");
        var defaultResult = await service.ExecuteAsync(toDefault);
        Ensure(defaultResult.Success, Diagnostics(defaultResult.Diagnostics));
        Ensure(setting.State.Style == FolderThumbnailResources.DefaultStyle && setting.State.Sha256 == DefaultHash,
            "default mask did not become effective after restoring it");
        Ensure(setting.Writes.Count == 2, "the two effective changes did not issue exactly two writes");
        Ensure(setting.Writes[0].FullSize && !setting.Writes[1].FullSize,
            "effective writes were issued in the wrong order");
    }

    public static async Task NoOpAvoidsWrite()
    {
        using var fixture = new Fixture();
        var setting = fixture.CreateSetting(FolderThumbnailResources.FullSizeStyle, FullHash);
        var environment = fixture.CreateEnvironment(ToolMutationMode.AllowSystem);
        var service = new OperationService(environment, setting);

        // The accepted choice is deliberately supplied with different casing.
        var plan = await service.PreviewAsync(Request("full size"));
        Ensure(plan.CanExecute, "case-insensitive no-op preview was blocked");
        Ensure(!plan.RequiresElevation, "an unchanged mask with no refresh unnecessarily requires elevation");
        var result = await service.ExecuteAsync(plan);

        Ensure(result.Success, Diagnostics(result.Diagnostics));
        Ensure(setting.Writes.Count == 0, "no-op execution wrote the thumbnail resource");
        Ensure(setting.State.Style == FolderThumbnailResources.FullSizeStyle,
            "no-op execution changed the current style");
    }

    public static async Task StaleHashIsRejected()
    {
        using var fixture = new Fixture();
        var setting = fixture.CreateSetting(FolderThumbnailResources.DefaultStyle, DefaultHash);
        var environment = fixture.CreateEnvironment(ToolMutationMode.AllowSystem);
        var service = new OperationService(environment, setting);
        var plan = await service.PreviewAsync(Request("Full size"));

        setting.SetInspection(new FolderThumbnailStatus(FolderThumbnailResources.DefaultStyle, ChangedHash));
        var result = await service.ExecuteAsync(plan);

        Ensure(!result.Success, "stale thumbnail plan was accepted");
        Ensure(result.Diagnostics.Any(d => d.Code == "TOOL-PLAN-STALE"),
            "stale thumbnail plan did not report TOOL-PLAN-STALE");
        Ensure(setting.Writes.Count == 0, "stale thumbnail plan attempted a resource write");
    }

    public static async Task NoOpRefreshDoesNotRequireSystemPermission()
    {
        using var fixture = new Fixture();
        var setting = fixture.CreateSetting(FolderThumbnailResources.FullSizeStyle, FullHash);
        var environment = fixture.CreateEnvironment(ToolMutationMode.AllowUserData);
        var service = new OperationService(environment, setting);
        var plan = await service.PreviewAsync(Request("Full size", refreshExplorer: true));
        Ensure(plan.CanExecute && !plan.RequiresElevation, "cache-only refresh unnecessarily requires system permission");
        var result = await service.ExecuteAsync(plan);
        Ensure(result.Success && setting.Writes.Count == 0 && environment.ExplorerFixture.WasRefreshed,
            "an explicitly requested no-op cache refresh did not run independently of the resource write");
    }

    public static async Task SlowInspectionCanBeCancelled()
    {
        using var fixture = new Fixture();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        var setting = fixture.CreateSetting(FolderThumbnailResources.DefaultStyle, DefaultHash);
        setting.OnInspect = () =>
        {
            started.Set();
            try { Ensure(release.Wait(TimeSpan.FromSeconds(10)), "inspection fixture was not released"); }
            finally { finished.Set(); }
        };
        var service = new OperationService(fixture.CreateEnvironment(ToolMutationMode.AllowSystem), setting);
        var preview = Task.Run(() => service.PreviewAsync(Request("Full size"), cancel.Token));
        try
        {
            Ensure(started.Wait(TimeSpan.FromSeconds(2)), "inspection did not start");
            cancel.Cancel();
            bool cancelled = false;
            try { await preview.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { cancelled = true; }
            Ensure(cancelled && !finished.IsSet && setting.Writes.Count == 0,
                "cancelling a preview waited for a slow read or allowed a mutation");
        }
        finally
        {
            release.Set();
            Ensure(finished.Wait(TimeSpan.FromSeconds(2)), "inspection fixture did not finish");
        }
    }

    public static async Task ForeignRequestPathsAreRejected()
    {
        using var fixture = new Fixture();
        var setting = fixture.CreateSetting(FolderThumbnailResources.DefaultStyle, DefaultHash);
        var environment = fixture.CreateEnvironment(ToolMutationMode.AllowSystem);
        var service = new OperationService(environment, setting);
        var foreignResource = fixture.NewFile("foreign-resource.mun");
        var foreignIcon = fixture.NewFile("foreign-icon.ico");

        var plan = await service.PreviewAsync(OperationRequest.Create("folder.thumbnail.set", [
            new("style", "Full size"),
            new("refreshExplorer", "false"),
            new("resourcePath", foreignResource),
            new("iconPath", foreignIcon)
        ]));

        var unknownFields = plan.Diagnostics.Where(d => d.Code == "TOOL-FIELD-UNKNOWN").ToArray();
        Ensure(!plan.CanExecute, "foreign resourcePath/iconPath fields were accepted by folder.thumbnail.set");
        Ensure(unknownFields.Any(d => d.Message.Contains("resourcePath", StringComparison.OrdinalIgnoreCase)),
            "foreign resourcePath was not rejected as an unknown field");
        Ensure(unknownFields.Any(d => d.Message.Contains("iconPath", StringComparison.OrdinalIgnoreCase)),
            "foreign iconPath was not rejected as an unknown field");
        Ensure(!plan.Request.Values.ContainsKey("resourcePath") && !plan.Request.Values.ContainsKey("iconPath"),
            "foreign paths survived request normalization");
        Ensure(setting.Writes.Count == 0, "foreign path request reached the setting seam");
    }

    public static async Task ReviewOnlyBlocks()
    {
        using var fixture = new Fixture();
        var setting = fixture.CreateSetting(FolderThumbnailResources.DefaultStyle, DefaultHash);
        var environment = fixture.CreateEnvironment(ToolMutationMode.ReviewOnly);
        var service = new OperationService(environment, setting);
        var plan = await service.PreviewAsync(Request("Full size"));
        var result = await service.ExecuteAsync(plan);

        Ensure(!plan.CanExecute, "review-only thumbnail plan was executable");
        Ensure(!result.Success && result.Diagnostics.Any(d => d.Code == "TOOL-PLAN-BLOCKED"),
            "review-only thumbnail execution was not blocked");
        Ensure(setting.Writes.Count == 0, "review-only execution wrote the thumbnail resource");
        Ensure(!environment.ExplorerFixture.WasRefreshed, "review-only execution refreshed Explorer");
    }

    public static async Task UserDataBlocksSystemWrite()
    {
        using var fixture = new Fixture();
        var setting = fixture.CreateSetting(FolderThumbnailResources.DefaultStyle, DefaultHash);
        var environment = fixture.CreateEnvironment(ToolMutationMode.AllowUserData);
        var service = new OperationService(environment, setting);
        var plan = await service.PreviewAsync(Request("Full size"));
        var result = await service.ExecuteAsync(plan);

        Ensure(plan.CanExecute, "allow-user-data thumbnail preview was unexpectedly blocked");
        Ensure(!result.Success && result.Diagnostics.Any(d => d.Code == "TOOL-MUTATION-DENIED"),
            "allow-user-data host did not block the protected system write");
        Ensure(setting.Writes.Count == 0, "allow-user-data host reached the thumbnail write seam");
        Ensure(!environment.ExplorerFixture.WasRefreshed, "blocked system write refreshed Explorer");
    }

    public static async Task RefreshFailureReportsAppliedMask()
    {
        using var fixture = new Fixture();
        var setting = fixture.CreateSetting(FolderThumbnailResources.DefaultStyle, DefaultHash);
        var environment = fixture.CreateEnvironment(ToolMutationMode.AllowSystem);
        environment.ExplorerFixture.RefreshError = "fixture thumbnail cache reset failed";
        var service = new OperationService(environment, setting);
        var plan = await service.PreviewAsync(Request("Full size", refreshExplorer: true));
        var result = await service.ExecuteAsync(plan);

        Ensure(!result.Success, "refresh failure was reported as a successful thumbnail operation");
        Ensure(result.Diagnostics.Any(d => d.Code == "TOOL-THUMBNAIL-SET"),
            "the applied mask was not reported before refresh failed");
        Ensure(result.Diagnostics.Any(d => d.Code == "TOOL-EXPLORER-REFRESH"),
            "refresh failure was not explicitly reported");
        Ensure(setting.Writes.Count == 1 && setting.State.Style == FolderThumbnailResources.FullSizeStyle,
            "refresh failure rolled back or skipped the applied mask");
        Ensure(environment.ExplorerFixture.WasRefreshed && environment.ExplorerFixture.ResetThumbs
            && !environment.ExplorerFixture.ResetIcons,
            "thumbnail refresh did not request the expected Explorer cache operation");
        Ensure(result.RecoveryPath is not null, "refresh failure did not retain the operation journal");
    }

    public static async Task RefreshFalseSkipsExplorer()
    {
        using var fixture = new Fixture();
        var setting = fixture.CreateSetting(FolderThumbnailResources.DefaultStyle, DefaultHash);
        var environment = fixture.CreateEnvironment(ToolMutationMode.AllowSystem);
        var service = new OperationService(environment, setting);
        var plan = await service.PreviewAsync(Request("Full size", refreshExplorer: false));
        var result = await service.ExecuteAsync(plan);

        Ensure(result.Success, Diagnostics(result.Diagnostics));
        Ensure(setting.State.Style == FolderThumbnailResources.FullSizeStyle && setting.Writes.Count == 1,
            "refresh-disabled thumbnail setting did not apply the mask");
        Ensure(!environment.ExplorerFixture.WasRefreshed,
            "refreshExplorer=false still refreshed Explorer");
        Ensure(!result.Diagnostics.Any(d => d.Code is "TOOL-EXPLORER-REFRESH" or "TOOL-EXPLORER-REFRESHED"),
            "refreshExplorer=false reported an Explorer refresh");
    }

    public static async Task InspectErrorBlocks()
    {
        using var fixture = new Fixture();
        var setting = fixture.CreateSetting(FolderThumbnailResources.DefaultStyle, DefaultHash,
            "fixture resource inspection failed");
        var environment = fixture.CreateEnvironment(ToolMutationMode.AllowSystem);
        var service = new OperationService(environment, setting);
        var plan = await service.PreviewAsync(Request("Full size"));
        var result = await service.ExecuteAsync(plan);

        Ensure(!plan.CanExecute, "thumbnail inspection error did not block its preview");
        Ensure(plan.Diagnostics.Any(d => d.Code == "TOOL-THUMBNAIL-INSPECT"),
            "thumbnail inspection error diagnostic was not retained");
        Ensure(!result.Success && result.Diagnostics.Any(d => d.Code == "TOOL-PLAN-BLOCKED"),
            "thumbnail inspection error was not blocked at execution");
        Ensure(setting.Writes.Count == 0, "thumbnail inspection error reached the write seam");
    }

    public static async Task UnknownStateWarns()
    {
        using var fixture = new Fixture();
        var setting = fixture.CreateSetting(FolderThumbnailResources.UnknownStyle, DefaultHash);
        var environment = fixture.CreateEnvironment(ToolMutationMode.AllowSystem);
        var service = new OperationService(environment, setting);
        var plan = await service.PreviewAsync(Request("Full size", refreshExplorer: false));

        Ensure(plan.CanExecute, "an inspectable unknown thumbnail state was blocked instead of warned");
        var warning = plan.Diagnostics.SingleOrDefault(d => d.Code == "TOOL-THUMBNAIL-CUSTOM");
        Ensure(warning is not null && warning.Severity == "warning",
            "unknown thumbnail state did not produce an explicit warning");
    }

    public static Task BuiltInAssetsMatchPinnedHashes()
    {
        var fullHash = Convert.ToHexString(SHA256.HashData(FolderThumbnailAssets.FullSize));
        var defaultHash = Convert.ToHexString(SHA256.HashData(FolderThumbnailAssets.Default));
        Ensure(fullHash == "E4E6FFA8E47B64E588D2AA5484B75AB5C7131D568B3BA55F225B548C4B3D8E44",
            "Transparent.ico does not match the pinned SHA-256 in Assets/FolderThumbnails/README.md");
        Ensure(defaultHash == "1A6782D065C2B3A4597BC67527032A149B63858D6B7DE5D4FE37FBD9C94ABB72",
            "HalfMask.ico does not match the pinned SHA-256 in Assets/FolderThumbnails/README.md");
        return Task.CompletedTask;
    }

    private static OperationRequest Request(string style, bool refreshExplorer = false)
        => OperationRequest.Create("folder.thumbnail.set", [
            new("style", style),
            new("refreshExplorer", refreshExplorer.ToString().ToLowerInvariant())
        ]);

    private static string Diagnostics(IEnumerable<Diagnostic> diagnostics)
        => string.Join(" | ", diagnostics.Select(d => $"{d.Code}: {d.Message}"));

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "ShellStudio.Tools.Tests", "FolderThumbnailSetting-" + Guid.NewGuid().ToString("N"));

        public Fixture() => Directory.CreateDirectory(_root);

        public string JournalRoot => Path.Combine(_root, "journal");

        public InMemoryToolEnvironment CreateEnvironment(ToolMutationMode mode)
            => new(JournalRoot, mode);

        public FakeFolderThumbnailSetting CreateSetting(string style, string hash, string? error = null)
        {
            var resourcePath = NewFile(Path.Combine("Windows", "SystemResources", "imageres.dll.mun"));
            return new FakeFolderThumbnailSetting(resourcePath, new FolderThumbnailStatus(style, hash, error));
        }

        public string NewFile(string relativePath)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0x53, 0x54, 0x55, 0x44, 0x49, 0x4F]);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Fixture cleanup must not hide a test result.
            }
        }
    }

    private sealed class FakeFolderThumbnailSetting : IFolderThumbnailSetting
    {
        private static string HashFor(bool fullSize)
            => fullSize ? FullHash : DefaultHash;

        public FakeFolderThumbnailSetting(string resourcePath, FolderThumbnailStatus state)
        {
            ResourcePath = resourcePath;
            State = state;
        }

        public string ResourcePath { get; }
        public FolderThumbnailStatus State { get; private set; }
        public List<ThumbnailWrite> Writes { get; } = [];

        public Action? OnInspect { get; set; }
        public FolderThumbnailStatus Inspect() { OnInspect?.Invoke(); return State; }

        public void SetInspection(FolderThumbnailStatus state) => State = state;

        public Task SetAsync(bool fullSize, string expectedHash, string recoveryDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(new ThumbnailWrite(fullSize, expectedHash, recoveryDirectory));
            if (State.Error is not null)
                throw new IOException(State.Error);
            if (!State.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The fixture thumbnail resource hash did not match the reviewed hash.");

            State = State with
            {
                Style = fullSize ? FolderThumbnailResources.FullSizeStyle : FolderThumbnailResources.DefaultStyle,
                Sha256 = HashFor(fullSize),
                Error = null
            };
            return Task.CompletedTask;
        }
    }

    private sealed record ThumbnailWrite(bool FullSize, string ExpectedHash, string RecoveryDirectory);
}
