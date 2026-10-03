namespace SpireShowdown;

// Steam's Spire strategy emits controller_* InputEventAction events, not raw
// JoyButton state. This mapper intentionally has NO raw-joypad connection gate.
internal static class SpireControllerActions
{
    internal static ControllerState Sample(Func<string,float> strength,float leftX,float leftY)
    {
        float Value(string name)=>Math.Clamp(strength("controller_"+name),0,1);
        bool Pressed(string name)=>Value(name)>.5f;
        ushort buttons=0;
        foreach(var (name,mask) in new (string,ushort)[] {
            ("face_button_south",0x100),("face_button_east",0x200),
            ("face_button_west",0x400),("face_button_north",0x800),
            ("start_button",0x1000),("right_bumper",0x10),("left_bumper",0x40),
            ("d_pad_left",1),("d_pad_right",2),("d_pad_down",4),("d_pad_up",8) })
            if(Pressed(name)) buttons|=mask;
        byte Trigger(string name)=>(byte)Math.Round(Value(name)*255);
        var tl=Trigger("left_trigger"); var tr=Trigger("right_trigger");
        if(tl>=230) buttons|=0x40; if(tr>=230) buttons|=0x20;
        static sbyte Axis(float value)=>(sbyte)Math.Clamp((int)Math.Round(value*80),-80,80);
        return new(true,0,buttons,Axis(leftX),Axis(-leftY),
            Axis(Value("r_stick_right")-Value("r_stick_left")),
            Axis(Value("r_stick_up")-Value("r_stick_down")),tl,tr,"spire_actions");
    }
}
