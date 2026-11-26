using RelaxDesk.Core;

static class InputChecks
{
    sealed class QueueClock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }
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
        var queue=new OutgoingControlQueue(5);Check(queue.TryWrite(new("input-v2",Input:samples[1])));Check(queue.TryWrite(new("input-v2",Input:new("move",X:9))));Check(queue.Depth==1 && queue.CoalescedMoves==1);
        var reused = new OutgoingControlQueue(3);
        var move = new Packet("input-v2", 1, Input: new("move", X: 1));
        var click = new Packet("input-v2", 2, Input: new("button", Button: 1));
        var later = move with { Sequence = 3 };
        var latest = move with { Sequence = 4 };
        Check(reused.TryWrite(move) && reused.TryWrite(click) && reused.TryRead(out var first) && first == move);
        Check(reused.TryWrite(later) && reused.TryWrite(latest) && reused.Depth == 2);
        Check(reused.TryRead(out var second) && second == click && reused.TryRead(out var last) && last == latest && reused.Depth == 0);
        var stalledWriter = new OutgoingControlQueue(6);
        long sequence = 0;
        Packet Enqueue(RemoteInput input)
        {
            var packet = new Packet("input-v2", ++sequence, Input: input);
            if (!stalledWriter.TryWrite(packet)) throw new IOException("Pointer burst crowded out reliable input");
            return packet;
        }
        for (int i = 0; i < 999; i++) Enqueue(new("move", X: i));
        var lastBeforeClick = Enqueue(new("move", X: 999));
        var buttonDown = Enqueue(new("button", Button: 1));
        for (int i = 0; i < 999; i++) Enqueue(new("move", X: i));
        var lastBeforeRelease = Enqueue(new("move", X: 999));
        var buttonUp = Enqueue(new("button", Button: 1, Up: true));
        var keyDown = Enqueue(new("key", VirtualKey: 65));
        var keyUp = Enqueue(new("key", VirtualKey: 65, Up: true));
        Check(stalledWriter.Depth == 6 && stalledWriter.CoalescedMoves == 1998);
        foreach (var expected in new[] { lastBeforeClick, buttonDown, lastBeforeRelease, buttonUp, keyDown, keyUp })
            Check(stalledWriter.TryRead(out var actual) && actual == expected);
        Check(stalledWriter.Depth == 0);
        var acrossDisplays = new OutgoingControlQueue(4);
        var oldDisplayMove = move with { DisplayGeneration = 1 };
        var newDisplayMove = later with { DisplayGeneration = 2 };
        var newDisplayClick = click with { Sequence = 5, DisplayGeneration = 2 };
        Check(acrossDisplays.TryWrite(oldDisplayMove) && acrossDisplays.TryWrite(newDisplayMove));
        Check(acrossDisplays.Depth == 2 && acrossDisplays.CoalescedMoves == 0);
        Check(acrossDisplays.TryWrite(newDisplayClick) && acrossDisplays.TryWrite(keyUp with { Sequence = 6, DisplayGeneration = 2 }));
        Check(!acrossDisplays.TryWrite(latest with { Sequence = 7, DisplayGeneration = 2 }));
        foreach (var expected in new[] { oldDisplayMove, newDisplayMove, newDisplayClick, keyUp with { Sequence = 6, DisplayGeneration = 2 } })
            Check(acrossDisplays.TryRead(out var actual) && actual == expected);
        long beforeQueue = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++)
            if (!reused.TryWrite(click) || !reused.TryRead(out var next) || !ReferenceEquals(next, click))
                throw new IOException("Control queue lost a prebuilt packet");
        long queueBytes = GC.GetAllocatedBytesForCurrentThread() - beforeQueue;
        Check(queueBytes == 0);
        Console.WriteLine($"PASS fixed control queue storage: 1024 prebuilt write/read pairs, queue_allocated_bytes={queueBytes}");
        var clock = new QueueClock();
        var timedQueue = new OutgoingControlQueue(clock: clock);
        Check(timedQueue.TryWrite(move));
        clock.Now = 15;
        Check(timedQueue.TryWrite(latest));
        clock.Now = 20;
        double delay = -1;
        await foreach (var timed in timedQueue.ReadAllAsync(CancellationToken.None, (_, elapsed) => delay = elapsed))
        {
            Check(timed == latest && delay == 5);
            break;
        }
        Check(PointerPosition.TryNormalize(0,0,640,360,1920,1080,false,out int nx,out int ny)&&nx==0&&ny==0);
        Check(PointerPosition.TryNormalize(639,359,640,360,1920,1080,false,out nx,out ny)&&nx==65535&&ny==65535);
        Check(!PointerPosition.TryNormalize(640,360,640,360,1920,1080,false,out _,out _));
        Check(!PointerPosition.TryNormalize(100,5,800,600,1920,1080,false,out _,out _));
        Check(PointerPosition.TryNormalize(0,75,800,600,1920,1080,false,out nx,out ny)&&nx==0&&ny==0);
        Check(PointerPosition.TryNormalize(799,524,800,600,1920,1080,false,out nx,out ny)&&nx==65535&&ny==65535);
        Check(PointerPosition.TryNormalize(-10,999,800,600,1920,1080,true,out nx,out ny)&&nx==0&&ny==65535);
        Check(!PointerPosition.TryNormalize(double.NaN,0,800,600,1920,1080,true,out _,out _));
        Check(!PointerPosition.TryNormalize(0,0,0,600,1920,1080,false,out _,out _));
        Check(!PointerPosition.TryNormalize(0,0,800,600,0,1080,false,out _,out _));
        Console.WriteLine($"PASS {count} extended input protocol/state/letterbox contracts; no Windows input");return count;
    }
}
