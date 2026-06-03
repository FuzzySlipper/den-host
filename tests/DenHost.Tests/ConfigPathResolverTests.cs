using DenHost.Configuration;

namespace DenHost.Tests;

public class ConfigPathResolverTests
{
    [Fact]
    public void ExtractConfigFlag_HandlesSpaceForm()
    {
        var (path, remaining) = ConfigPathResolver.ExtractConfigFlag(new[] { "--config", "/tmp/x.json", "health" });
        Assert.Equal("/tmp/x.json", path);
        Assert.Equal(new[] { "health" }, remaining);
    }

    [Fact]
    public void ExtractConfigFlag_HandlesEqualsForm()
    {
        var (path, remaining) = ConfigPathResolver.ExtractConfigFlag(new[] { "--config=/tmp/x.json", "run" });
        Assert.Equal("/tmp/x.json", path);
        Assert.Equal(new[] { "run" }, remaining);
    }

    [Fact]
    public void ExtractConfigFlag_NoFlag_ReturnsNull()
    {
        var (path, remaining) = ConfigPathResolver.ExtractConfigFlag(new[] { "health", "--json" });
        Assert.Null(path);
        Assert.Equal(new[] { "health", "--json" }, remaining);
    }

    [Fact]
    public void ExtractConfigFlag_ThrowsWhenValueMissing()
    {
        Assert.Throws<ArgumentException>(() =>
            ConfigPathResolver.ExtractConfigFlag(new[] { "--config" }));
    }

    [Fact]
    public void Resolve_FlagTakesPrecedence()
    {
        var path = ConfigPathResolver.Resolve("/flag.json", _ => "/env.json");
        Assert.Equal("/flag.json", path);
    }

    [Fact]
    public void Resolve_FallsBackToEnvVar()
    {
        var path = ConfigPathResolver.Resolve(null, key => key == ConfigPathResolver.EnvVar ? "/env.json" : null);
        Assert.Equal("/env.json", path);
    }

    [Fact]
    public void Resolve_FallsBackToDefault()
    {
        var path = ConfigPathResolver.Resolve(null, _ => null);
        Assert.EndsWith("den-host.json", path);
    }
}
