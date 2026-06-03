using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DenHost.Harness;
using Microsoft.Extensions.Logging;

namespace DenHost.Harness.Modules.Hermes;

/// <summary>
/// Hermes-specific configuration for a single harness module slot.
/// Parsed from the harness module's settings dictionary.
/// Keys: binary_path, profile, home, role, roles, max_turns,
/// provider, model, extra_args (CSV).
/// </summary>
public sealed class HermesModuleSettings
{
    public string? BinaryPath { get; init; }
    public string? Profile { get; init; }
    public string? Home { get; init; }
    public string? Role { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
    public int MaxTurns { get; init; } = 1;
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();

    public static HermesModuleSettings From(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var rolesCsv = settings.TryGetValue("roles", out var rs) ? rs : "";
        var roles = string.IsNullOrWhiteSpace(rolesCsv)
            ? Array.Empty<string>()
            : rs!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var extraCsv = settings.TryGetValue("extra_args", out var ea) ? ea : "";
        var extra = string.IsNullOrWhiteSpace(extraCsv)
            ? Array.Empty<string>()
            : ea!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new HermesModuleSettings
        {
            BinaryPath = settings.TryGetValue("binary_path", out var bp) ? bp : null,
            Profile = settings.TryGetValue("profile", out var p) ? p : null,
            Home = settings.TryGetValue("home", out var h) ? h : null,
            Role = settings.TryGetValue("role", out var r) ? r : null,
            Roles = roles,
            MaxTurns = int.TryParse(settings.GetValueOrDefault("max_turns"), out var mt) ? mt : 1,
            Provider = settings.TryGetValue("provider", out var pv) ? pv : null,
            Model = settings.TryGetValue("model", out var m) ? m : null,
            ExtraArgs = extra,
        };
    }
}

/// <summary>
/// Description of how to invoke the hermes CLI. Built by
/// HermesHarnessModule; the actual launch is in SystemHermesProcessLauncher
/// (or a test fake), which keeps the firewall between the harness module
/// and the den-host process boundary.
/// </summary>
public sealed record HermesInvocation(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory,
    string LogFilePath);

/// <summary>
/// Outcome of a hermes CLI invocation. Exit code 0 = success.
/// </summary>
public sealed record HermesRunResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Abstract process launcher. The default implementation
/// (<see cref="SystemHermesProcessLauncher"/>) uses
/// <see cref="System.Diagnostics.Process"/>; tests can inject
/// a fake to verify the invocation shape without spawning hermes.
/// </summary>
public interface IHermesProcessLauncher
{
    Task<HermesRunResult> RunAsync(HermesInvocation invocation, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class SystemHermesProcessLauncher : IHermesProcessLauncher
{
    public async Task<HermesRunResult> RunAsync(HermesInvocation invocation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = invocation.ExecutablePath,
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in invocation.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }
        foreach (var (key, value) in invocation.Environment)
        {
            psi.Environment[key] = value;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(invocation.LogFilePath)!);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start hermes process: {invocation.ExecutablePath}");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Tee stdout/stderr to the log file as the process runs. The launcher's
        // accumulated StringBuilders are the source of truth for the final
        // HermesRunResult; the log file is a side artifact for operators.
        await using var logStream = new FileStream(invocation.LogFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var logWriter = new StreamWriter(logStream, Encoding.UTF8);
        var teeTask = Task.Run(async () =>
        {
            var lastStdoutLen = 0;
            var lastStderrLen = 0;
            while (!process.HasExited)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                if (stdout.Length > lastStdoutLen)
                {
                    var chunk = stdout.ToString(lastStdoutLen, stdout.Length - lastStdoutLen);
                    await logWriter.WriteLineAsync("[stdout] " + chunk.TrimEnd()).ConfigureAwait(false);
                    lastStdoutLen = stdout.Length;
                }
                if (stderr.Length > lastStderrLen)
                {
                    var chunk = stderr.ToString(lastStderrLen, stderr.Length - lastStderrLen);
                    await logWriter.WriteLineAsync("[stderr] " + chunk.TrimEnd()).ConfigureAwait(false);
                    lastStderrLen = stderr.Length;
                }
                await logWriter.FlushAsync().ConfigureAwait(false);
            }
            // Final flush.
            if (stdout.Length > lastStdoutLen)
            {
                await logWriter.WriteLineAsync("[stdout] " + stdout.ToString(lastStdoutLen, stdout.Length - lastStdoutLen).TrimEnd()).ConfigureAwait(false);
            }
            if (stderr.Length > lastStderrLen)
            {
                await logWriter.WriteLineAsync("[stderr] " + stderr.ToString(lastStderrLen, stderr.Length - lastStderrLen).TrimEnd()).ConfigureAwait(false);
            }
            await logWriter.FlushAsync().ConfigureAwait(false);
        }, cancellationToken);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"hermes invocation timed out after {timeout.TotalSeconds:0.#}s");
        }

