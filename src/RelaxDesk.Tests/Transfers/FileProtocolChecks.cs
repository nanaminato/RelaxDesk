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
        foreach (long rate in new long[] { -1, 1, 65535, 1024L * 1024 * 1024 + 1 })
        {
            try { _ = new FileTransferClient(Stream.Null, default, rate); throw new Exception("Invalid rate accepted"); }
            catch (ArgumentOutOfRangeException) { count++; }
        }
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
        string resumeToken=new string('A',64),prefix=new string('B',64);
        foreach(var resumeMessage in new[]{new TransferMessage("upload",id,1,"scope/file",Length:large,Resumable:true,ResumeToken:resumeToken),new("download",id,1,"scope/file",Offset:large,Resumable:true,ResumeToken:resumeToken,Hash:prefix),new("ready",id,1,Length:large,Offset:large,ResumeToken:resumeToken,Hash:prefix,Completed:true),new("committed",id,1),new("forget",id,1,ResumeToken:resumeToken)})
        {using var wire=new MemoryStream();await FileDataProtocol.Write(wire,resumeMessage,default);wire.Position=0;var roundtrip=await FileDataProtocol.Read(wire,default);Check(roundtrip==resumeMessage&&!roundtrip.ToString().Contains(resumeToken)&&!roundtrip.ToString().Contains("scope/file"));}
        Reject(()=>FileDataProtocol.Validate(new("forget",id,1)));
        foreach(var invalidResume in new[]{new TransferMessage("upload",id,1,"file",Resumable:true,ResumeToken:"short"),new("upload",id,1,"file",ResumeToken:resumeToken),new("download",id,1,"file",Resumable:true,ResumeToken:resumeToken),new("ready",id,1,ResumeToken:resumeToken),new("ready",id,1,Length:1,Offset:2,ResumeToken:resumeToken,Hash:prefix),new("ready",id,1,Completed:true),new("ping",id,1,Resumable:true),new("chunk",id,1,Data:[1],ResumeToken:resumeToken),new("committed",id,1,Offset:1),new("upload",id,1,"file",Offset:1)})Reject(()=>FileDataProtocol.Validate(invalidResume));
        foreach(var invalid in new[]{valid with{Sequence=0},valid with{Id="bad"},valid with{Offset=-1},valid with{Data=[]},valid with{Data=new byte[TransferLimits.ChunkBytes+1]},valid with{Path="x"},valid with{Hash=new string('A',64)},valid with{Length=-1},valid with{Kind="unknown"},valid with{Entries=[]},valid with{Overwrite=true}})Reject(()=>FileDataProtocol.Validate(invalid));
        byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,FileDataProtocol.EnvelopeLimit+1);
        try{await FileDataProtocol.Read(new MemoryStream(header),default);throw new Exception("Oversize file data accepted");}catch(InvalidDataException){count++;}
        Check(SessionCapabilities.Grant([SessionCapabilities.FileTransfer],false,false).Length==0);
        Check(SessionCapabilities.Grant([SessionCapabilities.FileTransfer],false,true) is [SessionCapabilities.FileTransfer]);
        var gate=new AccessGate();gate.Approve([SessionCapabilities.FileTransfer]);Check(gate.Allows(SessionCapabilities.FileTransfer)&&!gate.Allows(SessionCapabilities.ClipboardText));
        Check(!gate.AcceptControl(new("file-state-v2",1,Code:1)));Check(!gate.AcceptControl(new("file-state-v2",1,Features:[SessionCapabilities.ClipboardText])));
        Check(!gate.AcceptControl(new("file-state-v2",1,FileResume:true)));Check(!gate.AcceptControl(new("file-state-v2",1,FileScope:Guid.NewGuid().ToString("N"))));
        Check(gate.AcceptControl(new("file-state-v2",1)));gate.RevokeFeature(SessionCapabilities.FileTransfer);Check(gate.AcceptControl(new("file-state-v2",2)));
        Check(gate.Accept(new("input",3,Code:65)));gate.Revoke();Check(!gate.Allows(SessionCapabilities.FileTransfer));Check(!gate.AcceptControl(new("file-state-v2",4)));
        return count;
    }
}
