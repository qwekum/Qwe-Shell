using System.Text;
using System.Text.Json;
using System.IO;
using ShellStudio.Core;
using ShellStudio;

int passed = 0;
int failed = 0;
void Test(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failed++; }
}

Test("request round trip retains Unicode and number spelling", () =>
{
    using var source = JsonDocument.Parse("{\"operation\":\"evaluate\",\"documents\":[{\"path\":\"猫.nss\",\"text\":\"$x=1.25e+3\"}],\"context\":{\"dpi\":144}}");
    var request = PreviewProtocol.CreateRequest("revision-猫", "evaluate", source.RootElement, "request-1");
    byte[] bytes = PreviewProtocol.SerializeRequest(request);
    var parsed = PreviewProtocol.ParseRequest(bytes);
    Equal("request-1", parsed.Id);
    Equal("revision-猫", parsed.Revision);
    Equal("evaluate", parsed.Operation);
    Equal("$x=1.25e+3", parsed.Payload.GetProperty("documents")[0].GetProperty("text").GetString());
});

Test("framing handles partial reads and little endian length", () =>
{
    byte[] payload = Encoding.UTF8.GetBytes("{\"value\":\"ok\"}");
    using var stream = new MemoryStream();
    PreviewProtocol.WriteFrameAsync(stream, payload).AsTask().GetAwaiter().GetResult();
    byte[] framed = stream.ToArray();
    Equal(payload.Length, BitConverter.ToInt32(framed, 0));
    using var input = new ChunkedStream(framed, 1);
    var result = PreviewProtocol.ReadFrameAsync(input).AsTask().GetAwaiter().GetResult();
    True(payload.SequenceEqual(result));
});

Test("response validation echoes bounded fields", () =>
{
    byte[] bytes = Encoding.UTF8.GetBytes("{\"version\":1,\"id\":\"a\",\"revision\":\"r\",\"operation\":\"render\",\"status\":\"ok\",\"result\":{\"available\":true,\"value\":\"猫\"},\"diagnostics\":[]}");
    var response = PreviewProtocol.ParseResponse(bytes);
    True(response.IsSuccess);
    Equal("猫", response.Result.GetProperty("value").GetString());
});

Test("invalid protocol inputs fail closed", () =>
{
    Throws<InvalidDataException>(() => PreviewProtocol.ParseRequest(Encoding.UTF8.GetBytes("{\"version\":1,\"id\":\"x\",\"revision\":\"r\",\"operation\":\"run\",\"payload\":{}}")));
    Throws<InvalidDataException>(() => PreviewProtocol.ParseResponse(Encoding.UTF8.GetBytes("{\"version\":1,\"id\":\"x\",\"revision\":\"r\",\"operation\":\"evaluate\",\"status\":\"ok\",\"result\":null,\"diagnostics\":[{\"code\":\"x\",\"message\":\"bad\",\"severity\":\"wat\"}]}")));
    Throws<InvalidDataException>(() => PreviewProtocol.ReadFrameAsync(new MemoryStream([0xff, 0xff, 0xff, 0x7f])).AsTask().GetAwaiter().GetResult());
});

Test("failure preserves explicit stale status", () =>
{
    var failure = PreviewProtocol.CreateFailure("id", "rev", "evaluate", "PREVIEW_TIMEOUT", "timed out", "cancelled");
    True(failure.IsStale);
    Equal("PREVIEW_TIMEOUT", failure.Diagnostics[0].Code);
});

Test("disabled and unscoped reads are explicit unavailable values", () =>
{
    string path = Path.Combine(Path.GetTempPath(), "qwe-shell-preview-policy.txt");
    var request = PreviewReadRequest.FileText(path);
    var provider = new CountingProvider(_ => PreviewReadResult.FromValue("should not be read"));
    var broker = new PreviewReadBroker(PreviewReadPolicy.Disabled, provider);
    var disabled = broker.Capture("rev-disabled", [request]);
    True(!disabled.Entries[0].Available);
    Equal("PREVIEW_READ_NOT_OPTED_IN", disabled.Entries[0].Diagnostic?.Code);
    Equal(0, provider.Calls);

    var enabled = PreviewReadPolicy.Create(true);
    broker.ReplacePolicy(enabled);
    var denied = broker.Capture("rev-denied", [request]);
    True(!denied.Entries[0].Available);
    Equal("PREVIEW_READ_SCOPE", denied.Entries[0].Diagnostic?.Code);
    Equal(0, provider.Calls);
});