        try { await teeTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

        return new HermesRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

/// <summary>
/// Hermes harness module. Resolves the hermes binary + profile from
/// config, exposes generic Den-facing capabilities / pool members,
/// and provides Wake/Stop/CollectEvidence/ResetSession hooks that
/// shell out to the hermes CLI as a child process. The hermes binary
/// itself is Python, but the python interpreter never enters the
/// den-host process -- the firewall is the process boundary.
/// </summary>
public sealed class HermesHarnessModule : IHarnessModule
{
    public const string DefaultHermesBinary = "hermes";

    /// <summary>
    /// Last-resort fallback for HERMES_HOME when the operator has not
    /// configured <c>Settings.home</c> and the $HERMES_HOME environment
    /// variable is unset. Correct for the den-k8 host; on other machines,
    /// either set the env var or set <c>Settings.home</c> in den-host.json.
    /// </summary>
    public const string DefaultHermesHome = "/home/agent/.hermes";

    public static readonly TimeSpan DefaultSmokeTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultWakeTimeout = TimeSpan.FromHours(1);

    /// <summary>
    /// Describes how the Hermes home directory was resolved.
    /// </summary>
    public enum HermesHomeSource
    {
        /// <summary>Set explicitly in the harness module's <c>Settings.home</c>.</summary>
        Config = 0,
        /// <summary>Discovered from the <c>HERMES_HOME</c> environment variable.</summary>
        Environment = 1,
        /// <summary>Fell back to the hardcoded <c>DefaultHermesHome</c>.</summary>
        Default = 2,
    }

    /// <summary>
    /// Resolved Hermes home with the source it came from. The home
    /// is passed to the child hermes process as the HERMES_HOME env var.
    /// </summary>
    public sealed record ResolvedHermesHome(string Value, HermesHomeSource Source);

    private readonly HermesModuleSettings _settings;
    private readonly string _logDir;
    private readonly string _runDir;
    private readonly IHermesProcessLauncher _launcher;
    private readonly ILogger<HermesHarnessModule> _logger;

    public HermesHarnessModule(
        string name,
        HermesModuleSettings settings,
        string logDir,
        string runDir,
        IHermesProcessLauncher launcher,
        ILogger<HermesHarnessModule> logger)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required", nameof(name));
        Name = name;
        _settings = settings;
        _logDir = logDir;
        _runDir = runDir;
        _launcher = launcher;
        _logger = logger;
    }

    public string Name { get; }
    public HarnessModuleKind Kind => HarnessModuleKind.Hermes;

    public bool IsAvailable()
    {
        if (string.IsNullOrWhiteSpace(_settings.Profile)) return false;
        var binary = ResolveBinaryPath();
        if (binary is null) return false;
        if (!File.Exists(binary)) return false;
        return true;
    }

    public Task<IReadOnlyList<HarnessCapability>> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var roles = ResolveRoles();
        var caps = roles.Select(r => new HarnessCapability(r, new[] { $"harness.hermes.{r}" })).ToList();
        return Task.FromResult<IReadOnlyList<HarnessCapability>>(caps);
    }

    public Task<IReadOnlyList<PoolMemberDescriptor>> GetLocalInventoryAsync(CancellationToken cancellationToken)
    {
        // For #1917, the Hermes module's pool member set is the configured
        // profile (one member per Hermes profile) plus any roles configured
        // for it. Future work (#1917+ follow-up) could enumerate
        // ~/.hermes/profiles/* to discover additional pool members.
        var roles = ResolveRoles();
        var descriptors = new List<PoolMemberDescriptor>
        {
            new(
                PoolMemberId: PoolMemberId(Name, _settings.Profile),
                Role: roles.FirstOrDefault() ?? "hermes",
                DisplayName: $"hermes:{_settings.Profile}",
                Enabled: true),
        };
        return Task.FromResult<IReadOnlyList<PoolMemberDescriptor>>(descriptors);
    }

