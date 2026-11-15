using Microsoft.Extensions.Options;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.Modules.Ai;

namespace TriSoftic.GrowthEngine.Tests.Unit;

/// <summary>
/// Opt-in smoke test against the real Bedrock endpoint. Costs a fraction of a cent.
/// Run with: TE_LIVE_BEDROCK=us-east-1 dotnet test --filter LiveBedrock   (returns immediately when the variable is unset)
/// </summary>
public sealed class LiveBedrockTests
{
    [Fact]
    public async Task LiveBedrock_haiku_round_trip()
    {
        var region = Environment.GetEnvironmentVariable("TE_LIVE_BEDROCK");
        if (string.IsNullOrWhiteSpace(region)) return;

        var opts = new AiOptions { Bedrock = new ProviderOptions { Region = region, Models = new() { ["Draft"] = Environment.GetEnvironmentVariable("TE_LIVE_MODEL") ?? "us.anthropic.claude-haiku-4-5-20251001-v1:0" } } };
        var provider = new AnthropicBedrockProvider(Options.Create(opts));
        Assert.True(provider.IsConfigured);

        var r = await provider.CompleteAsync(provider.ModelFor(AiTier.Draft)!, "Reply with exactly one word.", "Say: pong", 256, CancellationToken.None);
        Console.WriteLine($"LIVE: '{r.Text.Trim()}' in={r.InputTokens} out={r.OutputTokens}");
        Assert.False(string.IsNullOrWhiteSpace(r.Text));
        Assert.True(r.OutputTokens > 0);
    }
}
