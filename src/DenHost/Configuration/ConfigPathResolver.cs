namespace DenHost.Configuration;

/// <summary>
/// Resolves the den-host config file path from command-line args
/// and the <c>DEN_HOST_CONFIG</c> environment variable.
/// </summary>
public static class ConfigPathResolver
{
    public const string EnvVar = "DEN_HOST_CONFIG";

    /// <summary>
    /// Extract the explicit <c>--config &lt;path&gt;</c> flag, if present.
    /// Returns the path and the remaining args (with the flag consumed).
    /// If no flag is present, returns null and the original args.
    /// </summary>
    public static (string? ConfigPath, IReadOnlyList<string> RemainingArgs) ExtractConfigFlag(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var result = new List<string>(args.Count);
        string? configPath = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--config")
            {
                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException("--config requires a path argument");
                }
                configPath = args[i + 1];
                i++; // consume the value
                continue;
            }
            if (args[i].StartsWith("--config=", StringComparison.Ordinal))
            {
                configPath = args[i]["--config=".Length..];
                continue;
            }
            result.Add(args[i]);
        }
        return (configPath, result);
    }

    /// <summary>
    /// Resolve the effective config path using the precedence:
    /// explicit flag &gt; <c>DEN_HOST_CONFIG</c> env &gt; ./den-host.json.
    /// </summary>
    public static string Resolve(string? flagValue, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(flagValue))
        {
            return flagValue;
        }
        var envValue = getEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return envValue;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "den-host.json");
    }
}