    public async Task<WorkerHandle> WakeAsync(WakeEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsAvailable())
        {
            throw new InvalidOperationException(
                $"Hermes harness module '{Name}' is not available. " +
                $"Check that hermes binary is on PATH or set harness binary_path, " +
                $"and that the configured profile exists. " +
                $"binary={ResolveBinaryPath() ?? "<null>"} profile={_settings.Profile ?? "<null>"}");
        }

        var localRunId = $"hermes-{Guid.NewGuid():N}";
        var invocation = BuildInvocation(envelope, localRunId);

        _logger.LogInformation(
            "Hermes wake: local_run_id={LocalRunId} binary={Binary} args=[{Args}] env_hermes_home={Home} log={Log}",
            localRunId, invocation.ExecutablePath, string.Join(" ", invocation.Arguments),
            invocation.Environment.GetValueOrDefault("HERMES_HOME"), invocation.LogFilePath);

        // Launch the process. We do not await its completion here -- the host
        // gets back a handle and can poll, stop, or collect evidence.
        var psi = new ProcessStartInfo
        {
            FileName = invocation.ExecutablePath,
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in invocation.Arguments) psi.ArgumentList.Add(arg);
        foreach (var (key, value) in invocation.Environment) psi.Environment[key] = value;

        Directory.CreateDirectory(Path.GetDirectoryName(invocation.LogFilePath)!);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        await using var logStream = new FileStream(invocation.LogFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var logWriter = new StreamWriter(logStream, Encoding.UTF8);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start hermes process: {invocation.ExecutablePath}");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Background tee that flushes stdout/stderr into the log file.
        // Uses the same position-based slicing pattern as
        // SystemHermesProcessLauncher: read from the last persisted offset
        // instead of mutating the StringBuilder. This avoids both the
        // StringBuilder.Clear() race (where OutputDataReceived could append
        // between read and clear) and the unobserved-exception risk of a
        // fire-and-forget _ = Task.Run(...) lambda. The catch here is
        // last-resort: a tee failure must not crash the host, and the
        // WorkerHandle we return is still valid (operator can collect
        // evidence from whatever made it to disk).
        _ = Task.Run(async () =>
        {
            try
            {
                var lastStdoutLen = 0;
                var lastStderrLen = 0;
                while (!process.HasExited)
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    if (stdout.Length > lastStdoutLen)
                    {
                        var chunk = stdout.ToString(lastStdoutLen, stdout.Length - lastStdoutLen);
                        await logWriter.WriteLineAsync("[stdout] " + chunk.TrimEnd()).ConfigureAwait(false);
                        lastStdoutLen = stdout.Length;
                    }
                    if (stderr.Length > lastStderrLen)
                    {
                        var chunk = stderr.ToString(lastStderrLen, stderr.Length - lastStderrLen);
                        await logWriter.WriteLineAsync("[stderr] " + chunk.TrimEnd()).ConfigureAwait(false);
                        lastStderrLen = stderr.Length;
                    }
                    await logWriter.FlushAsync().ConfigureAwait(false);
                }
                // Final flush after the process has exited.
                if (stdout.Length > lastStdoutLen)
                {
                    await logWriter.WriteLineAsync("[stdout] " + stdout.ToString(lastStdoutLen, stdout.Length - lastStdoutLen).TrimEnd()).ConfigureAwait(false);
                }
                if (stderr.Length > lastStderrLen)
                {
                    await logWriter.WriteLineAsync("[stderr] " + stderr.ToString(lastStderrLen, stderr.Length - lastStderrLen).TrimEnd()).ConfigureAwait(false);
                }
                await logWriter.FlushAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hermes wake: log tee task failed for {RunId}; wake handle is still valid", localRunId);
            }
        }, cancellationToken);

        return new WorkerHandle(
            LocalRunId: localRunId,
            Kind: Kind,
            ProcessId: process.Id,
            StartedAt: DateTimeOffset.UtcNow,
            LogFilePath: invocation.LogFilePath,
            Status: WorkerStatus.Running);
    }

