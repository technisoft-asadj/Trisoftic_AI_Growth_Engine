using System.Net.Http.Json;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;

namespace TriSoftic.GrowthEngine.Modules.Ai;

public sealed record ProviderResponse(string Text, int InputTokens, int OutputTokens);

public interface IAiProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    string? ModelFor(AiTier tier);
    /// <exception cref="AiException">Outcome Refused when the provider declined; Failed otherwise.</exception>
    Task<ProviderResponse> CompleteAsync(string model, string system, string user, int maxTokens, CancellationToken ct);
}

/// <summary>
/// Claude via the official SDK. Thinking is left at the model default (always-on adaptive on current models) and no
/// sampling parameters or forced tool_choice are sent, which current models reject. A refusal is surfaced, never
/// silently re-routed to another model (requirements must not change silently, Architecture 5.4).
/// </summary>
public sealed class AnthropicProvider(IOptions<AiOptions> options) : IAiProvider
{
    private AnthropicClient? _client;
    public string Name => "Anthropic";
    public bool IsConfigured => options.Value.Anthropic.Enabled;
    public string? ModelFor(AiTier tier) => options.Value.Anthropic.Models.GetValueOrDefault(tier.ToString());

    public async Task<ProviderResponse> CompleteAsync(string model, string system, string user, int maxTokens, CancellationToken ct)
    {
        _client ??= new AnthropicClient { ApiKey = options.Value.Anthropic.ApiKey };
        Message response;
        try
        {
            response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = model,
                MaxTokens = maxTokens,
                System = system,
                Messages = [new UserMessageParam { Content = user }],
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AiException($"Anthropic request failed: {ex.Message}", AiOutcome.Failed, ex);
        }

        var stop = response.StopReason?.ToString() ?? "";
        if (stop.Contains("refusal", StringComparison.OrdinalIgnoreCase))
            throw new AiException("The model declined this request (safety refusal).", AiOutcome.Refused);
        if (stop.Contains("max_tokens", StringComparison.OrdinalIgnoreCase))
            throw new AiException("The response was cut off at the token limit.", AiOutcome.Failed);

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return new ProviderResponse(text, (int)response.Usage.InputTokens, (int)response.Usage.OutputTokens);
    }
}

/// <summary>OpenAI Chat Completions over HTTP. Disabled unless an API key and tier->model map are configured.</summary>
public sealed class OpenAiProvider(IHttpClientFactory http, IOptions<AiOptions> options) : IAiProvider
{
    public string Name => "OpenAi";
    public bool IsConfigured => options.Value.OpenAi.Enabled;
    public string? ModelFor(AiTier tier) => options.Value.OpenAi.Models.GetValueOrDefault(tier.ToString());

    public async Task<ProviderResponse> CompleteAsync(string model, string system, string user, int maxTokens, CancellationToken ct)
    {
        var o = options.Value.OpenAi;
        var client = http.CreateClient("openai");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl!.TrimEnd('/')}/v1/chat/completions");
        req.Headers.Authorization = new("Bearer", o.ApiKey);
        req.Content = JsonContent.Create(new
        {
            model,
            max_completion_tokens = maxTokens,
            messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
        });

        HttpResponseMessage res;
        try { res = await client.SendAsync(req, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new AiException($"OpenAI request failed: {ex.Message}", AiOutcome.Failed, ex); }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
                throw new AiException($"OpenAI returned {(int)res.StatusCode}", AiOutcome.Failed);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var choice = doc.RootElement.GetProperty("choices")[0];
            var finish = choice.TryGetProperty("finish_reason", out var f) ? f.GetString() : null;
            if (finish == "content_filter") throw new AiException("The model declined this request (content filter).", AiOutcome.Refused);
            if (finish == "length") throw new AiException("The response was cut off at the token limit.", AiOutcome.Failed);
            var text = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
            var usage = doc.RootElement.GetProperty("usage");
            return new ProviderResponse(text, usage.GetProperty("prompt_tokens").GetInt32(), usage.GetProperty("completion_tokens").GetInt32());
        }
    }
}
