using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Results;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Ai;

public sealed record ImageProviderRequest(string Prompt, string NegativePrompt, string AspectRatio);
public sealed record ImageProviderResult(byte[] Bytes, string ContentType);

public interface IImageProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    string Model { get; }
    /// <exception cref="AiException">Refused when the provider's own filter declined; Failed otherwise.</exception>
    Task<ImageProviderResult> GenerateAsync(ImageProviderRequest request, CancellationToken ct);
}

/// <summary>Stability AI models on Amazon Bedrock (InvokeModel). Same AWS credential chain as Bedrock text; no extra account or key.</summary>
public sealed class BedrockStabilityProvider(IOptions<AiOptions> options) : IImageProvider
{
    private Amazon.BedrockRuntime.AmazonBedrockRuntimeClient? _client;
    public string Name => "Bedrock";
    public bool IsConfigured => options.Value.Bedrock.BedrockEnabled;
    public string Model => options.Value.Images.Model;

    public async Task<ImageProviderResult> GenerateAsync(ImageProviderRequest r, CancellationToken ct)
    {
        var o = options.Value.Images;
        _client ??= new Amazon.BedrockRuntime.AmazonBedrockRuntimeClient(Amazon.RegionEndpoint.GetBySystemName(o.Region));
        var body = JsonSerializer.SerializeToUtf8Bytes(new { prompt = r.Prompt, negative_prompt = r.NegativePrompt, aspect_ratio = r.AspectRatio, output_format = "png" });
        Amazon.BedrockRuntime.Model.InvokeModelResponse res;
        try
        {
            res = await _client.InvokeModelAsync(new Amazon.BedrockRuntime.Model.InvokeModelRequest
            {
                ModelId = o.Model, ContentType = "application/json", Accept = "application/json", Body = new MemoryStream(body),
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AiException($"Image request failed: {ex.Message}", AiOutcome.Failed, ex);
        }

        using var doc = await JsonDocument.ParseAsync(res.Body, cancellationToken: ct);
        var root = doc.RootElement;
        // On success finish_reasons is [null]; a safety filter reports its reason as text and returns no usable picture.
        if (root.TryGetProperty("finish_reasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array
            && reasons.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.String } why)
            throw new AiException("The image model declined this request (" + why.GetString() + ").", AiOutcome.Refused);
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0 || images[0].GetString() is not { Length: > 0 } b64)
            throw new AiException("The image model returned no picture.", AiOutcome.Failed);
        return new ImageProviderResult(Convert.FromBase64String(b64), "image/png");
    }
}

public sealed record ImageRequest(Guid ProductId, string Brief, string AspectRatio);
public sealed record ImageOutput(Guid AssetId, string Model, string AspectRatio, decimal CostUsd);

/// <summary>
/// What an AI picture may and may not be (ADR-24): an illustration, never a fake screenshot, fake gameplay, readable text, a logo or a real person.
/// The brief is checked here before anything is sent or paid for, and the same rules are written into the prompt and the negative prompt.
/// </summary>
public static partial class ImagePrompt
{
    public static readonly string[] AspectRatios = ["1:1", "4:5", "5:4", "16:9", "9:16", "3:2", "2:3", "21:9", "9:21"];
    public const int MaxBriefLength = 500;

    public const string Negative = "photograph, photorealistic, photo, text, letters, words, captions, watermark, logo, brand mark, user interface, screenshot, app screen, HUD, buttons, score, "
        + "realistic human face, photograph of a real person, celebrity, low quality, blurry, distorted";

    private static readonly (Regex Pattern, string Why)[] Refusals =
    [
        (Screens(), "an illustration cannot show a screenshot, app screen or user interface, because it would look like the real product. Upload a real screenshot instead."),
        (Gameplay(), "an illustration cannot show gameplay, because it would look like the real game. Upload real gameplay footage or a real screenshot instead."),
        (Words(), "an illustration cannot contain readable text. Put any words in the post text."),
        (Marks(), "an illustration cannot contain a logo, badge or someone else's brand. Upload your real logo as a file."),
        (People(), "an illustration cannot show a real person or a famous name."),
        (Humans(), "AI pictures of people come out looking like real photographs of real-looking strangers, which is misleading in marketing. Describe objects, places or a cartoon character instead (for example a colourful board on a sunny balcony)."),
    ];

