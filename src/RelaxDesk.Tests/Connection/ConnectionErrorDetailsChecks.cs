using RelaxDesk.Core;

internal static class ConnectionErrorDetailsChecks
{
    public static int Run()
    {
        int count = 0; void Check(bool value) { if (!value) throw new Exception("Connection error detail assertion " + (count + 1)); count++; }
        string secret = "private-password-invitation-clipboard-input";
        Exception error = new IOException(secret, new InvalidDataException(secret)) { Source = secret, HelpLink = secret }; error.Data[secret] = secret;
        var time = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.FromHours(9));
        var detail = ConnectionErrorDetails.Capture(error, "cpu-decode", new string('A', 32), time); string report = detail.Report();
        Check(detail.Stage == "cpu-decode"); Check(detail.CorrelationId == new string('a', 32)); Check(detail.RecordedUtc.Offset == TimeSpan.Zero);
        Check(report.Contains("cause_0_type=IOException")); Check(report.Contains("cause_1_type=InvalidDataException"));
        Check(report.Contains("hresult=0x80131620")); Check(!report.Contains(secret)); Check(!report.Contains("HelpLink") && !report.Contains("Source="));
        Check(ConnectionErrorDetails.Capture(error, secret, secret).Stage == "unknown");
        Check(ConnectionErrorDetails.Capture(error, "connect", secret).CorrelationId == "none");
        foreach (string? invalid in new string?[] { null, "", new string('x', 32), new string('a', 33), "\r\nforged" })
            Check(ConnectionErrorDetails.Capture(error, "connect", invalid).CorrelationId == "none");
        for (int i = 0; i < 20; i++) error = new IOException(new string('x', 10000), error);
        var nested = ConnectionErrorDetails.Capture(error, "control-read", "none"); report = nested.Report();
        Check(nested.CausesTruncated); Check(report.Contains("cause_3_type=IOException") && !report.Contains("cause_4"));
        Check(report.Length < 1024); Check(!report.Contains(new string('x', 100)));
        Check(detail.Report() == detail.Report());
        string nativeReport = ConnectionErrorDetails.Capture(new System.ComponentModel.Win32Exception(5, secret), "connect", null).Report();
        Check(nativeReport.Contains("native_error=5")); Check(!nativeReport.Contains(secret));
        foreach (string stage in new[] { "configuration", "authenticating", "awaiting-approval", "negotiating", "session-setup", "control-write", "media-read", "media-validate", "gpu-present", "cpu-submit", "viewer-update", "legacy" })
            Check(ConnectionErrorDetails.Capture(error, stage, null).Stage == stage);
        return count;
    }
}
