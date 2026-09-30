using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpireShowdown;

internal sealed class BridgeHost : IAsyncDisposable
{
    private readonly Process _process;

    private BridgeHost(Process process, BridgeClient client)
    {
        _process = process;
        Client = client;
    }

    public BridgeClient Client { get; }

    public static async Task<BridgeHost> StartAsync(CancellationToken cancellationToken)
    {
        var bridgePath = LocateBridge();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var startInfo = new ProcessStartInfo
        {
            FileName = bridgePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("--token");
        startInfo.ArgumentList.Add(token);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Spire Showdown bridge.");
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
                ?? throw new InvalidOperationException("The bridge exited before becoming ready.");
            using var ready = JsonDocument.Parse(line);
            var root = ready.RootElement;
            if (root.GetProperty("type").GetString() != "bridge_ready")
                throw new InvalidDataException("The bridge returned an invalid startup message.");

            var client = new BridgeClient(token);
            await client.ConnectAsync(root.GetProperty("port").GetUInt16(), cancellationToken);
            return new BridgeHost(process, client);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    private static string LocateBridge()
    {
        var configured = Environment.GetEnvironmentVariable("SPIRE_SHOWDOWN_BRIDGE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? AppContext.BaseDirectory;
        var name = OperatingSystem.IsWindows()
            ? "spire-showdown-bridge.exe"
            : "spire-showdown-bridge";
        var candidate = Path.Combine(assemblyDir, name);
        if (File.Exists(candidate))
            return candidate;

        throw new FileNotFoundException(
            $"Could not find {name}. Set SPIRE_SHOWDOWN_BRIDGE or place it beside the mod DLL.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Client.SendAsync<object>("shutdown", null, CancellationToken.None);
        }
        catch
        {
            // Process cleanup below is authoritative.
        }
        await Client.DisposeAsync();
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
        _process.Dispose();
    }
}