    /// <returns>A plain-language reason when the brief asks for something an AI picture must not be, otherwise null.</returns>
    public static string? Refusal(string brief)
    {
        foreach (var (p, why) in Refusals) if (p.IsMatch(brief)) return "This brief was not sent: " + why;
        return null;
    }

    public static string Build(ApprovedKnowledge k, string brief)
    {
        var sb = new StringBuilder();
        sb.Append(brief.Trim().TrimEnd('.')).Append(". ");
        sb.Append($"A flat stylised cartoon-style digital illustration (clearly not a photograph, painted look, simplified shapes) for a marketing post about \"{k.ProductName}\" ({Short(k.Summary, 200)}). ");
        if (!string.IsNullOrWhiteSpace(k.Tone)) sb.Append($"Mood: {Short(k.Tone!, 80)}. ");
        sb.Append("Clean composition, bright colours, no text, no letters, no logos, no interface elements, no real people.");
        return sb.ToString();
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"\b(screen ?shots?|app screens?|screens? of|ui|user interface|interface|mock ?-?ups?|dashboard of|in-app)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Screens();
    [GeneratedRegex(@"\b(game ?play|in-?game|gameplay footage|level screen|leaderboard)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Gameplay();
    [GeneratedRegex(@"\b(text|the words?|slogan|headline|caption|typography|that says|that reads|lettering|written on)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Words();
    [GeneratedRegex(@"\b(logos?|badges?|trademarks?|app store|google play|play store|facebook|instagram|tiktok|whatsapp|youtube|brand ?marks?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Marks();
    [GeneratedRegex(@"\b(friends?|family|families|people|persons?|man|men|woman|women|girls?|boys?|kids?|child|children|crowd|couple|group of|players?|faces?|smil\w+|laugh\w+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Humans();
    [GeneratedRegex(@"\b(celebrit\w+|famous|real person|politician|president|actor|actress|footballer|cricketer|influencer|portrait of)\b", RegexOptions.IgnoreCase)]
    private static partial Regex People();
}

/// <summary>Makes one AI picture for a product: checks the brief, the budgets and the pause switch, calls the image model, records the cost, and files the result in the asset library.</summary>
public sealed class ImageGenerator(
    IProductKnowledge knowledge, IEnumerable<IImageProvider> providers, IOptions<AiOptions> options, IPauseSwitch pause, AiDbContext db,
    IGeneratedAssetStore store, ICurrentUser user, ICorrelationContext corr, IClock clock)
{
    public async Task<Result<ImageOutput>> GenerateAsync(ImageRequest r, CancellationToken ct)
    {
        var opt = options.Value;
        if (string.IsNullOrWhiteSpace(r.Brief) || r.Brief.Length > ImagePrompt.MaxBriefLength)
            return new Error("image.brief", $"Describe the picture in up to {ImagePrompt.MaxBriefLength} characters.", ErrorKind.Validation);
        if (!ImagePrompt.AspectRatios.Contains(r.AspectRatio))
            return new Error("image.shape", "Choose one of these shapes: " + string.Join(", ", ImagePrompt.AspectRatios) + ".", ErrorKind.Validation);
        if (ImagePrompt.Refusal(r.Brief) is { } refusal) return new Error("image.refused", refusal, ErrorKind.Policy);

        var k = await knowledge.GetApprovedAsync(r.ProductId, ct);
        if (k is null) return new Error("grounding.no_approved_profile", "This product has no approved profile yet. Submit and approve a profile before generating pictures.", ErrorKind.Conflict);

        var provider = providers.FirstOrDefault(p => p.IsConfigured);
        if (provider is null) return new Error("image.not_configured", "AI pictures are not set up: they need AWS Bedrock (Ai:Bedrock:Region).", ErrorKind.Conflict);
        var model = provider.Model;

        if (await pause.IsPausedAsync(PauseScope.Ai, ct)) throw await Blocked(provider.Name, model, r.ProductId, AiOutcome.Paused, "AI generation is paused by an administrator");
        var price = opt.Images.PriceFor(model);
        var (productSpend, globalSpend, productImages) = await MonthToDateAsync(r.ProductId, ct);
        if (productImages >= opt.Images.MaxPerProductPerMonth)
            throw await Blocked(provider.Name, model, r.ProductId, AiOutcome.BudgetBlocked, $"This product has made its {opt.Images.MaxPerProductPerMonth} pictures for the month");
        if (productSpend + price > opt.MonthlyBudgetUsdPerProduct)
            throw await Blocked(provider.Name, model, r.ProductId, AiOutcome.BudgetBlocked, $"Monthly AI budget for this product is used up (${productSpend:F2} of ${opt.MonthlyBudgetUsdPerProduct:F2})");
        if (globalSpend + price > opt.MonthlyBudgetUsdGlobal)
            throw await Blocked(provider.Name, model, r.ProductId, AiOutcome.BudgetBlocked, $"Global monthly AI budget is used up (${globalSpend:F2} of ${opt.MonthlyBudgetUsdGlobal:F2})");

        var prompt = Redactor.Redact(ImagePrompt.Build(k, r.Brief));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ImageProviderResult result;
        try { result = await provider.GenerateAsync(new ImageProviderRequest(prompt, ImagePrompt.Negative, r.AspectRatio), ct); }
        catch (AiException ex)
        {
            await Record(provider.Name, model, r.ProductId, 0, ex.Outcome, (int)sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
        await Record(provider.Name, model, r.ProductId, price, AiOutcome.Succeeded, (int)sw.ElapsedMilliseconds, null);

        var name = $"ai-{Slug(k.ProductName)}-{clock.UtcNow:yyyyMMdd-HHmmss}.png";
        var provenance = $"AI-generated with {model} on {clock.UtcNow:yyyy-MM-dd}. Brief: {r.Brief.Trim()} (profile v{k.ProfileVersion}, shape {r.AspectRatio}). An illustration, not a real screenshot.";
        var (assetId, error) = await store.SaveAsync(new NewGeneratedAsset(r.ProductId, name, result.ContentType, result.Bytes, provenance), ct);
        if (assetId is null) return new Error("image.save", error ?? "The picture could not be saved.", ErrorKind.Validation);
        return new ImageOutput(assetId.Value, model, r.AspectRatio, price);
    }

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } x ? (x.Length > 40 ? x[..40] : x) : "product";

    private async Task<AiException> Blocked(string provider, string model, Guid productId, AiOutcome outcome, string message)
    {
        await Record(provider, model, productId, 0, outcome, 0, message);
        return new AiException(message, outcome);
    }

    private async Task Record(string provider, string model, Guid productId, decimal cost, AiOutcome outcome, int ms, string? error)
    {
        db.Usage.Add(new AiUsageRecord
        {
            OccurredAt = clock.UtcNow, Provider = provider, Model = model, Purpose = "image.generate", TemplateKey = "image.generate", TemplateVersion = 1,
            ProductId = productId, UserId = user.UserId, EstimatedCostUsd = cost, Outcome = outcome, LatencyMs = ms, CorrelationId = corr.CorrelationId,
            Error = error is null ? null : (error.Length > 1900 ? error[..1900] : error),
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<(decimal Product, decimal Global, int ProductImages)> MonthToDateAsync(Guid productId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var q = db.Usage.AsNoTracking().Where(u => u.OccurredAt >= start && u.Outcome == AiOutcome.Succeeded);
        var global = await q.SumAsync(u => (decimal?)u.EstimatedCostUsd, ct) ?? 0m;
        var mine = q.Where(u => u.ProductId == productId);
        var product = await mine.SumAsync(u => (decimal?)u.EstimatedCostUsd, ct) ?? 0m;
        var images = await mine.CountAsync(u => u.Purpose == "image.generate", ct);
        return (product, global, images);
    }
}

/// <summary>The AI module's side of <see cref="IImageMaker"/>: the same generator, guards, caps and ledger as the editor's picture box.</summary>
public sealed class ImageMaker(ImageGenerator gen) : IImageMaker
{
    public string? CheckBrief(string brief)
        => string.IsNullOrWhiteSpace(brief) || brief.Length > ImagePrompt.MaxBriefLength ? $"Describe the picture in up to {ImagePrompt.MaxBriefLength} characters." : ImagePrompt.Refusal(brief);

    public async Task<(Guid? AssetId, string? Error, decimal CostUsd)> MakeAsync(Guid productId, string brief, string aspectRatio, CancellationToken ct = default)
    {
        var r = await gen.GenerateAsync(new ImageRequest(productId, brief, aspectRatio), ct);
        return r.IsSuccess ? (r.Value!.AssetId, null, r.Value.CostUsd) : (null, r.Error!.Message, 0m);
    }
}
