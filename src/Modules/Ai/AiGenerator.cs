using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Ai;

public sealed class AiGenerator(
    AiDbContext db, IEnumerable<IAiProvider> providers, IOptions<AiOptions> options, IPauseSwitch pause,
    ICurrentUser user, ICorrelationContext corr, IClock clock, ILogger<AiGenerator> log) : IAiGenerator
{
    public async Task<AiResult> GenerateAsync(AiRequest r, CancellationToken ct = default)
    {
        var opt = options.Value;
        var providerName = r.ProviderOverride ?? opt.DefaultProvider;
        var provider = providers.FirstOrDefault(p => p.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase) && p.IsConfigured);
        var model = provider?.ModelFor(r.Tier);

        if (await pause.IsPausedAsync(PauseScope.Ai, ct))
            throw await Blocked(r, providerName, model ?? "-", AiOutcome.Paused, "AI generation is paused by an administrator", ct);
        if (provider is null || model is null)
            throw await Blocked(r, providerName, model ?? "-", AiOutcome.Failed, $"AI provider '{providerName}' is not configured", ct);

        var (productSpend, globalSpend) = await MonthToDateAsync(r.ProductId, ct);
        if (r.ProductId is not null && productSpend >= opt.MonthlyBudgetUsdPerProduct)
            throw await Blocked(r, provider.Name, model, AiOutcome.BudgetBlocked, $"Monthly AI budget for this product is used up (${productSpend:F2} of ${opt.MonthlyBudgetUsdPerProduct:F2})", ct);
        if (globalSpend >= opt.MonthlyBudgetUsdGlobal)
            throw await Blocked(r, provider.Name, model, AiOutcome.BudgetBlocked, $"Global monthly AI budget is used up (${globalSpend:F2} of ${opt.MonthlyBudgetUsdGlobal:F2})", ct);

        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await provider.CompleteAsync(model, Redactor.Redact(r.System), Redactor.Redact(r.User), r.MaxTokens, ct);
            var cost = opt.EstimateCost(model, resp.InputTokens, resp.OutputTokens);
            if (cost == 0 && resp.OutputTokens > 0) log.LogWarning("No price configured for model {Model}; cost recorded as 0", model);
            await Record(r, provider.Name, model, resp.InputTokens, resp.OutputTokens, cost, AiOutcome.Succeeded, (int)sw.ElapsedMilliseconds, null);
            return new AiResult(resp.Text, provider.Name, model, resp.InputTokens, resp.OutputTokens, cost);
        }
        catch (AiException ex)
        {
            await Record(r, provider.Name, model, 0, 0, 0, ex.Outcome, (int)sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
    }

    private async Task<AiException> Blocked(AiRequest r, string provider, string model, AiOutcome outcome, string message, CancellationToken ct)
    {
        await Record(r, provider, model, 0, 0, 0, outcome, 0, message);
        return new AiException(message, outcome);
    }

    private async Task Record(AiRequest r, string provider, string model, int inTok, int outTok, decimal cost, AiOutcome outcome, int ms, string? error)
    {
        db.Usage.Add(new AiUsageRecord
        {
            OccurredAt = clock.UtcNow, Provider = provider, Model = model, Purpose = r.Purpose, TemplateKey = r.TemplateKey, TemplateVersion = r.TemplateVersion,
            ProductId = r.ProductId, UserId = user.UserId, InputTokens = inTok, OutputTokens = outTok, EstimatedCostUsd = cost,
            Outcome = outcome, LatencyMs = ms, CorrelationId = corr.CorrelationId, Error = error is null ? null : (error.Length > 1900 ? error[..1900] : error),
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<(decimal Product, decimal Global)> MonthToDateAsync(Guid? productId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var q = db.Usage.AsNoTracking().Where(u => u.OccurredAt >= start && u.Outcome == AiOutcome.Succeeded);
        var global = await q.SumAsync(u => (decimal?)u.EstimatedCostUsd, ct) ?? 0m;
        var product = productId is null ? 0m : await q.Where(u => u.ProductId == productId).SumAsync(u => (decimal?)u.EstimatedCostUsd, ct) ?? 0m;
        return (product, global);
    }
}