Test("read snapshots cache by revision and refresh deliberately", () =>
{
    string path = Path.Combine(Path.GetTempPath(), "qwe-shell-preview-cache.txt");
    var request = PreviewReadRequest.FileText(path);
    int sequence = 0;
    var provider = new CountingProvider(_ => PreviewReadResult.FromValue(++sequence));
    var policy = PreviewReadPolicy.Create(true, filePaths: [path]);
    var broker = new PreviewReadBroker(policy, provider);
    var first = broker.Capture("rev-cache", [request]);
    var cached = broker.Capture("rev-cache", [request]);
    var refreshed = broker.Refresh("rev-cache");
    Equal(1, first.Entries[0].Value.GetInt32());
    Equal(1, cached.Entries[0].Value.GetInt32());
    Equal(2, refreshed.Entries[0].Value.GetInt32());
    Equal(2, provider.Calls);
    True(broker.TryGetSnapshot("rev-cache", out var retained) && retained is not null);
    Equal("rev-cache", retained!.Revision);
});

Test("resource boundary validates bytes and preserves provenance", () =>
{
    string path = Path.Combine(Path.GetTempPath(), "qwe-shell-preview-icon.png");
    var request = PreviewReadRequest.Resource(PreviewResourceKind.Png, path);
    var policy = PreviewReadPolicy.Create(true, resourceScopes: [new PreviewResourceScope(PreviewResourceKind.Png, path)]);
    byte[] png = MinimalPng(4, 3);
    var broker = new PreviewReadBroker(policy, new CountingProvider(_ => PreviewReadResult.FromResource(png)));
    var snapshot = broker.Capture("rev-resource", [request]);
    var resource = snapshot.Resources[0];
    True(resource.Available);
    Equal(4, resource.Width);
    Equal(3, resource.Height);
    Equal(png.Length, resource.ByteLength);
    using var payload = JsonDocument.Parse(snapshot.ToNativePayload().GetRawText());
    Equal("available", payload.RootElement.GetProperty("resources")[0].GetProperty("status").GetString());
    Equal(Convert.ToBase64String(png), payload.RootElement.GetProperty("resources")[0].GetProperty("content").GetString());

    var invalid = new PreviewReadBroker(policy, new CountingProvider(_ => PreviewReadResult.FromResource([1, 2, 3])));
    var invalidSnapshot = invalid.Capture("rev-invalid-resource", [request]);
    True(!invalidSnapshot.Resources[0].Available);
    Equal("PREVIEW_RESOURCE_FORMAT", invalidSnapshot.Resources[0].Diagnostic?.Code);
});

Test("managed resource boundary rejects oversized PNGs and typ1 fonts", () =>
{
    string pngPath = Path.Combine(Path.GetTempPath(), "qwe-shell-preview-oversized.png");
    var pngRequest = PreviewReadRequest.Resource(PreviewResourceKind.Png, pngPath);
    var pngPolicy = PreviewReadPolicy.Create(true,
        resourceScopes: [new PreviewResourceScope(PreviewResourceKind.Png, pngPath)]);
    var oversizedPng = MinimalPng(4097, 1);
    var pngBroker = new PreviewReadBroker(pngPolicy,
        new CountingProvider(_ => PreviewReadResult.FromResource(oversizedPng)));
    var pngSnapshot = pngBroker.Capture("rev-oversized-png", [pngRequest]);
    True(!pngSnapshot.Resources[0].Available);
    Equal("PREVIEW_RESOURCE_FORMAT", pngSnapshot.Resources[0].Diagnostic?.Code);

    string fontPath = Path.Combine(Path.GetTempPath(), "qwe-shell-preview-typ1.font");
    var fontRequest = PreviewReadRequest.Resource(PreviewResourceKind.Font, fontPath);
    var fontPolicy = PreviewReadPolicy.Create(true,
        resourceScopes: [new PreviewResourceScope(PreviewResourceKind.Font, fontPath)]);
    byte[] typ1 = [(byte)'t', (byte)'y', (byte)'p', (byte)'1', 0, 0, 0, 0, 0, 0, 0, 0];
    var fontBroker = new PreviewReadBroker(fontPolicy,
        new CountingProvider(_ => PreviewReadResult.FromResource(typ1)));
    var fontSnapshot = fontBroker.Capture("rev-typ1-font", [fontRequest]);
    True(!fontSnapshot.Resources[0].Available);
    Equal("PREVIEW_RESOURCE_FORMAT", fontSnapshot.Resources[0].Diagnostic?.Code);
});

