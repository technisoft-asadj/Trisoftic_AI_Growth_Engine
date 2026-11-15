using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TriSoftic.GrowthEngine.Modules.Automation;

public sealed class Condition
{
    public string Field { get; set; } = "";
    /// <summary>eq, ne, gt, lt, contains, exists, empty</summary>
    public string Op { get; set; } = "eq";
    public string? Value { get; set; }
}

/// <summary>One step. Which fields apply depends on <see cref="Kind"/>: wait, if, task, notify, webhook.</summary>
public sealed class Step
{
    public string Kind { get; set; } = "";
    public int? Minutes { get; set; }
    public string? Field { get; set; }
    public string? Op { get; set; }
    public string? Value { get; set; }
    public string? Title { get; set; }
    public string? Notes { get; set; }
    public int? DueInMinutes { get; set; }
    /// <summary>"owner" (the record's ownerUserId), "none", or a user id.</summary>
    public string? Assignee { get; set; }
    public string? To { get; set; }
    public string? Message { get; set; }
    public string? Name { get; set; }
}

public sealed class WorkflowDefinition
{
    /// <summary>All must hold when the event arrives or no run is started.</summary>
    public List<Condition> Conditions { get; set; } = [];
    public List<Step> Steps { get; set; } = [];

    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);
    public string ToJson() => JsonSerializer.Serialize(this, Opts);
    public static WorkflowDefinition FromJson(string json) => JsonSerializer.Deserialize<WorkflowDefinition>(json, Opts) ?? new();
}

public static partial class Definitions
{
    public static readonly string[] Ops = ["eq", "ne", "gt", "lt", "contains", "exists", "empty"];
    public static readonly string[] StepKinds = ["wait", "if", "task", "notify", "webhook", "email"];
    public const int MaxSteps = 20;
    public const int MaxWaitMinutes = 60 * 24 * 30;

    /// <summary>Events workflows may start from, with the facts each record offers for conditions and templates.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Triggers = new Dictionary<string, string[]>
    {
        ["lead.created"] = ["status", "source", "score", "ownerUserId", "campaignId", "contactName", "nextAction"],
        ["lead.assigned"] = ["status", "source", "score", "ownerUserId", "campaignId", "contactName"],
        ["lead.status-changed"] = ["status", "previousStatus", "source", "score", "ownerUserId", "contactName"],
        ["opportunity.created"] = ["name", "status", "stage", "stageKind", "value", "currency", "ownerUserId", "contactName"],
        ["opportunity.stage-changed"] = ["name", "status", "stage", "previousStage", "stageKind", "value", "currency", "ownerUserId", "contactName"],
        ["booking.created"] = ["status", "attendeeName", "attendeeEmail", "start", "ownerUserId", "leadId"],
        ["booking.cancelled"] = ["status", "attendeeName", "attendeeEmail", "start", "ownerUserId", "leadId"],
    };

    public static bool IsKnownTrigger(string t) => Triggers.ContainsKey(t) || ExternalTrigger().IsMatch(t);

