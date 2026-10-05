using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace SpireShowdown;
internal static partial class DuelRuntime
{
    private static volatile ControllerState? _latestController;
    public static bool IsUnlocked=>_settings?.ArenaBackend=="melee_unlocked";
    public static bool CanHandleFight(int count)=>count==2 ? CanStart || IsUnlocked
        : IsUnlocked && count is 3 or 4;

    private static void SaveArenaPreferences(bool lab,bool ffa)
    {
        if (_settings is null || _settingsPath is null) return;
        _settings=_settings with {LabView=lab,EnableFfa=ffa};
        _settings.Save(_settingsPath);
        _overlay?.SetArenaConfiguration(IsUnlocked,lab,ffa);
    }

    private static async Task RunArenaAsync(NHandImageCollection hands,RelicPickingResult result,NTreasureRoomRelicHolder holder)
    {
        if (_overlay is null || _settings is null) { await InvokeVanillaAsync(hands,result,holder); return; }
        await DuelLock.WaitAsync();
        using var userCancellation=new CancellationTokenSource();
        _activeDuelCancellation=userCancellation;
        ArenaNegotiator? negotiation=null;
        string? activeId=null;
        var committed=false;
        try
        {
            _latestController=null;
            _overlay.ShowLoading(result);
            var players=result.fight!.playersInvolved.OrderBy(p=>p.NetId).ToArray();
            var material=Encoding.UTF8.GetBytes($"arena-1:{string.Join(':',players.Select(p=>p.NetId))}:{result.relic.Id}");
            var hash=SHA256.HashData(material);
            var id=Convert.ToHexString(hash.AsSpan(0,12)).ToLowerInvariant();
            var seed=BinaryPrimitives.ReadUInt64LittleEndian(hash);
            var localId=LocalContext.NetId??throw new InvalidOperationException("Local player unavailable");
            var localIndex=Array.FindIndex(players,p=>p.NetId==localId);
            ulong? winnerId;
            if (localIndex<0)
            {
                _overlay.SetStatus("Watching the contenders duel");
                using var watchTimeout=new CancellationTokenSource(TimeSpan.FromMinutes(17));
                // Spectators also replace the original result, but never launch
                // another arena and never count toward the contender handshake.
                winnerId=await ObserveArenaAsync(id,players.Select(p=>p.NetId).ToHashSet(),watchTimeout.Token);
            }
            else
            {
                negotiation=new ArenaNegotiator(id,new ArenaPeer(_localConnectCode??"UNSET#000","melee_unlocked",MainFile.Version,_settings.FfaEndpoint),
                    players.Where(p=>p.NetId!=localId).Select(p=>p.NetId));
                if (!CanStart || _bridge is null) throw new InvalidOperationException("Arena setup is not ready");
                if (!IsConnectCode(_localConnectCode)) throw new InvalidOperationException("Slippi connect code is not configured");
                if(players.Length>2 && !_settings.EnableFfa) throw new InvalidOperationException("Experimental FFA is disabled in settings");
                using var overall=new CancellationTokenSource(TimeSpan.FromMinutes(16));
                using var linked=CancellationTokenSource.CreateLinkedTokenSource(overall.Token,userCancellation.Token,negotiation.RemoteCancelled);
                using var handshakeTimeout=CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(25));
                var peers=await negotiation.ExchangePeersAsync(handshakeTimeout.Token);
                var roster=players.Select((p,i)=>new ArenaParticipant(p.NetId,
                    DuelCoordinator.SelectArenaRules(seed,(byte)players.Length).Characters[i],
                    i==localIndex?_localConnectCode!:peers[p.NetId].Code,
                    i==localIndex?_settings.FfaEndpoint:peers[p.NetId].Endpoint)).ToArray();
                var rules=DuelCoordinator.SelectArenaRules(seed,(byte)players.Length);
                var opponent=peers.Values.First().Code;
                var duel=new DuelSpec(id,seed,opponent,roster[localIndex].Character,
                    roster.First(p=>p.PlayerId!=localId).Character,rules.Stage,1,false,9,
                    _settings.LabView,_settings.ControllerMode??"auto",roster,(byte)localIndex);
                activeId=id;
                var started=await _bridge.Client.SendAsync("start_duel",new {duel},linked.Token);
                started.Require("started"); _overlay.SetArenaRunning(true);
                using var launch=CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                launch.CancelAfter(TimeSpan.FromSeconds(90));
                await WaitForPhaseAsync(_bridge.Client,"ready",launch.Token);
                var target=_overlay.GetNativeTarget();
                (await _bridge.Client.SendAsync("attach_window",new {parent_handle=target.ParentHandle,bounds=target.Bounds},launch.Token)).Require("accepted");
                _overlay.SetStatus("Arena ready — waiting for all contenders");
                await negotiation.AgreeReadyAsync(launch.Token);
                await _overlay.FinishSummonAsync(launch.Token);
                (await _bridge.Client.SendAsync<object>("reveal_window",null,launch.Token)).Require("accepted");
                _overlay.SetStatus("One stock. Make it count!");
                var completed=await WaitForPhaseAsync(_bridge.Client,"completed",linked.Token);
                if(players.Length==2)
                {
                    var localWon=completed.Boolean("local_won")??throw new InvalidDataException("Arena omitted winner");
                    winnerId=localWon?localId:players.First(p=>p.NetId!=localId).NetId;
                }
                else
                {
                    var index=completed.Element("winner_idx")?.GetInt32()??-1;
                    if(index<0 || index>=roster.Length) throw new InvalidDataException("Invalid FFA winner index");
                    winnerId=roster[index].PlayerId;
                }
                (await _bridge.Client.SendAsync("finish_duel",new {duel_id=id},CancellationToken.None)).Require("accepted");
                activeId=null; _overlay.SetArenaRunning(false); _overlay.RestoreSpireFocus();
                using var agreement=new CancellationTokenSource(TimeSpan.FromSeconds(25));
                _overlay.SetStatus("Confirming the winner with every contender");
                if(!await negotiation.AgreeOutcomeAsync(winnerId,agreement.Token)) throw new InvalidOperationException("Contenders disagree on the winner");
                await negotiation.AgreeCommitAsync(winnerId.Value,agreement.Token);
            }
            if(winnerId is null) throw new OperationCanceledException("Arena cancelled");
            var winner=players.Single(p=>p.NetId==winnerId);
            result.player=winner; committed=true;
            _overlay.SetStatus(winnerId==localId?"You win the relic!":"Arena finished — relic awarded");
            await Task.Delay(1000);
            _overlay.HideOverlay();
            var hand=hands.GetHand(winner.NetId);
            if(hand is not null) await hand.GrabRelic(holder);
            foreach(var p in players) hands.GetHand(p.NetId)?.SetIsInFight(false);
        }
        catch(Exception error)
        {
            MainFile.Logger.Error($"Arena failed: {error}");
            if(activeId is not null && _bridge is not null) await TryCancelAsync(_bridge.Client,activeId);
            activeId=null; _overlay.SetArenaRunning(false); _overlay.RestoreSpireFocus();
            if(negotiation is not null && !committed)
            {
                try { using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    await negotiation.AgreeOutcomeAsync(null,timeout.Token); } catch { }
            }
            _overlay.SetStatus(error is OperationCanceledException ? "Arena cancelled" : error.Message);
            // Keep the failure readable without requiring log collection.
            await Task.Delay(error is OperationCanceledException ? 1000 : 8000);
            _overlay.HideOverlay();
            if(!committed) await InvokeVanillaAsync(hands,result,holder);
            else foreach(var p in result.fight!.playersInvolved) hands.GetHand(p.NetId)?.SetIsInFight(false);
        }
        finally
        {
            if(activeId is not null && _bridge is not null) await TryCancelAsync(_bridge.Client,activeId);
            negotiation?.Dispose(); _overlay.HideOverlay(); _overlay.RestoreSpireFocus();
            _activeDuelCancellation=null; DuelLock.Release();
        }
    }

    private static async Task<ulong?> ObserveArenaAsync(string id,HashSet<ulong> contenders,CancellationToken token)
    {
        var observed=new Dictionary<ulong,string>();
        var result=new TaskCompletionSource<ulong?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service=RunManager.Instance.NetService;
        MessageHandlerDelegate<SlippiConnectCodeMessage> handler=(message,sender)=>
        {
            if(message.DuelId!=id || !contenders.Contains(sender)) return;
            if(message.Kind=="arena_outcome" && message.ConnectCode=="cancel") { result.TrySetResult(null); return; }
            if(message.Kind!="arena_commit") return;
            if(!ulong.TryParse(message.ConnectCode,out var winner) || !contenders.Contains(winner)) return;
            if(observed.TryGetValue(sender,out var old) && old!=message.ConnectCode) { result.TrySetResult(null); return; }
            observed[sender]=message.ConnectCode;
            if(observed.Count==contenders.Count)
                result.TrySetResult(observed.Values.Distinct().Count()==1?winner:null);
        };
        service.RegisterMessageHandler(handler);
        try { return await result.Task.WaitAsync(token); }
        finally { service.UnregisterMessageHandler(handler); }
    }
}
