using System.Text.Json;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace SpireShowdown;
internal sealed record ArenaPeer(string Code, string Backend, string Version, string? Endpoint);

// All 2-4 contenders agree on backend, readiness and winner. Spectators never
// count, retries cover late handlers, and messages never enter Spire's replay.
internal sealed class ArenaNegotiator : IDisposable
{
    private readonly string _duelId;
    private readonly ArenaPeer _local;
    private readonly HashSet<ulong> _expected;
    private readonly Dictionary<ulong,ArenaPeer> _peers=[];
    private readonly Dictionary<ulong,string> _outcomes=[];
    private readonly HashSet<ulong> _peerAcks=[], _ready=[], _readyAcks=[], _outcomeAcks=[];
    private readonly Dictionary<ulong,string> _commits=[];
    private readonly Action<SlippiConnectCodeMessage> _send;
    private readonly Action _unregister;
    private readonly CancellationTokenSource _remoteCancelled=new();
    private string? _localOutcome;
    public CancellationToken RemoteCancelled => _remoteCancelled.Token;

    public ArenaNegotiator(string duelId,ArenaPeer local,IEnumerable<ulong> remotePlayerIds)
    {
        _duelId=duelId; _local=local; _expected=remotePlayerIds.ToHashSet();
        if (_expected.Count is <1 or >3) throw new ArgumentException("A duel needs 1-3 remote contenders");
        var service=RunManager.Instance.NetService;
        if (!service.IsConnected) throw new InvalidOperationException("Spire multiplayer disconnected");
        _send=message=>service.SendMessage(message);
        MessageHandlerDelegate<SlippiConnectCodeMessage> handler=(message,sender)=>
        {
            if (message.DuelId!=_duelId || !_expected.Contains(sender) || message.ConnectCode is null || message.ConnectCode.Length>1024) return;
            switch (message.Kind)
            {
                case "arena_peer": case "arena_peer_ack":
                    ArenaPeer? peer;
                    try { peer=JsonSerializer.Deserialize<ArenaPeer>(message.ConnectCode); }
                    catch (JsonException) { return; }
                    if (peer is null || string.IsNullOrWhiteSpace(peer.Code) || peer.Code.Length>18 || !peer.Code.Contains('#')) return;
                    if (peer.Backend!=_local.Backend || peer.Version!=_local.Version)
                    { _remoteCancelled.Cancel(); return; }
                    _peers[sender]=peer;
                    if (message.Kind=="arena_peer_ack") _peerAcks.Add(sender);
                    else Send("arena_peer_ack",JsonSerializer.Serialize(_local));
                    break;
                case "arena_ready": case "arena_ready_ack":
                    // Spire broadcasts replies. An ACK for somebody else's
                    // announcement must not count as an ACK for ours.
                    if (message.Kind=="arena_ready_ack")
                    { if(message.ConnectCode==_local.Code) _readyAcks.Add(sender); }
                    else if(_peers.TryGetValue(sender,out var readyPeer) && message.ConnectCode==readyPeer.Code)
                    { _ready.Add(sender); Send("arena_ready_ack",message.ConnectCode); }
                    break;
                case "arena_outcome":
                    if (message.ConnectCode!="cancel" && !ulong.TryParse(message.ConnectCode,out _)) return;
                    if (_outcomes.TryGetValue(sender,out var previous) && previous!=message.ConnectCode)
                    { _remoteCancelled.Cancel(); return; }
                    _outcomes[sender]=message.ConnectCode;
                    Send("arena_outcome_ack",message.ConnectCode);
                    if (message.ConnectCode=="cancel") _remoteCancelled.Cancel();
                    break;
                case "arena_outcome_ack":
                    if (message.ConnectCode==_localOutcome) _outcomeAcks.Add(sender);
                    break;
                case "arena_commit":
                    if (ulong.TryParse(message.ConnectCode,out _)) _commits[sender]=message.ConnectCode;
                    break;
            }
        };
        service.RegisterMessageHandler(handler);
        _unregister=()=>service.UnregisterMessageHandler(handler);
    }

    private void Send(string kind,string value)=>_send(new SlippiConnectCodeMessage
    { DuelId=_duelId,ConnectCode=value,Kind=kind });

    public async Task<IReadOnlyDictionary<ulong,ArenaPeer>> ExchangePeersAsync(CancellationToken token)
    {
        while (_peers.Count!=_expected.Count || _peerAcks.Count!=_expected.Count)
        {
            token.ThrowIfCancellationRequested(); RemoteCancelled.ThrowIfCancellationRequested();
            Send("arena_peer",JsonSerializer.Serialize(_local)); await Task.Delay(150,token);
        }
        return new Dictionary<ulong,ArenaPeer>(_peers);
    }
    public async Task AgreeReadyAsync(CancellationToken token)
    {
        while(_ready.Count!=_expected.Count || _readyAcks.Count!=_expected.Count)
        {
            token.ThrowIfCancellationRequested(); RemoteCancelled.ThrowIfCancellationRequested();
            Send("arena_ready",_local.Code); await Task.Delay(100,token);
        }
    }
    public async Task<bool> AgreeOutcomeAsync(ulong? winner,CancellationToken token)
    {
        var outcome=winner?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"cancel";
        if (_localOutcome!=outcome) _outcomeAcks.Clear();
        _localOutcome=outcome;
        while (_outcomes.Count!=_expected.Count || _outcomeAcks.Count!=_expected.Count)
        {
            token.ThrowIfCancellationRequested(); Send("arena_outcome",outcome); await Task.Delay(150,token);
        }
        return outcome!="cancel" && !RemoteCancelled.IsCancellationRequested && _outcomes.Values.All(v=>v==outcome);
    }
    public void Dispose() { _unregister(); _remoteCancelled.Dispose(); }

    public async Task AgreeCommitAsync(ulong winner,CancellationToken token)
    {
        var value=winner.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Broadcast only after outcome agreement. Spectators must not award a
        // relic merely because they saw provisional result proposals.
        do
        {
            token.ThrowIfCancellationRequested(); RemoteCancelled.ThrowIfCancellationRequested();
            Send("arena_commit",value);
            if (_commits.Values.Any(v=>v!=value)) throw new InvalidOperationException("Arena commit disagreement");
            if (_commits.Count==_expected.Count) return;
            await Task.Delay(100,token);
        } while(true);
    }
}
