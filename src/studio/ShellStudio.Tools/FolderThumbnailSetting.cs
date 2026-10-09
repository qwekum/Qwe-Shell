namespace ShellStudio.Tools;

/// <summary>Fixed-target setting boundary. Test doubles can use disposable fixture files.</summary>
public interface IFolderThumbnailSetting
{
    string ResourcePath { get; }
    FolderThumbnailStatus Inspect();
    Task SetAsync(bool fullSize, string expectedHash, string recoveryDirectory, CancellationToken cancellationToken);
}

public sealed record FolderThumbnailStatus(string Style, string Sha256, string? Error = null);

public sealed class WindowsFolderThumbnailSetting : IFolderThumbnailSetting
{
    private readonly IToolEnvironment environment;

    public WindowsFolderThumbnailSetting(IToolEnvironment environment)
        => this.environment = environment ?? throw new ArgumentNullException(nameof(environment));

    public string ResourcePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "SystemResources", "imageres.dll.mun");

    public FolderThumbnailStatus Inspect()
    {
        var result = FolderThumbnailResources.Inspect(ResourcePath, FolderThumbnailAssets.FullSize, FolderThumbnailAssets.Default);
        return new(result.Style, result.Sha256, result.Error);
    }

    public async Task SetAsync(bool fullSize, string expectedHash, string recoveryDirectory, CancellationToken cancellationToken)
    {
        var result = await FolderThumbnailResources.SetAsync(ResourcePath,
            fullSize ? FolderThumbnailAssets.FullSize : FolderThumbnailAssets.Default,
            expectedHash, recoveryDirectory, environment, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new IOException(result.Error + (result.RecoveryPath is null ? "" : " Recovery: " + result.RecoveryPath));
    }
}
