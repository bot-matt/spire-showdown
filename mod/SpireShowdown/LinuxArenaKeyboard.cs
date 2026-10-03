using System.Runtime.InteropServices;

namespace SpireShowdown;

// Wine may have no foreground HWND after X11 reparenting. Read keyboard state
// on the native side, but ONLY while focus is in Spire or its arena descendants.
internal sealed class LinuxArenaKeyboard : IDisposable
{
    private readonly nint _display;
    private readonly nuint _parent;
    private readonly byte[] _keys=new byte[32];
    private readonly Dictionary<ulong,byte> _codes=new();
    public LinuxArenaKeyboard(ulong parent)
    {
        _parent=(nuint)parent;
        _display=Open(0);
        if(_display==0) throw new InvalidOperationException("Cannot open X11 keyboard input");
    }

    public bool OwnsFocus()
    {
        Focus(_display,out nuint window,out _);
        bool owned=false;
        for(int depth=0;window>1 && depth<64;depth++)
        {
            if(window==_parent) { owned=true; break; }
            if(Tree(_display,window,out _,out nuint parent,out nint children,out _)==0) break;
            if(children!=0) Free(children);
            if(parent==window) break;
            window=parent;
        }
        return owned;
    }

    public ControllerState? Read()
    {
        if(!OwnsFocus()) return null;
        Query(_display,_keys);
        bool Down(ulong symbol)
        {
            if(!_codes.TryGetValue(symbol,out byte code)) _codes[symbol]=code=Code(_display,(nuint)symbol);
            return code!=0 && (_keys[code>>3]&(1<<(code&7)))!=0;
        }
        ushort buttons=0;
        if(Down('z')) buttons|=0x100;
        if(Down('x')) buttons|=0x200;
        if(Down(' ')) buttons|=0x400;
        if(Down('q')) buttons|=0x40;
        if(Down('e')) buttons|=0x10;
        if(Down(0xff0d)) buttons|=0x1000;
        var sx=(sbyte)((Down(0xff53)||Down('d')?80:0)-(Down(0xff51)||Down('a')?80:0));
        var sy=(sbyte)((Down(0xff52)||Down('w')?80:0)-(Down(0xff54)||Down('s')?80:0));
        return new(true,0,buttons,sx,sy,0,0,0,0,"linux_keyboard");
    }

    public void RestoreParentFocusFromChild()
    {
        Focus(_display,out nuint focused,out _);
        if(focused==_parent || !OwnsFocus()) return;
        // Godot's cached HasFocus flag can remain true after foreign-child
        // focus changes. Use the actual X11 focus, not that cached flag.
        SetFocus(_display,_parent,2,0);
        Flush(_display);
    }

    public void Dispose() { if(_display!=0) Close(_display); }
    private const string Library="libX11.so.6";
    [DllImport(Library,EntryPoint="XOpenDisplay")] private static extern nint Open(nint name);
    [DllImport(Library,EntryPoint="XCloseDisplay")] private static extern int Close(nint display);
    [DllImport(Library,EntryPoint="XGetInputFocus")] private static extern int Focus(nint display,out nuint window,out int revert);
    [DllImport(Library,EntryPoint="XQueryTree")] private static extern int Tree(nint display,nuint window,out nuint root,out nuint parent,out nint children,out uint count);
    [DllImport(Library,EntryPoint="XFree")] private static extern int Free(nint value);
    [DllImport(Library,EntryPoint="XQueryKeymap")] private static extern int Query(nint display,[Out] byte[] keys);
    [DllImport(Library,EntryPoint="XKeysymToKeycode")] private static extern byte Code(nint display,nuint symbol);
    [DllImport(Library,EntryPoint="XSetInputFocus")] private static extern int SetFocus(nint display,nuint window,int revert,nuint time);
    [DllImport(Library,EntryPoint="XFlush")] private static extern int Flush(nint display);
}
