using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Ai;
using TriSoftic.GrowthEngine.Modules.Jobs;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class GroundingTests
{
    private static ApprovedKnowledge Knowledge() => new(
        Guid.NewGuid(), "Pharmacy POS", "BusinessSoftware", "Live", 3, "Point of sale for pharmacies",
        [new KnowledgeFact("f1", "Feature", "Works offline"), new KnowledgeFact("f2", "Pricing", "Plans start at 29 USD per month")],
        [new KnowledgeClaim("c1", "FDA approved", Prohibited: true), new KnowledgeClaim("c2", "Easy to learn", Prohibited: false)],
        [], [new KnowledgeMarket("PK", ["en"])], ["Windows"], "friendly", [], []);

    private static GroundedDraft Draft(string body, params GroundedClaim[] claims) => new() { Title = "t", Body = body, Claims = [.. claims] };
    private static GroundedClaim Claim(string text, string? refId, bool assumption = false) => new() { Text = text, FactRef = refId, Assumption = assumption };

    [Fact]
    public void Grounded_draft_passes()
    {
        var v = GroundingValidator.Validate(Draft("It works offline. Plans start at 29 USD per month.", Claim("Works offline", "f1"), Claim("Starts at 29 USD", "f2")), Knowledge(), "");
        Assert.Empty(v);
    }

    [Fact]
    public void Unknown_factRef_is_rejected()
        => Assert.Contains(GroundingValidator.Validate(Draft("x", Claim("Has AI", "f99")), Knowledge(), ""), s => s.Contains("unknown factRef"));

    [Fact]
    public void Claim_without_source_and_not_assumption_is_rejected()
        => Assert.Contains(GroundingValidator.Validate(Draft("x", Claim("Fastest in town", null)), Knowledge(), ""), s => s.Contains("no factRef"));

    [Fact]
    public void Labelled_assumption_is_allowed()
        => Assert.Empty(GroundingValidator.Validate(Draft("x", Claim("Pharmacies want speed", null, assumption: true)), Knowledge(), ""));

    [Fact]
    public void Prohibited_claim_in_body_is_rejected()
        => Assert.Contains(GroundingValidator.Validate(Draft("Our software is FDA approved!", Claim("Works offline", "f1")), Knowledge(), ""), s => s.Contains("prohibited"));

    [Theory]
    [InlineData("Trusted by 5,000 pharmacies")]
    [InlineData("Save 40% on stock")]
    public void Invented_figures_are_rejected(string body)
        => Assert.Contains(GroundingValidator.Validate(Draft(body, Claim("Works offline", "f1")), Knowledge(), ""), s => s.Contains("does not appear"));

    [Fact]
    public void Figure_from_the_users_own_brief_is_allowed()
        => Assert.Empty(GroundingValidator.Validate(Draft("Launch on 15 December", Claim("Works offline", "f1")), Knowledge(), "Launch on 15 December"));

    [Fact]
    public void Prohibited_claim_ids_cannot_be_cited_as_support()
        => Assert.Contains(GroundingValidator.Validate(Draft("x", Claim("FDA approved", "c1")), Knowledge(), ""), s => s.Contains("unknown factRef"));

    [Fact]
    public void Empty_body_is_rejected()
        => Assert.NotEmpty(GroundingValidator.Validate(Draft(" "), Knowledge(), ""));

    [Fact]
    public void Json_is_extracted_from_chatty_output()
    {
        var d = GroundedGenerator.ParseDraft("Sure!\n```json\n{\"title\":\"a\",\"body\":\"b\",\"claims\":[{\"text\":\"c\",\"factRef\":\"f1\",\"assumption\":false}],\"questions\":[\"q\"]}\n```", out var err);
        Assert.Null(err);
        Assert.Equal("b", d!.Body);
        Assert.Single(d.Questions);
    }

    [Fact]
    public void Garbage_is_reported_not_thrown()
    {
        Assert.Null(GroundedGenerator.ParseDraft("no json here", out var err));
        Assert.NotNull(err);
    }
}

public sealed class RedactorTests
{
    [Theory]
    [InlineData("mail me at jane.doe@example.com now", "jane.doe@example.com")]
    [InlineData("key sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "sk-ant-api03")]
    [InlineData("call +92 300 1234567 today", "1234567")]
    public void Sensitive_tokens_are_removed(string input, string leaked)
        => Assert.DoesNotContain(leaked, Redactor.Redact(input));

    [Fact]
    public void Plain_marketing_text_is_untouched()
        => Assert.Equal("Fast checkout for pharmacies", Redactor.Redact("Fast checkout for pharmacies"));
}

public sealed class JobBackoffTests
{
    [Fact]
    public void Backoff_grows_and_is_capped()
    {
        var one = Job.Backoff(1, new Random(1)); var five = Job.Backoff(5, new Random(1)); var huge = Job.Backoff(40, new Random(1));
        Assert.True(five > one);
        Assert.True(huge <= TimeSpan.FromSeconds(3600 * 1.2));
    }
}

