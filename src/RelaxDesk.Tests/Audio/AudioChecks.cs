using System.Buffers.Binary;
using RelaxDesk.Core;

internal static class AudioChecks
{
    internal static async Task<int> Run()
    {
        int count=0;void Check(bool result){if(!result)throw new InvalidDataException("Audio contract failed");count++;}
        async Task Reject(byte[] bytes){try{await AudioProtocol.Read(new MemoryStream(bytes));throw new Exception("Malformed audio accepted");}catch(Exception e)when(e is InvalidDataException or EndOfStreamException){count++;}}
        var sample=new AudioFrame(1,100,true,new byte[1920]);BinaryPrimitives.WriteInt16LittleEndian(sample.Data,short.MinValue);BinaryPrimitives.WriteInt16LittleEndian(sample.Data.AsSpan(2),short.MaxValue);
        using var wire=new MemoryStream();await AudioProtocol.Write(wire,sample);byte[] encoded=wire.ToArray();wire.Position=0;var decoded=await AudioProtocol.Read(wire);
        Check(decoded.Sequence==1&&decoded.Timestamp100ns==100&&decoded.Discontinuity&&decoded.Data.SequenceEqual(sample.Data)&&decoded.Frames==480);
        foreach(int offset in new[]{0,4,6,15,23,24,28,32,39}){var bad=(byte[])encoded.Clone();bad[offset]=0xFF;await Reject(bad);}
        for(int length=0;length<AudioProtocol.HeaderSize;length++)await Reject(encoded[..length]);
        await Reject(encoded[..^1]);
        foreach(int bytes in new[]{0,1,3,AudioProtocol.MaximumFrames*4+4})
        {
            try{await AudioProtocol.Write(Stream.Null,new(1,0,false,new byte[bytes]));throw new Exception("Invalid PCM accepted");}catch(InvalidDataException){count++;}
        }
        var half=AudioProtocol.Gain(sample.Data,0.5,false);Check(BinaryPrimitives.ReadInt16LittleEndian(half)==-16384&&BinaryPrimitives.ReadInt16LittleEndian(half.AsSpan(2))==16384);
        Check(AudioProtocol.Gain(sample.Data,1,false).SequenceEqual(sample.Data));Check(AudioProtocol.Gain(sample.Data,1,true).All(value=>value==0));Check(AudioProtocol.Gain(sample.Data,0,false).All(value=>value==0));
        Check(!AudioProtocol.IsSilent(sample.Data)&&AudioProtocol.IsSilent(new byte[1920])&&AudioProtocol.IsSilent([]));
        foreach(double gain in new[]{-1,1.01,double.NaN,double.PositiveInfinity}){try{AudioProtocol.Gain(sample.Data,gain,false);throw new Exception("Invalid gain accepted");}catch(ArgumentException){count++;}}
        var buffer=new AudioJitterBuffer();for(int i=1;i<=100;i++)buffer.Push(sample with{Sequence=i,Timestamp100ns=i,Discontinuity=false});
        Check(buffer.BufferedFrames==9600&&buffer.Dropped==80);Check(buffer.TryTake(out var oldest)&&oldest!.Sequence==81);
        try{buffer.Push(sample);throw new Exception("Audio replay accepted");}catch(InvalidDataException){count++;}
        try{buffer.Push(sample with{Sequence=101,Timestamp100ns=0,Discontinuity=false});throw new Exception("Timestamp regression accepted");}catch(InvalidDataException){count++;}
        buffer.Push(sample with{Sequence=101,Timestamp100ns=0,Discontinuity=true});Check(buffer.BufferedFrames==480);
        Check(buffer.DiscontinuitySequence==101);
        buffer.Flush();Check(buffer.BufferedFrames==0&&!buffer.TryTake(out _));
        Check(buffer.DiscontinuitySequence==101);
        try{buffer.Push(sample with{Sequence=101});throw new Exception("Flush reset replay state");}catch(InvalidDataException){count++;}
        Check(new AudioJitterBuffer().BufferedFrames==0);
        Check(new AudioJitterBuffer().DiscontinuitySequence==0);
        return count;
    }
}
