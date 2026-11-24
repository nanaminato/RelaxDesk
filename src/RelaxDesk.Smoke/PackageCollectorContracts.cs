using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxDesk.Windows;

internal sealed partial class PackageCollector
{
    public static async Task Contracts(string fixtures)
    {
        string fixture = Path.Combine(Path.GetFullPath(fixtures), "collector-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixture, "manifest-fixture"); Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool value, string reason) { if (!value) throw new IOException(reason); checks++; }
        async Task Reject() { bool denied = false; try { await Verify(root); } catch { denied = true; } Check(denied, "Invalid package manifest accepted"); }
        string file = Path.Combine(root, "item.txt"), manifest = Path.Combine(root, "SHA256SUMS.json");
        byte[] content = [1, 2, 3]; File.WriteAllBytes(file, content); string hash = Convert.ToHexString(SHA256.HashData(content));
        void Entries(params object[] entries) => File.WriteAllText(manifest, JsonSerializer.Serialize(entries));
        Entries(new { path = "item.txt", sha256 = hash, bytes = 3 }); await Verify(root); checks++;
        Entries(new { path = "item.txt", sha256 = hash }); await Verify(root); checks++;
        Entries(new { path = "item.txt", sha256 = hash, bytes = 2 }); await Reject();
        Entries(new { path = "item.txt", sha256 = new string('0', 64) }); await Reject();
        Entries(new { path = "item.txt", sha256 = "invalid" }); await Reject();
        Entries(new { path = "item.txt", sha256 = hash }, new { path = "ITEM.TXT", sha256 = hash }); await Reject();
        foreach (string bad in new[] { "", "../outside.txt", file, "item.txt:stream", "item.txt.", "item.txt ", "sub//item.txt", "sub/../item.txt", new string('x', 513), "item\0.txt" })
        { Entries(new { path = bad, sha256 = hash }); await Reject(); }
        File.WriteAllText(manifest, "[]"); await Reject();
        File.WriteAllText(manifest, "{}"); await Reject();
        File.WriteAllText(manifest, "[{}"); await Reject();
        Entries(Enumerable.Range(0, 8193).Select(_ => (object)new { path = "item.txt", sha256 = hash }).ToArray()); await Reject();
        File.WriteAllBytes(manifest, new byte[4 * 1024 * 1024 + 1]); await Reject();
        string outside = Path.Combine(fixture, "outside"); Directory.CreateDirectory(outside); File.WriteAllBytes(Path.Combine(outside, "item.txt"), content);
        string junction = Path.Combine(root, "redirected"); ServiceSetupSmoke.Junction(junction, outside);
        Entries(new { path = "redirected/item.txt", sha256 = hash }); await Reject();
        Check(File.ReadAllBytes(Path.Combine(outside, "item.txt")).SequenceEqual(content), "Manifest read modified outside sentinel"); Directory.Delete(junction);
        string unicode = Path.Combine(root, "中文 日本語"); Directory.CreateDirectory(unicode); File.WriteAllBytes(Path.Combine(unicode, "a.bin"), content);
        Entries(new { path = "中文 日本語/a.bin", sha256 = hash, bytes = 3 }); await Verify(root); checks++;
        foreach (int length in new[] { 0, 100, 1024 * 1024, 1024 * 1024 + 4096 })
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', length))); using var reader = new StreamReader(stream);
            var captured = await Capture(reader);
            Check(captured.Text.Length == Math.Min(length, 1024 * 1024) && captured.Truncated == (length > 1024 * 1024) && stream.Position == stream.Length, "Collector capture cap/drain failed");
        }
        string application = string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? typeof(PackageCollector).Assembly.Location : Environment.ProcessPath ?? throw new IOException("Smoke executable path unavailable");
        string results = Path.Combine(fixture, "runner"); Directory.CreateDirectory(results);
        var collector = new PackageCollector(Environment.CurrentDirectory, results);
        Check(await collector.Exec("small", application, ["--collector-output-fixture", "10"]) == 0, "Small actual child failed");
        Check(await collector.Exec("overflow", application, ["--collector-output-fixture", (1024 * 1024 + 100).ToString()]) != 0 && collector.steps.Last().status == "FAIL", "Truncated child incorrectly passed");
        Check(new FileInfo(Path.Combine(results, "overflow.stdout.txt")).Length <= 1024 * 1024 + 100, "Collector output file exceeded bound");
        Directory.CreateDirectory(Path.Combine(results, "unwritable.stdout.txt"));
        Check(await collector.Exec("unwritable", application, ["--collector-output-fixture", "10"]) != 0 && collector.steps.Last().status == "FAIL", "Child exit 0 hid failed evidence write");
        var applications = new OfficeApplications("host", "viewer", "shell", "service", "smoke", "tests");
        var cases = OfficeAcceptanceCases.Create(applications, fixture);
        foreach (string requirement in new[] { "office-core-tls", "office-collector", "office-broker", "office-service-setup", "office-file-resume", "office-persistent-host", "office-network-shaped", "office-topology", "office-viewer-audio", "office-viewer-file-session", "office-viewer-reconnect", "office-viewer-video-settings-static", "office-viewer-failure-viewer-update", "office-viewer-diagnostics", "office-viewer-error-details", "office-host-recovery", "office-host-service-setup-ui" })
            Check(cases.Any(test => test.Name == requirement), "Office acceptance omitted " + requirement);
        string[] forbidden = ["--keyboard-contracts", "--capture-test", "--capture-diagnose", "--video-desktop", "--render-test", "--present-diagnose"];
        Check(cases.All(test => !test.Arguments.Any(argument => forbidden.Contains(argument))), "Physical action entered synthetic office suite");
        Console.WriteLine($"PASS {checks} collector manifest/capture/actual-child/evidence-write/coverage contracts; office_cases={cases.Length}; isolated fixtures only, no package, capture/input/SCM mutation; fixture={fixture}");
    }
}
