using DenHost.Configuration;

namespace DenHost.Host;

/// <summary>
/// Resolved adapter identity for this host, derived from
/// <see cref="AdapterOptions"/> at startup. The instance is
/// immutable and shared across background services.
/// </summary>
public sealed record AdapterIdentity
{
    public required string Kind { get; init; }
    public required string InstanceId { get; init; }
    public required string Host { get; init; }
    public required IReadOnlyList<string> ManagedRoles { get; init; }
    public required IReadOnlyList<string> ManagedCapabilities { get; init; }
    public string? ProjectId { get; init; }

    public static AdapterIdentity From(AdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new AdapterIdentity
        {
            Kind = options.Kind,
            InstanceId = options.InstanceId,
            Host = options.Host,
            ManagedRoles = options.ManagedRoles,
            ManagedCapabilities = options.ManagedCapabilities,
            ProjectId = options.ProjectId,
        };
    }
}
