using System.Runtime.InteropServices;
using Godot;

namespace SpireShowdown;

// Godot's X11 ConfigureNotify dispatch also sees SubstructureNotify events
// for foreign children. Without isolation, resizing Melee changes Godot's
// cached main-window size even though the actual X11 parent never resized.
internal sealed class LinuxArenaEventGuard : IDisposable
{
    private const long SubstructureNotifyMask = 1L << 19;
    private readonly nint _display;
    private readonly nuint _window;
    private readonly nint _originalMask;
    private bool _disposed;

    private LinuxArenaEventGuard(nint display, nuint window, nint originalMask)
    {
        _display = display;
        _window = window;
        _originalMask = originalMask;
    }

    public static LinuxArenaEventGuard? Acquire(ulong window)
    {
        if (!OperatingSystem.IsLinux()) return null;
        if (DisplayServer.GetName() != "X11")
            throw new InvalidOperationException("Embedded Melee requires Spire's --display-driver x11 launch option.");
        var display = (nint)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.DisplayHandle);
        if (display == 0 || XGetWindowAttributes(display, (nuint)window, out var attributes) == 0)
            throw new InvalidOperationException("Cannot isolate Spire's X11 arena events.");
        var guard = new LinuxArenaEventGuard(display, (nuint)window, attributes.YourEventMask);
        // XSelectInput changes only THIS connection's subscription. Keep all
        // keyboard, pointer, focus, and real main-window StructureNotify events.
        XSelectInput(display, (nuint)window, (nint)((long)attributes.YourEventMask & ~SubstructureNotifyMask));
        XSync(display, 0);
        MainFile.Logger.Info("Isolated foreign X11 arena resize events from Spire");
        return guard;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Drain pending child events while the guard is still active before
        // reinstating the exact original per-connection event subscription.
        XSync(_display, 0);
        XSelectInput(_display, _window, _originalMask);
        XSync(_display, 0);
        MainFile.Logger.Info("Restored Spire's X11 event subscription");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowAttributes
    {
        public int X, Y, Width, Height, BorderWidth, Depth;
        public nint Visual;
        public nuint Root;
        public int Class, BitGravity, WinGravity, BackingStore;
        public nuint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public nuint Colormap;
        public int MapInstalled, MapState;
        public nint AllEventMasks, YourEventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public nint Screen;
    }

    [DllImport("libX11.so.6")]
    private static extern int XGetWindowAttributes(nint display, nuint window, out WindowAttributes attributes);
    [DllImport("libX11.so.6")]
    private static extern int XSelectInput(nint display, nuint window, nint eventMask);
    [DllImport("libX11.so.6")]
    private static extern int XSync(nint display, int discard);
}
