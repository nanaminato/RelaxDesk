using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace RelaxDesk.Core;

public sealed record ConnectionEndpoint(string Address,int Port)
{
    public override string ToString()=>Address.Contains(':')?$"[{Address}]:{Port}":$"{Address}:{Port}";
}
public static class ConnectionHistory
{
    public const int Capacity=20,MaximumBytes=16*1024;
    sealed record Document(int Format,ConnectionEndpoint[] Entries);
    static readonly JsonSerializerOptions Options=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,AllowDuplicateProperties=false};
    public static ConnectionEndpoint Endpoint(string? address,int port)
    {
        if(address is null||address.Length>128||!IPAddress.TryParse(address,out var ip)||ip.Equals(IPAddress.Any)||ip.Equals(IPAddress.IPv6Any)||port is <1 or >65535)throw new InvalidDataException("Invalid history endpoint");
        return new(ip.ToString(),port);
    }
    static ConnectionEndpoint[] Validate(ConnectionEndpoint[] entries)
    {
        if(entries.Length>Capacity)throw new InvalidDataException("History limit exceeded");
        var result=entries.Select(entry=>entry is null?throw new InvalidDataException("Null history endpoint"):Endpoint(entry.Address,entry.Port)).ToArray();
        if(result.Distinct().Count()!=result.Length)throw new InvalidDataException("Duplicate history endpoints");return result;
    }
    public static ConnectionEndpoint[] Decode(byte[] bytes)
    {
        if(bytes.Length is <2 or >MaximumBytes)throw new InvalidDataException("History document limit");
        var document=JsonSerializer.Deserialize<Document>(bytes,Options);
        if(document is null||document.Format!=1||document.Entries is null)throw new InvalidDataException("Invalid history format");return Validate(document.Entries);
    }
    public static byte[] Encode(ConnectionEndpoint[] entries)=>JsonSerializer.SerializeToUtf8Bytes(new Document(1,Validate(entries)),Options);
    public static ConnectionEndpoint[] Remember(ConnectionEndpoint[] entries,string address,int port)
    {
        var selected=Endpoint(address,port);return new[]{selected}.Concat(Validate(entries).Where(entry=>entry!=selected)).Take(Capacity).ToArray();
    }
}
