using System.Text;

namespace RelaxDesk.Core;

public sealed record RemoteInput(string Kind,int VirtualKey=0,int ScanCode=0,bool Extended=false,bool Up=false,
    int RepeatCount=1,bool Repeat=false,int X=0,int Y=0,int Button=0,int WheelX=0,int WheelY=0,string? Text=null,int Display=0)
{
    public override string ToString()=>$"RemoteInput kind={Kind} (key/text/position redacted)";
}

public static class InputProtocol
{
    public const int Version=2,MaximumTextUnits=1024,MaximumRepeat=32,MaximumMessageRepeats=1024;
    static readonly UTF8Encoding Utf8=new(false,true);
    public static bool TextValid(string? text)
    {
        if(text is not {Length:>0 and <=MaximumTextUnits}||text.Any(c=>c=='\0'||c<32&&c is not ('\r' or '\n' or '\t')))return false;
        try{return Utf8.GetByteCount(text)<=4*MaximumTextUnits;}catch(EncoderFallbackException){return false;}
    }
    public static bool Valid(RemoteInput? input)
    {
        if(input is null||input.Display!=0)return false;
        return input.Kind switch
        {
            "key"=>input.VirtualKey is >=1 and <=254 and not (0xE5 or 0xE7)&&input.ScanCode is >=0 and <=255&&input.RepeatCount is >=1 and <=MaximumRepeat&&
                (!input.Up||!input.Repeat&&input.RepeatCount==1)&&input==new RemoteInput("key",VirtualKey:input.VirtualKey,ScanCode:input.ScanCode,Extended:input.Extended,Up:input.Up,RepeatCount:input.RepeatCount,Repeat:input.Repeat),
            "move"=>input.X is >=0 and <=65535&&input.Y is >=0 and <=65535&&input==new RemoteInput("move",X:input.X,Y:input.Y),
            "button"=>input.Button is >=1 and <=5&&input==new RemoteInput("button",Button:input.Button,Up:input.Up),
            "wheel"=>(input.WheelX!=0||input.WheelY!=0)&&input.WheelX is >=-32768 and <=32767&&input.WheelY is >=-32768 and <=32767&&input==new RemoteInput("wheel",WheelX:input.WheelX,WheelY:input.WheelY),
            "text"=>TextValid(input.Text)&&input==new RemoteInput("text",Text:input.Text),
            "release"=>input==new RemoteInput("release"),
            _=>false
        };
    }
    public static bool ValidPacket(Packet packet)=>packet.Sequence>0&&Valid(packet.Input)&&packet==new Packet("input-v2",packet.Sequence,Input:packet.Input);
    public static Packet Encode(RemoteInput input,long sequence)
    {var packet=new Packet("input-v2",sequence,Input:input);if(!ValidPacket(packet))throw new InvalidDataException("Invalid extended input");return packet;}
    public static int ResolveKey(int key,int scan,bool extended)=>key switch{0x10=>scan==0x36?0xA1:0xA0,0x11=>extended?0xA3:0xA2,0x12=>extended?0xA5:0xA4,_=>key};
    public static RemoteInput[] KeyMessage(int key,long flags,bool up)
    {
        int scan=(int)((flags>>16)&255);bool extended=(flags&(1L<<24))!=0;key=ResolveKey(key,scan,extended);
        // Pause has an E1 sequence, not the ordinary E0 + make-code encoding.
        if(key==0x13){scan=0;extended=false;}
        int count=up?1:Math.Max(1,(int)(flags&65535));if(count>MaximumMessageRepeats)throw new InvalidDataException("Keyboard repeat backlog exceeds bound");
        var result=new List<RemoteInput>();bool repeat=!up&&(flags&(1L<<30))!=0;
        while(count>0){int part=Math.Min(count,MaximumRepeat);var input=new RemoteInput("key",VirtualKey:key,ScanCode:scan,Extended:extended,Up:up,RepeatCount:part,Repeat:repeat);if(!Valid(input))return [];result.Add(input);count-=part;repeat=true;}
        return result.ToArray();
    }
}

// Viewer state only. Repeats still travel, but release state has one entry per
// physical key/button. No text/composition contents are retained here.
public sealed class PressedRemoteInputs
{
    readonly List<RemoteInput> held=[];
    static bool Same(RemoteInput a,RemoteInput b)=>a.Kind==b.Kind&&(a.Kind=="button"?a.Button==b.Button:a.ScanCode!=0&&b.ScanCode!=0?a.ScanCode==b.ScanCode&&a.Extended==b.Extended:a.ScanCode==b.ScanCode&&a.VirtualKey==b.VirtualKey&&a.Extended==b.Extended);
    public int Count=>held.Count;
    public void Update(RemoteInput input)
    {
        if(input.Kind=="release"){held.Clear();return;}if(input.Kind is not ("key" or "button"))return;
        int index=held.FindIndex(previous=>Same(previous,input));if(input.Up){if(index>=0)held.RemoveAt(index);}else if(index<0)held.Add(input with{Repeat=false,RepeatCount=1});
    }
    public RemoteInput[] ReleaseButtons(){var result=held.Where(input=>input.Kind=="button").Reverse().Select(input=>input with{Up=true}).ToArray();held.RemoveAll(input=>input.Kind=="button");return result;}
    public RemoteInput[] ReleaseAll(){var result=held.AsEnumerable().Reverse().Select(input=>input with{Up=true,Repeat=false,RepeatCount=1}).ToArray();held.Clear();return result;}
}
