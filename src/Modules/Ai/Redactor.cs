using System.Text.RegularExpressions;

namespace TriSoftic.GrowthEngine.Modules.Ai;

/// <summary>
/// Removes obviously sensitive tokens before text leaves the platform (Architecture 7: never put keys, tokens or
/// customer PII in prompts). Defence in depth: callers should not pass such data in the first place.
/// </summary>
public static partial class Redactor
{
    public static string Redact(string text)
    {
        text = Email().Replace(text, "[email]");
        text = Secret().Replace(text, "[secret]");
        text = Phone().Replace(text, "[phone]");
        return text;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    // sk-..., sk-ant-..., AKIA..., long bearer-like tokens
    [GeneratedRegex(@"\b(sk-[A-Za-z0-9_\-]{16,}|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,})")]
    private static partial Regex Secret();

    [GeneratedRegex(@"(?<![\w.])\+?\d[\d\s().\-]{8,}\d(?![\w])")]
    private static partial Regex Phone();
}
