using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShellStudio;
using ShellStudio.Core;
using ShellStudio.Tools;

var tests = new List<(string Name, Action Body)>
{
    ("parser_scopes_profile_discovery_to_args_property", ParserScopesProfileDiscovery),
    ("export_prefers_staged_profile_bytes_and_preserves_metadata", ExportPrefersStagedBytes),
    ("rebase_moves_profile_to_root_relative_destination", RebaseMovesProfile),
    ("rebase_rejects_conflicting_existing_profile", RebaseRejectsConflict),
    ("export_diagnoses_missing_file_dependency", ExportDiagnosesMissingDependency),
    ("workspace_export_rejects_unsafe_import_paths", WorkspaceExportRejectsUnsafeImports)
};
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SHELL_STUDIO_LANGUAGE_DLL")))
    tests.Insert(0, ("native_parser_accepts_generated_profile_command", NativeParserAcceptsGeneratedCommand));

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine("PASS " + test.Name);
    }
    catch (Exception ex)
    {
        failures.Add(test.Name + ": " + ex.Message);
        Console.WriteLine("FAIL " + test.Name + ": " + ex);
    }
}

return failures.Count == 0 ? 0 : 1;

static void ParserScopesProfileDiscovery()
{
    using var scope = new TempScope();
    const string id = "parser-scoped";
    string config =
        "// --profile \"@path.location(@app.cfg)\\imports\\studio-actions\\ignored-comment.json\"\n" +
        "item(title=\"--profile \\\"@path.location(@app.cfg)\\\\imports\\\\studio-actions\\\\ignored-title.json\\\"\" cmd=\"tool\" " +
        "args='--profile \"@path.location(@app.cfg)\\imports\\studio-actions\\parser-scoped.json\"')";
    var language = new SpanLanguage();
    language.Register(config, ArgsDocument(config));
    var workspace = CreateWorkspace(scope, language);
    var template = new StudioTemplate { Configuration = config };
    byte[] bytes = ProfileBytes(id, "explorer.refresh");
    string profilePath = ProfilePath(scope, id);
    var diagnostics = ToolTemplateProfiles.Export(template, workspace,
        Staged(new FileEdit(profilePath, "MISSING", bytes)), language);

    Ensure(!diagnostics.Any(d => d.Code == "TEMPLATE_PROFILE_MISSING"),
        "a profile-looking comment or title was treated as a reference");
    Ensure(template.Assets.TryGetValue("studio-actions/" + id + ".json", out var packaged) && packaged.SequenceEqual(bytes),
        "the actual args property reference was not packaged");
    Ensure(!template.Assets.ContainsKey("studio-actions/ignored-comment.json") &&
        !template.Assets.ContainsKey("studio-actions/ignored-title.json"),
        "unrelated source text was packaged as an action profile");
}

static void ExportPrefersStagedBytes()
{
    using var scope = new TempScope();
    const string id = "staged-profile";
    string config = Config(id);
    var language = RegisterConfig(language: new SpanLanguage(), config);
    var workspace = CreateWorkspace(scope, language);
    var template = new StudioTemplate { Configuration = config };
    byte[] diskBytes = ProfileBytes(id, "explorer.refresh");
    byte[] stagedBytes = ProfileBytes(id, "explorer.refresh", metadata: true);
    string profilePath = ProfilePath(scope, id);
    Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
    File.WriteAllBytes(profilePath, diskBytes);

    var diagnostics = ToolTemplateProfiles.Export(template, workspace,
        Staged(new FileEdit(profilePath, SourceFile.Hash(diskBytes), stagedBytes)), language);

    Ensure(!diagnostics.Any(d => d.Severity == "error"), "staged export failed: " + Messages(diagnostics));
    Ensure(template.Assets.TryGetValue("studio-actions/" + id + ".json", out var packaged) && packaged.SequenceEqual(stagedBytes),
        "the on-disk profile replaced the reviewed staged bytes");
    Ensure(Encoding.UTF8.GetString(packaged!).Contains("metadata", StringComparison.Ordinal),
        "profile metadata was not preserved in the package bytes");
}

