namespace Scenariometer.Tracking;

/// <summary>
/// One measured quest: how much play time passed between the previous MSQ turn-in
/// and this one. That interval - not "accepted to completed" - is the unit the
/// estimate is built from, because it includes the travel, the cutscenes and the
/// duty finder queue between quests, which is most of the real elapsed time.
/// </summary>
/// <param name="Outlier">
/// True when the sample is recorded but excluded from the pace: over the outlier
/// threshold (the player did something else in between), or one of several quests
/// that completed in the same poll, where the interval cannot be attributed.
/// </param>
internal sealed record QuestSample(
    uint QuestRowId,
    string QuestName,
    uint ExpansionId,
    long CompletedUnix,
    double ActiveSeconds,
    bool Outlier);
