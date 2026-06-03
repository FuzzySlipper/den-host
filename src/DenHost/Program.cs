using DenHost.Cli;
using DenHost.Cli.Hosting;
using DenHost.Configuration;
using DenHost.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace DenHost;

/// <summary>
/// Entry point. Parses <c>--config</c> and dispatches to the
/// matching <see cref="ICliCommand"/>. The Generic Host is built
/// lazily after the config path is known, so validation errors
/// surface as a clean process exit with a meaningful log line.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var rawArgs = args ?? Array.Empty<string>();
            var (configFlag, commandArgs) = ConfigPathResolver.ExtractConfigFlag(rawArgs);
            var configPath = ConfigPathResolver.Resolve(configFlag, Environment.GetEnvironmentVariable);

            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

            builder.Configuration.Sources.Clear();
            builder.Configuration
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile(configPath, optional: false, reloadOnChange: false);

            builder.Services.AddDenHost(builder.Configuration);

            using var host = builder.Build();

            var cliHost = host.Services.GetRequiredService<ICliHost>();
            var dispatcher = host.Services.GetRequiredService<CliDispatcher>();
            var exitCode = await dispatcher.DispatchAsync(cliHost, commandArgs, CancellationToken.None)
                .ConfigureAwait(false);
            return exitCode;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"den-host: fatal: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}
