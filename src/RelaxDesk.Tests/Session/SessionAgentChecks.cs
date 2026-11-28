using System.Buffers.Binary;
using System.Text;
using RelaxDesk.Core;

internal static class SessionAgentChecks
{
    public static async Task<int> Run()
    {
        int count=0;void Check(bool condition){if(!condition)throw new IOException($"Agent contract {count+1} failed");count++;}
        void Reject(Action action){try{action();throw new Exception("Agent policy accepted invalid operation");}catch(InvalidDataException){count++;}}
        string nonce=new('A',32),other=new('B',32);var who=new SessionAgentIdentity("approved",2,123);
        var registry=new SessionAgentRegistry("approved");var registration=new SessionAgentPacket("register",nonce);
        foreach(var identity in new[]{who with{User="other"},who with{SessionId=0},who with{ProcessId=0},who with{ProcessId=-1}})Reject(()=>registry.Acquire(identity,registration));
        foreach(var packet in new[]{registration with{Kind="heartbeat"},registration with{Nonce="bad"},registration with{Sequence=1},registration with{Lease=other},registration with{Version=2}})Reject(()=>registry.Acquire(who,packet));
        Reject(()=>registry.Acquire(who,new("input-register",nonce)));
        Reject(()=>registry.Acquire(who,new("input-started",nonce,123,other)));
        var first=registry.Acquire(who,registration);Check(SessionAgentProtocol.Identifier(first.Id));Check(first.Approval==new SessionAgentPacket("registered",nonce,Lease:first.Id));
        Reject(()=>registry.Acquire(who with{ProcessId=999},registration));
        var heartbeat=new SessionAgentPacket("heartbeat",nonce,1,first.Id);
        foreach(var packet in new[]{heartbeat with{Nonce=other},heartbeat with{Lease=other},heartbeat with{Sequence=0},heartbeat with{Sequence=2},heartbeat with{Kind="alive"},heartbeat with{Version=0}})Reject(()=>first.Accept(packet));
        Check(first.Accept(heartbeat)==new SessionAgentPacket("alive",nonce,1,first.Id));Reject(()=>first.Accept(heartbeat));
        Check(first.Accept(heartbeat with{Sequence=2}).Sequence==2);
        registry.Invalidate(999);Check(!first.Revoked.IsCompleted);registry.Invalidate(2);Check(first.Revoked.IsCompleted);
        Reject(()=>first.Accept(heartbeat with{Sequence=3}));Reject(()=>registry.Acquire(who,registration));registry.Release(first);
        var next=registry.Acquire(who,registration);Check(next.Id!=first.Id);registry.Release(first);Check(!next.Revoked.IsCompleted);
        Check(next.Accept(new("heartbeat",nonce,1,next.Id)).Sequence==1);
        registry.Release(next);
        var leases=Enumerable.Range(1,4).Select(i=>registry.Acquire(who with{SessionId=(uint)i},registration)).ToArray();
        Reject(()=>registry.Acquire(who with{SessionId=5},registration));registry.Stop();Check(leases.All(item=>item.Revoked.IsCompleted));
        foreach(var item in leases)registry.Release(item);Reject(()=>registry.Acquire(who,registration));
        var raced=new SessionAgentRegistry("approved");int acquired=0;var attempts=await Task.WhenAll(Enumerable.Range(0,16).Select(_=>Task.Run(()=>{try{var lease=raced.Acquire(who,registration);Interlocked.Increment(ref acquired);return lease;}catch(InvalidDataException){return null;}})));
        Check(acquired==1);raced.Release(attempts.Single(item=>item is not null)!);
        foreach(var packet in new[]{registration,first.Approval,heartbeat,new SessionAgentPacket("alive",nonce,1,first.Id),new SessionAgentPacket("stopped",nonce,Lease:first.Id),new SessionAgentPacket("input-register",nonce),new SessionAgentPacket("input-started",nonce,123,first.Id)})
        {using var stream=new MemoryStream();await SessionAgentProtocol.Write(stream,packet,CancellationToken.None);stream.Position=0;Check(await SessionAgentProtocol.Read(stream,CancellationToken.None)==packet);}
        async Task RejectBytes(byte[] bytes)
        {try{await SessionAgentProtocol.Read(new MemoryStream(bytes),CancellationToken.None);throw new Exception("Invalid agent framing accepted");}catch(Exception error)when(error is IOException or InvalidDataException or System.Text.Json.JsonException){count++;}}
        foreach(int size in new[]{-1,0,1,SessionAgentProtocol.Limit+1,int.MaxValue}){byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,size);await RejectBytes(header);}
        await RejectBytes([]);await RejectBytes([1,2,3]);await RejectBytes([4,0,0,0,1]);
        foreach(string json in new[]{"[]","null","{}",$"{{\"kind\":\"register\",\"nonce\":\"{nonce}\",\"sequence\":0,\"lease\":null,\"version\":1,\"extra\":true}}",$"{{\"kind\":\"register\",\"kind\":\"register\",\"nonce\":\"{nonce}\",\"sequence\":0,\"lease\":null,\"version\":1}}",$"{{\"kind\":\"register\",\"nonce\":\"{nonce}\",\"sequence\":0,\"lease\":null,\"version\":2}}",$"{{\"kind\":\"register\",\"nonce\":\"{nonce}\",\"sequence\":-1,\"lease\":null,\"version\":1}}"})
        {byte[] body=Encoding.UTF8.GetBytes(json),header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,body.Length);await RejectBytes(header.Concat(body).ToArray());}
        Console.WriteLine($"PASS {count} session-agent framing/identity/lease/replay/revocation/cap/concurrency checks; memory only, no service or Windows permissions");return count;
    }
}
