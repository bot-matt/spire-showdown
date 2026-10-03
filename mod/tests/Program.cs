using SpireShowdown;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var original = new SlippiConnectCodeMessage { DuelId = "test", ConnectCode = "cancel", Kind = "outcome" };
var writer = new PacketWriter();
original.Serialize(writer);
var decoded = new SlippiConnectCodeMessage();
decoded.Deserialize(new PacketReader(writer.Values));
Assert(decoded.Kind == original.Kind && decoded.ConnectCode == original.ConnectCode && decoded.DuelId == original.DuelId, "packet round trip");

for (var scenario = 0; scenario < 3; scenario++)
{
    var a = new FakeNetService(11);
    var b = new FakeNetService(22);
    a.Peer = b; b.Peer = a;
    RunManager.Instance.NetService = a;
    using var first = new DuelNegotiator("duel", "FIRST#111", 22);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var firstCode = first.ExchangeConnectCodesAsync(timeout.Token);
    // Deliberately drop the first code before the second handler exists.
    await Task.Delay(300);
    RunManager.Instance.NetService = b;
    using var second = new DuelNegotiator("duel", "OTHER#222", 11);
    var secondCode = second.ExchangeConnectCodesAsync(timeout.Token);
    Assert(await firstCode == "OTHER#222" && await secondCode == "FIRST#111", "late peer handshake");
    var outcomes = await Task.WhenAll(
        first.AgreeOutcomeAsync(scenario == 2 ? null : 11UL, timeout.Token),
        second.AgreeOutcomeAsync(scenario == 1 ? 22UL : 11UL, timeout.Token));
    Assert(outcomes.All(x => x == (scenario == 0)), "matching/disagreeing/cancelled outcomes");
    if (scenario == 2) Assert(second.RemoteCancelled.IsCancellationRequested, "remote cancellation reaches peer");
}
for (int count=2;count<=4;count++)
{
    var services=Enumerable.Range(1,count).Select(i=>new FakeNetService((ulong)i)).ToArray();
    foreach(var service in services) service.Peers.AddRange(services.Where(p=>p!=service));
    var group=new List<ArenaNegotiator>();
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
    try
    {
        for(int i=0;i<count;i++)
        {
            RunManager.Instance.NetService=services[i];
            group.Add(new ArenaNegotiator("arena",new ArenaPeer($"P{i}#123","unlocked","test",null),
                Enumerable.Range(1,count).Select(n=>(ulong)n).Where(n=>n!=(ulong)i+1)));
        }
        var peers=await Task.WhenAll(group.Select(p=>p.ExchangePeersAsync(timeout.Token)));
        Assert(peers.All(p=>p.Count==count-1),"all contenders exchange metadata");
        // Every handler can acknowledge receipt, but only its owner's ready
        // announcement proves that player finished loading their arena.
        var firstReady=group[0].AgreeReadyAsync(timeout.Token);
        await Task.Delay(250,timeout.Token);
        Assert(!firstReady.IsCompleted,"receipt acknowledgement must not release readiness barrier");
        await Task.WhenAll(group.Skip(1).Select(p=>p.AgreeReadyAsync(timeout.Token)).Append(firstReady));
        var outcomes=await Task.WhenAll(group.Select(p=>p.AgreeOutcomeAsync(1UL,timeout.Token)));
        Assert(outcomes.All(p=>p),"2-4 contenders agree on a winner");
        await Task.WhenAll(group.Select(p=>p.AgreeCommitAsync(1UL,timeout.Token)));
    }
    finally { foreach(var member in group) member.Dispose(); }
}
Console.WriteLine("PASS: packet round-trip, late-peer handshake, winner agreement, disagreement, cancellation, 2-4 player arena metadata and readiness barrier.");

namespace MegaCrit.Sts2.Core.Logging { public enum LogLevel { VeryDebug } }
namespace MegaCrit.Sts2.Core.Multiplayer.Transport { public enum NetTransferMode { Reliable } }
namespace MegaCrit.Sts2.Core.Multiplayer.Serialization
{
    public class PacketWriter
    {
        public List<string> Values { get; } = [];
        public void WriteString(string value) => Values.Add(value);
    }
    public class PacketReader(List<string> values)
    {
        private int _index;
        public string ReadString() => values[_index++];
    }
}
namespace MegaCrit.Sts2.Core.Multiplayer.Game
{
    public interface INetMessage
    {
        bool ShouldBroadcast { get; }
        MegaCrit.Sts2.Core.Multiplayer.Transport.NetTransferMode Mode { get; }
        MegaCrit.Sts2.Core.Logging.LogLevel LogLevel { get; }
        bool ShouldBuffer { get; }
        void Serialize(PacketWriter writer);
        void Deserialize(PacketReader reader);
    }
    public delegate void MessageHandlerDelegate<T>(T message, ulong senderId);
    public class FakeNetService(ulong id)
    {
        private MessageHandlerDelegate<SlippiConnectCodeMessage>? _handler;
        public FakeNetService? Peer { get; set; }
        public List<FakeNetService> Peers { get; }=[];
        public bool IsConnected => true;
        public void RegisterMessageHandler(MessageHandlerDelegate<SlippiConnectCodeMessage> handler) => _handler = handler;
        public void UnregisterMessageHandler(MessageHandlerDelegate<SlippiConnectCodeMessage> handler) => _handler = null;
        public void SendMessage(SlippiConnectCodeMessage message)
        {
            Peer?._handler?.Invoke(message,id);
            foreach(var peer in Peers) peer._handler?.Invoke(message,id);
        }
    }
}
namespace MegaCrit.Sts2.Core.Runs
{
    public class RunManager
    {
        public static RunManager Instance { get; } = new();
        public FakeNetService NetService { get; set; } = null!;
    }
}
