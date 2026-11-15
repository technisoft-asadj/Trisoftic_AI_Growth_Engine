using System.Net.Mail;
using System.Text.RegularExpressions;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Crm;

public static partial class Normalizer
{
    /// <summary>Returns the canonical form, or null when the value is not plausible for the type.</summary>
    public static string? Method(MethodType type, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        if (type == MethodType.Email)
        {
            v = v.ToLowerInvariant();
            if (v.Length > 320 || !MailAddress.TryCreate(v, out var addr) || addr.Address != v || !v.Contains('.', StringComparison.Ordinal) && !v.EndsWith("localhost", StringComparison.Ordinal)) return null;
            return v;
        }
        // phone / whatsapp: keep digits, keep a leading '+', treat 00 as '+'
        var plus = v.StartsWith('+') || v.StartsWith("00");
        var digits = NonDigits().Replace(v, "");
        if (v.StartsWith("00")) digits = digits[2..];
        if (digits.Length is < 7 or > 15) return null;
        return (plus ? "+" : "") + digits;
    }

    public static MethodType MethodFor(ContactChannel c) => c switch { ContactChannel.Email => MethodType.Email, ContactChannel.WhatsApp => MethodType.WhatsApp, _ => MethodType.Phone };

    /// <summary>Channels that share the same underlying address (a phone number can receive calls, SMS and WhatsApp).</summary>
    public static IEnumerable<MethodType> MethodsFor(ContactChannel c) => c switch
    {
        ContactChannel.Email => [MethodType.Email],
        ContactChannel.WhatsApp => [MethodType.WhatsApp, MethodType.Phone],
        _ => [MethodType.Phone, MethodType.WhatsApp],
    };

    public static string? Domain(string? website)
    {
        if (string.IsNullOrWhiteSpace(website)) return null;
        var s = website.Trim().ToLowerInvariant();
        if (!s.Contains("://")) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || !u.Host.Contains('.')) return null;
        return u.Host.StartsWith("www.") ? u.Host[4..] : u.Host;
    }

    public static string DisplayName(string first, string last, string? fallback)
    {
        var n = $"{first} {last}".Trim();
        return n.Length > 0 ? n : (fallback ?? "Unnamed contact");
    }

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigits();
}
