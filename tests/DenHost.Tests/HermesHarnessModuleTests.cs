using DenHost.Harness;
using DenHost.Harness.Modules.Hermes;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenHost.Tests;

public class HermesHarnessModuleTests : IDisposable
{
    private readonly string _tempDir;

    public HermesHarnessModuleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "den-host-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private static HermesModuleSettings BuildSettings(
        string? binaryPath = null,
        string? profile = "den-mcp-runner",
        string? home = null,
        string? role = null,
        IReadOnlyList<string>? roles = null,
        int maxTurns = 1) => new()
    {
        BinaryPath = binaryPath,
        Profile = profile,
        Home = home,
        Role = role,
        Roles = roles ?? Array.Empty<string>(),
        MaxTurns = maxTurns,
    };

    private HermesHarnessModule Build(
        string name,
        HermesModuleSettings settings,
        IHermesProcessLauncher launcher) =>
        new(name, settings, _tempDir, _tempDir, launcher, NullLogger<HermesHarnessModule>.Instance);

    [Fact]
    public void Kind_IsHermes()
    {
        var module = Build("hermes-default", BuildSettings(), new FakeLauncher(_ => MakeVersionResult()));
        Assert.Equal(HarnessModuleKind.Hermes, module.Kind);
        Assert.Equal("hermes-default", module.Name);
    }

    [Fact]
    public void IsAvailable_FalseWhenProfileMissing()
    {
        var module = Build("h", BuildSettings(profile: null, binaryPath: "/bin/true"), new FakeLauncher(_ => MakeVersionResult()));
        Assert.False(module.IsAvailable());
    }

    [Fact]
    public void IsAvailable_FalseWhenBinaryMissing()
    {
        var module = Build("h", BuildSettings(binaryPath: "/nonexistent/path/binary", profile: "p"), new FakeLauncher(_ => MakeVersionResult()));
        Assert.False(module.IsAvailable());
    }

    [Fact]
    public void IsAvailable_TrueWhenBinaryExistsAndProfileSet()
    {
        // Use /bin/true (or /usr/bin/true) as a real binary that always exists.
        var realBinary = File.Exists("/bin/true") ? "/bin/true" : "/usr/bin/true";
        var module = Build("h", BuildSettings(binaryPath: realBinary, profile: "p"), new FakeLauncher(_ => MakeVersionResult()));
        Assert.True(module.IsAvailable());
    }

    [Fact]
    public async Task SmokeAsync_OnPassedVersion_ReportsPassed()
    {
        var realBinary = File.Exists("/bin/true") ? "/bin/true" : "/usr/bin/true";
        var module = Build("hermes-default", BuildSettings(binaryPath: realBinary, profile: "p"),
            new FakeLauncher(inv => HermesRunExe(inv)));

        var result = await module.SmokeAsync(CancellationToken.None);

        Assert.Equal(HarnessSmokeOutcome.Passed, result.Outcome);
        Assert.Contains("Hermes Agent", result.Detail ?? "");
    }

    [Fact]
    public async Task SmokeAsync_OnBinaryMissing_ReportsBlockedWithEvidence()
    {
        var module = Build("hermes-x", BuildSettings(binaryPath: "/no/such/binary", profile: "p"),
            new FakeLauncher(_ => MakeVersionResult()));

        var result = await module.SmokeAsync(CancellationToken.None);

        Assert.Equal(HarnessSmokeOutcome.Blocked, result.Outcome);
        Assert.NotNull(result.BlockerEvidencePath);
        Assert.True(File.Exists(result.BlockerEvidencePath!));
    }

    [Fact]
    public async Task SmokeAsync_OnProfileMissing_ReportsBlocked()
    {
        var realBinary = File.Exists("/bin/true") ? "/bin/true" : "/usr/bin/true";
        var module = Build("hermes-x", BuildSettings(binaryPath: realBinary, profile: null),
            new FakeLauncher(_ => MakeVersionResult()));

        var result = await module.SmokeAsync(CancellationToken.None);

        Assert.Equal(HarnessSmokeOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task SmokeAsync_PassedDetailIncludesResolvedHomeAndSource()
    {
        // Use the real hermes binary on this host so the smoke genuinely
        // passes. We pin the config home to a known value and assert the
        // detail line surfaces that home and the source label.
        // (Skip rather than Assert.True so the test self-skips on hosts
        // without a hermes install instead of failing.)
        var hermesBinary = "/home/agent/.hermes/hermes-agent/.venv/bin/hermes";
        if (!File.Exists(hermesBinary)) return; // hermes not installed on this host
        var module = Build("h", BuildSettings(binaryPath: hermesBinary, home: "/configured/home", profile: "den-mcp-runner"),
            new FakeLauncher(_ => MakeVersionResult()));

        var result = await module.SmokeAsync(CancellationToken.None);

        Assert.Equal(HarnessSmokeOutcome.Passed, result.Outcome);
        Assert.Contains("home=/configured/home", result.Detail);
        Assert.Contains("source=config", result.Detail);
    }

    [Fact]
    public void ResolveHome_ConfigTakesPrecedence()
    {
        var module = Build("h", BuildSettings(binaryPath: "/bin/true", home: "/from/config", profile: "p"),
            new FakeLauncher(_ => MakeVersionResult()));
        var resolved = module.ResolveHome();
        Assert.Equal("/from/config", resolved.Value);
        Assert.Equal(HermesHarnessModule.HermesHomeSource.Config, resolved.Source);
    }

    [Fact]
    public void ResolveHome_EnvVarTakesPrecedenceOverDefault()
    {
        Environment.SetEnvironmentVariable("HERMES_HOME", "/from/env");
        try
        {
            var module = Build("h", BuildSettings(binaryPath: "/bin/true", home: null, profile: "p"),
                new FakeLauncher(_ => MakeVersionResult()));
            var resolved = module.ResolveHome();
            Assert.Equal("/from/env", resolved.Value);
            Assert.Equal(HermesHarnessModule.HermesHomeSource.Environment, resolved.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HERMES_HOME", null);
        }
    }

    [Fact]
    public void ResolveHome_ConfigBeatsEnvVar()
    {
        Environment.SetEnvironmentVariable("HERMES_HOME", "/from/env");
        try
        {
            var module = Build("h", BuildSettings(binaryPath: "/bin/true", home: "/from/config", profile: "p"),
                new FakeLauncher(_ => MakeVersionResult()));
            var resolved = module.ResolveHome();
            Assert.Equal("/from/config", resolved.Value);
            Assert.Equal(HermesHarnessModule.HermesHomeSource.Config, resolved.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HERMES_HOME", null);
        }
    }

    [Fact]
    public void ResolveHome_FallsBackToDefaultWhenNothingSet()
    {
        Environment.SetEnvironmentVariable("HERMES_HOME", null);
        var module = Build("h", BuildSettings(binaryPath: "/bin/true", home: null, profile: "p"),
            new FakeLauncher(_ => MakeVersionResult()));
        var resolved = module.ResolveHome();
        Assert.Equal(HermesHarnessModule.DefaultHermesHome, resolved.Value);
        Assert.Equal(HermesHarnessModule.HermesHomeSource.Default, resolved.Source);
    }

    [Fact]
    public void BuildInvocation_UsesResolvedHomeInEnv()
    {
        // When the operator does not set Settings.home but $HERMES_HOME is
        // set, the wake invocation's env must use the env value.
        Environment.SetEnvironmentVariable("HERMES_HOME", "/from/env");
        try
        {
            var module = Build("h", BuildSettings(binaryPath: "/bin/true", home: null, profile: "p"),
                new FakeLauncher(_ => MakeVersionResult()));
            var envelope = new WakeEnvelope("den-host", 1917, null, null, "harness:h:p", "coder", null);

            var inv = module.BuildInvocation(envelope, "local-run-1");

            Assert.Equal("/from/env", inv.Environment["HERMES_HOME"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HERMES_HOME", null);
        }
    }

    [Fact]
    public async Task GetCapabilitiesAsync_DerivesFromRoles()
    {
        var module = Build("h", BuildSettings(roles: new[] { "coder", "reviewer" }, profile: "p"),
            new FakeLauncher(_ => MakeVersionResult()));

        var caps = await module.GetCapabilitiesAsync(CancellationToken.None);

        Assert.Equal(2, caps.Count);
        Assert.Contains(caps, c => c.Role == "coder");
        Assert.Contains(caps, c => c.Role == "reviewer");
    }

    [Fact]
    public async Task GetCapabilitiesAsync_DefaultsToProfileName()
    {
        var module = Build("h", BuildSettings(profile: "my-profile"),
            new FakeLauncher(_ => MakeVersionResult()));

        var caps = await module.GetCapabilitiesAsync(CancellationToken.None);

        Assert.Single(caps);
        Assert.Equal("my-profile", caps[0].Role);
    }

    [Fact]
    public async Task GetLocalInventoryAsync_ReturnsPoolMember()
    {
        var module = Build("hermes-default", BuildSettings(profile: "p"),
            new FakeLauncher(_ => MakeVersionResult()));

        var inv = await module.GetLocalInventoryAsync(CancellationToken.None);

        Assert.Single(inv);
        Assert.Equal("harness:hermes-default:p", inv[0].PoolMemberId);
        Assert.True(inv[0].Enabled);
    }

    [Fact]
    public void BuildInvocation_IncludesExpectedArgsAndEnv()
    {
        var module = Build("h", BuildSettings(profile: "p", home: "/h", maxTurns: 5, role: "coder"),
            new FakeLauncher(_ => MakeVersionResult()));
        var envelope = new WakeEnvelope("den-host", 1917, null, null, "harness:h:p", "coder", null);

        var inv = module.BuildInvocation(envelope, "local-run-1");

        Assert.Contains("--profile", inv.Arguments);
        Assert.Contains("p", inv.Arguments);
        Assert.Contains("--max-turns", inv.Arguments);
        Assert.Contains("5", inv.Arguments);
        Assert.Contains("--role", inv.Arguments);
        Assert.Contains("coder", inv.Arguments);
        Assert.Contains("--project", inv.Arguments);
        Assert.Contains("den-host", inv.Arguments);
        Assert.Equal("/h", inv.Environment["HERMES_HOME"]);
        Assert.Equal("p", inv.Environment["HERMES_PROFILE"]);
        Assert.Equal("h", inv.Environment["DEN_HOST_MODULE"]);
        Assert.Equal("local-run-1", inv.Environment["DEN_HOST_LOCAL_RUN_ID"]);
        Assert.Contains("local-run-1", inv.LogFilePath);
    }

    [Fact]
    public void PoolMemberId_StableAndGeneric()
    {
        Assert.Equal("harness:h:p", HermesHarnessModule.PoolMemberId("h", "p"));
        Assert.Equal("harness:h:default", HermesHarnessModule.PoolMemberId("h", null));
    }

    [Fact]
    public void HermesModuleSettings_ParsesFromDictionary()
    {
        var dict = new Dictionary<string, string>
        {
            ["binary_path"] = "/bin/hermes",
            ["profile"] = "p1",
            ["home"] = "/h",
            ["role"] = "coder",
            ["roles"] = "coder,reviewer",
            ["max_turns"] = "7",
            ["provider"] = "deepseek",
            ["model"] = "deepseek-v4",
            ["extra_args"] = "--accept-hooks,--yolo",
        };
        var s = HermesModuleSettings.From(dict);
        Assert.Equal("/bin/hermes", s.BinaryPath);
        Assert.Equal("p1", s.Profile);
        Assert.Equal("/h", s.Home);
        Assert.Equal("coder", s.Role);
        Assert.Equal(new[] { "coder", "reviewer" }, s.Roles);
        Assert.Equal(7, s.MaxTurns);
        Assert.Equal("deepseek", s.Provider);
        Assert.Equal("deepseek-v4", s.Model);
        Assert.Equal(new[] { "--accept-hooks", "--yolo" }, s.ExtraArgs);
    }

    private static HermesRunResult MakeVersionResult() => new(
        ExitCode: 0,
        StandardOutput: "Hermes Agent v0.15.1 (2026.5.29)\n",
        StandardError: "");

    // For the smoke we need the launcher to actually run the binary so the
    // launcher exercises the real System.Diagnostics.Process path. We use
    // /bin/true as a stand-in: it always exits 0 with no output, so the
    // assertion that "Hermes Agent" appears in the detail will fail for
    // /bin/true. The smoke test uses a fake launcher instead.
    private static HermesRunResult HermesRunExe(HermesInvocation inv)
    {
        // Pretend to be hermes --version for the smoke test.
        return new HermesRunResult(0, "Hermes Agent v0.15.1 (test fake)\n", "");
    }

    private sealed class FakeLauncher : IHermesProcessLauncher
    {
        private readonly Func<HermesInvocation, HermesRunResult> _handler;
        public FakeLauncher(Func<HermesInvocation, HermesRunResult> handler) { _handler = handler; }
        public Task<HermesRunResult> RunAsync(HermesInvocation invocation, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(invocation));
    }
}
