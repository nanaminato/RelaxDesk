using RelaxDesk.Mobile.Media;
using RelaxDesk.Core;

if (args is ["--serve-probe", var syntheticAsset])
{
    await AndroidProbeFixtureHost.Run(syntheticAsset);
    return;
}

int checks = 0;
void Check(bool pass, string message)
{
    if (!pass) throw new InvalidOperationException(message);
    checks++;
}
void Reject(Action work, string message)
{
    try { work(); }
    catch (InvalidDataException) { checks++; return; }
    throw new InvalidOperationException(message);
}

byte[] bootstrap = [0,0,0,1,0x67,0x42,0,0,1,0x68,0xce,0,0,0,1,0x65,0x88];
byte[] delta = [0,0,1,0x41,0x9a];
Check(AvcAccessUnit.HasBootstrap(bootstrap), "SPS/PPS/IDR across mixed start-code lengths");
Check(AvcAccessUnit.TryGetCodecSpecificData(bootstrap, out var sps, out var pps) &&
      sps.SequenceEqual(new byte[]{0,0,0,1,0x67,0x42}) &&
      pps.SequenceEqual(new byte[]{0,0,0,1,0x68,0xce}), "CSD uses exact parameter sets with four-byte prefixes");
Check(!AvcAccessUnit.HasBootstrap([0x77, ..bootstrap]), "Junk preceding first start code");
Check(!AvcAccessUnit.HasBootstrap(delta), "Dependent slice cannot bootstrap");
Check(!AvcAccessUnit.HasBootstrap(bootstrap.AsSpan(0, bootstrap.Length - 1)), "Truncated IDR");
Check(!AvcAccessUnit.HasBootstrap([0,0,0,14,0x67,0x42,0x68,0x65]), "AVCC not accepted by Annex B gate");
Check(!AvcAccessUnit.HasBootstrap([0,0,1,0x65,0x88]), "IDR without parameter sets");
Check(!AvcAccessUnit.HasBootstrap([0,0,1,0xe7,0x42,0,0,1,0x68,0xce,0,0,1,0x65,0x88]), "Forbidden NAL bit");

MediaFrame Frame(long n, bool key = false) => new(MediaCodec.H264,n,0,640,360,key,false,0,0,[],key ? bootstrap : delta,1,1);
var gate = new AvcFrameGate();
Check(gate.Accept(Frame(1)) == FrameAction.Drop, "No decode before bootstrap");
Check(gate.Accept(Frame(2, true)) == FrameAction.Reconfigure, "First config");
Check(gate.Accept(Frame(3)) == FrameAction.Decode, "Continuous delta");
Check(gate.Accept(Frame(5)) == FrameAction.Drop, "Gap discards deltas");
Check(gate.Accept(Frame(6, true) with { Data = delta }) == FrameAction.Drop, "Mislabelled keyframe");
Check(gate.Accept(Frame(7, true)) == FrameAction.Reconfigure, "Gap recovery");
Check(gate.Accept(Frame(8) with { VideoGeneration = 2 }) == FrameAction.Drop, "Video change waits for bootstrap");
Check(gate.Accept(Frame(9, true) with { VideoGeneration = 2 }) == FrameAction.Reconfigure, "Video change config");
Check(gate.Accept(Frame(10) with { VideoGeneration = 2, DisplayGeneration = 2 }) == FrameAction.Drop, "Display change waits");
Check(gate.Accept(Frame(11, true) with { VideoGeneration = 2, DisplayGeneration = 2 }) == FrameAction.Reconfigure, "Display change config");
Reject(() => gate.Accept(Frame(11)), "Replay accepted");
Reject(() => gate.Accept(Frame(12)), "Old generation accepted");
Reject(() => gate.Accept(Frame(12) with { Codec = MediaCodec.DeflateBgra }), "Deflate accepted");
Check(gate.Accept(Frame(12, true) with { VideoGeneration = 2, DisplayGeneration = 2, Width = 800 }) == FrameAction.Reconfigure, "Size change config");

using var stream = new MemoryStream();
await MediaProtocol.Write(stream, Frame(1, true));
await MediaProtocol.Write(stream, Frame(2));
stream.Position = 0;
var wireGate = new AvcFrameGate();
var channel = new TcpTlsVideoChannel(stream, true, true);
Check(wireGate.Accept(await channel.Read(default)) == FrameAction.Reconfigure, "Core wire -> bootstrap");
Check(wireGate.Accept(await channel.Read(default)) == FrameAction.Decode, "Core wire -> delta");
Check(stream.Position == stream.Length, "Exact frame boundaries");

if (args is not ([] or [_] or [_, "--idle"]))
    throw new ArgumentException("Usage: RelaxDesk.Android.Tests [probe.rdm2 [--idle]] | --serve-probe probe.rdm2");
if (args is [var file, ..])
{
    using var fixture = File.OpenRead(file);
    var reader = new TcpTlsVideoChannel(fixture, true, true);
    var fixtureGate = new AvcFrameGate();
    int count = 0;
    while (fixture.Position < fixture.Length)
    {
        var action = fixtureGate.Accept(await reader.Read(default));
        Check(action == (count == 0 ? FrameAction.Reconfigure : FrameAction.Decode), "Generated fixture GOP");
        count++;
    }
    Check(count == 48, "Generated fixture frame count");
}
Console.WriteLine($"Android media contracts: {checks}");
Console.WriteLine($"Android device proof contracts: {DeviceProofTests.Run()}");
if (args is [var fixturePath, ..])
    Console.WriteLine($"Android pinned TLS loopback contracts: {await SessionLoopbackTests.Run(fixturePath, includeIdle: args.Length == 2)}");