static void RebaseMovesProfile()
{
    using var scope = new TempScope();
    const string id = "rebase-profile";
    string config = Config(id);
    var language = RegisterConfig(language: new SpanLanguage(), config);
    byte[] bytes = ProfileBytes(id, "explorer.refresh", metadata: true);
    string generic = Path.Combine(scope.Root, "imports", "studio-assets", "template-id", "studio-actions", id + ".json");
    var rebased = new TemplateRebaseResult(config,
        [new FileEdit(generic, "MISSING", bytes)],
        [new FileEdit(Path.Combine(scope.Root, "other.nss"), "MISSING", Encoding.UTF8.GetBytes("source"))],
        [new Diagnostic("TEMPLATE_ASSET_CONFLICT", "generic conflict", File: generic)]);
    var template = new StudioTemplate
    {
        Configuration = config,
        Assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["studio-actions/" + id + ".json"] = bytes
        }
    };

    var result = ToolTemplateProfiles.Rebase(template, Path.Combine(scope.Root, "shell.nss"), rebased, language);
    string destination = Path.Combine(scope.Root, "imports", "studio-actions", id + ".json");
    var edit = result.Assets.SingleOrDefault(item => item.Path.Equals(destination, StringComparison.OrdinalIgnoreCase));
    Ensure(edit is not null && edit.ExpectedHash == "MISSING" && edit.Content.SequenceEqual(bytes),
        "the profile was not rebased to imports/studio-actions with exact bytes");
    Ensure(!result.Assets.Any(item => item.Path.Equals(generic, StringComparison.OrdinalIgnoreCase)),
        "the generic template asset edit was retained");
    Ensure(!result.Diagnostics.Any(d => d.Code == "TEMPLATE_ASSET_CONFLICT" && d.File?.Equals(generic, StringComparison.OrdinalIgnoreCase) == true),
        "the generic conflict diagnostic was retained after the owned profile move");
    Ensure(Encoding.UTF8.GetString(edit!.Content).Contains("metadata", StringComparison.Ordinal),
        "profile metadata bytes changed during rebase");
}

static void RebaseRejectsConflict()
{
    using var scope = new TempScope();
    const string id = "conflict-profile";
    string config = Config(id);
    var language = RegisterConfig(language: new SpanLanguage(), config);
    byte[] packageBytes = ProfileBytes(id, "explorer.refresh");
    byte[] existingBytes = ProfileBytes(id, "explorer.refresh", metadata: true);
    string destination = Path.Combine(scope.Root, "imports", "studio-actions", id + ".json");
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, existingBytes);
    string generic = Path.Combine(scope.Root, "imports", "studio-assets", "template-id", "studio-actions", id + ".json");
    var template = new StudioTemplate
    {
        Configuration = config,
        Assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["studio-actions/" + id + ".json"] = packageBytes
        }
    };
    var result = ToolTemplateProfiles.Rebase(template, Path.Combine(scope.Root, "shell.nss"),
        new TemplateRebaseResult(config, [new FileEdit(generic, "MISSING", packageBytes)], [], []), language);

    Ensure(result.Diagnostics.Any(d => d.Code == "TEMPLATE_PROFILE_CONFLICT" && d.Severity == "error"),
        "a different existing profile did not block rebase");
    Ensure(!result.Assets.Any(item => item.Path.Equals(destination, StringComparison.OrdinalIgnoreCase)),
        "a conflicting destination was still staged");
    Ensure(File.ReadAllBytes(destination).SequenceEqual(existingBytes),
        "rebase changed the conflicting existing profile");
}

static void ExportDiagnosesMissingDependency()
{
    using var scope = new TempScope();
    const string id = "dependency-profile";
    string config = Config(id);
    var language = RegisterConfig(language: new SpanLanguage(), config);
    var workspace = CreateWorkspace(scope, language);
    var template = new StudioTemplate { Configuration = config };
    byte[] bytes = ProfileBytes(id, "launch.user-script", new Dictionary<string, string>
    {
        ["scriptPath"] = "missing-script.ps1",
        ["workingDirectory"] = ""
    });
    var diagnostics = ToolTemplateProfiles.Export(template, workspace,
        Staged(new FileEdit(ProfilePath(scope, id), "MISSING", bytes)), language);

    Ensure(diagnostics.Any(d => d.Code == "TEMPLATE_PROFILE_DEPENDENCY" && d.Severity == "warning" &&
        d.Message.Contains("missing file dependency", StringComparison.OrdinalIgnoreCase)),
        "missing action-profile file dependency was not diagnosed: " + Messages(diagnostics));
}

