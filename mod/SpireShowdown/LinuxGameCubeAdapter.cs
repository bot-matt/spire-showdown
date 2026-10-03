using System.Runtime.InteropServices;

namespace SpireShowdown;

// Own USB access on Linux, not through Proton's Windows USB driver layer.
// Device discovery and reads never block Godot's render/input thread.
internal sealed class LinuxGameCubeAdapter : IDisposable
{
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;
    private readonly Action<string> _log;
    private ControllerState? _sample;
    private long _received;
    private string _status = "Looking for GameCube adapter";
    public string Status => Volatile.Read(ref _status);

    public LinuxGameCubeAdapter(Action<string> log)
    {
        _log=log;
        _thread=new Thread(ReadLoop) { IsBackground=true, Name="Spire GameCube USB" };
        _thread.Start();
    }

    public ControllerState? Sample => Environment.TickCount64-Interlocked.Read(ref _received)<250
        ? Volatile.Read(ref _sample) : null;

    private void SetStatus(string value)
    {
        if(value==Status) return;
        Volatile.Write(ref _status,value); _log(value);
    }

    private void ReadLoop()
    {
        nint context=0;
        try
        {
            if(Usb.Init(out context)!=0) { SetStatus("GameCube USB initialization failed"); return; }
            while(!_stop.WaitOne(0))
            {
                nint handle=0;
                int iface=0; byte input=0x81, output=0x02;
                bool claimed=false;
                try
                {
                    handle=FindAdapter(context);
                    if(handle==0) { _stop.WaitOne(1500); continue; }
                    Endpoints(handle,ref iface,ref input,ref output);
                    Usb.AutoDetach(handle,1);
                    int error=Usb.Claim(handle,iface);
                    if(error!=0) { SetStatus($"GameCube adapter could not be claimed (USB {error}); close other adapter apps"); _stop.WaitOne(1500); continue; }
                    claimed=true;
                    error=Usb.Transfer(handle,output,[0x13],1,out int written,100);
                    if(error!=0 || written!=1) { SetStatus($"GameCube adapter startup failed (USB {error})"); _stop.WaitOne(1500); continue; }
                    Usb.Transfer(handle,output,[0x11,0,0,0,0],5,out _,100);
                    var report=new byte[37]; var decoder=new GameCubeReportDecoder();
                    SetStatus("GameCube adapter connected — plug a controller into any port");
                    int failures=0;
                    while(!_stop.WaitOne(0))
                    {
                        error=Usb.Transfer(handle,input,report,report.Length,out int count,20);
                        if(error==-7) continue; // Timeout: retain packet-age checking, not stuck buttons.
                        if(error!=0 || count!=37) { if(++failures>10) break; continue; }
                        failures=0;
                        var sample=decoder.Decode(report);
                        Volatile.Write(ref _sample,sample);
                        Interlocked.Exchange(ref _received,Environment.TickCount64);
                        if(sample is not null) SetStatus("GameCube controller connected (native Linux USB)");
                    }
                }
                finally
                {
                    Volatile.Write(ref _sample,null);
                    if(claimed) Usb.Release(handle,iface); // Auto-detach restores the kernel driver.
                    if(handle!=0) Usb.Close(handle);
                }
                _stop.WaitOne(1500);
            }
        }
        catch(Exception error) { SetStatus($"GameCube adapter unavailable: {error.Message}"); }
        finally { if(context!=0) Usb.Exit(context); }
    }

    private nint FindAdapter(nint context)
    {
        nint count=Usb.Devices(context,out nint list);
        if(count<0) return 0;
        try
        {
            for(nint i=0;i<count;i++)
            {
                nint device=Marshal.ReadIntPtr(list,(int)i*IntPtr.Size);
                var descriptor=new byte[18];
                if(Usb.Descriptor(device,descriptor)!=0 || BitConverter.ToUInt16(descriptor,8)!=0x057e || BitConverter.ToUInt16(descriptor,10)!=0x0337) continue;
                int error=Usb.Open(device,out nint handle);
                if(error==0) return handle;
                SetStatus(error==-3 ? "GameCube adapter permission denied — Linux USB/udev access is required"
                    : $"GameCube adapter could not be opened (USB {error})");
                return 0;
            }
            SetStatus("No GameCube USB adapter detected (use Wii U/Switch mode)");
            return 0;
        }
        finally { Usb.FreeDevices(list,1); }
    }

    private static void Endpoints(nint handle,ref int iface,ref byte input,ref byte output)
    {
        if(Usb.Configuration(Usb.Device(handle),out nint ptr)!=0) return;
        try
        {
            var config=Marshal.PtrToStructure<ConfigDescriptor>(ptr);
            for(int i=0;i<config.Interfaces;i++)
            {
                var item=Marshal.PtrToStructure<Interface>(config.InterfaceList+i*Marshal.SizeOf<Interface>());
                for(int alt=0;alt<item.Count;alt++)
                {
                    var descriptor=Marshal.PtrToStructure<InterfaceDescriptor>(item.Alternates+alt*Marshal.SizeOf<InterfaceDescriptor>());
                    byte foundIn=0,foundOut=0;
                    for(int ep=0;ep<descriptor.Endpoints;ep++)
                    {
                        var endpoint=Marshal.PtrToStructure<EndpointDescriptor>(descriptor.EndpointList+ep*Marshal.SizeOf<EndpointDescriptor>());
                        if((endpoint.Attributes&3)!=3) continue;
                        if((endpoint.Address&0x80)!=0) foundIn=endpoint.Address; else foundOut=endpoint.Address;
                    }
                    if(foundIn!=0 && foundOut!=0) { iface=descriptor.Number; input=foundIn; output=foundOut; return; }
                }
            }
        }
        finally { Usb.FreeConfiguration(ptr); }
    }

