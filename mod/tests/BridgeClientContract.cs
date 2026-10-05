using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SpireShowdown;

internal static class BridgeClientContract
{
    // A stalled UI context must not prevent socket replies or retain the RPC
    // lock: status callers otherwise hold controller input for another UI frame.
    private sealed class StalledContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state) =>
            Interlocked.Increment(ref Posts);
    }

    public static async Task RunAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            socket.NoDelay = true;
            using var reader = new StreamReader(socket.GetStream());
            using var writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            for (var i = 0; i < 100; i++)
            {
                var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("client closed");
                using var request = JsonDocument.Parse(line);
                await Task.Delay(1, timeout.Token); // force an asynchronous reply
                await writer.WriteLineAsync(JsonSerializer.Serialize(new {
                    protocol=1, request_id=request.RootElement.GetProperty("request_id").GetString(), type="accepted"
                }));
            }
        });
        await using var client = new BridgeClient("contract-test-token");
        await client.ConnectAsync((ushort)((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        await Task.Run(() =>
        {
            var context = new StalledContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                for (var i = 0; i < 100; i++)
                {
                    var response = client.SendAsync("arena_options", new { sequence=i }, timeout.Token)
                        .WaitAsync(timeout.Token).GetAwaiter().GetResult();
                    if (response.Type != "accepted") throw new Exception("bad reply");
                }
                if (context.Posts != 0) throw new Exception("transport waited for the UI context");
            }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        });
        await server;
        Console.WriteLine("PASS: 100 input RPCs complete without pumping a stalled UI context");
    }
}
