using System.Security.Cryptography;
using System.Text;

namespace TriSoftic.GrowthEngine.BuildingBlocks.Security;

/// <summary>HMAC signing for webhooks in both directions (n8n, email providers, any other external tool).</summary>
public static class WebhookSigner
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static string Sign(string secret, long unixSeconds, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{unixSeconds}.{body}"));
        return "sha256=" + Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>True when the signature matches and the timestamp is within five minutes either way. Replay inside that window is a separate concern for the receiver.</summary>
    public static bool Verify(string secret, string? timestamp, string body, string? signature, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signature) || !long.TryParse(timestamp, out var ts)) return false;
        var age = now - DateTimeOffset.FromUnixTimeSeconds(ts);
        if (age > Tolerance || age < -Tolerance) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(secret, ts, body)), Encoding.UTF8.GetBytes(signature));
    }

    public static string Hash(string signature) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
}
