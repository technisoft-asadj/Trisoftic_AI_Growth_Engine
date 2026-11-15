using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriSoftic.GrowthEngine.BuildingBlocks.Abstractions;
using TriSoftic.GrowthEngine.BuildingBlocks.Parsing;
using TriSoftic.GrowthEngine.BuildingBlocks.Persistence;
using TriSoftic.GrowthEngine.BuildingBlocks.Security;

namespace TriSoftic.GrowthEngine.Modules.Analytics;

public static partial class AnalyticsModule
{
    public sealed record SourceIn(Guid? ProductId, string Name, SourceKind Kind, int? FreshnessHours, bool? Enabled);
    public sealed record MetricIn(string Key, string Name, string Category, MetricUnit Unit, Aggregation Aggregation, Direction Direction, string Definition);

    [GeneratedRegex("^[a-z][a-z0-9_]{1,39}$")] private static partial Regex KeyRx();

    public static IServiceCollection AddAnalyticsModule(this IServiceCollection s, IConfiguration c, bool runScheduler)
    {
        s.AddModuleDbContext<AnalyticsDbContext>(c, AnalyticsDbContext.Schema);
        s.AddScoped<MetricStore>();
        s.AddScoped<IMetricSink>(sp => sp.GetRequiredService<MetricStore>());
        s.AddScoped<IMetricReader>(sp => sp.GetRequiredService<MetricStore>());
        s.AddScoped<ISubjectStateProvider, DataSourceStateProvider>();
        s.AddScoped<IJobHandler, FreshnessCheckHandler>();
        if (runScheduler || c.GetValue<bool>("Jobs:RunInProcess")) s.AddHostedService<AnalyticsScheduler>();
        return s;
    }

