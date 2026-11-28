using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelaxDesk.Core;

public sealed record SessionAgentPacket(string Kind,string Nonce,long Sequence=0,string? Lease=null,int Version=1);

public static class SessionAgentProtocol
{
    public const int Limit=2048;
    public static readonly TimeSpan HeartbeatInterval=TimeSpan.FromSeconds(1),MessageTimeout=TimeSpan.FromSeconds(5);
    static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    public static bool Identifier(string? value)=>value is {Length:32}&&value.All(char.IsAsciiHexDigit);
    public static void Validate(SessionAgentPacket packet)
    {
        if(packet.Version!=1||!Identifier(packet.Nonce)||packet.Sequence<0||packet.Lease is not null&&!Identifier(packet.Lease)||packet.Kind is not ("register" or "registered" or "heartbeat" or "alive" or "stopped" or "input-register" or "input-started"))throw new InvalidDataException("Invalid session-agent packet");
    }
    public static async Task Write(Stream stream,SessionAgentPacket packet,CancellationToken ct)
    {
        Validate(packet);byte[] body=JsonSerializer.SerializeToUtf8Bytes(packet,Options);
        if(body.Length>Limit)throw new InvalidDataException("Session-agent packet too large");
        byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,body.Length);
        await stream.WriteAsync(header,ct);await stream.WriteAsync(body,ct);await stream.FlushAsync(ct);
    }
    public static async Task<SessionAgentPacket> Read(Stream stream,CancellationToken ct)
    {
        byte[] header=new byte[4];await stream.ReadExactlyAsync(header,ct);int length=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(length is <2 or >Limit)throw new InvalidDataException("Invalid session-agent frame length");
        byte[] body=new byte[length];await stream.ReadExactlyAsync(body,ct);
        using var json=JsonDocument.Parse(body,new(){MaxDepth=4});
        if(json.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Session-agent object required");
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var property in json.RootElement.EnumerateObject())if(!names.Add(property.Name))throw new InvalidDataException("Duplicate session-agent member");
        if(names.Count!=5||!names.SetEquals(["kind","nonce","sequence","lease","version"]))throw new InvalidDataException("Noncanonical session-agent schema");
        var packet=JsonSerializer.Deserialize<SessionAgentPacket>(body,Options)??throw new InvalidDataException("Missing session-agent packet");Validate(packet);return packet;
    }
}

public sealed record SessionAgentIdentity(string User,uint SessionId,int ProcessId);

public sealed class SessionAgentLease
{
    readonly object sync=new();
    readonly TaskCompletionSource revoked=new(TaskCreationOptions.RunContinuationsAsynchronously);
    long sequence;
    internal SessionAgentLease(SessionAgentIdentity identity,string nonce){Identity=identity;Nonce=nonce;Id=Guid.NewGuid().ToString("N");}
    public SessionAgentIdentity Identity{get;}
    public string Nonce{get;}
    public string Id{get;}
    public Task Revoked=>revoked.Task;
    public SessionAgentPacket Approval=>new("registered",Nonce,Lease:Id);
    public SessionAgentPacket Accept(SessionAgentPacket packet)
    {
        lock(sync)
        {
            if(revoked.Task.IsCompleted||sequence==long.MaxValue||packet!=new SessionAgentPacket("heartbeat",Nonce,sequence+1,Id))throw new InvalidDataException("Stale or unauthorized session-agent heartbeat");
            sequence++;return new("alive",Nonce,sequence,Id);
        }
    }
    internal void Revoke(){lock(sync)revoked.TrySetResult();}
}

/// <summary>Leases coordinate lifetimes only. They never grant screen, input, clipboard or file access.</summary>
public sealed class SessionAgentRegistry(string approvedUser)
{
    readonly object sync=new();
    readonly Dictionary<uint,SessionAgentLease> sessions=[];
    bool stopped;
    public SessionAgentLease Acquire(SessionAgentIdentity identity,SessionAgentPacket packet)
    {
        if(string.IsNullOrWhiteSpace(approvedUser)||identity.User!=approvedUser||identity.SessionId==0||identity.ProcessId<=0||!SessionAgentProtocol.Identifier(packet.Nonce)||packet!=new SessionAgentPacket("register",packet.Nonce))throw new InvalidDataException("Session-agent identity/registration rejected");
        lock(sync)
        {
            if(stopped||sessions.Count>=4||sessions.ContainsKey(identity.SessionId))throw new InvalidDataException("Session-agent slot occupied or broker stopped");
            var lease=new SessionAgentLease(identity,packet.Nonce);sessions.Add(identity.SessionId,lease);return lease;
        }
    }
    public void Release(SessionAgentLease lease)
    {
        lock(sync){lease.Revoke();if(sessions.TryGetValue(lease.Identity.SessionId,out var current)&&ReferenceEquals(current,lease))sessions.Remove(lease.Identity.SessionId);}
    }
    public void Invalidate(uint sessionId){lock(sync)if(sessions.TryGetValue(sessionId,out var lease))lease.Revoke();}
    public void Stop(){lock(sync){stopped=true;foreach(var lease in sessions.Values)lease.Revoke();}}
}
