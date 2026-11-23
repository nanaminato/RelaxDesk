using System.Text;
using RelaxDesk.Core;
internal static class ConnectionHistoryChecks
{
    internal static int Run()
    {
        int count=0;void Check(bool value){if(!value)throw new Exception("History contract");count++;}
        void Reject(Action action){bool failed=false;try{action();}catch(Exception e)when(e is InvalidDataException or System.Text.Json.JsonException){failed=true;}Check(failed);}
        ConnectionEndpoint[] entries=[];for(int i=0;i<25;i++)entries=ConnectionHistory.Remember(entries,"192.0.2."+i,48200);
        Check(entries.Length==20&&entries[0].Address=="192.0.2.24"&&entries[^1].Address=="192.0.2.5");
        entries=ConnectionHistory.Remember(entries,"192.0.2.10",48200);Check(entries.Length==20&&entries[0].Address=="192.0.2.10"&&entries.Distinct().Count()==20);
        var ipv6=ConnectionHistory.Endpoint("2001:0DB8:0000::1",443);Check(ipv6.Address=="2001:db8::1"&&ipv6.ToString()=="[2001:db8::1]:443");
        Check(ConnectionHistory.Decode(ConnectionHistory.Encode(entries)).SequenceEqual(entries));
        Check(ConnectionHistory.Decode(ConnectionHistory.Encode([])).Length==0);
        foreach(string? address in new string?[]{null,"","password=private","example.com","0.0.0.0","::",new('A',129)})Reject(()=>ConnectionHistory.Endpoint(address,1));
        foreach(int port in new[]{-1,0,65536})Reject(()=>ConnectionHistory.Endpoint("127.0.0.1",port));
        foreach(string json in new[]{"null","{}","{\"Format\":2,\"Entries\":[]}","{\"Format\":1,\"Entries\":null}","{\"Format\":1,\"Entries\":[null]}","{\"Format\":1,\"Entries\":[],\"Token\":\"secret\"}","{\"Format\":1,\"Format\":1,\"Entries\":[]}","{\"Format\":1,\"Entries\":[{\"Address\":\"127.0.0.1\",\"Port\":1,\"Pin\":\"secret\"}]}"})Reject(()=>ConnectionHistory.Decode(Encoding.UTF8.GetBytes(json)));
        Reject(()=>ConnectionHistory.Decode(new byte[ConnectionHistory.MaximumBytes+1]));Reject(()=>ConnectionHistory.Encode(Enumerable.Repeat(ipv6,21).ToArray()));Reject(()=>ConnectionHistory.Encode([ipv6,ipv6]));
        Check(!Encoding.UTF8.GetString(ConnectionHistory.Encode(entries)).Contains("Token"));return count;
    }
}
