namespace TriSoftic.GrowthEngine.Modules.Ai;

public sealed class ModelPrice { public decimal InputPerMTok { get; set; } public decimal OutputPerMTok { get; set; } }

public sealed class ProviderOptions
{
    public string? ApiKey { get; set; }
    /// <summary>AWS region (Bedrock only). Credentials come from the standard AWS chain (env, profile, instance/task role).</summary>
    public string? Region { get; set; }
    public string? BaseUrl { get; set; }
    /// <summary>Tier name (Draft/Strategy) -> model id.</summary>
    public Dictionary<string, string> Models { get; set; } = [];
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey) && Models.Count > 0;
    public bool BedrockEnabled => !string.IsNullOrWhiteSpace(Region) && Models.Count > 0;
}

/// <summary>Bound from the "Ai" configuration section. Keys come from the environment / secret store, never source control.</summary>
public sealed class AiOptions
{
    public string DefaultProvider { get; set; } = "Anthropic";
    public decimal MonthlyBudgetUsdPerProduct { get; set; } = 20m;
    public decimal MonthlyBudgetUsdGlobal { get; set; } = 60m;
    public decimal AlertThreshold { get; set; } = 0.8m;

    public ProviderOptions Anthropic { get; set; } = new()
    {
        Models = new() { ["Draft"] = "claude-sonnet-5-5", ["Strategy"] = "claude-opus-5-5" },
    };
    public ProviderOptions OpenAi { get; set; } = new() { BaseUrl = "https://api.openai.com" };

    /// <summary>Claude on Amazon Bedrock. Enabled by setting Ai:Bedrock:Region (e.g. us-east-1) and, if needed, overriding model ids.</summary>
    public ProviderOptions Bedrock { get; set; } = new()
    {
        // Verified usable on the dev account in us-east-1. The 5.x ids exist on Bedrock but need account entitlement; override via config when enabled.
        Models = new() { ["Draft"] = "us.anthropic.claude-sonnet-4-6", ["Strategy"] = "us.anthropic.claude-opus-4-6-v1" },
    };

    /// <summary>USD per million tokens, first-party list prices (override in config when prices change).</summary>
    public Dictionary<string, ModelPrice> Prices { get; set; } = new()
    {
        ["claude-opus-5-5"] = new() { InputPerMTok = 4m, OutputPerMTok = 20m },
        ["claude-sonnet-5-5"] = new() { InputPerMTok = 2m, OutputPerMTok = 10m },
        ["claude-haiku-5-5"] = new() { InputPerMTok = 0.10m, OutputPerMTok = 0.50m },
        ["claude-opus-4-6"] = new() { InputPerMTok = 5m, OutputPerMTok = 25m },
        ["claude-sonnet-4-6"] = new() { InputPerMTok = 3m, OutputPerMTok = 15m },
        ["claude-haiku-4-5"] = new() { InputPerMTok = 1m, OutputPerMTok = 5m },
    };

    /// <summary>Estimate only. Bedrock bills separately from first-party; the same list prices are used unless overridden under Ai:Prices.</summary>
    public decimal EstimateCost(string model, int input, int output)
    {
        // Normalise Bedrock ids: "us.anthropic.claude-opus-4-6-v1" / "anthropic.claude-haiku-4-5-20251001-v1:0" -> "claude-opus-4-6" / "claude-haiku-4-5"
        var key = System.Text.RegularExpressions.Regex.Replace(model, @"^(us\.|eu\.|apac\.|global\.)?anthropic\.|(-\d{8})?(-v\d+(:\d+)?)?$", "");
        if (!Prices.TryGetValue(model, out var p) && !Prices.TryGetValue(key, out p)) return 0m;
        return (input * p.InputPerMTok + output * p.OutputPerMTok) / 1_000_000m;
    }
}
