using System.Text;
using ShellStudio.Core;

string directory = Path.Combine(Path.GetTempPath(), "ShellStudio-dependencies-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string root = Path.Combine(directory, "shell.nss"), imported = Path.Combine(directory, "theme.nss");
    byte[] original = Encoding.UTF8.GetBytes("import 'theme.nss'"), theme = Encoding.UTF8.GetBytes("$color=#123456");
    byte[] changed = Encoding.UTF8.GetBytes("import 'theme.nss'\nitem(title='new')");
    File.WriteAllBytes(root, original); File.WriteAllBytes(imported, theme);
    var reviewed = new Dictionary<string, string> { [root] = SourceFile.Hash(original), [imported] = SourceFile.Hash(theme) };
    var transaction = new ConfigurationTransactions(root, [root, imported], Path.Combine(directory, "backups"));
    File.WriteAllText(imported, "$color=#abcdef");
    var rejected = transaction.Apply([new(root, SourceFile.Hash(original), changed)], dependencies: reviewed);
    Require(!rejected.Success && File.ReadAllBytes(root).SequenceEqual(original), "Changed unchanged import must prevent publication.");
    Require(!transaction.RecoveryPending && !File.Exists(root + ".studio-generation"), "Rejected review must not publish a generation.");
    File.WriteAllBytes(imported, theme);
    var committed = transaction.Apply([new(root, SourceFile.Hash(original), changed)], dependencies: reviewed);
    Require(committed.Success && File.ReadAllBytes(root).SequenceEqual(changed), "Matching dependency set should commit.");
    Require(File.ReadAllBytes(imported).SequenceEqual(theme), "Unchanged imports must remain byte-identical.");
    Console.WriteLine("PASS reviewed dependency conflict and publication checks");

    string directoryTarget = Path.Combine(directory, "directory-target.nss");
    Directory.CreateDirectory(directoryTarget);
    string recoveryId = Guid.NewGuid().ToString("N");
    var journal = new TransactionJournal
    {
        Id = recoveryId,
        Files = [new() {
            Path = directoryTarget,
            Existed = false,
            BackupFile = "0000.original",
            OriginalHash = "MISSING",
            NewHash = SourceFile.Hash(Encoding.UTF8.GetBytes("new"))
        }]
    };
    File.WriteAllBytes(directoryTarget + ".studio-transaction.json",
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(journal, Protocol.Json));
    var recovery = new ConfigurationTransactions(directoryTarget, [directoryTarget],
        Path.Combine(directory, "backups")).Recover();
    Require(!recovery.Success && recovery.Diagnostics.Any(d => d.Code == "RECOVERY_CONFLICT"),
        "Recovery treated a directory at a file target as a missing file.");
    Require(File.Exists(directoryTarget + ".studio-transaction.json") && Directory.Exists(directoryTarget),
        "Directory target recovery discarded the marker or directory.");
    Console.WriteLine("PASS directory-at-file-target recovery retains conflict marker");
}
finally { Directory.Delete(directory, true); }

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
