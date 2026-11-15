using TriSoftic.GrowthEngine.Modules.Crm;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class NormalizerTests
{
    [Theory]
    [InlineData("  Jane.Doe@Example.COM ", "jane.doe@example.com")]
    [InlineData("a@b.co", "a@b.co")]
    public void Emails_are_lowercased_and_trimmed(string raw, string expected) => Assert.Equal(expected, Normalizer.Method(MethodType.Email, raw));

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    [InlineData("Jane <jane@x.com>")]
    [InlineData("")]
    [InlineData(null)]
    public void Bad_emails_are_rejected(string? raw) => Assert.Null(Normalizer.Method(MethodType.Email, raw));

    [Theory]
    [InlineData("+92 300 123-4567", "+923001234567")]
    [InlineData("0092 300 1234567", "+923001234567")]
    [InlineData("(042) 3555 1234", "04235551234")]
    public void Phones_keep_digits_and_a_leading_plus(string raw, string expected) => Assert.Equal(expected, Normalizer.Method(MethodType.Phone, raw));

    [Theory]
    [InlineData("12345")]
    [InlineData("call me")]
    [InlineData("1234567890123456789")]
    public void Bad_phones_are_rejected(string raw) => Assert.Null(Normalizer.Method(MethodType.Phone, raw));

    [Theory]
    [InlineData("https://www.Example.com/about", "example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("shop.example.co.uk", "shop.example.co.uk")]
    [InlineData("localhost", null)]
    [InlineData("", null)]
    public void Domains_are_extracted(string raw, string? expected) => Assert.Equal(expected, Normalizer.Domain(raw));
}
