using TriSoftic.GrowthEngine.Modules.Scheduling;

namespace TriSoftic.GrowthEngine.Tests.Unit;

public sealed class SlotTests
{
    private static readonly Guid Ann = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Bob = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);
    private static SlotSpec Spec(string zone = "UTC", int dur = 30, int before = 0, int after = 0, int step = 30, int notice = 0, int days = 60)
        => new(Zone(zone), dur, before, after, step, notice, days);
    private static WeeklyWindow Day(DayOfWeek d, string start, string end) => new((int)d, SlotGenerator.ParseMinute(start)!.Value, SlotGenerator.ParseMinute(end)!.Value);
    private static List<Slot> Run(SlotSpec s, IReadOnlyList<WeeklyWindow> rules, DateTimeOffset now, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<Guid>? staff = null,
        IReadOnlyList<BusyBlock>? busy = null, IReadOnlySet<DateOnly>? closed = null)
        => SlotGenerator.Generate(s, rules, closed ?? new HashSet<DateOnly>(), staff ?? [Ann], busy ?? [], now, from, to);

    [Fact]
    public void A_window_yields_slots_that_fit_entirely_inside_it()
    {
        var mon = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);   // a Monday
        var slots = Run(Spec(), [Day(DayOfWeek.Monday, "09:00", "11:00")], mon.AddDays(-1), mon, mon.AddDays(1));
        Assert.Equal([9, 9, 10, 10], slots.Select(s => s.Start.Hour));
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 10, 30, 0, TimeSpan.Zero), slots.Last().Start);
        Assert.All(slots, s => Assert.Equal(TimeSpan.FromMinutes(30), s.End - s.Start));
    }

    [Fact]
    public void Local_hours_map_to_the_right_instants_in_the_types_zone()
    {
        // Karachi is UTC+5 all year: 09:00 local is 04:00 UTC.
        var mon = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        var slots = Run(Spec("Asia/Karachi"), [Day(DayOfWeek.Monday, "09:00", "10:00")], mon.AddDays(-1), mon.AddHours(-12), mon.AddDays(1));
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 4, 0, 0, TimeSpan.Zero), slots.First().Start);
        Assert.Equal(2, slots.Count);
    }

    [Fact]
    public void Daylight_saving_shifts_the_same_local_hour_to_a_different_instant()
    {
        // New York: 09:00 local is 14:00 UTC before the 2026-03-08 change and 13:00 UTC after it.
        var rules = new[] { Day(DayOfWeek.Friday, "09:00", "09:30"), Day(DayOfWeek.Monday, "09:00", "09:30") };
        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var slots = Run(Spec("America/New_York"), rules, from.AddDays(-1), from, from.AddDays(14));
        Assert.Contains(slots, s => s.Start == new DateTimeOffset(2026, 3, 6, 14, 0, 0, TimeSpan.Zero));    // Fri before DST
        Assert.Contains(slots, s => s.Start == new DateTimeOffset(2026, 3, 9, 13, 0, 0, TimeSpan.Zero));    // Mon after DST
    }

    [Fact]
    public void A_slot_never_lands_in_the_hour_skipped_by_spring_forward()
    {
        // 2026-03-08 is a Sunday; 02:00-03:00 local does not exist in New York.
        var from = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.Zero);
        var slots = Run(Spec("America/New_York", step: 30), [Day(DayOfWeek.Sunday, "00:00", "06:00")], from.AddDays(-1), from, from.AddDays(1));
        var zone = Zone("America/New_York");
        Assert.NotEmpty(slots);
        Assert.All(slots, s => Assert.False(zone.IsInvalidTime(TimeZoneInfo.ConvertTime(s.Start, zone).DateTime)));
        // The short day has 5 real hours, so 10 half-hour slots, not 12.
        Assert.Equal(10, slots.Count(s => s.Start >= new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero) && s.Start < new DateTimeOffset(2026, 3, 8, 11, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Buffers_block_neighbouring_slots_for_the_same_staff_member()
    {
        var mon = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        var busy = new[] { new BusyBlock(Ann, mon.AddHours(10), mon.AddHours(10).AddMinutes(30)) };   // someone is booked 10:00-10:30
        // 15 minutes of buffer after each meeting: the 09:30 slot ends at 10:00 plus its own buffer and so collides.
        var slots = Run(Spec(before: 15, after: 15), [Day(DayOfWeek.Monday, "09:00", "12:00")], mon.AddDays(-1), mon, mon.AddDays(1), busy: busy);
        var starts = slots.Select(s => s.Start.TimeOfDay).ToList();
        Assert.DoesNotContain(TimeSpan.FromHours(9.5), starts);
        Assert.DoesNotContain(TimeSpan.FromHours(10), starts);
        Assert.DoesNotContain(TimeSpan.FromHours(10.5), starts);
        Assert.Contains(TimeSpan.FromHours(9), starts);
        Assert.Contains(TimeSpan.FromHours(11), starts);
    }

    [Fact]
    public void A_slot_stays_offered_while_another_staff_member_is_free()
    {
        var mon = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        var busy = new[] { new BusyBlock(Ann, mon.AddHours(9), mon.AddHours(9.5)) };
        var slots = Run(Spec(), [Day(DayOfWeek.Monday, "09:00", "10:00")], mon.AddDays(-1), mon, mon.AddDays(1), staff: [Ann, Bob], busy: busy);
        var nine = slots.Single(s => s.Start.Hour == 9 && s.Start.Minute == 0);
        Assert.Equal([Bob], nine.FreeStaff);
        Assert.Equal([Ann, Bob], slots.Single(s => s.Start.Minute == 30).FreeStaff);
        // Everyone busy: the slot disappears.
        var both = busy.Append(new BusyBlock(Bob, mon.AddHours(9), mon.AddHours(9.5))).ToArray();
        Assert.DoesNotContain(Run(Spec(), [Day(DayOfWeek.Monday, "09:00", "10:00")], mon.AddDays(-1), mon, mon.AddDays(1), staff: [Ann, Bob], busy: both), s => s.Start.Hour == 9 && s.Start.Minute == 0);
    }

    [Fact]
    public void Closed_days_minimum_notice_and_booking_window_are_respected()
    {
        var mon = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        var rules = new[] { Day(DayOfWeek.Monday, "09:00", "10:00") };
        Assert.Empty(Run(Spec(), rules, mon.AddDays(-1), mon, mon.AddDays(1), closed: new HashSet<DateOnly> { new(2026, 3, 2) }));

        // 2 hours notice at 08:00 means nothing before 10:00 -> the whole 09:00-10:00 window is gone.
        Assert.Empty(Run(Spec(notice: 2), rules, mon.AddHours(8), mon, mon.AddDays(1)));
        Assert.Equal(2, Run(Spec(notice: 0), rules, mon.AddHours(8), mon, mon.AddDays(1)).Count);

        // A booking window of 1 day hides next week's Monday.
        Assert.Empty(Run(Spec(days: 1), rules, mon.AddDays(1), mon.AddDays(1), mon.AddDays(10)));
    }

    [Fact]
    public void Parsing_hours_accepts_24_00_as_an_end_and_rejects_garbage()
    {
        Assert.Equal(570, SlotGenerator.ParseMinute("09:30"));
        Assert.Equal(1440, SlotGenerator.ParseMinute("24:00"));
        Assert.Null(SlotGenerator.ParseMinute("25:00"));
        Assert.Null(SlotGenerator.ParseMinute("9am"));
        Assert.Null(SlotGenerator.ParseMinute(null));
    }

    [Fact]
    public void Lead_references_only_verify_with_the_right_secret_and_are_not_forgeable()
    {
        var id = Guid.NewGuid();
        var r = SchedulingModule.SignLeadRef(id, "secret-one");
        Assert.Equal(id, SchedulingModule.VerifyLeadRef(r, "secret-one"));
        Assert.Null(SchedulingModule.VerifyLeadRef(r, "secret-two"));
        Assert.Null(SchedulingModule.VerifyLeadRef(Guid.NewGuid().ToString("N") + r[32..], "secret-one"));   // someone else's lead id with a copied signature
        Assert.Null(SchedulingModule.VerifyLeadRef("nonsense", "secret-one"));
        Assert.Null(SchedulingModule.VerifyLeadRef(r, null));
    }
}
