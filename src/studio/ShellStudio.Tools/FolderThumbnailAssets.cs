namespace ShellStudio.Tools;

/// <summary>The two pinned mask assets; neither path nor icon bytes come from a tool request.</summary>
public static class FolderThumbnailAssets
{
    public static byte[] FullSize => Read("Transparent.ico");
    public static byte[] Default => Read("HalfMask.ico");

    private static byte[] Read(string name)
    {
        using var input = typeof(FolderThumbnailAssets).Assembly.GetManifestResourceStream(
            "ShellStudio.Tools.Assets.FolderThumbnails." + name)
            ?? throw new InvalidDataException("The built-in folder thumbnail mask is missing.");
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
