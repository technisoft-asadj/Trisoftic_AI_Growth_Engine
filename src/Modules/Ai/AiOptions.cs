namespace TriSoftic.GrowthEngine.Modules.Ai;

public sealed class ModelPrice { public decimal InputPerMTok { get; set; } public decimal OutputPerMTok { get; set; } }

public sealed class ProviderOptions
{
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }
    /// <summary>Tier name (Draft/Strategy) -> model id.</summary>
    public Dictionary<string, string> Models { get; set; } = [];
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey) && Models.Count > 0;
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

    /// <summary>USD per million tokens, first-party list prices (override in config when prices change).</summary>
    public Dictionary<string, ModelPrice> Prices { get; set; } = new()
    {
        ["claude-opus-5-5"] = new() { InputPerMTok = 4m, OutputPerMTok = 20m },
        ["claude-sonnet-5-5"] = new() { InputPerMTok = 2m, OutputPerMTok = 10m },
        ["claude-haiku-5-5"] = new() { InputPerMTok = 0.10m, OutputPerMTok = 0.50m },
    };

    public decimal EstimateCost(string model, int input, int output) =>
        Prices.TryGetValue(model, out var p) ? (input * p.InputPerMTok + output * p.OutputPerMTok) / 1_000_000m : 0m;
}
