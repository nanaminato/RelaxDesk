internal sealed record OfficeApplications(string Host, string Viewer, string Shell, string Service, string Smoke, string Tests);
internal sealed record OfficeAcceptanceCase(string Name, string Application, string[] Arguments, int Seconds = 120);

// Shared executable coverage for source verification and the final EXE collector.
// All sources/clipboards/audio are fixtures. No foreground input, real capture,
// visible GPU Present, SCM mutation or Startup registration belongs in this list.
internal static class OfficeAcceptanceCases
{
    public static OfficeAcceptanceCase[] Create(OfficeApplications a, string fixtures)
    {
        var cases = new List<OfficeAcceptanceCase>();
        void Add(string name, string application, params string[] args) => cases.Add(new(name, application, args));
        string Folder(string name) => Path.Combine(fixtures, name);
        Add("office-core-tls", a.Tests);
        Add("office-session-access", a.Smoke, a.Host, "--session-access");
        Add("office-desktop-pause", a.Smoke, a.Host, "--desktop-pause");
        Add("office-windows-session-end", a.Smoke, a.Host, "--session-unavailable");
        Add("office-quic-production", a.Smoke, a.Host, "--quic-production");
        Add("office-quic-files", a.Smoke, a.Host, "--file-transfer-udp", Folder("quic-files"));
        Add("office-auto-files", a.Smoke, a.Host, "--file-transfer-auto", Folder("auto-files"));
        Add("office-auto-recovery", a.Smoke, a.Host, "--wpf", a.Viewer, "auto-recovery-cpu");
        Add("office-collector", a.Smoke, "--collector-contracts", Folder("collector"));
        Add("office-broker", a.Smoke, a.Service, "--broker-contracts");
        Add("office-service-setup", a.Smoke, a.Service, "--service-setup-contracts", Folder("service"));
        foreach (string mode in new[] { "windows", "session", "display", "viewport", "input", "clipboard-image", "capture-refresh" })
            Add("office-" + mode, a.Smoke, "--" + mode + "-contracts");
        foreach (string mode in new[] { "history-store", "diagnostic-log", "file-root", "file-resume", "file-clipboard" })
            Add("office-" + mode, a.Smoke, "--" + mode + "-contracts", Folder(mode));
        foreach (string mode in new[] { "shaped", "media-cut", "control-cut" })
            Add("office-network-" + mode, a.Smoke, a.Host, "--network-impairment", mode);
        foreach (string mode in new[] { "reject-token", "reject-pin", "deny-v2", "reject-media", "deny-approval", "display-denied", "display-switch" })
            Add("office-" + mode, a.Smoke, a.Host, "--" + mode);
        Add("office-topology", a.Smoke, a.Host, "--topology-session", Folder("topology"));
        Add("office-file-transfer",a.Smoke,a.Host,"--file-transfer",Folder("files"));
        foreach (string mode in new[] { "viewport", "input-ui", "hwnd", "ui", "toolbar", "pointer" })
            Add("office-viewer-" + mode, a.Viewer, "--" + mode + "-contracts");
        Add("office-viewer-localization",a.Viewer,"--localization-contracts",Folder("viewer-localization"));
        Add("office-host-localization",a.Shell,"--localization-contracts",Folder("host-localization"));
        Add("office-viewer-automatic-quality", a.Viewer, "--automatic-quality-contracts", Folder("automatic-quality"));
        Add("office-viewer-diagnostics", a.Viewer, "--diagnostic-log-contracts", Folder("viewer-diagnostics"));
        Add("office-viewer-error-details", a.Viewer, "--error-details-contracts", Folder("error-details"));
        foreach (string mode in new[] { "cpu-decode", "cpu-submit", "viewer-update" })
            Add("office-viewer-failure-" + mode, a.Viewer, "--pipeline-failure-contracts", a.Host, mode);
        foreach (string mode in new[] { "display", "topology", "topology-race", "audio" })
        {
            string command = mode == "topology-race" ? "topology-race" : mode + "-session";
            Add("office-viewer-" + mode, a.Viewer, "--" + command + "-contracts", a.Host);
        }
        foreach (string mode in new[] { "history", "statistics" })
            Add("office-viewer-" + mode, a.Viewer, "--" + mode + "-contracts", a.Host, Folder(mode));
        Add("office-viewer-udp-statistics", a.Viewer, "--statistics-contracts", a.Host, Folder("statistics-udp"), "udp");
        Add("office-viewer-fallback-statistics", a.Viewer, "--statistics-contracts", a.Host, Folder("statistics-fallback"), "oversized");
        Add("office-viewer-host-without-udp", a.Viewer, "--statistics-contracts", a.Host, Folder("statistics-host-without-udp"), "host-without-udp");
        Add("office-viewer-text-quality", a.Viewer, "--office-text-quality", "software");
        Add("office-viewer-unified-session",a.Viewer,"--unified-session-contracts",a.Host,Folder("unified-session"));
        Add("office-host-first-approval",a.Shell,"--password-login-contracts",a.Host,Folder("first-approval"));
        foreach (string mode in new[] { "h264", "deflate", "audio", "static", "automatic" })
            Add("office-viewer-video-settings-" + mode, a.Viewer, "--video-settings-contracts", a.Host, mode);
        Add("office-host-service-setup-ui",a.Shell,"--service-setup-ui-contracts",a.Host,Folder("service-setup-ui"));
        Add("office-host-service", a.Shell, "--service-contracts", a.Host, a.Service);
        foreach (string mode in new[] { "recovery" }) Add("office-host-" + mode, a.Shell, "--" + mode + "-contracts", a.Host);
        if (cases.Count > 128 || cases.Select(test => test.Name).Distinct().Count() != cases.Count || cases.Any(test => test.Seconds is < 1 or > 180))
            throw new InvalidOperationException("Invalid office acceptance cases");
        return cases.ToArray();
    }
}