    public void Dispose()
    {
        _stop.Set();
        // Transfers are bounded to 100ms; leave no USB device claimed on return.
        _thread.Join(); _stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct ConfigDescriptor
    {
        public byte Length,Type; public ushort TotalLength;
        public byte Interfaces,Value,String,Attributes,Power;
        public nint InterfaceList,Extra; public int ExtraLength;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Interface { public nint Alternates; public int Count; }
    [StructLayout(LayoutKind.Sequential)] private struct InterfaceDescriptor
    {
        public byte Length,Type,Number,Alternate,Endpoints,Class,Subclass,Protocol,String;
        public nint EndpointList,Extra; public int ExtraLength;
    }
    [StructLayout(LayoutKind.Sequential)] private struct EndpointDescriptor
    {
        public byte Length,Type,Address,Attributes; public ushort MaxPacket;
        public byte Interval,Refresh,Sync; public nint Extra; public int ExtraLength;
    }

    private static class Usb
    {
        private const string Library="libusb-1.0.so.0";
        [DllImport(Library,EntryPoint="libusb_init")] public static extern int Init(out nint context);
        [DllImport(Library,EntryPoint="libusb_exit")] public static extern void Exit(nint context);
        [DllImport(Library,EntryPoint="libusb_get_device_list")] public static extern nint Devices(nint context,out nint list);
        [DllImport(Library,EntryPoint="libusb_free_device_list")] public static extern void FreeDevices(nint list,int unref);
        [DllImport(Library,EntryPoint="libusb_get_device_descriptor")] public static extern int Descriptor(nint device,[Out] byte[] descriptor);
        [DllImport(Library,EntryPoint="libusb_open")] public static extern int Open(nint device,out nint handle);
        [DllImport(Library,EntryPoint="libusb_close")] public static extern void Close(nint handle);
        [DllImport(Library,EntryPoint="libusb_set_auto_detach_kernel_driver")] public static extern int AutoDetach(nint handle,int enabled);
        [DllImport(Library,EntryPoint="libusb_claim_interface")] public static extern int Claim(nint handle,int iface);
        [DllImport(Library,EntryPoint="libusb_release_interface")] public static extern int Release(nint handle,int iface);
        [DllImport(Library,EntryPoint="libusb_interrupt_transfer")] public static extern int Transfer(nint handle,byte endpoint,[In,Out] byte[] data,int length,out int actual,uint timeout);
        [DllImport(Library,EntryPoint="libusb_get_device")] public static extern nint Device(nint handle);
        [DllImport(Library,EntryPoint="libusb_get_active_config_descriptor")] public static extern int Configuration(nint device,out nint config);
        [DllImport(Library,EntryPoint="libusb_free_config_descriptor")] public static extern void FreeConfiguration(nint config);
    }
}

internal sealed class GameCubeReportDecoder
{
    private static readonly ushort[] FirstButtons=[0x100,0x200,0x400,0x800,1,2,4,8];
    private static readonly ushort[] SecondButtons=[0x1000,0x10,0x20,0x40];
    private readonly byte[][] _origins=new byte[4][];
    public ControllerState? Decode(ReadOnlySpan<byte> report)
    {
        if(report.Length!=37 || report[0]!=0x21) return null;
        ControllerState? selected=null;
        for(int port=0;port<4;port++)
        {
            var data=report.Slice(1+port*9,9);
            if((data[0]&0x30)==0) { _origins[port]=null!; continue; }
            var origin=_origins[port]??=data.Slice(3,6).ToArray();
            ushort buttons=0;
            for(int bit=0;bit<8;bit++) if((data[1]&(1<<bit))!=0) buttons|=FirstButtons[bit];
            for(int bit=0;bit<4;bit++) if((data[2]&(1<<bit))!=0) buttons|=SecondButtons[bit];
            static sbyte Axis(byte value,byte origin)=>(sbyte)Math.Clamp((int)value-origin,-128,127);
            var state=new ControllerState(true,0,buttons,Axis(data[3],origin[0]),Axis(data[4],origin[1]),Axis(data[5],origin[2]),Axis(data[6],origin[3]),
                (byte)Math.Max(0,(int)data[7]-origin[4]),(byte)Math.Max(0,(int)data[8]-origin[5]),"linux_gamecube");
            if(selected is null || InputActive(state)) selected=state;
        }
        return selected;
    }
    internal static bool InputActive(ControllerState state)=>state.Buttons!=0 || Math.Abs((int)state.Sx)>3 || Math.Abs((int)state.Sy)>3 ||
        Math.Abs((int)state.Cx)>3 || Math.Abs((int)state.Cy)>3 || state.Tl>10 || state.Tr>10;
}