Test("registry payload uses the native full-key argument shape", () =>
{
    var request = PreviewReadRequest.RegistryValue("HKCU", @"Software\QweShell", "Greeting");
    var policy = PreviewReadPolicy.Create(true,
        registryScopes: [new PreviewRegistryScope("HKCU", @"Software\QweShell", "Greeting")]);
    var broker = new PreviewReadBroker(policy, new CountingProvider(_ => PreviewReadResult.FromValue("hello")));
    var snapshot = broker.Capture("rev-registry", [request]);
    var args = snapshot.Entries[0].Arguments;
    Equal(2, args.GetArrayLength());
    Equal(@"HKCU\Software\QweShell", args[0].GetString());
    Equal("Greeting", args[1].GetString());
});

Test("registry existence and value reads do not share a cache entry", () =>
{
    var exists = PreviewReadRequest.RegistryValueExists("HKCU", @"Software\QweShell", "Greeting");
    var value = PreviewReadRequest.RegistryValue("HKCU", @"Software\QweShell", "Greeting");
    True(exists.Identity != value.Identity);
    var policy = PreviewReadPolicy.Create(true,
        registryScopes: [new PreviewRegistryScope("HKCU", @"Software\QweShell", "Greeting")]);
    var provider = new CountingProvider(request => request.Function == "reg.exists"
        ? PreviewReadResult.FromValue(true) : PreviewReadResult.FromValue("hello"));
    var snapshot = new PreviewReadBroker(policy, provider).Capture("rev-registry-operations", [exists, value]);
    Equal(2, snapshot.Entries.Count);
    Equal(2, provider.Calls);
    Equal(true, snapshot.Entries.Single(entry => entry.Function == "reg.exists").Value.GetBoolean());
    Equal("hello", snapshot.Entries.Single(entry => entry.Function == "reg.get").Value.GetString());
});

Test("network and reparse-like scopes are rejected before provider access", () =>
{
    Throws<ArgumentException>(() => PreviewReadPolicy.Create(true, filePaths: [@"\\server\share\file.nss"]));
    Throws<ArgumentException>(() => PreviewReadPolicy.Create(true,
        resourceScopes: [new PreviewResourceScope(PreviewResourceKind.Icon, @"\\server\share\icon.ico")]));
});

Console.WriteLine($"{passed} preview worker protocol tests passed; {failed} failed");
return failed == 0 ? 0 : 1;

static void True(bool condition)
{
    if (!condition) throw new InvalidOperationException("assertion failed");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("expected " + typeof(T).Name);
}

static byte[] MinimalPng(int width, int height)
{
    byte[] bytes = new byte[33];
    new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
    bytes[11] = 13;
    bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
    bytes[16] = (byte)(width >> 24); bytes[17] = (byte)(width >> 16); bytes[18] = (byte)(width >> 8); bytes[19] = (byte)width;
    bytes[20] = (byte)(height >> 24); bytes[21] = (byte)(height >> 16); bytes[22] = (byte)(height >> 8); bytes[23] = (byte)height;
    bytes[24] = 8; bytes[25] = 6;
    return bytes;
}

sealed class ChunkedStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
{
    public override int Read(Span<byte> buffer)
    {
        int size = Math.Min(chunkSize, buffer.Length);
        return base.Read(buffer[..size]);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int size = Math.Min(chunkSize, buffer.Length);
        return base.ReadAsync(buffer[..size], cancellationToken);
    }
}

sealed class CountingProvider(Func<PreviewReadRequest, PreviewReadResult> factory) : IPreviewReadProvider
{
    public int Calls { get; private set; }

    public PreviewReadResult Read(PreviewReadRequest request)
    {
        Calls++;
        return factory(request);
    }
}
