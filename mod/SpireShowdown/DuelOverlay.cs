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
    private readonly Button _cancelButton = new();
    private readonly ArenaSummon _summon=new();
    private readonly CheckButton _labToggle=new();
    private readonly CheckButton _settingsLab=new();
    private readonly CheckButton _settingsFfa=new();
    private bool _arenaRunning, _unlocked;
    private bool _revealed;
    private double _inputElapsed;
    private LinuxGameCubeAdapter? _linuxAdapter;
    private LinuxArenaKeyboard? _linuxKeyboard;
    private int _arenaControllerMode;
    private ulong _inputSequence;
    private double _elapsed;
    private double _menuPollElapsed;
    private string _baseStatus = "Preparing the arena";

    public event Action? SmokeTestRequested;
    public event Action? CancelRequested;
    public event Action<string>? ControllerModeSaved;
    public event Action<bool,bool>? ArenaPreferencesSaved;
    public event Action<ControllerState>? ControllerSampled;
    public int LastActiveJoypad { get; private set; } = -1;

    public override void _Ready()
    {
        ProcessMode = Node.ProcessModeEnum.Always;
        Layer = 500;
        _backdrop.Color = new Color("111827f5");
        _backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_backdrop);
        _summon.MouseFilter=Control.MouseFilterEnum.Ignore;
        _summon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _backdrop.AddChild(_summon);

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
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.AddThemeFontSizeOverride("font_size", 25);
        _status.AnchorLeft = 0.15f;
        _status.AnchorTop = 0.62f;
        _status.AnchorRight = 0.85f;
        _status.AnchorBottom = 0.75f;
        _backdrop.AddChild(_status);
        _cancelButton.Text = "RETURN TO SPIRE";
        _cancelButton.AnchorLeft = 0.38f;
        _cancelButton.AnchorRight = 0.62f;
        _cancelButton.AnchorTop = 0.86f;
        _cancelButton.AnchorBottom = 0.93f;
        _cancelButton.Pressed += () => CancelRequested?.Invoke();
        _backdrop.AddChild(_cancelButton);
        _labToggle.Text="LAB VIEW";
        _labToggle.AnchorLeft=.1f; _labToggle.AnchorRight=.28f;
        _labToggle.AnchorTop=.81f; _labToggle.AnchorBottom=.86f;
        _labToggle.Toggled+=enabled=> { _settingsLab.SetPressedNoSignal(enabled); ArenaPreferencesSaved?.Invoke(enabled,_settingsFfa.ButtonPressed); };
        _backdrop.AddChild(_labToggle);

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
        SetProcessInput(true);
    }

    private void BuildSettingsPanel()
    {
        _settingsPanel.Color = new Color("111827fa");
        _settingsPanel.AnchorLeft = 0.3f;
        _settingsPanel.AnchorTop = 0.15f;
        _settingsPanel.AnchorRight = 0.7f;
        _settingsPanel.AnchorBottom = 0.82f;
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
        _controllerMode.AddItem("Native Melee bindings (any pad / keyboard)",2);
        _controllerMode.AddItem("Controller used in Spire only",3);
        _controllerMode.ItemSelected += _ => RefreshControllerLabel();
        _controllerMode.AnchorLeft = 0.12f;
        _controllerMode.AnchorTop = 0.22f;
        _controllerMode.AnchorRight = 0.88f;
        _controllerMode.AnchorBottom = 0.32f;
        _settingsPanel.AddChild(_controllerMode);

        _controllerDevice.HorizontalAlignment = HorizontalAlignment.Center;
        _controllerDevice.AnchorLeft = 0.08f;
        _controllerDevice.AnchorTop = 0.35f;
        _controllerDevice.AnchorRight = 0.92f;
        _controllerDevice.AnchorBottom = 0.45f;
        _settingsPanel.AddChild(_controllerDevice);
        var nextArena = new Label { Text = "Controller changes apply to the next arena.", HorizontalAlignment = HorizontalAlignment.Center };
        nextArena.AnchorLeft=.08f; nextArena.AnchorRight=.92f;
        nextArena.AnchorTop=.43f; nextArena.AnchorBottom=.48f;
        _settingsPanel.AddChild(nextArena);
        _settingsLab.Text="Lab view by default (Melee Unlocked)";
        _settingsLab.AnchorLeft=.12f; _settingsLab.AnchorRight=.88f;
        _settingsLab.AnchorTop=.48f; _settingsLab.AnchorBottom=.57f;
        _settingsPanel.AddChild(_settingsLab);
        _settingsFfa.Text="Experimental 3–4 player FFA (UDP endpoints required)";
        _settingsFfa.AnchorLeft=.12f; _settingsFfa.AnchorRight=.88f;
        _settingsFfa.AnchorTop=.61f; _settingsFfa.AnchorBottom=.70f;
        _settingsPanel.AddChild(_settingsFfa);

        var save = new Button { Text = "SAVE AND CLOSE" };
        save.AnchorLeft = 0.25f;
        save.AnchorTop = 0.74f;
        save.AnchorRight = 0.75f;
        save.AnchorBottom = 0.88f;
        save.Pressed += () =>
        {
            var mode = _controllerMode.Selected switch {1=>"gamecube_adapter",2=>"native",3=>"spire",_=>"auto"};
            ControllerModeSaved?.Invoke(mode);
            ArenaPreferencesSaved?.Invoke(_settingsLab.ButtonPressed,_settingsFfa.ButtonPressed);
            _settingsPanel.Visible = false;
        };
        _settingsPanel.AddChild(save);
    }

    public void SetControllerMode(string? mode)
    {
        _controllerMode.Select(mode switch {"gamecube_adapter"=>1,"native"=>2,"spire"=>3,_=>0});
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
        // F8 is a developer arena shortcut, not menu navigation. Handle it
        // before focused UI controls can consume it after returning from a
        // match; an unhandled-only listener is not reliable across focus changes.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F8 })
        {
            MainFile.Logger.Info("F8 CPU arena test requested");
            GetViewport().SetInputAsHandled();
            SmokeTestRequested?.Invoke();
            return;
        }
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
        ResetPresentation();
        _summon.Begin();
        _relic.Texture = result.relic.BigIcon;
        _baseStatus = "Preparing the arena";
        _status.Text = _baseStatus;
        _backdrop.Visible = true;
        SetProcess(true);
    }

    public void ShowSmokeTestLoading()
    {
        _elapsed = 0;
        ResetPresentation();
        _summon.Begin();
        _relic.Texture = null;
        _baseStatus = "Loading one-stock fight against level-9 Fox";
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
        _linuxKeyboard?.Dispose(); _linuxKeyboard=null;
        _linuxAdapter?.Dispose(); _linuxAdapter=null;
        _linuxArenaEvents?.Dispose();
        _linuxArenaEvents = null;
        _arenaRunning=false;
        _backdrop.Visible = false;
    }

    public void SetArenaConfiguration(bool unlocked,bool lab,bool ffa)
    {
        _unlocked=unlocked; _labToggle.Visible=unlocked;
        _labToggle.SetPressedNoSignal(lab); _settingsLab.SetPressedNoSignal(lab);
        _settingsFfa.SetPressedNoSignal(ffa);
    }
    public void SetArenaRunning(bool running)
    {
        _arenaRunning=running;
        if(running) _arenaControllerMode=_controllerMode.Selected;
        if(running) GetTree().Root.GrabFocus();
    }
    public override void _ExitTree()
    {
        _linuxKeyboard?.Dispose(); _linuxKeyboard=null;
        _linuxAdapter?.Dispose(); _linuxAdapter=null;
    }
    private void ResetPresentation()
    {
        _arenaRunning=false; _revealed=false;
        _status.AnchorTop=.62f; _status.AnchorBottom=.75f; _relic.Visible=true;
    }
    public async Task FinishSummonAsync(CancellationToken token)
    {
        while (_summon.Elapsed<1.25) await Task.Delay(16,token);
        _revealed=true; _relic.Visible=false; _status.AnchorTop=.82f; _status.AnchorBottom=.86f;
        _summon.Reveal();
    }

    public void RestoreSpireFocus() => GetTree().Root.GrabFocus();

    public void SetRuntimeReady(bool ready)
    {
        _version.Text = ready
            ? $"Spire Showdown {MainFile.Version}  •  READY"
            : $"Spire Showdown {MainFile.Version}  •  DISABLED — CHECK LOG";
        _version.Modulate = ready ? new Color("9ef0b8ff") : new Color("ff9b9bff");
    }

    private LinuxArenaEventGuard? _linuxArenaEvents;

    public (ulong ParentHandle, Bounds Bounds) GetNativeTarget()
    {
        var handle = DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);
        _linuxArenaEvents ??= LinuxArenaEventGuard.Acquire(unchecked((ulong)handle));
        // The frame lives in Godot's stretched/letterboxed UI, not raw window
        // percentages. Transform its actual shared rectangle to client pixels.
        var transform = _summon.GetViewport().GetFinalTransform() * _summon.GetGlobalTransformWithCanvas();
        var rect = transform * _summon.ArenaRect;
        var x = (int)Math.Round(rect.Position.X);
        var y = (int)Math.Round(rect.Position.Y);
        return (unchecked((ulong)handle), new Bounds(x,y,
            (uint)Math.Max(1,Math.Round(rect.Size.X)),(uint)Math.Max(1,Math.Round(rect.Size.Y))));
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
        _relic.Visible=!_revealed;
        _inputElapsed+=delta;
        if (_arenaRunning && _unlocked && _inputElapsed>=1.0/60 && _arenaControllerMode is 0 or 1 or 3)
        {
            _inputElapsed=0;
            if(OperatingSystem.IsLinux() && _arenaControllerMode is 0 or 3)
                _linuxKeyboard??=new LinuxArenaKeyboard(unchecked((ulong)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle)));
            if(OperatingSystem.IsLinux() && _arenaControllerMode is 0 or 1)
                _linuxAdapter??=new LinuxGameCubeAdapter(message=>MainFile.Logger.Info(message));
            var adapter=_linuxAdapter?.Sample;
            var state=_arenaControllerMode==1 ? adapter??new ControllerState(false,0,0,0,0,0,0,0,0,"linux_gamecube") : SampleController();
            if(_arenaControllerMode==0 && adapter is not null && !GameCubeReportDecoder.InputActive(state)) state=adapter;
            ControllerSampled?.Invoke(state with {Sequence=++_inputSequence});
        }
        var dots = new string('.', 1 + (int)(_elapsed * 2) % 3);
        _status.Text = _arenaControllerMode==1 && _arenaRunning && _linuxAdapter is not null && _linuxAdapter.Sample is null
            ? _linuxAdapter.Status : $"{_baseStatus}{dots}";
    }

    private ControllerState SampleController()
    {
        var ids=Input.GetConnectedJoypads();
        var id=LastActiveJoypad>=0 && ids.Contains(LastActiveJoypad) ? LastActiveJoypad
            : ids.FirstOrDefault(i=>!IsFakeController(Input.GetJoyName(i)),-1);
        var connected=id>=0 && !IsFakeController(Input.GetJoyName(id));
        ControllerState Keyboard()
        {
            if(_linuxKeyboard is not null)
                return _linuxKeyboard.Read()??new ControllerState(true,0,0,0,0,0,0,0,0,"linux_keyboard");
            bool KeyDown(Key key)=>Input.IsPhysicalKeyPressed(key);
            ushort keys=0;
            if(KeyDown(Key.Z)) keys|=0x100; // Attack
            if(KeyDown(Key.X)) keys|=0x200; // Special
            if(KeyDown(Key.Space)) keys|=0x400; // Jump
            if(KeyDown(Key.Q)) keys|=0x40; // Shield
            if(KeyDown(Key.E)) keys|=0x10; // Grab
            if(KeyDown(Key.Enter)) keys|=0x1000;
            var sx=(sbyte)((KeyDown(Key.Right)||KeyDown(Key.D)?80:0)-(KeyDown(Key.Left)||KeyDown(Key.A)?80:0));
            var sy=(sbyte)((KeyDown(Key.Up)||KeyDown(Key.W)?80:0)-(KeyDown(Key.Down)||KeyDown(Key.S)?80:0));
            return new(true,++_inputSequence,keys,sx,sy,0,0,0,0);
        }
        var keyboard=Keyboard();
        if (!connected) return keyboard;
        bool Pressed(JoyButton b)=>Input.IsJoyButtonPressed(id,b);
        ushort buttons=0;
        foreach(var (button,mask) in new (JoyButton,ushort)[] {
            (JoyButton.A,0x100),(JoyButton.B,0x200),(JoyButton.X,0x400),(JoyButton.Y,0x800),
            (JoyButton.Start,0x1000),(JoyButton.RightShoulder,0x10),(JoyButton.LeftShoulder,0x40),
            (JoyButton.DpadLeft,1),(JoyButton.DpadRight,2),(JoyButton.DpadDown,4),(JoyButton.DpadUp,8) })
            if(Pressed(button)) buttons|=mask;
        byte Trigger(JoyAxis a)=>(byte)Math.Clamp((int)(Input.GetJoyAxis(id,a)*255),0,255);
        var tl=Trigger(JoyAxis.TriggerLeft); var tr=Trigger(JoyAxis.TriggerRight);
        if(tl>=230) buttons|=0x40; if(tr>=230) buttons|=0x20;
        sbyte Axis(JoyAxis a,bool invert=false)=>(sbyte)Math.Clamp((int)(Input.GetJoyAxis(id,a)*(invert?-80:80)),-80,80);
        var mainX=Axis(JoyAxis.LeftX); var mainY=Axis(JoyAxis.LeftY,true);
        return new(true,++_inputSequence,(ushort)(buttons|keyboard.Buttons),
            keyboard.Sx!=0?keyboard.Sx:mainX,keyboard.Sy!=0?keyboard.Sy:mainY,
            Axis(JoyAxis.RightX),Axis(JoyAxis.RightY,true),tl,tr);
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

}
