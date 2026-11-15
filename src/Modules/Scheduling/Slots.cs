namespace TriSoftic.GrowthEngine.Modules.Scheduling;

public sealed record SlotSpec(TimeZoneInfo Zone, int DurationMinutes, int BufferBeforeMinutes, int BufferAfterMinutes, int StepMinutes, int MinNoticeHours, int MaxDaysAhead);
public sealed record WeeklyWindow(int DayOfWeek, int StartMinute, int EndMinute);
/// <summary>An existing confirmed booking's block (appointment plus buffers) for one staff member.</summary>
public sealed record BusyBlock(Guid StaffId, DateTimeOffset Start, DateTimeOffset End);
public sealed record Slot(DateTimeOffset Start, DateTimeOffset End, Guid[] FreeStaff);

/// <summary>
/// Pure slot generation. Weekly hours are local wall-clock times in the type's zone; they are converted to instants per date,
/// so a day with a DST change has the right length and a slot never lands in a skipped hour.
/// </summary>
public static class SlotGenerator
{
    public static DateTimeOffset LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);                       // spring-forward gap: use the first valid time after it
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);   // fall-back overlap: first occurrence
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public static List<Slot> Generate(SlotSpec spec, IReadOnlyList<WeeklyWindow> rules, IReadOnlySet<DateOnly> closedDates, IReadOnlyList<Guid> staff,
        IReadOnlyList<BusyBlock> busy, DateTimeOffset now, DateTimeOffset from, DateTimeOffset to)
    {
        var earliest = from > now.AddHours(spec.MinNoticeHours) ? from : now.AddHours(spec.MinNoticeHours);
        var latest = to < now.AddDays(spec.MaxDaysAhead) ? to : now.AddDays(spec.MaxDaysAhead);
        var result = new SortedDictionary<DateTimeOffset, Slot>();
        if (latest <= earliest || spec.DurationMinutes <= 0 || spec.StepMinutes <= 0 || staff.Count == 0) return [];

        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(earliest, spec.Zone).Date).AddDays(-1);
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(latest, spec.Zone).Date).AddDays(1);
        var dur = TimeSpan.FromMinutes(spec.DurationMinutes);
        var step = TimeSpan.FromMinutes(spec.StepMinutes);

        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            if (closedDates.Contains(day)) continue;
            foreach (var rule in rules.Where(r => r.DayOfWeek == (int)day.DayOfWeek))
            {
                var midnight = day.ToDateTime(TimeOnly.MinValue);
                var wStart = LocalToUtc(midnight.AddMinutes(rule.StartMinute), spec.Zone);
                var wEnd = LocalToUtc(midnight.AddMinutes(rule.EndMinute), spec.Zone);
                for (var s = wStart; s + dur <= wEnd; s += step)
                {
                    if (s < earliest || s >= latest || result.ContainsKey(s)) continue;   // a slot belongs to the range its start falls in
                    var blockStart = s.AddMinutes(-spec.BufferBeforeMinutes); var blockEnd = s + dur + TimeSpan.FromMinutes(spec.BufferAfterMinutes);
                    var free = staff.Where(st => !busy.Any(b => b.StaffId == st && b.Start < blockEnd && b.End > blockStart)).ToArray();
                    if (free.Length > 0) result[s] = new Slot(s, s + dur, free);
                }
            }
        }
        return [.. result.Values];
    }

    /// <summary>"09:30" -> 570. "24:00" is allowed as an end time.</summary>
    public static int? ParseMinute(string? hhmm)
    {
        if (hhmm is null || !TimeOnly.TryParseExact(hhmm.Trim() == "24:00" ? "00:00" : hhmm.Trim(), "HH:mm", out var t)) return null;
        return hhmm.Trim() == "24:00" ? 1440 : t.Hour * 60 + t.Minute;
    }
}
