using Godot;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;

namespace SpireShowdown;

internal sealed partial class DuelOverlay : CanvasLayer
{
    private readonly ColorRect _backdrop = new();
    private readonly TextureRect _relic = new();
    private readonly Label _title = new();
    private readonly Label _status = new();
    private double _elapsed;
    private string _baseStatus = "Preparing the arena";

    public event Action? SmokeTestRequested;

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

        _title.Text = "SPIRE SHOWDOWN";
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

        HideOverlay();
        SetProcessUnhandledKeyInput(true);
    }

    public void ShowLoading(RelicPickingResult result)
    {
        _elapsed = 0;
        _relic.Texture = result.relic.BigIcon;
        _baseStatus = "Preparing the arena";
        _status.Text = _baseStatus;
        Visible = true;
        SetProcess(true);
    }

    public void ShowSmokeTestLoading()
    {
        _elapsed = 0;
        _relic.Texture = null;
        _baseStatus = "Loading solo arena test";
        _status.Text = _baseStatus;
        Visible = true;
        SetProcess(true);
    }

    public void SetStatus(string text)
    {
        _baseStatus = text;
        _status.Text = text;
    }

    public void HideOverlay()
    {
        Visible = false;
        SetProcess(false);
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
        _elapsed += delta;
        var dots = new string('.', 1 + (int)(_elapsed * 2) % 3);
        _status.Text = $"{_baseStatus}{dots}";
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.F8 })
            return;
        GetViewport().SetInputAsHandled();
        SmokeTestRequested?.Invoke();
    }
}
