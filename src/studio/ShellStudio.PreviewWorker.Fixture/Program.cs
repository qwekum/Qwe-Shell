using System.Globalization;
using System.Text.Json;
using ShellStudio.Core;

// This executable is deliberately small and deterministic. It exercises the
// managed process owner without loading the native language DLL or opening UI.
// The test client passes a marker path through --language; its file stem
// selects the behavior and the file contents record the child PID.
const string markerArgument = "--language";
string? marker = null;
for (int index = 0; index + 1 < args.Length; index++)
    if (string.Equals(args[index], markerArgument, StringComparison.OrdinalIgnoreCase))
        marker = args[index + 1];

string mode = marker is null
    ? "ok"
    : Path.GetFileNameWithoutExtension(marker).ToLowerInvariant();
if (marker is not null)
{
    try { File.WriteAllText(marker, Environment.ProcessId.ToString(CultureInfo.InvariantCulture)); }
    catch { /* The PID is test evidence only; protocol behavior remains primary. */ }
}

if (mode is "delay" or "compose-delay")
{
    _ = await PreviewProtocol.ReadFrameAsync(Console.OpenStandardInput());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

byte[] frame;
try { frame = await PreviewProtocol.ReadFrameAsync(Console.OpenStandardInput()); }
catch { return; }

PreviewProtocol.Request request;
try { request = PreviewProtocol.ParseRequest(frame); }
catch { return; }

if (mode == "compose" && request.Revision == "replacement-delay")
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

using var resultDocument = JsonDocument.Parse("{}");
var response = new PreviewProtocol.Response(
    PreviewProtocol.Version,
    request.Id,
    request.Revision,
    request.Operation,
    "ok",
    resultDocument.RootElement.Clone(),
    []);

if (mode == "crash") return;

await PreviewProtocol.WriteFrameAsync(Console.OpenStandardOutput(),
    JsonSerializer.SerializeToUtf8Bytes(response, Protocol.Json));

if (mode == "compose")
{
    // The managed client intentionally leaves stdin open for a composed
    // preview. EOF is accepted for completeness; CloseCompositionAsync also
    // force-kills this fixture to model native window teardown.
    try
    {
        byte[] buffer = new byte[1];
        while (await Console.OpenStandardInput().ReadAsync(buffer) > 0) { }
    }
    catch { }
}
else if (mode == "hang-after-response")
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
}
else if (mode == "crash-after-response")
{
    await Task.Delay(200);
    return;
}
