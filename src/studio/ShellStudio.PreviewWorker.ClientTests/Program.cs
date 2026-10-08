using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ShellStudio.Core;
using ShellStudio;

int passed = 0;
int failed = 0;

async Task Test(string name, Func<Task> action)
{
    try
    {
        await action();
        Console.WriteLine("PASS " + name);
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine("FAIL " + name + ": " + ex.Message);
        failed++;
    }
}

string fixture = Path.Combine(AppContext.BaseDirectory, "ShellStudio.PreviewWorker.Fixture.exe");
if (!File.Exists(fixture))
    throw new FileNotFoundException("The fixture worker was not copied beside the client tests.", fixture);

await Test("broker read calls match the native io.file and reg names", () =>
{
    Equal("io.file.exists", PreviewReadRequest.FileExists(@"C:\Preview\sample.nss").Function);
    Equal("io.file.read", PreviewReadRequest.FileText(@"C:\Preview\sample.nss").Function);
    Equal("reg.exists", PreviewReadRequest.RegistryValueExists("HKCU", @"Software\QweShell", "Greeting").Function);
    Equal("reg.get", PreviewReadRequest.RegistryValue("HKCU", @"Software\QweShell", "Greeting").Function);
    return Task.CompletedTask;
});

await Test("caller cancellation kills the fixture worker", async () =>
{
    using var evidence = new FixtureEvidence("delay");
    await using var client = new PreviewWorkerClient(fixture, evidence.MarkerPath, TimeSpan.FromSeconds(5));
    using var cancellation = new CancellationTokenSource();
    var pending = client.ExecuteAsync(Request("cancel", "evaluate"), cancellation.Token);
    int pid = await evidence.WaitForPidAsync(pending);
    cancellation.Cancel();
    var response = await pending;
    Equal("cancelled", response.Status);
    Equal("PREVIEW_CANCELLED", response.Diagnostics[0].Code);
    True(await WaitForExit(pid, TimeSpan.FromSeconds(2)), "cancelled worker did not exit");
});

await Test("deadline cancellation kills the fixture worker", async () =>
{
    using var evidence = new FixtureEvidence("delay");
    await using var client = new PreviewWorkerClient(fixture, evidence.MarkerPath, TimeSpan.FromMilliseconds(150));
    var started = new TaskCompletionSource<(int Pid, DateTime Created)>(TaskCreationOptions.RunContinuationsAsynchronously);
    client.WorkerStarted += (pid, created) => started.TrySetResult((pid, created));
    var response = await client.ExecuteAsync(Request("timeout", "evaluate"));
    Equal("cancelled", response.Status);
    Equal("PREVIEW_TIMEOUT", response.Diagnostics[0].Code);
    var identity = await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    True(await WaitForExit(identity.Pid, TimeSpan.FromSeconds(2), identity.Created), "timed-out worker did not exit");
});

await Test("fixture marker failure is an explicit worker failure", async () =>
{
    using var evidence = new FixtureEvidence("delay");
    Directory.CreateDirectory(evidence.MarkerPath);
    await using var client = new PreviewWorkerClient(fixture, evidence.MarkerPath, TimeSpan.FromSeconds(5));
    var started = new TaskCompletionSource<(int Pid, DateTime Created)>(TaskCreationOptions.RunContinuationsAsynchronously);
    client.WorkerStarted += (pid, created) => started.TrySetResult((pid, created));
    var response = await client.ExecuteAsync(Request("marker-failure", "evaluate"));
    Equal("error", response.Status);
    True(response.Diagnostics.Any(d => d.Code == "PREVIEW_FAILED"), "marker failure did not fail the worker protocol");
    var identity = await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    True(await WaitForExit(identity.Pid, TimeSpan.FromSeconds(2), identity.Created), "failed fixture worker survived");
});

await Test("crash after a valid composition marks the retained result stale", async () =>
{
    using var evidence = new FixtureEvidence("crash-after-response");
    await using var client = new PreviewWorkerClient(fixture, evidence.MarkerPath, TimeSpan.FromSeconds(5));
    var response = await client.ComposeAsync(Request("crash-revision", "compose"));
    Equal("ok", response.Status);
    True(client.CompositionActive, "composition was not retained");
    Equal("crash-revision", client.CompositionRevision);
    int pid = evidence.ReadPid();
    True(await WaitForExit(pid, TimeSpan.FromSeconds(2)), "crash fixture did not exit");
    True(!client.CompositionActive, "crashed composition still reports active");
    True(client.CompositionRevision is null, "crashed composition retained its revision");
    var regular = await client.ExecuteAsync(Request("after-crash", "evaluate"));
    Equal("ok", regular.Status);
    True(!client.LastValidIsStale, "successful recovery remained stale");
});

