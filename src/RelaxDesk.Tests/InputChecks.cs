using RelaxDesk.Core;

static class InputChecks
{
    public static async Task<int> Run()
    {
        int count=0;
        void Check(bool value){if(!value)throw new IOException($"Input contract {count+1} failed");count++;}
        var samples=new RemoteInput[]{new("key",VirtualKey:0xA3,ScanCode:29,Extended:true),new("move",X:65535),new("button",Button:5),new("wheel",WheelX:-17,WheelY:23),new("text",Text:"中文 日本語 😀\r\n\t"),new("release")};
        foreach(var value in samples)
        {
            var packet=InputProtocol.Encode(value,1);Check(InputProtocol.ValidPacket(packet));
            using var stream=new MemoryStream();await Wire.Write(stream,packet);stream.Position=0;Check(await Wire.Read(stream)==packet);
            var gate=new AccessGate();Check(!gate.Accept(packet));gate.Approve();Check(!gate.Accept(packet));gate.Approve(inputVersion:2);Check(gate.Accept(packet));Check(!gate.Accept(packet));
            Check(!gate.Accept(packet with{Sequence=2,Text="unrelated"}));Check(!gate.Accept(packet with{Sequence=2,InputVersion=2}));
            Check(gate.Accept(new("ping-v2",2))==false);Check(gate.AcceptControl(new("ping-v2",2)));Check(!gate.Accept(packet with{Sequence=2}));
            gate.Revoke();int injections=0;Check(!gate.TryExecuteInput(packet with{Sequence=3},()=>injections++));Check(injections==0);
            Check(!packet.ToString().Contains("日本語")&&!value.ToString().Contains("日本語"));
        }
        foreach(var value in new RemoteInput[]{new("key"),new("key",VirtualKey:255),new("key",VirtualKey:0xE7),new("key",VirtualKey:65,ScanCode:256),new("key",VirtualKey:65,RepeatCount:33),new("key",VirtualKey:65,Up:true,Repeat:true),new("key",VirtualKey:65,Up:true,RepeatCount:2),new("move",X:-1),new("move",Y:65536),new("move",Display:1),new("button",Button:6),new("button",Button:1,X:1),new("wheel"),new("wheel",WheelX:32768),new("wheel",WheelY:-32769),new("text",Text:"\0"),new("text",Text:"\uD800"),new("text",Text:new string('x',1025)),new("text",Text:"\u001B"),new("release",Up:true),new("unknown")})Check(!InputProtocol.Valid(value));
        var repeats=InputProtocol.KeyMessage(65,(1L<<16)|65,false);Check(repeats.Select(r=>r.RepeatCount).SequenceEqual(new[]{32,32,1}));Check(!repeats[0].Repeat&&repeats[1].Repeat);
        Check(InputProtocol.KeyMessage(0x11,(29L<<16)|(1L<<24)|1,false)[0].VirtualKey==0xA3);
        Check(InputProtocol.KeyMessage(0x10,(54L<<16)|1,false)[0].VirtualKey==0xA1);
        Check(InputProtocol.KeyMessage(0x12,(56L<<16)|1,false)[0].VirtualKey==0xA4);
        Check(InputProtocol.KeyMessage(0x13,(69L<<16)|1,false)[0].ScanCode==0);
        Check(InputProtocol.KeyMessage(65,(1L<<30)|5,true)[0] is{Up:true,Repeat:false,RepeatCount:1});
        try{InputProtocol.KeyMessage(65,1025,false);throw new IOException("Unbounded repeats");}catch(InvalidDataException){count++;}
        var pressed=new PressedRemoteInputs();foreach(var key in repeats)pressed.Update(key);Check(pressed.Count==1);pressed.Update(new("button",Button:5));Check(pressed.ReleaseButtons() is[{Button:5,Up:true}]);Check(pressed.Count==1);Check(pressed.ReleaseAll() is[{Up:true,RepeatCount:1}]);Check(pressed.Count==0);
        var queue=new OutgoingControlQueue(5);Check(queue.TryWrite(new("input-v2",Input:samples[1])));Check(queue.TryWrite(new("input-v2",Input:new("move",X:9))));Check(queue.Depth==1);
        Console.WriteLine($"PASS {count} extended input protocol/state contracts; no Windows input");return count;
    }
}
