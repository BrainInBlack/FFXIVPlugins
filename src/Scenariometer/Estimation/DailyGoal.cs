using System;
using System.Globalization;
using Scenariometer.Tracking;

namespace Scenariometer.Estimation;

/// <summary>
/// "Finish the MSQ by date X" turned into "do N quests today".
///
/// This is a deliberately different question from <see cref="Estimate"/>. The
/// estimator answers "when will I finish at my current pace"; this answers "what
/// pace do I need to hit a date I picked". Neither is derived from the other - a
/// target can be comfortably slower or wildly faster than the measured pace, and
/// saying so is the useful part.
///
/// The plan is cumulative, not per day. Days you overshot bank against days you
/// miss, so getting ahead on Sunday lightens Monday instead of being forgotten -
/// and, more to the point, a morning no longer opens by declaring you a full day's
/// quota "behind" before you have played anything.
/// </summary>
internal sealed record DailyGoal(
    bool HasTarget,
    DateOnly Target,
    int DaysRemaining,
    int QuestsRemaining,
    int CompletedToday,
    int QuotaToday,
    int RemainingToday,
    int AheadBy)
{
    public static readonly DailyGoal None =
        new(false, default, 0, 0, 0, 0, 0, 0);

    /// <summary>The target date is in the past and there is still MSQ left.</summary>
    public bool Overdue => HasTarget && DaysRemaining <= 0 && QuestsRemaining > 0;

    /// <summary>Today's share is done. Not the same as the MSQ being finished.</summary>
    public bool DoneForToday => HasTarget && RemainingToday <= 0;

    /// <summary>Day-month-year, the European way.</summary>
    public const string GermanFormat = "dd.MM.yyyy";

    /// <summary>Month-day-year.</summary>
    public const string UsFormat = "MM/dd/yyyy";

    /// <summary>Year-month-day. What gets written to the config file, always.</summary>
    public const string IsoFormat = "yyyy-MM-dd";

    /// <summary>
    /// Every format is accepted on input regardless of which one is selected. Someone
    /// switching the setting should not find the date they already typed rejected,
    /// and the three are unambiguous from each other in practice.
    /// </summary>
    private static readonly string[] AcceptedFormats = [IsoFormat, GermanFormat, UsFormat];

    /// <summary>
    /// The chosen display format. Validated against the known set rather than trusted:
    /// this goes straight into DateOnly.ToString on every frame the window draws, and
    /// some invalid specifiers ("q", "zzz") throw rather than degrading to a literal.
    /// A hand-edited config should not be able to throw inside the draw path.
    /// </summary>
    public static string Pattern =>
        Array.IndexOf(AcceptedFormats, Plugin.Config.DateFormat) >= 0
            ? Plugin.Config.DateFormat
            : IsoFormat;

    /// <summary>A date as the user has asked to see it.</summary>
    public static string Format(DateOnly date) => date.ToString(Pattern, CultureInfo.InvariantCulture);

    /// <summary>Empty or unparseable means "no target set", which is not an error.</summary>
    public static bool TryParse(string value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value?.Trim() ?? string.Empty,
            AcceptedFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    public static DailyGoal Compute(
        string targetDate,
        string planStartDate,
        int dayStartHour,
        int questsRemaining,
        QuestHistory? history,
        DateTimeOffset now)
    {
        if (!TryParse(targetDate, out var target))
            return None;

        var today = LogicalDate(now, dayStartHour);

        // The plan runs from the day the target was set. With none recorded - an older
        // config, or a target set before this was tracked - it starts today, which
        // makes the carry-over zero rather than wrong.
        var planStart = TryParse(planStartDate, out var recorded) && recorded <= today
            ? recorded
            : today;

        var (completedToday, completedBefore) = Count(history, planStart, today, dayStartHour);

        // Inclusive of today: a target of "today" means one day to do it in, not zero.
        var daysRemaining = target.DayNumber - today.DayNumber + 1;
        var daysElapsed = today.DayNumber - planStart.DayNumber;
        var planDays = Math.Max(1, target.DayNumber - planStart.DayNumber + 1);

        // What the plan was for, reconstructed rather than stored: what is still to do
        // plus everything done since it started. Deriving it keeps the whole thing per
        // character, because the history it counts is.
        var planTotal = questsRemaining + completedToday + completedBefore;
        var perDay = (double)planTotal / planDays;

        // Previous days only, for the quota. Crediting today's work here would shrink
        // today's quota as it is done, and what the day owes has to be decided once,
        // when the day starts - not walked down by the very work that answers it.
        var carried = completedBefore - (perDay * daysElapsed);

        // Today's share, less whatever was banked on the days before it.
        var quotaToday = daysRemaining > 0
            ? (int)Math.Ceiling(Math.Max(0, perDay - carried))
            : questsRemaining;

        quotaToday = Math.Clamp(quotaToday, 0, questsRemaining + completedToday);

        // The standing counts today; the quota above does not. A figure that cannot
        // move until tomorrow is not a progress report - it left "16 quests behind"
        // frozen beside a Today row reading "10 of 21". Only the part above today's
        // own share counts, so an untouched morning still does not open a full day's
        // quota in the red, and a good day banks only what it earns past that share.
        var standing = carried + Math.Max(0, completedToday - perDay);

        // Past that share, "behind" and "left today" are one number and must print as
        // one; rounding them separately is what put "12 of 21" beside "8 behind". So
        // it is subtracted from the quota rather than rounded again. The identity
        // floor(completedToday - Q) == completedToday - ceil(Q) holds in exact
        // arithmetic, but perDay - carried and completedToday - perDay differ in the
        // last bit, and a quota landing on a whole number then reads one quest further
        // behind than the Today row has left. Sharing the integer makes the two rows
        // incapable of disagreeing rather than merely unlikely to.
        //
        // The other branch claims no such agreement: inside today's grace the standing
        // is deliberately still yesterday's, and at a quota of zero a surplus already
        // covers the day. Floor, so 0.7 of a quest banked does not read as a whole one.
        var aheadBy = completedToday >= perDay && quotaToday > 0
            ? completedToday - quotaToday
            : (int)Math.Floor(standing);

        return new DailyGoal(
            HasTarget: true,
            Target: target,
            DaysRemaining: Math.Max(0, daysRemaining),
            QuestsRemaining: questsRemaining,
            CompletedToday: completedToday,
            QuotaToday: quotaToday,
            RemainingToday: Math.Max(0, quotaToday - completedToday),
            AheadBy: aheadBy);
    }

    /// <summary>
    /// How many quests this character has finished since a plan started, today
    /// included - what restarting the plan would discard, in the terms the question
    /// asking about it is put in.
    /// </summary>
    public static int CompletedSince(
        QuestHistory? history,
        DateOnly planStart,
        int dayStartHour,
        DateTimeOffset now)
    {
        var (today, before) = Count(history, planStart, LogicalDate(now, dayStartHour), dayStartHour);
        return today + before;
    }

    /// <summary>
    /// Splits the samples since the plan started into today's and the days before it.
    ///
    /// Walks backwards and stops once past the plan start: samples are appended in
    /// completion order, so everything beyond that point is older still. A window that
    /// redraws every frame would otherwise scan a history that grows without bound.
    /// </summary>
    private static (int Today, int Before) Count(
        QuestHistory? history,
        DateOnly planStart,
        DateOnly today,
        int dayStartHour)
    {
        if (history is null)
            return (0, 0);

        var samples = history.Samples;
        var todayCount = 0;
        var beforeCount = 0;

        for (var i = samples.Count - 1; i >= 0; i--)
        {
            var day = LogicalDate(DateTimeOffset.FromUnixTimeSeconds(samples[i].CompletedUnix), dayStartHour);

            if (day < planStart)
                break;

            // A sample dated later than today means the clock moved; skip it rather
            // than stopping, so the ones behind it are still counted.
            if (day == today)
                todayCount++;
            else if (day < today)
                beforeCount++;
        }

        return (todayCount, beforeCount);
    }

    /// <summary>
    /// Which day an instant counts as, given the hour a day is considered to start.
    ///
    /// A session that runs past midnight is one evening, not two days: with the
    /// boundary at 06:00, a quest turned in at 02:00 on Tuesday belongs to Monday.
    /// The comparison is in local time on purpose - the boundary is about when the
    /// player sleeps, not about UTC.
    /// </summary>
    public static DateOnly LogicalDate(DateTimeOffset instant, int dayStartHour)
    {
        var local = instant.ToLocalTime();
        var date = local.Hour < dayStartHour ? local.Date.AddDays(-1) : local.Date;
        return DateOnly.FromDateTime(date);
    }
}
