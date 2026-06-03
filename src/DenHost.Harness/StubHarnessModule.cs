namespace DenHost.Harness;

/// <summary>
/// Default harness module slot used until a real harness is wired in
/// (den-host task #1917 wires the first Hermes module).
/// Always reports itself as unavailable and never executes anything,
/// so the host can boot and run health/CLI commands without
/// any harness module implementation present.
/// </summary>
public sealed class StubHarnessModule : IHarnessModule
{
    public StubHarnessModule(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Harness module name must be non-empty.", nameof(name));
        }
        Name = name;
    }

    public string Name { get; }

    public HarnessModuleKind Kind => HarnessModuleKind.Stub;

    public bool IsAvailable() => false;
}