static void WorkspaceExportRejectsUnsafeImports()
{
    foreach (var (label, import, expected) in new[]
    {
        ("dynamic", "path", "not a literal"),
        ("empty", "", "path is empty"),
        ("absolute", @"C:\\external.nss", "absolute import paths"),
        ("malformed", "\0", "path is malformed"),
        ("unresolved", "missing.nss", "unresolved")
    })
    {
        using var scope = new TempScope();
        string root = Path.Combine(scope.Root, "shell.nss");
        string managed = Path.Combine(scope.Root, "imports", "studio.nss");
        Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
        string rootSource = "import 'imports/studio.nss'";
        string managedSource = label == "dynamic" ? "import path" : "import '" + import + "'";
        var language = new SpanLanguage();
        language.Register(rootSource, ImportDocument(rootSource, "imports/studio.nss", literal: true));
        language.Register(managedSource, ImportDocument(managedSource, import, literal: label != "dynamic"));
        File.WriteAllText(root, rootSource);
        File.WriteAllText(managed, managedSource);
        var workspace = new Workspace(root, language);

        try
        {
            TemplateAssets.CreateWorkspace("unsafe-" + label, workspace, language);
            throw new InvalidOperationException("workspace export accepted " + label + " import");
        }
        catch (InvalidDataException ex)
        {
            Ensure(ex.Message.Contains(expected, StringComparison.OrdinalIgnoreCase),
                label + " import diagnostic was not actionable: " + ex.Message);
        }
    }
}

static void NativeParserAcceptsGeneratedCommand()
{
    string sourceDll = Environment.GetEnvironmentVariable("SHELL_STUDIO_LANGUAGE_DLL")!;
    Ensure(File.Exists(sourceDll), "SHELL_STUDIO_LANGUAGE_DLL does not point to a file: " + sourceDll);
    string runtimeDll = Path.Combine(AppContext.BaseDirectory, "ShellStudio.Language.dll");
    if (!Path.GetFullPath(sourceDll).Equals(Path.GetFullPath(runtimeDll), StringComparison.OrdinalIgnoreCase))
        File.Copy(sourceDll, runtimeDll, overwrite: true);

    using var scope = new TempScope();
    const string id = "native-profile";
    byte[] profileBytes = ProfileBytes(id, "explorer.refresh", metadata: true);
    var language = new NativeLanguage();
    string command = ActionProfileCommandGenerator.GenerateNss(
        JsonSerializer.Deserialize<SavedActionProfile>(profileBytes, Protocol.Json)!,
        "imports/studio-actions/" + id + ".json",
        "path.combine(app.dir, \"Studio\\\\ShellStudio.exe\")",
        new NilesoftSelectionBinding("@sel.tojson()"));
    var document = language.Parse(command);
    Ensure(document.Diagnostics.All(diagnostic => diagnostic.Severity != "error"),
        "native parser rejected generated profile command: " + Messages(document.Diagnostics));
    Ensure(SourceFile.Descendants(document.Nodes).SelectMany(node => node.Properties)
        .Any(property => property.Name.Equals("args", StringComparison.OrdinalIgnoreCase)),
        "native parser did not expose the generated args property span");

    var workspace = CreateWorkspace(scope, language);
    var template = new StudioTemplate { Configuration = command };
    string profilePath = ProfilePath(scope, id);
    var diagnostics = ToolTemplateProfiles.Export(template, workspace,
        Staged(new FileEdit(profilePath, "MISSING", profileBytes)), language);
    Ensure(!diagnostics.Any(diagnostic => diagnostic.Severity == "error"),
        "native parser export failed: " + Messages(diagnostics));
    Ensure(template.Assets.TryGetValue("studio-actions/" + id + ".json", out var packaged) &&
        packaged!.SequenceEqual(profileBytes), "native parser export did not package the profile bytes");
}

