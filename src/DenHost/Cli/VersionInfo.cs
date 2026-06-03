using System.Reflection;

namespace DenHost.Cli;

/// <summary>
/// Static accessor for the den-host assembly version info. Used by the
/// dispatcher's built-in "version" command. Centralized so tests can
/// reference the same constants.
/// </summary>
public static class VersionInfo
{
    private static readonly Assembly s_assembly = typeof(VersionInfo).Assembly;

    public static readonly string InformationalVersion =
        s_assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? s_assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public static readonly string FileVersion =
        s_assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
        ?? InformationalVersion;
}
