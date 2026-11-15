using TriSoftic.GrowthEngine.BuildingBlocks.Security;
using TriSoftic.GrowthEngine.Modules.Automation;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class AutomationRulesTests
{
    private static readonly Dictionary<string, object?> Ctx = new(StringComparer.OrdinalIgnoreCase)
    {
        ["status"] = "New", ["score"] = 42, ["contactName"] = "Ayesha Khan", ["ownerUserId"] = null, ["value"] = 1200.5m, ["flag"] = true,
    };

    [Theory]
    [InlineData("status", "eq", "new", true)]       // text compare ignores case
    [InlineData("status", "eq", "Contacted", false)]
    [InlineData("status", "ne", "Contacted", true)]
    [InlineData("score", "gt", "40", true)]
    [InlineData("score", "gt", "42", false)]
    [InlineData("score", "lt", "100", true)]
    [InlineData("score", "eq", "42.0", true)]       // numbers compare as numbers
    [InlineData("value", "gt", "1200", true)]
    [InlineData("contactName", "contains", "khan", true)]
    [InlineData("contactName", "exists", null, true)]
    [InlineData("ownerUserId", "empty", null, true)]
    [InlineData("ownerUserId", "exists", null, false)]
    [InlineData("missing", "empty", null, true)]
    [InlineData("missing", "eq", "x", false)]
    [InlineData("missing", "ne", "x", true)]
    [InlineData("status", "gt", "1", false)]         // greater-than on text is never true
    [InlineData("flag", "eq", "true", true)]
    public void Conditions_evaluate_against_the_records_current_facts(string field, string op, string? value, bool expected)
        => Assert.Equal(expected, Definitions.Matches(Ctx, field, op, value));

    [Fact]
    public void Templates_fill_known_fields_and_blank_unknown_ones_without_failing()
    {
        Assert.Equal("Call Ayesha Khan (score 42)", Definitions.Render("Call {{contactName}} (score {{ score }})", Ctx));
        Assert.Equal("Hello !", Definitions.Render("Hello {{nope}}!", Ctx));
        Assert.Equal(10, Definitions.Render(new string('x', 50), Ctx, max: 10).Length);
    }

    private static List<string> Check(WorkflowDefinition d, bool webhook = true, bool email = false) => Definitions.Validate(d, n => webhook && n == "hook", email);
    private static WorkflowDefinition Def(params Step[] steps) => new() { Steps = [.. steps] };

    [Fact]
    public void A_sensible_definition_validates_and_nonsense_is_explained()
    {
        Assert.Empty(Check(Def(new Step { Kind = "wait", Minutes = 60 }, new Step { Kind = "if", Field = "status", Op = "eq", Value = "New" },
            new Step { Kind = "task", Title = "Call", DueInMinutes = 30 }, new Step { Kind = "notify", Message = "hi" }, new Step { Kind = "webhook", Name = "hook" })));

        Assert.NotEmpty(Check(Def()));                                                                                  // no steps
        Assert.NotEmpty(Check(Def(new Step { Kind = "teleport" })));                                                    // unknown kind
        Assert.NotEmpty(Check(Def(new Step { Kind = "wait", Minutes = 0 })));
        Assert.NotEmpty(Check(Def(new Step { Kind = "wait", Minutes = 60 * 24 * 31 })));                                // over 30 days
        Assert.NotEmpty(Check(Def(new Step { Kind = "task", Title = " " })));
        Assert.NotEmpty(Check(Def(new Step { Kind = "task", Title = "x", Assignee = "someone" })));                     // not owner/none/guid
        Assert.Empty(Check(Def(new Step { Kind = "task", Title = "x", Assignee = Guid.NewGuid().ToString() })));
        Assert.NotEmpty(Check(Def(new Step { Kind = "notify", Message = "x", To = "none" })));                          // a notification needs someone
        Assert.NotEmpty(Check(Def(new Step { Kind = "webhook", Name = "unknown" })));                                   // not configured
        Assert.NotEmpty(Check(Def(new Step { Kind = "if", Field = "status", Op = "eq" })));                             // value missing
        Assert.NotEmpty(Check(Def(new Step { Kind = "if", Field = "status", Op = "like", Value = "x" })));              // bad operator
        Assert.NotEmpty(Check(Def(new Step { Kind = "email" })));                                                       // no email provider yet
        Assert.NotEmpty(Check(Def(Enumerable.Range(0, 21).Select(_ => new Step { Kind = "notify", Message = "x" }).ToArray())));
    }

    [Fact]
    public void Triggers_include_platform_events_and_namespaced_external_ones_only()
    {
        Assert.True(Definitions.IsKnownTrigger("lead.created"));
        Assert.True(Definitions.IsKnownTrigger("external.crm-sync"));
        Assert.False(Definitions.IsKnownTrigger("lead.exploded"));
        Assert.False(Definitions.IsKnownTrigger("external."));
        Assert.False(Definitions.IsKnownTrigger("EXTERNAL.x"));
    }

    [Fact]
    public void Definitions_round_trip_through_json()
    {
        var d = new WorkflowDefinition { Conditions = [new Condition { Field = "source", Op = "eq", Value = "form" }], Steps = [new Step { Kind = "wait", Minutes = 5 }] };
        var back = WorkflowDefinition.FromJson(d.ToJson());
        Assert.Equal("form", back.Conditions.Single().Value);
        Assert.Equal(5, back.Steps.Single().Minutes);
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Webhook_signatures_accept_the_exact_request_and_reject_tampering_staleness_and_wrong_secrets()
    {
        const string secret = "s3cret"; const string body = "{\"a\":1}";
        var ts = Now.ToUnixTimeSeconds();
        var sig = WebhookSigner.Sign(secret, ts, body);
        Assert.True(WebhookSigner.Verify(secret, ts.ToString(), body, sig, Now));
        Assert.False(WebhookSigner.Verify(secret, ts.ToString(), "{\"a\":2}", sig, Now));                  // body changed
        Assert.False(WebhookSigner.Verify("other", ts.ToString(), body, sig, Now));                        // wrong secret
        Assert.False(WebhookSigner.Verify(secret, (ts + 1).ToString(), body, sig, Now));                   // timestamp changed
        Assert.False(WebhookSigner.Verify(secret, ts.ToString(), body, sig, Now.AddMinutes(6)));           // too old
        Assert.False(WebhookSigner.Verify(secret, ts.ToString(), body, sig, Now.AddMinutes(-6)));          // from the future
        Assert.True(WebhookSigner.Verify(secret, ts.ToString(), body, sig, Now.AddMinutes(4)));
        Assert.False(WebhookSigner.Verify(secret, "abc", body, sig, Now));
        Assert.False(WebhookSigner.Verify(secret, ts.ToString(), body, null, Now));
        Assert.False(WebhookSigner.Verify("", ts.ToString(), body, sig, Now));
    }
}
