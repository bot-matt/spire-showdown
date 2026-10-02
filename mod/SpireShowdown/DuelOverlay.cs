using Godot;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SpireShowdown;

internal sealed partial class DuelOverlay : CanvasLayer
{
    private readonly ColorRect _backdrop = new();
    private readonly TextureRect _relic = new();
    private readonly Label _title = new();
    private readonly Label _status = new();
    private readonly Label _version = new();
    private readonly Button _settingsButton = new();
    private readonly ColorRect _settingsPanel = new();
    private readonly OptionButton _controllerMode = new();
    private readonly Label _controllerDevice = new();
    private double _elapsed;
    private double _menuPollElapsed;
    private string _baseStatus = "Preparing the arena";

    public event Action? SmokeTestRequested;
    public event Action<string>? ControllerModeSaved;
    public int LastActiveJoypad { get; private set; } = -1;

    public override void _Ready()
    {
        Layer = 500;
        _backdrop.Color = new Color("111827f5");
        _backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_backdrop);

        _relic.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _relic.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        _relic.AnchorLeft = 0.4f;
        _relic.AnchorTop = 0.25f;
        _relic.AnchorRight = 0.6f;
        _relic.AnchorBottom = 0.52f;
        _backdrop.AddChild(_relic);

        _title.Text = $"SPIRE SHOWDOWN {MainFile.Version}";
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.AddThemeFontSizeOverride("font_size", 42);
        _title.AnchorLeft = 0.2f;
        _title.AnchorTop = 0.1f;
        _title.AnchorRight = 0.8f;
        _title.AnchorBottom = 0.2f;
        _backdrop.AddChild(_title);

        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.AddThemeFontSizeOverride("font_size", 25);
        _status.AnchorLeft = 0.15f;
        _status.AnchorTop = 0.62f;
        _status.AnchorRight = 0.85f;
        _status.AnchorBottom = 0.75f;
        _backdrop.AddChild(_status);

        _version.Text = $"Spire Showdown {MainFile.Version}  •  STARTING";
        _version.HorizontalAlignment = HorizontalAlignment.Right;
        _version.MouseFilter = Control.MouseFilterEnum.Ignore;
        _version.AddThemeFontSizeOverride("font_size", 15);
        _version.AddThemeColorOverride("font_outline_color", new Color("111827e6"));
        _version.AddThemeConstantOverride("outline_size", 5);
        _version.AnchorLeft = 0.68f;
        _version.AnchorTop = 0.012f;
        _version.AnchorRight = 0.985f;
        _version.AnchorBottom = 0.055f;
        _version.Visible = false;
        AddChild(_version);

        _settingsButton.Text = "CONTROLLER SETTINGS";
        _settingsButton.AnchorLeft = 0.78f;
        _settingsButton.AnchorTop = 0.06f;
        _settingsButton.AnchorRight = 0.985f;
        _settingsButton.AnchorBottom = 0.105f;
        _settingsButton.Visible = false;
        _settingsButton.Pressed += () => _settingsPanel.Visible = true;
        AddChild(_settingsButton);

        BuildSettingsPanel();

