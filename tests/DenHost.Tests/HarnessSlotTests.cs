using DenHost.Harness;

namespace DenHost.Tests;

public class HarnessSlotTests
{
    [Fact]
    public void StubHarnessModule_ReportsUnavailable()
    {
        var module = new StubHarnessModule("stub-a");
        Assert.Equal("stub-a", module.Name);
        Assert.Equal(HarnessModuleKind.Stub, module.Kind);
        Assert.False(module.IsAvailable());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void StubHarnessModule_RejectsEmptyName(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() => new StubHarnessModule(name!));
    }

    [Fact]
    public void HarnessModuleKind_HasExpectedValues()
    {
        // The harness firewall must keep these generic across the listed harnesses.
        Assert.True(Enum.IsDefined(typeof(HarnessModuleKind), HarnessModuleKind.Stub));
        Assert.True(Enum.IsDefined(typeof(HarnessModuleKind), HarnessModuleKind.Hermes));
        Assert.True(Enum.IsDefined(typeof(HarnessModuleKind), HarnessModuleKind.Pi));
        Assert.True(Enum.IsDefined(typeof(HarnessModuleKind), HarnessModuleKind.Codex));
        Assert.True(Enum.IsDefined(typeof(HarnessModuleKind), HarnessModuleKind.ClaudeCode));
        Assert.True(Enum.IsDefined(typeof(HarnessModuleKind), HarnessModuleKind.OpenCode));
        Assert.True(Enum.IsDefined(typeof(HarnessModuleKind), HarnessModuleKind.DenNative));
    }
}
