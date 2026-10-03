using Godot;
namespace SpireShowdown;

// Spire owns the summon animation; the foreign arena remains unmapped until
// the engine and every contender are ready. No blocking of the Godot main loop.
internal sealed partial class ArenaSummon : Control
{
    private double _time;
    private bool _open;
    public double Elapsed => _time;
    public Rect2 ArenaRect => new(Size.X*.1f,Size.Y*.2f,Size.X*.8f,Size.Y*.6f);
    public void Begin() { _time=0; _open=false; Visible=true; SetProcess(true); }
    public void Reveal() { _open=true; QueueRedraw(); }
    public override void _Process(double delta) { _time+=delta; QueueRedraw(); }
    public override void _Draw()
    {
        var rect=ArenaRect;
        var center=rect.GetCenter();
        var t=Mathf.Clamp((float)_time/1.25f,0,1);
        var ease=1-Mathf.Pow(1-t,3);
        var gold=new Color("dcb36aff");
        if (!_open)
        {
            var radius=Mathf.Min(rect.Size.X,rect.Size.Y)*.30f*ease;
            for(var ring=0;ring<3;ring++)
                DrawArc(center,radius+ring*13,(float)_time*(ring%2==0?1:-1),
                    (float)_time*(ring%2==0?1:-1)+Mathf.Tau*.83f,96,
                    new Color(gold, .5f-ring*.12f),2,true);
            for(var i=0;i<12;i++)
            {
                var angle=i*Mathf.Tau/12+(float)_time*.4f;
                var point=center+new Vector2(Mathf.Cos(angle),Mathf.Sin(angle))*(radius+26);
                DrawLine(point,center+(point-center)*1.045f,new Color(gold,.6f),3,true);
            }
        }
        var growing=new Rect2(center-rect.Size*ease*.5f,rect.Size*ease);
        DrawRect(growing.Grow(7),new Color(gold,.10f),false,8,true);
        DrawRect(growing.Grow(2),new Color(gold,_open?.85f:.35f),false,2,true);
    }
}