await Test("composition lifetime blocks, replaces, and closes deterministically", async () =>
{
    using var evidence = new FixtureEvidence("compose");
    await using var client = new PreviewWorkerClient(fixture, evidence.MarkerPath, TimeSpan.FromSeconds(5));
    var first = await client.ComposeAsync(Request("revision-one", "compose"));
    Equal("ok", first.Status);
    True(client.CompositionActive, "first composition was not retained");
    Equal("revision-one", client.CompositionRevision);
    int firstPid = evidence.ReadPid();

    var blocked = await client.ExecuteAsync(Request("blocked", "evaluate"));
    Equal("error", blocked.Status);
    Equal("PREVIEW_COMPOSE_ACTIVE", blocked.Diagnostics[0].Code);
    Equal("revision-one", client.LastValidResponse?.Revision);

    var second = await client.ComposeAsync(Request("revision-two", "compose"));
    Equal("ok", second.Status);
    True(client.CompositionActive, "replacement composition was not retained");
    Equal("revision-two", client.CompositionRevision);
    True(await WaitForExit(firstPid, TimeSpan.FromSeconds(2)), "replaced composition worker did not exit");

    int secondPid = evidence.ReadPid();
    await client.CloseCompositionAsync();
    True(!client.CompositionActive, "closed composition still reports active");
    True(client.CompositionRevision is null, "closed composition retained its revision");
    True(await WaitForExit(secondPid, TimeSpan.FromSeconds(2)), "closed composition worker did not exit");

    var regular = await client.ExecuteAsync(Request("after-close", "evaluate"));
    Equal("ok", regular.Status);
});

await Test("cancelled replacement preserves the last composed window", async () =>
{
    using var evidence = new FixtureEvidence("compose");
    await using var client = new PreviewWorkerClient(fixture, evidence.MarkerPath, TimeSpan.FromSeconds(5));
    Equal("ok", (await client.ComposeAsync(Request("retained", "compose"))).Status);
    int retainedPid = evidence.ReadPid();
    using var cancellation = new CancellationTokenSource();
    var pending = client.ComposeAsync(Request("replacement-delay", "compose"), cancellation.Token);
    int cancelledPid = await evidence.WaitForPidAsync(pending, retainedPid);
    cancellation.Cancel();
    var replacement = await pending;
    Equal("cancelled", replacement.Status);
    True(client.CompositionActive, "the last valid composition was closed by a failed replacement");
    Equal("retained", client.CompositionRevision);
    Equal("retained", client.LastValidResponse?.Revision);
    True(client.LastValidIsStale, "failed replacement did not mark the retained preview stale");
    True(cancelledPid != retainedPid, "replacement did not start its own worker");
    True(await WaitForExit(cancelledPid, TimeSpan.FromSeconds(2)), "cancelled replacement worker survived");
    await client.CloseCompositionAsync();
    True(await WaitForExit(retainedPid, TimeSpan.FromSeconds(2)), "retained worker survived explicit close");
});

await Test("valid response from a worker that stays alive has bounded cleanup", async () =>
{
    using var evidence = new FixtureEvidence("hang-after-response");
    await using var client = new PreviewWorkerClient(fixture, evidence.MarkerPath, TimeSpan.FromSeconds(30));
    var response = await client.ExecuteAsync(Request("completed", "evaluate")).WaitAsync(TimeSpan.FromSeconds(5));
    Equal("ok", response.Status);
    True(await WaitForExit(evidence.ReadPid(), TimeSpan.FromSeconds(2)), "completed worker survived cleanup");
});

Console.WriteLine($"{passed} preview worker client lifecycle tests passed; {failed} failed");
return failed == 0 ? 0 : 1;

static PreviewProtocol.Request Request(string revision, string operation)
{
    using var document = JsonDocument.Parse("{}");
    return PreviewProtocol.CreateRequest(revision, operation, document.RootElement.Clone(), revision);
}

static void True(bool condition, string message = "assertion failed")
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T? actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
}

static async Task<bool> WaitForExit(int pid, TimeSpan timeout, DateTime? created = null)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (created.HasValue && process.StartTime.ToUniversalTime() != created.Value) return true;
            if (process.HasExited) return true;
        }
        catch (ArgumentException) { return true; }
        await Task.Delay(25);
    }
    return false;
}

sealed class FixtureEvidence : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "qwe-shell-preview-client-" + Guid.NewGuid().ToString("N"));
    public string MarkerPath { get; }

    public FixtureEvidence(string mode)
    {
        Directory.CreateDirectory(directory);
        MarkerPath = Path.Combine(directory, mode + ".marker");
    }

    public int ReadPid()
    {
        for (int attempt = 0; attempt < 80; attempt++)
        {
            try
            {
                string text = File.ReadAllText(MarkerPath).Trim();
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) return pid;
            }
            catch (IOException) { }
            Thread.Sleep(10);
        }
        throw new InvalidOperationException("fixture PID marker was not written");
    }

    public async Task<int> WaitForPidAsync(Task<PreviewProtocol.Response> pending, int? previousPid = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                string text = await File.ReadAllTextAsync(MarkerPath);
                if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) && pid != previousPid)
                    return pid;
            }
            catch (IOException) { }
            if (pending.IsCompleted)
            {
                var response = await pending;
                throw new InvalidOperationException("Fixture ended before readiness: " + response.Status + "; " + string.Join("; ", response.Diagnostics.Select(d => d.Code + " " + d.Message)));
            }
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Fixture readiness was not published within 3 seconds.");
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