    public Task StopAsync(WorkerHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.ProcessId is not int pid)
        {
            _logger.LogWarning("Hermes stop: handle {RunId} has no process id; nothing to stop", handle.LocalRunId);
            return Task.CompletedTask;
        }
        try
        {
            var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            process.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hermes stop: failed to kill process {Pid} for {RunId}", pid, handle.LocalRunId);
        }
        return Task.CompletedTask;
    }

    public Task<HarnessRunEvidence> CollectEvidenceAsync(WorkerHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        int? exit = null;
        if (handle.ProcessId is int pid)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                if (p.HasExited) exit = p.ExitCode;
                p.Dispose();
            }
            catch
            {
                // process is gone; exit is unknown
            }
        }
        string? logTail = null;
        try
        {
            if (File.Exists(handle.LogFilePath))
            {
                var fi = new FileInfo(handle.LogFilePath);
                const int tailBytes = 4 * 1024;
                using var fs = fi.OpenRead();
                fs.Seek(-Math.Min(tailBytes, fi.Length), SeekOrigin.End);
                using var sr = new StreamReader(fs);
                logTail = sr.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hermes collect-evidence: failed to read log tail for {RunId}", handle.LocalRunId);
        }
        return Task.FromResult(new HarnessRunEvidence(
            LocalRunId: handle.LocalRunId,
            ExitCode: exit,
            EndedAt: exit is null ? null : DateTimeOffset.UtcNow,
            LogTail: logTail,
            Notes: $"hermes module '{Name}' (profile={_settings.Profile})"));
    }

    public Task ResetSessionAsync(WorkerHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        // Hermes stores session state under $HERMES_HOME/sessions/<profile>/.
        // The host does not delete session files here; that is a Hermes-specific
        // operation, and the Hermes module keeps it inside its own boundary.
        _logger.LogInformation(
            "Hermes reset-session: profile={Profile} run_id={RunId} (no-op; Hermes-specific session cleanup is internal)",
            _settings.Profile, handle.LocalRunId);
        return Task.CompletedTask;
    }

    public async Task<HarnessSmokeResult> SmokeAsync(CancellationToken cancellationToken)
    {
        var binary = ResolveBinaryPath();
        if (binary is null || !File.Exists(binary))
        {
            var detail = $"hermes binary not found (binary_path={_settings.BinaryPath ?? "<PATH>"}, default={DefaultHermesBinary}). " +
                         "Set harness binary_path or install hermes on PATH.";
            var blocker = Path.Combine(_logDir, $"blocker-hermes-{Name}-binary-missing.txt");
            try
            {
                Directory.CreateDirectory(_logDir);
                await File.WriteAllTextAsync(blocker, detail, cancellationToken).ConfigureAwait(false);
            }
            catch { /* best effort */ }
            return new HarnessSmokeResult(Name, Kind, HarnessSmokeOutcome.Blocked, detail, blocker);
        }
        if (string.IsNullOrWhiteSpace(_settings.Profile))
        {
            var detail = $"hermes profile not configured. Set harness profile in den-host.json.";
            var blocker = Path.Combine(_logDir, $"blocker-hermes-{Name}-profile-missing.txt");
            try
            {
                Directory.CreateDirectory(_logDir);
                await File.WriteAllTextAsync(blocker, detail, cancellationToken).ConfigureAwait(false);
            }
            catch { /* best effort */ }
            return new HarnessSmokeResult(Name, Kind, HarnessSmokeOutcome.Blocked, detail, blocker);
        }

        var invocation = BuildSmokeInvocation();
        var home = ResolveHome();
        try
        {
            var result = await _launcher.RunAsync(invocation, DefaultSmokeTimeout, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                var firstLine = result.StandardOutput.Split('\n').FirstOrDefault()?.Trim() ?? "";
                return new HarnessSmokeResult(Name, Kind, HarnessSmokeOutcome.Passed,
                    $"hermes binary returned 0; first line: {firstLine}; home={home.Value} (source={home.Source.ToString().ToLowerInvariant()})");
            }
            return new HarnessSmokeResult(Name, Kind, HarnessSmokeOutcome.Failed,
                $"hermes --version exited {result.ExitCode}; stderr: {result.StandardError.Trim()}; home={home.Value} (source={home.Source.ToString().ToLowerInvariant()})");
        }
        catch (Exception ex)
        {
            return new HarnessSmokeResult(Name, Kind, HarnessSmokeOutcome.Failed,
                $"hermes smoke threw: {ex.GetType().Name}: {ex.Message}; home={home.Value} (source={home.Source.ToString().ToLowerInvariant()})");
        }
    }

    public HermesInvocation BuildInvocation(WakeEnvelope envelope, string localRunId)
    {
        var binary = ResolveBinaryPath() ?? DefaultHermesBinary;
        var args = new List<string>
        {
            "--profile", _settings.Profile!,
            "chat",
            "-Q",
            "--source", "den-host",
            "--max-turns", _settings.MaxTurns.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(_settings.Provider)) { args.Add("--provider"); args.Add(_settings.Provider); }
        if (!string.IsNullOrEmpty(_settings.Model)) { args.Add("--model"); args.Add(_settings.Model); }
        if (envelope.ProjectId is not null) { args.Add("--project"); args.Add(envelope.ProjectId); }
        if (envelope.TaskId is int tid) { args.Add("--task"); args.Add(tid.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (envelope.AssignmentId is int aid) { args.Add("--assignment"); args.Add(aid.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (envelope.RunId is not null) { args.Add("--run"); args.Add(envelope.RunId); }
        if (envelope.PoolMemberId is not null) { args.Add("--pool-member"); args.Add(envelope.PoolMemberId); }
        if (envelope.Role is not null) { args.Add("--role"); args.Add(envelope.Role); }
        args.Add("-t");
        args.Add("terminal,file");
        args.Add("--yolo");
        args.Add("--accept-hooks");
        args.AddRange(_settings.ExtraArgs);

        var env = new Dictionary<string, string>
        {
            ["HERMES_HOME"] = ResolveHome().Value,
            ["HERMES_PROFILE"] = _settings.Profile!,
            ["DEN_HOST_MODULE"] = Name,
            ["DEN_HOST_LOCAL_RUN_ID"] = localRunId,
        };

        var logFile = Path.Combine(_logDir, $"hermes-{localRunId}.log");
        return new HermesInvocation(binary, args, env, Environment.CurrentDirectory, logFile);
    }

    private HermesInvocation BuildSmokeInvocation()
    {
        var binary = ResolveBinaryPath() ?? DefaultHermesBinary;
        var args = new List<string> { "--version" };
        var env = new Dictionary<string, string>
        {
            ["HERMES_HOME"] = ResolveHome().Value,
            ["HERMES_PROFILE"] = _settings.Profile!,
        };
        var logFile = Path.Combine(_logDir, $"hermes-smoke-{Name}-{Guid.NewGuid():N}.log");
        return new HermesInvocation(binary, args, env, Environment.CurrentDirectory, logFile);
    }

    /// <summary>
    /// Resolve the Hermes home directory. Order: explicit
    /// <c>Settings.home</c>, then <c>$HERMES_HOME</c>, then
    /// <c>DefaultHermesHome</c>. Returns the value plus the source
    /// so callers (the smoke detail line) can tell operators which
    /// one was used.
    /// </summary>
    public ResolvedHermesHome ResolveHome()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Home))
        {
            return new ResolvedHermesHome(_settings.Home!, HermesHomeSource.Config);
        }
        var envHome = Environment.GetEnvironmentVariable("HERMES_HOME");
        if (!string.IsNullOrWhiteSpace(envHome))
        {
            return new ResolvedHermesHome(envHome, HermesHomeSource.Environment);
        }
        return new ResolvedHermesHome(DefaultHermesHome, HermesHomeSource.Default);
    }

    private string? ResolveBinaryPath()
    {
        if (!string.IsNullOrWhiteSpace(_settings.BinaryPath)) return _settings.BinaryPath;
        // Default: look up `hermes` on PATH. The launcher resolves PATH itself.
        if (Environment.GetEnvironmentVariable("PATH") is { } path)
        {
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir, "hermes");
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // skip unreadable path entries
                }
            }
        }
        // Fallback: well-known location on this host. Real installations
        // beyond this host are out of scope for the smoke.
        var wellKnown = DefaultHermesBinary == "hermes"
            ? Path.Combine(DefaultHermesHome, "hermes-agent", ".venv", "bin", "hermes")
            : null;
        if (wellKnown is not null && File.Exists(wellKnown)) return wellKnown;
        return null;
    }

    private IReadOnlyList<string> ResolveRoles()
    {
        var roles = new List<string>();
        if (!string.IsNullOrWhiteSpace(_settings.Role)) roles.Add(_settings.Role);
        roles.AddRange(_settings.Roles);
        if (roles.Count == 0)
        {
            // Default: derive a single role from the profile name.
            // Hermes profile names are operator-chosen; we treat the
            // profile name as a role token at the harness firewall so
            // Core/Channels can address it generically.
            roles.Add(_settings.Profile ?? "hermes");
        }
        return roles;
    }

    /// <summary>
    /// Build a stable, generic pool member id from the harness module name
    /// and the configured Hermes profile. The id does not embed any
    /// Hermes-specific path so Core/Channels can address it generically.
    /// </summary>
    public static string PoolMemberId(string moduleName, string? profile) =>
        $"harness:{moduleName}:{(profile ?? "default")}";
}