        HideOverlay();
        SetProcess(true);
        SetProcessUnhandledKeyInput(true);
        SetProcessInput(true);
    }

    private void BuildSettingsPanel()
    {
        _settingsPanel.Color = new Color("111827fa");
        _settingsPanel.AnchorLeft = 0.3f;
        _settingsPanel.AnchorTop = 0.25f;
        _settingsPanel.AnchorRight = 0.7f;
        _settingsPanel.AnchorBottom = 0.68f;
        _settingsPanel.Visible = false;
        AddChild(_settingsPanel);

        var heading = new Label { Text = "SPIRE SHOWDOWN CONTROLLER" };
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        heading.AddThemeFontSizeOverride("font_size", 26);
        heading.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        heading.OffsetTop = 24;
        heading.OffsetBottom = 62;
        _settingsPanel.AddChild(heading);

        _controllerMode.AddItem("Auto-detect controller used in Spire", 0);
        _controllerMode.AddItem("GameCube USB adapter", 1);
        _controllerMode.ItemSelected += _ => RefreshControllerLabel();
        _controllerMode.AnchorLeft = 0.12f;
        _controllerMode.AnchorTop = 0.32f;
        _controllerMode.AnchorRight = 0.88f;
        _controllerMode.AnchorBottom = 0.46f;
        _settingsPanel.AddChild(_controllerMode);

        _controllerDevice.HorizontalAlignment = HorizontalAlignment.Center;
        _controllerDevice.AnchorLeft = 0.08f;
        _controllerDevice.AnchorTop = 0.51f;
        _controllerDevice.AnchorRight = 0.92f;
        _controllerDevice.AnchorBottom = 0.66f;
        _settingsPanel.AddChild(_controllerDevice);

        var save = new Button { Text = "SAVE AND CLOSE" };
        save.AnchorLeft = 0.25f;
        save.AnchorTop = 0.74f;
        save.AnchorRight = 0.75f;
        save.AnchorBottom = 0.88f;
        save.Pressed += () =>
        {
            var mode = _controllerMode.Selected == 1 ? "gamecube_adapter" : "auto";
            ControllerModeSaved?.Invoke(mode);
            _settingsPanel.Visible = false;
        };
        _settingsPanel.AddChild(save);
    }

    public void SetControllerMode(string? mode)
    {
        _controllerMode.Select(mode == "gamecube_adapter" ? 1 : 0);
        RefreshControllerLabel();
    }

    public string? ActiveControllerName()
    {
        var joypads = Input.GetConnectedJoypads();
        if (LastActiveJoypad >= 0 && joypads.Contains(LastActiveJoypad))
        {
            var active = Input.GetJoyName(LastActiveJoypad);
            if (!IsFakeController(active))
                return active;
        }
        foreach (var joypad in joypads)
        {
            var name = Input.GetJoyName(joypad);
            if (!IsFakeController(name))
                return name;
        }
        if (OperatingSystem.IsWindows())
            return "Steam Input / XInput Controller";
        return DetectLinuxGamepad();
    }

    private static bool IsFakeController(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("extest fake device")
            || lower.Contains("virtual mouse")
            || lower.Contains("tablet")
            || lower.Contains("touchscreen");
    }

    private static string? DetectLinuxGamepad()
    {
        const string inputRoot = "/sys/class/input";
        if (!Directory.Exists(inputRoot))
            return null;
        var names = new List<string>();
        foreach (var joystick in Directory.EnumerateDirectories(inputRoot, "js*"))
        {
            try
            {
                var name = File.ReadAllText(Path.Combine(joystick, "device", "name")).Trim();
                var lower = name.ToLowerInvariant();
                if (lower.Contains("mouse") || lower.Contains("tablet")
                    || lower.Contains("touch") || lower.Contains("keyboard")
                    || lower.Contains("pen"))
                    continue;
                names.Add(name);
            }
            catch (IOException)
            {
                // A hot-unplugged device can disappear during enumeration.
            }
        }
        return names.FirstOrDefault(name =>
                name.Contains("steam", StringComparison.OrdinalIgnoreCase)
                || name.Contains("xbox", StringComparison.OrdinalIgnoreCase)
                || name.Contains("x-box", StringComparison.OrdinalIgnoreCase)
                || name.Contains("gamepad", StringComparison.OrdinalIgnoreCase)
                || name.Contains("controller", StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault();
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventJoypadButton { Pressed: true } button)
        {
            if (!IsFakeController(Input.GetJoyName(button.Device)))
                LastActiveJoypad = button.Device;
        }
        else if (@event is InputEventJoypadMotion motion && Math.Abs(motion.AxisValue) > 0.35f)
        {
            if (!IsFakeController(Input.GetJoyName(motion.Device)))
                LastActiveJoypad = motion.Device;
        }
        else
            return;
        RefreshControllerLabel();
    }

    private void RefreshControllerLabel()
    {
        var name = ActiveControllerName();
        _controllerDevice.Text = _controllerMode.Selected == 1
            ? "Slippi will use the official GameCube USB adapter."
            : name is null
                ? "No Spire controller detected. Connect one and press a button."
                : $"Detected from Spire: {name}";
    }

    public void ShowLoading(RelicPickingResult result)
    {
        _elapsed = 0;
        _relic.Texture = result.relic.BigIcon;
        _baseStatus = "Preparing the arena";
        _status.Text = _baseStatus;
        _backdrop.Visible = true;
        SetProcess(true);
    }

    public void ShowSmokeTestLoading()
    {
        _elapsed = 0;
        _relic.Texture = null;
        _baseStatus = "Loading solo arena test";
        _status.Text = _baseStatus;
        _backdrop.Visible = true;
        SetProcess(true);
    }

    public void SetStatus(string text)
    {
        _baseStatus = text;
        _status.Text = text;
    }

    public void HideOverlay()
    {
        _backdrop.Visible = false;
    }

    public void SetRuntimeReady(bool ready)
    {
        _version.Text = ready
            ? $"Spire Showdown {MainFile.Version}  •  READY"
            : $"Spire Showdown {MainFile.Version}  •  DISABLED — CHECK LOG";
        _version.Modulate = ready ? new Color("9ef0b8ff") : new Color("ff9b9bff");
    }

    public (ulong ParentHandle, Bounds Bounds) GetNativeTarget()
    {
        var handle = DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);
        var size = DisplayServer.WindowGetSize();
        var x = (int)(size.X * 0.04f);
        var y = (int)(size.Y * 0.05f);
        return (unchecked((ulong)handle), new Bounds(x, y, (uint)(size.X - x * 2), (uint)(size.Y - y * 2)));
    }

    public override void _Process(double delta)
    {
        _menuPollElapsed += delta;
        if (_menuPollElapsed >= 0.5)
        {
            _menuPollElapsed = 0;
            var onMainMenu = ContainsMainMenu(GetTree().Root);
            _version.Visible = onMainMenu;
            _settingsButton.Visible = onMainMenu;
            if (!onMainMenu)
                _settingsPanel.Visible = false;
        }
        if (!_backdrop.Visible)
            return;
        _elapsed += delta;
        var dots = new string('.', 1 + (int)(_elapsed * 2) % 3);
        _status.Text = $"{_baseStatus}{dots}";
    }

    private static bool ContainsMainMenu(Node node)
    {
        if (node is NMainMenu)
            return true;
        foreach (Node child in node.GetChildren())
        {
            if (ContainsMainMenu(child))
                return true;
        }
        return false;
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.F8 })
            return;
        GetViewport().SetInputAsHandled();
        SmokeTestRequested?.Invoke();
    }
}