    public static async Task SeedAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        using var scope = sp.CreateScope();
        await BuiltInMetrics.SeedAsync(scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>(), ct);
    }

    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/analytics").RequireAuthorization().WithTags("Analytics");

        g.MapGet("/metrics", async (AnalyticsDbContext db, ICurrentUser u, CancellationToken ct) =>
            u.HasPermission(Permissions.AnalyticsRead) ? Results.Ok(await db.Definitions.AsNoTracking().OrderBy(d => d.Category).ThenBy(d => d.Name).ToListAsync(ct)) : Results.Forbid());

        g.MapPost("/metrics", async (MetricIn req, AnalyticsDbContext db, ICurrentUser u, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.AnalyticsImport)) return Results.Forbid();
            if (!KeyRx().IsMatch(req.Key ?? "")) return Results.Problem(statusCode: 400, title: "The key is lowercase letters, digits and underscores, starting with a letter (2-40 characters)");
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Definition) || req.Definition.Length > 1000) return Results.Problem(statusCode: 400, title: "A name and a definition (what exactly is counted) are required");
            if (await db.Definitions.AnyAsync(d => d.Key == req.Key, ct)) return Results.Problem(statusCode: 409, title: "A metric with this key already exists");
            var d = new MetricDefinition { Key = req.Key!, Name = req.Name.Trim(), Category = string.IsNullOrWhiteSpace(req.Category) ? "Custom" : req.Category.Trim(), Unit = req.Unit, Aggregation = req.Aggregation, Direction = req.Direction, Definition = req.Definition.Trim() };
            db.Definitions.Add(d);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("analytics.metric.created", "MetricDefinition", d.Key, Summary: d.Name), ct);
            return Results.Created($"/api/v1/analytics/metrics/{d.Key}", d);
        });

        // ---- sources ----
        g.MapGet("/sources", async (AnalyticsDbContext db, ICurrentUser u, IClock clock, Guid? productId, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.AnalyticsRead)) return Results.Forbid();
            var scope = await u.AccessibleProductIdsAsync(ct);
            var q = db.Sources.AsNoTracking().AsQueryable();
            if (productId is { } p) { if (!await u.CanAccessProductAsync(p, ct)) return Results.NotFound(); q = q.Where(s => s.ProductId == p); }
            else if (scope is not null) q = q.Where(s => s.ProductId != null && scope.Contains(s.ProductId.Value));
            var rows = await q.OrderBy(s => s.Name).ToListAsync(ct);
            var counts = await db.Observations.AsNoTracking().GroupBy(o => o.DataSourceId).Select(gr => new { Id = gr.Key, N = gr.Count() }).ToListAsync(ct);
            var now = clock.UtcNow;
            return Results.Ok(rows.Select(s => new { s.Id, s.ProductId, s.Name, s.Kind, s.FreshnessHours, s.LastRetrievedAt, s.Enabled, stale = Freshness.IsStale(s.LastRetrievedAt, s.FreshnessHours, now), observations = counts.FirstOrDefault(c => c.Id == s.Id)?.N ?? 0 }));
        });

        g.MapPost("/sources", async (SourceIn req, AnalyticsDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.AnalyticsImport) || req.ProductId is not { } pid || !await u.CanAccessProductAsync(pid, ct)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200) return Results.Problem(statusCode: 400, title: "A name is required");
            if (req.FreshnessHours is < 1 or > 24 * 400) return Results.Problem(statusCode: 400, title: "Freshness window must be between 1 hour and 400 days");
            if (await db.Sources.AnyAsync(s => s.ProductId == pid && s.Name == req.Name.Trim(), ct)) return Results.Problem(statusCode: 409, title: "This product already has a source with that name");
            var s = new DataSource { ProductId = pid, Name = req.Name.Trim(), Kind = req.Kind, FreshnessHours = req.FreshnessHours ?? (req.Kind == SourceKind.Manual ? 24 * 35 : 48), CreatedAt = clock.UtcNow };
            db.Sources.Add(s);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("analytics.source.created", "DataSource", s.Id.ToString(), pid, s.Name, After: new { s.Kind, s.FreshnessHours }), ct);
            return Results.Created($"/api/v1/analytics/sources/{s.Id}", new { s.Id, s.Name, s.Kind, s.FreshnessHours });
        });

        // ---- imports ----
        g.MapPost("/products/{productId:guid}/imports", async (Guid productId, Guid sourceId, bool? dryRun, HttpRequest req, AnalyticsDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.AnalyticsImport) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == sourceId && s.ProductId == productId, ct);
            if (source is null) return Results.Problem(statusCode: 400, title: "Choose one of this product's data sources");
            if (!req.HasFormContentType) return Results.Problem(statusCode: 400, title: "Expected multipart/form-data with a CSV file");
            var form = await req.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Results.Problem(statusCode: 400, title: "A CSV file is required");
            if (file.Length > 2 * 1024 * 1024) return Results.Problem(statusCode: 400, title: "The file is larger than 2 MB");
            if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return Results.Problem(statusCode: 400, title: "Only .csv files are accepted");

            string text; using (var sr = new StreamReader(file.OpenReadStream(), Encoding.UTF8)) text = await sr.ReadToEndAsync(ct);
            var defs = await db.Definitions.AsNoTracking().ToDictionaryAsync(d => d.Key, ct);
            var now = clock.UtcNow;
            var (rows, errors, problem) = ImportParser.Parse(Csv.Parse(text), defs, DateOnly.FromDateTime(now.UtcDateTime));
            if (problem is not null) return Results.Problem(statusCode: 400, title: problem);

            // Never overwrite: a figure that is already there must be rolled back first, so every import stays reversible.
            var allErrors = errors.ToList(); var accepted = new List<ParsedRow>();
            var existing = await db.Observations.AsNoTracking().Where(o => o.DataSourceId == sourceId).Select(o => new { o.MetricKey, o.PeriodStart, o.PeriodEnd, o.Channel, o.Market, o.CampaignId, o.Currency }).ToListAsync(ct);
            var have = existing.Select(o => $"{o.MetricKey}|{o.PeriodStart}|{o.PeriodEnd}|{o.Channel.ToLowerInvariant()}|{o.Market.ToLowerInvariant()}|{o.CampaignId}|{o.Currency}").ToHashSet();
            foreach (var r in rows)
            {
                if (have.Contains($"{r.MetricKey}|{r.Start}|{r.End}|{r.Channel.ToLowerInvariant()}|{r.Market.ToLowerInvariant()}|{r.CampaignId}|{r.Currency}"))
                    allErrors.Add(new RowError(r.Row, "This figure is already stored for this source. Roll back the earlier import first if it was wrong"));
                else accepted.Add(r);
            }

            var preview = dryRun == true;
            Guid? batchId = null;
            if (!preview && accepted.Count > 0)
            {
                var batch = new ImportBatch { ProductId = productId, DataSourceId = sourceId, FileName = Path.GetFileName(file.FileName), Status = BatchStatus.Applied, Rows = rows.Count + errors.Count, Imported = accepted.Count, CreatedBy = u.UserId, CreatedAt = now };
                db.Batches.Add(batch);
                foreach (var r in accepted)
                    db.Observations.Add(new MetricObservation { ProductId = productId, DataSourceId = sourceId, ImportBatchId = batch.Id, MetricKey = r.MetricKey, PeriodStart = r.Start, PeriodEnd = r.End, Value = r.Value,
                        Status = r.Value is null ? ObservationStatus.Unavailable : ObservationStatus.Measured, Currency = r.Currency, Channel = r.Channel, Market = r.Market, CampaignId = r.CampaignId, RetrievedAt = now });
                source.LastRetrievedAt = now;
                await db.SaveChangesAsync(ct);
                batchId = batch.Id;
                await audit.WriteAsync(new AuditEntry("analytics.import.applied", "ImportBatch", batch.Id.ToString(), productId, batch.FileName, After: new { batch.Imported, Rejected = allErrors.Count, Source = source.Name }), ct);
            }
            return Results.Ok(new
            {
                dryRun = preview, batchId, rowsInFile = rows.Count + errors.Count, wouldImport = accepted.Count, imported = preview ? 0 : accepted.Count,
                unavailableValues = accepted.Count(r => r.Value is null), rejected = allErrors.Count,
                errors = allErrors.OrderBy(e => e.Row).Take(200),
                note = allErrors.Count > 0 ? "Rows listed under errors were not imported. Fix them and import again (rows that were accepted are not repeated)." : null,
            });
        }).DisableAntiforgery();

        g.MapGet("/products/{productId:guid}/imports", async (Guid productId, AnalyticsDbContext db, ICurrentUser u, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.AnalyticsRead) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var names = await db.Sources.AsNoTracking().Where(s => s.ProductId == productId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            return Results.Ok((await db.Batches.AsNoTracking().Where(b => b.ProductId == productId).OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct))
                .Select(b => new { b.Id, b.FileName, source = names.GetValueOrDefault(b.DataSourceId), b.Status, b.Imported, b.CreatedAt, b.RolledBackAt }));
        });

        g.MapPost("/imports/{id:guid}/rollback", async (Guid id, AnalyticsDbContext db, ICurrentUser u, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var b = await db.Batches.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (b is null || !u.HasPermission(Permissions.AnalyticsImport) || !await u.CanAccessProductAsync(b.ProductId, ct)) return Results.NotFound();
            if (b.Status == BatchStatus.RolledBack) return Results.NoContent();
            var removed = await db.Observations.Where(o => o.ImportBatchId == id).ExecuteDeleteAsync(ct);
            b.Status = BatchStatus.RolledBack; b.RolledBackAt = clock.UtcNow; b.RolledBackBy = u.UserId;
            // The source is only as fresh as what remains.
            var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == b.DataSourceId, ct);
            if (source is not null) source.LastRetrievedAt = await db.Observations.Where(o => o.DataSourceId == source.Id).Select(o => (DateTimeOffset?)o.RetrievedAt).OrderByDescending(x => x).FirstOrDefaultAsync(ct);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("analytics.import.rolled-back", "ImportBatch", id.ToString(), b.ProductId, b.FileName, After: new { removed }), ct);
            return Results.NoContent();
        });

        // ---- reading ----
        g.MapGet("/summary", async (Guid productId, DateOnly? from, DateOnly? to, string? channel, string? market, AnalyticsDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.AnalyticsRead) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var now = clock.UtcNow; var today = DateOnly.FromDateTime(now.UtcDateTime);
            var end = to ?? today; var start = from ?? end.AddDays(-29);
            if (end < start || end.DayNumber - start.DayNumber > 800) return Results.Problem(statusCode: 400, title: "Provide a period of up to about two years");

            var q = db.Observations.AsNoTracking().Where(o => o.ProductId == productId && o.PeriodStart >= start && o.PeriodEnd <= end);
            if (!string.IsNullOrWhiteSpace(channel)) { var ch = channel.Trim().ToLower(); q = q.Where(o => o.Channel.ToLower() == ch); }
            if (!string.IsNullOrWhiteSpace(market)) { var mk = market.Trim().ToLower(); q = q.Where(o => o.Market.ToLower() == mk); }
            var obs = await q.ToListAsync(ct);
            var sources = await db.Sources.AsNoTracking().Where(s => s.ProductId == productId).ToDictionaryAsync(s => s.Id, ct);
            var defs = await db.Definitions.AsNoTracking().OrderBy(d => d.Category).ThenBy(d => d.Name).ToListAsync(ct);

            var tiles = defs.Select(d => TileBuilder.Build(d, obs.Where(o => o.MetricKey == d.Key)
                .Select(o => new Obs(o.DataSourceId, sources.GetValueOrDefault(o.DataSourceId)?.Name ?? "unknown", o.PeriodStart, o.PeriodEnd, o.Value, o.Currency, o.Channel, o.Market, o.CampaignId, o.RetrievedAt,
                    sources.TryGetValue(o.DataSourceId, out var s) && Freshness.IsStale(s.LastRetrievedAt, s.FreshnessHours, now))).ToList())).ToList();

            return Results.Ok(new
            {
                meta = new
                {
                    productId, from = start, to = end, channel, market, asOf = now,
                    notes = new[]
                    {
                        "A dash means nothing was measured for that period; it is not zero.",
                        "Only figures whose whole period falls inside the selected dates are included.",
                        "Money is shown per currency and never added across currencies.",
                        "Where two sources report the same figure, the most recently retrieved one is used and disagreements are counted.",
                    },
                },
                sources = sources.Values.Select(s => new { s.Id, s.Name, s.Kind, s.LastRetrievedAt, s.FreshnessHours, stale = Freshness.IsStale(s.LastRetrievedAt, s.FreshnessHours, now) }),
                tiles,
            });
        });

        g.MapGet("/series", async (Guid productId, string metric, DateOnly? from, DateOnly? to, string? channel, AnalyticsDbContext db, ICurrentUser u, IClock clock, CancellationToken ct) =>
        {
            if (!u.HasPermission(Permissions.AnalyticsRead) || !await u.CanAccessProductAsync(productId, ct)) return Results.NotFound();
            var end = to ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime); var start = from ?? end.AddDays(-89);
            var q = db.Observations.AsNoTracking().Where(o => o.ProductId == productId && o.MetricKey == metric && o.PeriodStart >= start && o.PeriodEnd <= end);
            if (!string.IsNullOrWhiteSpace(channel)) { var ch = channel.Trim().ToLower(); q = q.Where(o => o.Channel.ToLower() == ch); }
            var rows = await q.OrderBy(o => o.PeriodStart).ThenByDescending(o => o.RetrievedAt).ToListAsync(ct);
            return Results.Ok(rows.GroupBy(o => (o.PeriodStart, o.PeriodEnd, o.Channel.ToLower(), o.Market.ToLower(), o.CampaignId, o.Currency)).Select(gr => gr.First())
                .Select(o => new { o.PeriodStart, o.PeriodEnd, o.Value, o.Currency, channel = o.Channel, market = o.Market, o.RetrievedAt }));
        });

        return app;
    }
}
