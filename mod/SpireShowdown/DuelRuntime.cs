using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

namespace SpireShowdown;

internal static class DuelRuntime
{
    private static readonly AsyncLocal<bool> Bypass = new();
    private static readonly SemaphoreSlim DuelLock = new(1, 1);
    private static BridgeHost? _bridge;
    private static DuelOverlay? _overlay;
    private static string? _localConnectCode;
    private static SpireShowdownSettings? _settings;
    private static int _shutdownStarted;

    public static bool BypassHook => Bypass.Value;
    public static bool CanStart { get; private set; }

    public static async Task InitializeAsync()
    {
        var (settings, settingsPath) = SpireShowdownSettings.Load();
        _settings = settings;
        var tree = Engine.GetMainLoop() as SceneTree
            ?? throw new InvalidOperationException("Godot scene tree is unavailable");
        _overlay = new DuelOverlay { Name = "SpireShowdownOverlay" };
        _overlay.SmokeTestRequested += () => _ = RunSoloSmokeTestAsync();
        tree.Root.AddChild(_overlay);
        if (OperatingSystem.IsLinux()
            && !DisplayServer.GetName().Equals("x11", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Bazzite window embedding requires StS2 to use X11/XWayland; " +
                "add --display-driver x11 to the game's Steam launch options.");
        tree.Root.TreeExiting += ShutdownNow;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownNow();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _bridge = await BridgeHost.StartAsync(timeout.Token);
        var preflight = await _bridge.Client.SendAsync(
            "preflight",
            new
            {
                slippi = NullIfBlank(settings.SlippiPath),
                iso = NullIfBlank(settings.MeleeIsoPath)
            },
            timeout.Token);
        preflight.Require("preflight");
        var report = preflight.Element("report")
            ?? throw new InvalidDataException("Bridge omitted its preflight report.");
        if (!report.GetProperty("ready").GetBoolean())
            throw new InvalidOperationException(DescribePreflight(report));
        _localConnectCode = settings.ConnectCode?.Trim();
        if (!IsConnectCode(_localConnectCode)
            && report.TryGetProperty("connect_code", out var discoveredCode))
            _localConnectCode = discoveredCode.GetString();
        if (!IsConnectCode(_localConnectCode))
            throw new InvalidOperationException($"set connect_code in {settingsPath}");

        CanStart = true;
        _overlay.SetRuntimeReady(true);
        MainFile.Logger.Info(
            $"Spire Showdown {MainFile.Version} bridge preflight passed; " +
            $"two-player relic duels are enabled. Config: {settingsPath}");
    }

    public static void MarkUnavailable() => _overlay?.SetRuntimeReady(false);

    private static void ShutdownNow()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;
        CanStart = false;
        _bridge?.StopNow();
        _bridge = null;
    }

    private static async Task RunSoloSmokeTestAsync()
    {
        if (_bridge is null || _overlay is null || _settings is null || !CanStart)
            return;
        if (!await DuelLock.WaitAsync(0))
            return;

        string? duelId = null;
        try
        {
            var playback = FindPlayback(_settings.PlaybackPath)
                ?? throw new FileNotFoundException(
                    "Slippi Playback was not found; set playback_path in spire-showdown.json");
            var replay = FindReplay(_settings.ReplayPath)
                ?? throw new FileNotFoundException(
                    "No .slp replay was found; set replay_path in spire-showdown.json");

            _overlay.ShowSmokeTestLoading();
            var started = await _bridge.Client.SendAsync(
                "start_smoke_test",
                new { playback, replay },
                CancellationToken.None);
            started.Require("started");
            duelId = started.String("duel_id")
                ?? throw new InvalidDataException("Bridge omitted its smoke-test ID.");

            var target = _overlay.GetNativeTarget();
            var attached = await _bridge.Client.SendAsync(
                "attach_window",
                new { parent_handle = target.ParentHandle, bounds = target.Bounds },
                CancellationToken.None);
            attached.Require("accepted");
            _overlay.HideOverlay();
            MainFile.Logger.Info(
                $"Solo embed smoke test is playing {Path.GetFileName(replay)} for 30 seconds.");
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
        catch (Exception error)
        {
            MainFile.Logger.Error($"Solo embed smoke test failed: {error}");
        }
        finally
        {
            if (duelId is not null)
                await TryCancelAsync(_bridge.Client, duelId);
            _overlay.HideOverlay();
            DuelLock.Release();
        }
    }

    public static async Task RunAsync(
        NHandImageCollection hands,
        RelicPickingResult result,
        NTreasureRoomRelicHolder holder)
    {
        if (_bridge is null || _overlay is null || _localConnectCode is null)
        {
            await InvokeVanillaAsync(hands, result, holder);
            return;
        }

        await DuelLock.WaitAsync();
        string? duelId = null;
        var resultCommitted = false;
        try
        {
            _overlay.ShowLoading(result);
            var (duelTemplate, localPlayer, remotePlayer) = CreateDuel(result);
            _overlay.SetStatus("Waiting for the other contender");
            using var negotiationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var opponentCode = await DuelNegotiator.ExchangeConnectCodesAsync(
                duelTemplate.DuelId,
                _localConnectCode,
                remotePlayer.NetId,
                negotiationTimeout.Token);
            var duel = duelTemplate with { OpponentConnectCode = opponentCode };
            duelId = duel.DuelId;

            var started = await _bridge.Client.SendAsync(
                "start_duel",
                new { duel },
                CancellationToken.None);
            started.Require("started");

            using var launchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            await WaitForPhaseAsync(_bridge.Client, "ready", launchTimeout.Token);

            var target = _overlay.GetNativeTarget();
            var attached = await _bridge.Client.SendAsync(
                "attach_window",
                new { parent_handle = target.ParentHandle, bounds = target.Bounds },
                launchTimeout.Token);
            attached.Require("accepted");
            _overlay.HideOverlay();

            using var matchTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var completed = await WaitForPhaseAsync(
                _bridge.Client,
                "completed",
                matchTimeout.Token);
            var localWon = completed.Boolean("local_won")
                ?? throw new InvalidDataException("Slippi completed without identifying the local winner.");
            var winner = localWon ? localPlayer : remotePlayer;

            var finished = await _bridge.Client.SendAsync(
                "finish_duel",
                new { duel_id = duelId },
                CancellationToken.None);
            finished.Require("accepted");

            result.player = winner;
            resultCommitted = true;
            var winnerHand = hands.GetHand(winner.NetId);
            if (winnerHand is not null)
                await winnerHand.GrabRelic(holder);
            foreach (var player in result.fight!.playersInvolved)
                hands.GetHand(player.NetId)?.SetIsInFight(false);
        }
        catch (Exception error)
        {
            if (resultCommitted)
            {
                MainFile.Logger.Error($"Melee winner was recorded, but relic presentation failed: {error}");
                foreach (var player in result.fight!.playersInvolved)
                    hands.GetHand(player.NetId)?.SetIsInFight(false);
                return;
            }
            MainFile.Logger.Error($"Slippi duel failed; returning to vanilla RPS: {error}");
            if (duelId is not null)
                await TryCancelAsync(_bridge.Client, duelId);
            _overlay.HideOverlay();
            await InvokeVanillaAsync(hands, result, holder);
        }
        finally
        {
            _overlay.HideOverlay();
            DuelLock.Release();
        }
    }

    private static async Task<BridgeResponse> WaitForPhaseAsync(
        BridgeClient client,
        string wanted,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await client.SendAsync<object>("status", null, cancellationToken);
            status.Require("status");
            var phase = status.String("slippi_phase");
            if (wanted == "ready" && _overlay is not null)
            {
                _overlay.SetStatus(phase == "connecting"
                    ? "Connecting contenders"
                    : "Launching Slippi");
            }
            if (phase == wanted)
                return status;
            if (phase == "completed" && wanted != "completed")
                throw new InvalidOperationException("Slippi ended before its window became ready.");
            await Task.Delay(75, cancellationToken);
        }
    }

