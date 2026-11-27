using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RelaxDesk.Core;

public sealed class AdmissionRateLimits(TimeProvider? clock = null)
{
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly object sync = new();
    readonly Dictionary<(string Scope, string Key), (long Start, int Count)> counters = new();
    readonly Dictionary<string, (long Window, long Last, int Count)> failures = new(StringComparer.Ordinal);
    const int Capacity = 2048;
    static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static string AddressKey(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork) return address.ToString();
        return Convert.ToHexString(address.GetAddressBytes().AsSpan(0, 8));
    }

    public bool Take(string scope, string key, int limit)
    {
        lock (sync)
        {
            long now = time.GetTimestamp();
            var id = (scope, key);
            if (counters.TryGetValue(id, out var current) && time.GetElapsedTime(current.Start, now) < Window)
            {
                if (current.Count >= limit) return false;
                counters[id] = (current.Start, current.Count + 1);
                return true;
            }
            if (counters.Count >= Capacity && !counters.ContainsKey(id))
            {
                foreach (var stale in counters.Where(x => time.GetElapsedTime(x.Value.Start, now) >= Window).ToArray())
                    counters.Remove(stale.Key);
                if (counters.Count >= Capacity) return false;
            }
            counters[id] = (now, 1);
            return true;
        }
    }

    public bool CanVerifyPassword(string key)
    {
        lock (sync)
        {
            long now = time.GetTimestamp();
            if (!failures.TryGetValue(key, out var value) || time.GetElapsedTime(value.Window, now) >= Window) return true;
            int seconds = value.Count < 3 ? 0 : 1 << Math.Min(value.Count - 2, 4);
            return time.GetElapsedTime(value.Last, now) >= TimeSpan.FromSeconds(seconds);
        }
    }

    public void PasswordFailed(string key)
    {
        lock (sync)
        {
            long now = time.GetTimestamp();
            if (!failures.TryGetValue(key, out var current) && failures.Count == Capacity)
            {
                foreach (var stale in failures.Where(x => time.GetElapsedTime(x.Value.Window, now) >= Window).ToArray())
                    failures.Remove(stale.Key);
                if (failures.Count == Capacity) return; // hard attempt quotas still apply
            }
            if (current.Count == 0 || time.GetElapsedTime(current.Window, now) >= Window)
                failures[key] = (now, now, 1);
            else failures[key] = (current.Window, now, Math.Min(6, current.Count + 1));
        }
    }

    public void ClearPasswordFailures(string key)
    {
        lock (sync) failures.Remove(key);
    }

}

// This credential proves prior issuance; callers still verify a fresh device proof,
// the password, and current local trust. It is not a bearer access token.
public static class DeviceAdmissionCredential
{
    public static string Issue(X509Certificate2 host, string fingerprint, DateTimeOffset now)
    {
        long expires = now.AddDays(30).ToUnixTimeSeconds();
        using var key = host.GetRSAPrivateKey() ?? throw new CryptographicException("Device issuer unavailable");
        byte[] signature = key.SignData(Payload(fingerprint, expires), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return FormattableString.Invariant($"{expires}.{Convert.ToBase64String(signature)}");
    }

    public static bool Verify(X509Certificate2 host, string fingerprint, string value, DateTimeOffset now)
    {
        if (value.Length is < 16 or > 800 || value.IndexOf('.') is not (> 0 and < 16)) return false;
        int separator = value.IndexOf('.');
        if (!long.TryParse(value.AsSpan(0, separator), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out long expires) ||
            expires <= now.ToUnixTimeSeconds() || expires > now.AddDays(30).ToUnixTimeSeconds()) return false;
        Span<byte> signature = stackalloc byte[512];
        if (!Convert.TryFromBase64Chars(value.AsSpan(separator + 1), signature, out int count)) return false;
        using var key = host.GetRSAPublicKey();
        return key is not null && count == (key.KeySize + 7) / 8 &&
            key.VerifyData(Payload(fingerprint, expires), signature[..count], HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    static byte[] Payload(string fingerprint, long expires) =>
        Encoding.ASCII.GetBytes(FormattableString.Invariant($"RelaxDesk device-admission v1\n{fingerprint}\n{expires}"));
}

public sealed class SingleSessionAdmission
{
    public sealed class Lease
    {
        internal readonly CancellationTokenSource Replaced = new();
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Ready;
        public CancellationToken Replacement => Replaced.Token;
        public Task Completion => Finished.Task;
    }

    readonly object sync = new();
    Lease? active, reserved;

    public Lease? Begin()
    {
        lock (sync)
            return active is null && reserved is null ? active = new() : null;
    }

    public Lease? ReadReady()
    {
        lock (sync) return reserved is null && active is { Ready: true } ? active : null;
    }

    public void MarkReady(Lease value)
    {
        lock (sync) { if (active == value) value.Ready = true; }
    }

    public void MarkStopping(Lease value)
    {
        lock (sync) { if (active == value) value.Ready = false; }
    }

    public Lease? ReserveReplacement(Lease expected)
    {
        Lease successor;
        lock (sync)
        {
            if (active != expected || !expected.Ready || reserved is not null) return null;
            successor = reserved = new();
        }
        expected.Replaced.Cancel();
        return successor;
    }

    public bool Promote(Lease successor)
    {
        lock (sync)
        {
            if (active is not null || reserved != successor) return false;
            reserved = null;
            active = successor;
            return true;
        }
    }

    public void End(Lease value, bool clean = true, Action? onIdle = null)
    {
        lock (sync)
        {
            if (active == value)
            {
                active = null;
                if (!clean) reserved = null;
            }
            if (reserved == value) reserved = null;
            value.Finished.TrySetResult();
            if (active is null && reserved is null && clean) onIdle?.Invoke();
        }
    }
}
