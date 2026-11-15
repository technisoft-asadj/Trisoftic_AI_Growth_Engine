using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.Modules.Crm;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class LeadTransitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Normal_progression_and_first_contact_time()
    {
        var l = new Lead();
        Assert.Null(l.FirstContactedAt);                 // not yet contacted is "unknown", never zero
        l.MarkContacted(Now);
        Assert.Equal(Now, l.FirstContactedAt);
        l.Qualify("Has budget and a Q1 deadline", Now.AddDays(1));
        Assert.Equal(LeadStatus.Qualified, l.Status);
        l.Convert(Guid.NewGuid(), Now.AddDays(2));
        Assert.Equal(LeadStatus.Converted, l.Status);
        Assert.False(l.IsOpen);
    }

    [Fact]
    public void Cannot_skip_steps()
    {
        Assert.Throws<IllegalTransitionException>(() => new Lead().Qualify("x", Now));
        Assert.Throws<IllegalTransitionException>(() => new Lead().Convert(Guid.NewGuid(), Now));
        var l = new Lead(); l.MarkContacted(Now);
        Assert.Throws<IllegalTransitionException>(() => l.MarkContacted(Now));
    }

    [Fact]
    public void Qualification_and_disqualification_need_a_written_reason()
    {
        var l = new Lead(); l.MarkContacted(Now);
        Assert.Throws<IllegalTransitionException>(() => l.Qualify("  ", Now));
        Assert.Throws<IllegalTransitionException>(() => l.Disqualify("", Now));
        l.Disqualify("Not a pharmacy", Now);
        Assert.Equal("Not a pharmacy", l.DisqualifyReason);
        Assert.Throws<IllegalTransitionException>(() => l.Disqualify("again", Now));
    }

    [Fact]
    public void Reopen_clears_the_closed_state_and_the_response_clock()
    {
        var l = new Lead(); l.MarkContacted(Now); l.Disqualify("later", Now);
        l.Reopen();
        Assert.Equal(LeadStatus.New, l.Status);
        Assert.Null(l.DisqualifyReason); Assert.Null(l.ClosedAt); Assert.Null(l.FirstContactedAt);
        Assert.Throws<IllegalTransitionException>(() => new Lead().Reopen());
    }
}

public sealed class ScorerTests
{
    private static ScoreRule Rule(ScoreField f, int pts, string? v = null, Guid? product = null, bool enabled = true) =>
        new() { Name = f + (v ?? ""), Field = f, Value = v, Points = pts, ProductId = product, Enabled = enabled };
    private static ScoreInput In(string source = "form:demo", bool company = false, bool phone = false, string? title = null, string[]? tags = null, string? msg = null) =>
        new(source, company, phone, title, tags ?? [], msg);

    [Fact]
    public void Sums_matching_rules_and_explains_each_hit()
    {
        var rules = new[] { Rule(ScoreField.HasCompany, 20), Rule(ScoreField.HasPhone, 10), Rule(ScoreField.JobTitleContains, 15, "owner"), Rule(ScoreField.SourceEquals, 5, "referral") };
        var (score, hits) = Scorer.Evaluate(rules, Guid.NewGuid(), In(company: true, phone: true, title: "Shop Owner"));
        Assert.Equal(45, score);
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public void Clamps_to_0_100_and_ignores_disabled_or_other_product_rules()
    {
        var p = Guid.NewGuid();
        var big = Enumerable.Range(0, 6).Select(i => Rule(ScoreField.HasCompany, 30)).ToArray();
        Assert.Equal(100, Scorer.Evaluate(big, p, In(company: true)).Score);
        Assert.Equal(0, Scorer.Evaluate([Rule(ScoreField.HasCompany, -40)], p, In(company: true)).Score);
        Assert.Equal(0, Scorer.Evaluate([Rule(ScoreField.HasCompany, 20, enabled: false), Rule(ScoreField.HasCompany, 20, product: Guid.NewGuid())], p, In(company: true)).Score);
        Assert.Equal(20, Scorer.Evaluate([Rule(ScoreField.HasCompany, 20, product: p)], p, In(company: true)).Score);
    }

    [Fact]
    public void Text_rules_are_case_insensitive_and_need_a_value()
    {
        var p = Guid.NewGuid();
        Assert.Equal(10, Scorer.Evaluate([Rule(ScoreField.MessageContains, 10, "DEMO")], p, In(msg: "Please book a demo")).Score);
        Assert.Equal(10, Scorer.Evaluate([Rule(ScoreField.TagEquals, 10, "VIP")], p, In(tags: ["vip"])).Score);
        Assert.Equal(0, Scorer.Evaluate([Rule(ScoreField.MessageContains, 10, "")], p, In(msg: "anything")).Score);
    }
}

public sealed class CsvTests
{
    [Fact]
    public void Parses_quotes_embedded_commas_newlines_and_bom()
    {
        var rows = Csv.Parse("﻿name,note\r\n\"Doe, Jane\",\"said \"\"hi\"\"\nthen left\"\r\nBob,plain\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal("Doe, Jane", rows[1][0]);
        Assert.Equal("said \"hi\"\nthen left", rows[1][1]);
        Assert.Equal(["Bob", "plain"], rows[2]);
    }

    [Fact]
    public void Skips_blank_lines_and_handles_missing_trailing_newline()
    {
        var rows = Csv.Parse("a,b\n\n1,2");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["1", "2"], rows[1]);
    }
}
