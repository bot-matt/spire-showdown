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
    private static string? _settingsPath;
    private static int _shutdownStarted;

    public static bool BypassHook => Bypass.Value;
    public static bool CanStart { get; private set; }

    public static async Task InitializeAsync()
    {
        var (settings, settingsPath) = SpireShowdownSettings.Load();
        _settings = settings;
        _settingsPath = settingsPath;
        var tree = Engine.GetMainLoop() as SceneTree
            ?? throw new InvalidOperationException("Godot scene tree is unavailable");
        _overlay = new DuelOverlay { Name = "SpireShowdownOverlay" };
        _overlay.SmokeTestRequested += () => _ = RunSoloSmokeTestAsync();
        _overlay.ControllerModeSaved += SaveControllerMode;
        tree.Root.AddChild(_overlay);
        _overlay.SetControllerMode(settings.ControllerMode);
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
            _overlay.ShowSmokeTestLoading();
            ApplyControllerSettings();
            // Let the F8 key-up reach StS2 before Dolphin owns an embedded
            // window; otherwise Dolphin treats it as "load state slot 8".
            await Task.Delay(300);
            var seed = BinaryPrimitives.ReadUInt64LittleEndian(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
            var rules = DuelCoordinator.SelectRules(seed, 26);
            duelId = $"cpu-test-{seed:x16}";
            var duel = new DuelSpec(
                duelId, seed, "", rules.FirstCharacter, rules.SecondCharacter,
                rules.Stage, 1, true, 5);
            var started = await _bridge.Client.SendAsync(
                "start_cpu_test",
                new { duel },
                CancellationToken.None);
            started.Require("started");

            using var launchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            var target = _overlay.GetNativeTarget();
            var attached = await _bridge.Client.SendAsync(
                "attach_window",
                new { parent_handle = target.ParentHandle, bounds = target.Bounds },
                launchTimeout.Token);
            attached.Require("accepted");
            await WaitForPhaseAsync(_bridge.Client, "ready", launchTimeout.Token);
            _overlay.HideOverlay();
            using var matchTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            await WaitForPhaseAsync(_bridge.Client, "completed", matchTimeout.Token);
            var finished = await _bridge.Client.SendAsync(
                "finish_duel", new { duel_id = duelId }, CancellationToken.None);
            finished.Require("accepted");
            duelId = null;
        }
        catch (Exception error)
        {
            MainFile.Logger.Error($"Solo CPU fight failed: {error}");
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

            ApplyControllerSettings();

            var started = await _bridge.Client.SendAsync(
                "start_duel",
                new { duel },
                CancellationToken.None);
            started.Require("started");

            using var launchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            _overlay.SetStatus("Embedding Slippi");
            var target = _overlay.GetNativeTarget();
            var attached = await _bridge.Client.SendAsync(
                "attach_window",
                new { parent_handle = target.ParentHandle, bounds = target.Bounds },
                launchTimeout.Token);
            attached.Require("accepted");

            await WaitForPhaseAsync(_bridge.Client, "ready", launchTimeout.Token);
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

    private static void SaveControllerMode(string mode)
    {
        if (_settings is null || _settingsPath is null)
            return;
        _settings = _settings with { ControllerMode = mode };
        _settings.Save(_settingsPath);
        MainFile.Logger.Info($"Controller mode saved: {mode}");
    }

    private static void ApplyControllerSettings()
    {
        if (_settings is null)
            return;
        var adapter = _settings.ControllerMode == "gamecube_adapter";
        var configDir = ControllerConfigDirectory(_settings.SlippiPath);
        Directory.CreateDirectory(configDir);
        var dolphinPath = Path.Combine(configDir, "Dolphin.ini");
        // Auto-boot and CPU setup are Gecko injections. Fresh portable
        // Slippi installs otherwise leave the cheat engine disabled.
        UpsertIniValue(dolphinPath, "Core", "EnableCheats", "True");
        UpsertIniValue(dolphinPath, "Core", "SIDevice0", adapter ? "12" : "6");
        for (var port = 1; port < 4; port++)
            UpsertIniValue(dolphinPath, "Core", $"SIDevice{port}", "0");
        UpsertIniValue(dolphinPath, "Input", "BackgroundInput", "True");
        if (adapter)
            return;

        var name = _overlay?.ActiveControllerName()
            ?? throw new InvalidOperationException(
                "No controller is connected to Spire. Open Controller Settings or select GameCube Adapter.");
        var profile = OperatingSystem.IsWindows()
            ? WindowsGamepadProfile
            : LinuxGamepadProfile(name);
        File.WriteAllText(Path.Combine(configDir, "GCPadNew.ini"), profile);
        MainFile.Logger.Info($"Mapped Slippi Player 1 to Spire controller: {name}");
    }

    private static string ControllerConfigDirectory(string? slippiPath)
    {
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(slippiPath))
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(slippiPath))!, "User", "Config");
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "SlippiOnline", "Config");
    }

    private static void UpsertIniValue(string path, string section, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var sectionLine = $"[{section}]";
        var start = lines.FindIndex(line => line.Trim().Equals(sectionLine, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0)
                lines.Add("");
            start = lines.Count;
            lines.Add(sectionLine);
        }
        var end = lines.FindIndex(start + 1, line => line.TrimStart().StartsWith("[", StringComparison.Ordinal));
        if (end < 0)
            end = lines.Count;
        var existing = lines.FindIndex(start + 1, end - start - 1, line =>
            line.TrimStart().StartsWith(key + " ", StringComparison.OrdinalIgnoreCase)
            || line.TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            lines[existing] = $"{key} = {value}";
        else
            lines.Insert(end, $"{key} = {value}");
        File.WriteAllLines(path, lines);
    }

    private const string WindowsGamepadProfile = """
[GCPad1]
Device = XInput/0/Gamepad
Buttons/A = `Button A`
Buttons/B = `Button B`
Buttons/X = `Button X`
Buttons/Y = `Button Y`
Buttons/Z = `Shoulder R`
Buttons/Start = `Start`
Main Stick/Up = `Left Y+`
Main Stick/Down = `Left Y-`
Main Stick/Left = `Left X-`
Main Stick/Right = `Left X+`
C-Stick/Up = `Right Y+`
C-Stick/Down = `Right Y-`
C-Stick/Left = `Right X-`
C-Stick/Right = `Right X+`
Triggers/L = `Trigger L`
Triggers/R = `Trigger R`
D-Pad/Up = `Pad N`
D-Pad/Down = `Pad S`
D-Pad/Left = `Pad W`
D-Pad/Right = `Pad E`
Rumble/Motor = `Motor L` | `Motor R`
[GCPad2]
[GCPad3]
[GCPad4]
""";

    private static string LinuxGamepadProfile(string name) => $$"""
[GCPad1]
Device = evdev/0/{{name}}
Buttons/A = `Button 0`
Buttons/B = `Button 1`
Buttons/X = `Button 2`
Buttons/Y = `Button 3`
Buttons/Z = `Button 5`
Buttons/Start = `Button 7`
Main Stick/Up = `Axis 1-`
Main Stick/Down = `Axis 1+`
Main Stick/Left = `Axis 0-`
Main Stick/Right = `Axis 0+`
C-Stick/Up = `Axis 4-`
C-Stick/Down = `Axis 4+`
C-Stick/Left = `Axis 3-`
C-Stick/Right = `Axis 3+`
Triggers/L = `Axis 2+`
Triggers/R = `Axis 5+`
D-Pad/Up = `Axis 7-`
D-Pad/Down = `Axis 7+`
D-Pad/Left = `Axis 6-`
D-Pad/Right = `Axis 6+`
Rumble/Motor = Motor
[GCPad2]
[GCPad3]
[GCPad4]
""";

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