    private static (DuelSpec Duel, Player Local, Player Remote) CreateDuel(
        RelicPickingResult result)
    {
        var players = result.fight!.playersInvolved.OrderBy(player => player.NetId).ToArray();
        var material = Encoding.UTF8.GetBytes(
            $"{players[0].NetId}:{players[1].NetId}:{result.relic.Id}");
        var hash = SHA256.HashData(material);
        var seed = BinaryPrimitives.ReadUInt64LittleEndian(hash);
        var rules = DuelCoordinator.SelectRules(seed, 26);
        var localNetId = LocalContext.NetId
            ?? throw new InvalidOperationException("The local multiplayer player ID is unavailable.");
        var local = players.Single(player => player.NetId == localNetId);
        var remote = players.Single(player => player != local);
        var localIsFirst = local == players[0];
        var duel = new DuelSpec(
            Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant(),
            seed,
            "",
            localIsFirst ? rules.FirstCharacter : rules.SecondCharacter,
            localIsFirst ? rules.SecondCharacter : rules.FirstCharacter,
            rules.Stage);
        return (duel, local, remote);
    }

    private static async Task InvokeVanillaAsync(
        NHandImageCollection hands,
        RelicPickingResult result,
        NTreasureRoomRelicHolder holder)
    {
        Bypass.Value = true;
        try
        {
            await hands.DoFight(result, holder);
        }
        finally
        {
            Bypass.Value = false;
        }
    }

    private static async Task TryCancelAsync(BridgeClient client, string duelId)
    {
        try
        {
            await client.SendAsync(
                "cancel_duel",
                new { duel_id = duelId, reason = "local_failure" },
                CancellationToken.None);
        }
        catch
        {
            // The original failure is more useful than cleanup noise.
        }
    }

    private static bool IsConnectCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 9
        && value.Contains('#', StringComparison.Ordinal);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? FindPlayback(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        var roots = new List<string>();
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            roots.Add(Path.Combine(home, ".config", "Slippi Launcher", "playback"));
        var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            roots.Add(Path.Combine(appData, "Slippi Launcher", "playback"));

        foreach (var root in roots.Where(Directory.Exists))
        {
            var candidate = Directory.EnumerateFiles(root)
                .FirstOrDefault(path =>
                    Path.GetFileName(path).Contains("playback", StringComparison.OrdinalIgnoreCase)
                    && (path.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
            if (candidate is not null)
                return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static string? FindReplay(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, "Slippi");
        if (!Directory.Exists(root))
            return null;
        return Directory.EnumerateFiles(root, "*.slp", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private static string DescribePreflight(JsonElement report)
    {
        if (report.TryGetProperty("problems", out var problems)
            && problems.ValueKind == JsonValueKind.Array)
            return string.Join("; ", problems.EnumerateArray().Select(value => value.GetString()));
        return "Slippi or the Melee ISO was not found";
    }
}
