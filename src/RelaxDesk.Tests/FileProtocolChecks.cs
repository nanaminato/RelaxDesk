using System.Buffers.Binary;
using RelaxDesk.Core;

internal static class FileProtocolChecks
{
    public static async Task<int> Run()
    {
        int count = 0;
        void Check(bool condition) { if (!condition) throw new InvalidDataException($"File protocol assertion {count + 1}"); count++; }
        void Reject(Action action) { try { action(); throw new Exception("Invalid file operation accepted"); } catch (InvalidDataException) { count++; } }
        foreach (string path in new[] { "../x", "..\\x", "C:\\x", "\\\\server\\x", "/root", "a//b", "a/./b", "a/../b", "a:stream", "a\0b", "CON", "con.txt", "COM1.log", "LPT².txt", "NUL", "file.", "file ", ".relaxdesk-internal", "a?b", "a|b", new string('a', 256) }) Reject(() => TransferPaths.Relative(path));
        Check(TransferPaths.Relative("中文\\日本語/file.txt") == "中文/日本語/file.txt");
        Check(TransferPaths.Relative("", true) == ""); Reject(() => TransferPaths.Relative(""));
        long large = 4L * 1024 * 1024 * 1024 + 17;
        TransferLimits.ValidateChunk(large + 100, large, large, 100); count++;
        Reject(() => TransferLimits.ValidateChunk(large + 100, large, large, 101));
        Reject(() => TransferLimits.ValidateChunk(large + 100, large, large - 1, 100));
        Reject(() => TransferLimits.ValidateChunk(large, large, large, 1));
        Reject(() => TransferLimits.ValidateChunk(-1, 0, 0, 1));
        Reject(() => TransferLimits.ValidateChunk(TransferLimits.MaximumFileBytes + 1, 0, 0, 1));
        string id = Guid.NewGuid().ToString("N");
        foreach (var message in new[] { new TransferMessage("upload", id, 1, "目录/file", Length: large), new("download",id,1,"目录/file"),new("list",id,1,""),new("mkdir",id,1,"empty"),new("chunk",id,1,Offset:large,Data:[1]),new("complete",id,1,Hash:new string('A',64)),new("result",id,1,Error:"exists"),new("entries",id,1,Entries:[new("目录/file",false,large)]),new("ready",id,1,Length:large),new("progress",id,1,Offset:large),new("cancel",id,1),new("ping",id,1),new("pong",id,1) })
        {
            using var bytes=new MemoryStream();await FileDataProtocol.Write(bytes,message,default);bytes.Position=0;
            var read=await FileDataProtocol.Read(bytes,default);Check(read.Kind==message.Kind&&read.Offset==message.Offset&&read.Length==message.Length&&read.Path==message.Path);
        }
        var valid=new TransferMessage("chunk",id,1,Offset:large,Data:[1]);
        foreach(var invalid in new[]{valid with{Sequence=0},valid with{Id="bad"},valid with{Offset=-1},valid with{Data=[]},valid with{Data=new byte[TransferLimits.ChunkBytes+1]},valid with{Path="x"},valid with{Hash=new string('A',64)},valid with{Length=-1},valid with{Kind="unknown"},valid with{Entries=[]},valid with{Overwrite=true}})Reject(()=>FileDataProtocol.Validate(invalid));
        byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,FileDataProtocol.EnvelopeLimit+1);
        try{await FileDataProtocol.Read(new MemoryStream(header),default);throw new Exception("Oversize file data accepted");}catch(InvalidDataException){count++;}
        Check(SessionCapabilities.Grant([SessionCapabilities.FileTransfer],false,false).Length==0);
        Check(SessionCapabilities.Grant([SessionCapabilities.FileTransfer],false,true) is [SessionCapabilities.FileTransfer]);
        var gate=new AccessGate();gate.Approve([SessionCapabilities.FileTransfer]);Check(gate.Allows(SessionCapabilities.FileTransfer)&&!gate.Allows(SessionCapabilities.ClipboardText));
        Check(!gate.AcceptControl(new("file-state-v2",1,Code:1)));Check(!gate.AcceptControl(new("file-state-v2",1,Features:[SessionCapabilities.ClipboardText])));
        Check(gate.AcceptControl(new("file-state-v2",1)));gate.RevokeFeature(SessionCapabilities.FileTransfer);Check(gate.AcceptControl(new("file-state-v2",2)));
        Check(gate.Accept(new("input",3,Code:65)));gate.Revoke();Check(!gate.Allows(SessionCapabilities.FileTransfer));Check(!gate.AcceptControl(new("file-state-v2",4)));
        return count;
    }
}