    [GeneratedRegex(@"^external\.[a-z0-9._-]{1,50}$")]
    private static partial Regex ExternalTrigger();

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}")]
    private static partial Regex Token();

    /// <summary>Returns the problems found, or an empty list when the definition is runnable.</summary>
    public static List<string> Validate(WorkflowDefinition d, Func<string, bool> webhookExists, bool emailAvailable)
    {
        var errors = new List<string>();
        if (d.Steps.Count == 0) errors.Add("Add at least one step");
        if (d.Steps.Count > MaxSteps) errors.Add($"At most {MaxSteps} steps");
        if (d.Conditions.Count > 10) errors.Add("At most 10 conditions");
        foreach (var c in d.Conditions) CheckCondition(c.Field, c.Op, c.Value, "Condition", errors);

        for (var i = 0; i < d.Steps.Count; i++)
        {
            var s = d.Steps[i]; var at = $"Step {i + 1}";
            if (!StepKinds.Contains(s.Kind)) { errors.Add($"{at}: unknown kind '{s.Kind}'"); continue; }
            switch (s.Kind)
            {
                case "wait":
                    if (s.Minutes is null or < 1 || s.Minutes > MaxWaitMinutes) errors.Add($"{at}: wait between 1 minute and 30 days");
                    break;
                case "if": CheckCondition(s.Field, s.Op, s.Value, at, errors); break;
                case "task":
                    if (string.IsNullOrWhiteSpace(s.Title) || s.Title.Length > 300) errors.Add($"{at}: a task title is required");
                    if (s.DueInMinutes is < 0 or > MaxWaitMinutes) errors.Add($"{at}: due time is out of range");
                    if (!ValidRecipient(s.Assignee ?? "owner", allowNone: true)) errors.Add($"{at}: assignee must be 'owner', 'none' or a user id");
                    break;
                case "notify":
                    if (string.IsNullOrWhiteSpace(s.Message) || s.Message.Length > 1000) errors.Add($"{at}: a message is required");
                    if (!ValidRecipient(s.To ?? "owner", allowNone: false)) errors.Add($"{at}: recipient must be 'owner' or a user id");
                    break;
                case "webhook":
                    if (string.IsNullOrWhiteSpace(s.Name) || !webhookExists(s.Name)) errors.Add($"{at}: webhook '{s.Name}' is not configured");
                    break;
                case "email":
                    if (!emailAvailable) errors.Add($"{at}: sending email is not available yet");
                    break;
            }
        }
        return errors;
    }

    private static bool ValidRecipient(string v, bool allowNone) => v == "owner" || (allowNone && v == "none") || Guid.TryParse(v, out _);

    private static void CheckCondition(string? field, string? op, string? value, string at, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(field) || field.Length > 60) errors.Add($"{at}: a field is required");
        if (op is null || !Ops.Contains(op)) errors.Add($"{at}: operator must be one of {string.Join(", ", Ops)}");
        else if (op is not ("exists" or "empty") && string.IsNullOrEmpty(value)) errors.Add($"{at}: a value is required for '{op}'");
    }

    public static bool Matches(IReadOnlyDictionary<string, object?> ctx, string field, string op, string? value)
    {
        ctx.TryGetValue(field, out var raw);
        var actual = raw switch { null => null, bool b => b ? "true" : "false", IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => raw.ToString() };
        switch (op)
        {
            case "exists": return !string.IsNullOrEmpty(actual);
            case "empty": return string.IsNullOrEmpty(actual);
        }
        if (actual is null || value is null) return op == "ne" && !(actual is null && value is null);
        var numeric = double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) & double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v);
        return op switch
        {
            "eq" => numeric ? a == v : string.Equals(actual, value, StringComparison.OrdinalIgnoreCase),
            "ne" => numeric ? a != v : !string.Equals(actual, value, StringComparison.OrdinalIgnoreCase),
            "gt" => numeric && a > v,
            "lt" => numeric && a < v,
            "contains" => actual.Contains(value, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public static bool AllMatch(IReadOnlyDictionary<string, object?> ctx, IEnumerable<Condition> conditions) => conditions.All(c => Matches(ctx, c.Field, c.Op, c.Value));

    /// <summary>Replaces {{field}} with the record's current value. Unknown fields become empty, never an error.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, object?> ctx, int max = 1000)
    {
        var s = Token().Replace(template, m => ctx.TryGetValue(m.Groups[1].Value, out var v) && v is not null ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "");
        return s.Length <= max ? s : s[..max];
    }
}

/// <summary>HMAC signing for webhooks in both directions (n8n and any other automation tool).</summary>
public static class WebhookSigner
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static string Sign(string secret, long unixSeconds, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{unixSeconds}.{body}"));
        return "sha256=" + Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>True when the signature matches and the timestamp is within five minutes either way. Replay inside that window is stopped by the receipt table.</summary>
    public static bool Verify(string secret, string? timestamp, string body, string? signature, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signature) || !long.TryParse(timestamp, out var ts)) return false;
        var age = now - DateTimeOffset.FromUnixTimeSeconds(ts);
        if (age > Tolerance || age < -Tolerance) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(secret, ts, body)), Encoding.UTF8.GetBytes(signature));
    }

    public static string Hash(string signature) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
}
