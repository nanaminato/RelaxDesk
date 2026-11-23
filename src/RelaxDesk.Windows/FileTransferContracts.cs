using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class FileTransferContracts
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateHardLink(string name, string existing, nint security);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputBytes, nint output, uint outputBytes, out uint returned, nint overlapped);
    static void CreateFixtureJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        using var handle = WindowsFileRoot.Open(path, 0x40000000, 0, 3, 0x02200000);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target)), printable = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        byte[] buffer = new byte[16 + substitute.Length + 2 + printable.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)printable.Length));
        substitute.CopyTo(buffer, 16); printable.CopyTo(buffer, 16 + substitute.Length + 2);
        if (!DeviceIoControl(handle, 0x900A4, buffer, (uint)buffer.Length, 0, 0, out _, 0)) throw new IOException($"Fixture junction unavailable Win32={Marshal.GetLastWin32Error()}");
    }
    public static async Task Run(string outputDirectory)
    {
        string fixture = Path.Combine(Path.GetFullPath(outputDirectory), "scope-" + Guid.NewGuid().ToString("N"));
        string authorized = Path.Combine(fixture, "authorized"), outside = Path.Combine(fixture, "outside");
        Directory.CreateDirectory(authorized); Directory.CreateDirectory(outside);
        var root = new WindowsFileRoot(authorized); int count = 0;
        void Check(bool condition) { if (!condition) throw new IOException($"File scope contract {count + 1} failed"); count++; }
        void Rejected(Action action)
        {
            try { action(); throw new Exception("Unauthorized file operation accepted"); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { count++; }
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        byte[] data = new byte[TransferLimits.ChunkBytes * 2 + 23]; new Random(823).NextBytes(data);
        string checksum = Convert.ToHexString(SHA256.HashData(data));
        await using (var upload = root.CreateUpload("中文/日本語/data.bin", data.LongLength, false))
        {
            for (int offset = 0; offset < data.Length; offset += TransferLimits.ChunkBytes)
                await upload.Write(offset, data.AsSpan(offset, Math.Min(TransferLimits.ChunkBytes, data.Length - offset)).ToArray(), deadline.Token);
            Check(upload.ReceivedBytes == data.Length);
            await upload.Commit(checksum, deadline.Token);
        }
        await using (var read = root.OpenRead("中文/日本語/data.bin"))
        {
            byte[] result = new byte[data.Length]; await read.Stream.ReadExactlyAsync(result, deadline.Token);
            Check(result.SequenceEqual(data) && read.Stream.Length == data.Length);
        }
        await using (var empty = root.CreateUpload("empty.txt", 0, false)) await empty.Commit(Convert.ToHexString(SHA256.HashData([])), deadline.Token);
        Check(new FileInfo(Path.Combine(authorized, "empty.txt")).Length == 0);
        Rejected(() => root.CreateUpload("empty.txt", 0, false));
        await using (var replace = root.CreateUpload("empty.txt", 1, true)) { await replace.Write(0, [42], deadline.Token); await replace.Commit(Convert.ToHexString(SHA256.HashData(new byte[] { 42 })), deadline.Token); }
        Check(File.ReadAllBytes(Path.Combine(authorized, "empty.txt")).SequenceEqual(new byte[] { 42 }));
        root.CreateDirectory("folder/empty");
        Check(root.List("folder", 0) is [{ Directory: true, Path: "folder/empty" }]);
        await using (var interrupted = root.CreateUpload("cancelled.bin", data.Length, false)) await interrupted.Write(0, data[..64], deadline.Token);
        Check(!File.Exists(Path.Combine(authorized, "cancelled.bin")) && !Directory.EnumerateFiles(authorized, ".relaxdesk-*", SearchOption.AllDirectories).Any());
        await using (var corrupt = root.CreateUpload("corrupt.bin", 1, false))
        {
            await corrupt.Write(0, [1], deadline.Token);
            try { await corrupt.Commit(new string('0', 64), deadline.Token); throw new Exception("Checksum mismatch accepted"); }
            catch (InvalidDataException) { count++; }
        }
        Check(!File.Exists(Path.Combine(authorized, "corrupt.bin")));
        foreach (string path in new[] { "../outside/no", "..\\outside\\no", "C:/outside", "/outside", "a//b", "a/./b", "CON.txt", "a/NUL", "a:stream", "a.", "a ", ".relaxdesk-forged.partial" })
            Rejected(() => root.CreateUpload(path, 0, false));
        await using (var pinned = root.CreateUpload("pinned/data.bin", 1, false))
        {
            Rejected(() => Directory.Move(Path.Combine(authorized, "pinned"), Path.Combine(authorized, "renamed")));
            await pinned.Write(0, [1], deadline.Token); await pinned.Commit(Convert.ToHexString(SHA256.HashData(new byte[] { 1 })), deadline.Token);
        }
        string outsideFile = Path.Combine(outside, "not-authorized.txt"); await File.WriteAllTextAsync(outsideFile, "fixture only", deadline.Token);
        if (CreateHardLink(Path.Combine(authorized, "hardlink.txt"), outsideFile, 0)) Rejected(() => root.OpenRead("hardlink.txt"));
        else Console.WriteLine($"SKIP hardlink creation Win32={Marshal.GetLastWin32Error()}");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(authorized, "escape"), outside);
            Rejected(() => root.OpenRead("escape/not-authorized.txt"));
            Rejected(() => root.CreateUpload("escape/no.bin", 0, false));
            Rejected(() => root.CreateDirectory("escape/new"));
        }
        catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException || error is IOException && (error.HResult & 0xffff) == 1314) { Console.WriteLine("SKIP symbolic link creation privilege unavailable; junction tested independently"); }
        CreateFixtureJunction(Path.Combine(authorized, "junction"), outside);
        Rejected(() => root.OpenRead("junction/not-authorized.txt"));
        Rejected(() => root.CreateUpload("junction/no.bin", 0, false));
        Rejected(() => root.CreateDirectory("junction/new"));
        Check(File.ReadAllText(outsideFile) == "fixture only" && !File.Exists(Path.Combine(outside, "no.bin")));
        // Logical >4GiB boundary only; no multi-gigabyte allocation/copy.
        long largeOffset = 4L * 1024 * 1024 * 1024 + 7;
        TransferLimits.ValidateChunk(largeOffset + 64, largeOffset, largeOffset, 64); count++;
        Rejected(() => TransferLimits.ValidateChunk(largeOffset + 64, largeOffset, largeOffset, 65));
        Console.WriteLine($"PASS {count} actual scoped Windows file contracts: Unicode/empty/overwrite/hash/cancel/temp cleanup, traversal/ADS/devices, parent rename lock, supported link rejection, >4GiB logical bounds; fixtures={fixture}; no physical LAN claim");
    }
}
