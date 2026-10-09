using System.Collections.ObjectModel;

namespace ShellStudio.Tools;

public sealed class CmdBatchCommand
{
    internal CmdBatchCommand(string command, IReadOnlyDictionary<string, string> environment)
        => (Command, Environment) = (command, environment);
    public string Command { get; }
    public IReadOnlyDictionary<string, string> Environment { get; }
}

/// <summary>Encodes CMD data without embedding percent expressions in CMD syntax.</summary>
public static class CmdBatchEncoder
{
    public const int MaxCommandLength = 8191;

    public static bool TryEncode(string scriptPath, IReadOnlyList<string> arguments, out CmdBatchCommand command, out string error)
    {
        command = null!;
        error = "";
        var values = new[] { scriptPath }.Concat(arguments).ToArray();
        if (values.Any(value => value is null || value.Any(character => char.IsControl(character) || character == '"')))
        {
            error = "Batch paths and arguments cannot contain control characters or embedded quotes.";
            return false;
        }
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var tokens = new List<string>();
        var privatePrefix = "QWE_BATCH_" + Guid.NewGuid().ToString("N") + "_";
        // Percent expansion occurs once. Values produced by expansion are not
        // rescanned for percent expressions; quotes keep metacharacters as data.
        // Delayed expansion is disabled by the controller so exclamation marks
        // remain literal. Do not use CALL, which would expand the data again.
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index].Length == 0) { tokens.Add("\"\""); continue; }
            var name = privatePrefix + index;
            environment.Add(name, values[index]);
            tokens.Add("\"%" + name + "%\"");
        }
        var encoded = "\"" + string.Join(" ", tokens) + "\"";
        // Check both the encoded and expanded command, including fixed switches.
        var expandedLength = 2L + values.Sum(value => (long)value.Length + 2) + values.Length - 1;
        if (encoded.Length + 16 > MaxCommandLength || expandedLength + 16 > MaxCommandLength)
        {
            error = "The encoded batch command exceeds CMD's 8191 character limit.";
            return false;
        }
        command = new CmdBatchCommand(encoded, new ReadOnlyDictionary<string, string>(environment));
        return true;
    }
}