static string Config(string id) =>
    "item(title=\"Tool\" cmd=\"tool\" args='--profile \"@path.location(@app.cfg)\\imports\\studio-actions\\" + id + ".json\"')";

static SpanLanguage RegisterConfig(SpanLanguage language, string config)
{
    language.Register(config, ArgsDocument(config));
    return language;
}

static Workspace CreateWorkspace(TempScope scope, ILanguageService language)
{
    string root = Path.Combine(scope.Root, "shell.nss");
    File.WriteAllText(root, "item(title='Workspace')");
    return new Workspace(root, language);
}

static string ProfilePath(TempScope scope, string id) =>
    Path.Combine(scope.Root, "imports", "studio-actions", id + ".json");

static IReadOnlyDictionary<string, FileEdit> Staged(FileEdit edit) =>
    new Dictionary<string, FileEdit>(StringComparer.OrdinalIgnoreCase) { [edit.Path] = edit };

static byte[] ProfileBytes(
    string id,
    string operationId,
    IReadOnlyDictionary<string, string>? overrides = null,
    bool metadata = false)
{
    var parameters = OperationCatalog.TryGet(operationId, out var descriptor)
        ? descriptor.Fields.ToDictionary(field => field.Name, field => field.DefaultValue, StringComparer.OrdinalIgnoreCase)
        : throw new InvalidOperationException("Unknown test operation: " + operationId);
    if (overrides is not null)
        foreach (var pair in overrides) parameters[pair.Key] = pair.Value;
    var profile = new SavedActionProfile("Test profile", operationId, parameters,
        OperationSelection.Empty, id, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(profile, Protocol.Json);
    if (!metadata) return bytes;
    var json = JsonNode.Parse(bytes)?.AsObject() ?? throw new InvalidOperationException("Profile JSON did not parse.");
    json["metadata"] = new JsonObject { ["owner"] = "template-test", ["revision"] = 7 };
    return JsonSerializer.SerializeToUtf8Bytes(json, Protocol.Json);
}

static SyntaxDocument ImportDocument(string source, string import, bool literal)
{
    int expressionStart = source.IndexOf(literal ? "'" : "path", StringComparison.Ordinal);
    int expressionLength = literal ? import.Length + 2 : "path".Length;
    return new SyntaxDocument
    {
        Nodes =
        [
            new SyntaxNode
            {
                Id = "import-node",
                Kind = "import",
                Name = "import",
                Start = 0,
                Length = source.Length,
                Expression = new ExpressionNode
                {
                    Id = "import-expression",
                    Kind = literal ? "literal" : "identifier",
                    Text = literal ? "'" + import + "'" : "path",
                    LiteralString = literal ? import : null,
                    Start = expressionStart,
                    Length = expressionLength
                }
            }
        ]
    };
}

static SyntaxDocument ArgsDocument(string source)
{
    int propertyStart = source.IndexOf("args=", StringComparison.Ordinal);
    Ensure(propertyStart >= 0, "test fixture has no args property");
    int valueStart = propertyStart + "args=".Length;
    return new SyntaxDocument
    {
        Nodes =
        [
            new SyntaxNode
            {
                Id = "fixture-node",
                Kind = "item",
                Name = "item",
                Start = 0,
                Length = source.Length,
                Properties =
                [
                    new SyntaxProperty
                    {
                        Name = "args",
                        Start = propertyStart,
                        Length = source.Length - propertyStart,
                        ValueStart = valueStart,
                        ValueLength = source.Length - valueStart
                    }
                ]
            }
        ]
    };
}

static string Messages(IEnumerable<Diagnostic> diagnostics) => string.Join(" | ", diagnostics.Select(d => d.Code + ": " + d.Message));
static void Ensure(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class SpanLanguage : ILanguageService
{
    private readonly Dictionary<string, SyntaxDocument> documents = new(StringComparer.Ordinal);
    public void Register(string source, SyntaxDocument document) => documents[source] = document;
    public SyntaxDocument Parse(string text) => documents.GetValueOrDefault(text) ?? new SyntaxDocument();
}

sealed class TempScope : IDisposable
{
    public TempScope() => Directory.CreateDirectory(Root);
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ShellStudio-template-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch { }
    }
}
