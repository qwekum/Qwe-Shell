namespace ShellStudio.Tools;

/// <summary>
/// Builds a typed, non-elevated process launch for an explicitly selected user
/// script. The elevated operation host has no generic script dispatch path;
/// this contract only accepts known script extensions and an argument vector.
/// </summary>
public static class UserScriptLauncher
{
    private static readonly IReadOnlyDictionary<string, (string Interpreter, string[] Prefix)> Interpreters =
        new Dictionary<string, (string Interpreter, string[] Prefix)>(StringComparer.OrdinalIgnoreCase)
        {
            [".ps1"] = ("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"]),
            [".bat"] = ("cmd.exe", ["/d", "/s", "/c"]),
            [".cmd"] = ("cmd.exe", ["/d", "/s", "/c"]),
            [".vbs"] = ("wscript.exe", ["//NoLogo"]),
            [".js"] = ("wscript.exe", ["//NoLogo"]),
            [".wsf"] = ("wscript.exe", ["//NoLogo"])
        };

    public static bool TryBuild(
        string scriptPath,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyList<string> selection,
        out ProcessLaunchSpec specification,
        out string error)
    {
        specification = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(scriptPath) || !Path.IsPathFullyQualified(scriptPath))
        {
            error = "The script path must be absolute.";
            return false;
        }
        var extension = Path.GetExtension(scriptPath);
        if (!Interpreters.TryGetValue(extension, out var interpreter))
        {
            error = "Only .ps1, .cmd, .bat, .vbs, .js, and .wsf user scripts are supported.";
            return false;
        }
        if (arguments.Count > 1024 || arguments.Any(value => value is null || value.IndexOf('\0') >= 0 || value.Length > 32_760))
        {
            error = "The user script argument vector is invalid or exceeds its bounds.";
            return false;
        }
        if (selection.Count > 100_000 || selection.Any(value => string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(value)))
        {
            error = "Every selected script path must be an absolute path within the bounded selection.";
            return false;
        }
        if (workingDirectory is not null && !Path.IsPathFullyQualified(workingDirectory))
        {
            error = "The script working directory must be absolute.";
            return false;
        }

        var launchArguments = new List<string>(interpreter.Prefix.Length + 1 + arguments.Count + selection.Count);
        launchArguments.AddRange(interpreter.Prefix);
        launchArguments.Add(scriptPath);
        launchArguments.AddRange(arguments);
        // Selection is data appended after the script's explicit parameters.
        // Keeping each path as one argument preserves spaces and the complete
        // multi-selection through ProcessStartInfo.ArgumentList.
        launchArguments.AddRange(selection);
        specification = new ProcessLaunchSpec(interpreter.Interpreter, launchArguments, workingDirectory, Elevate: false);
        return true;
    }
}
