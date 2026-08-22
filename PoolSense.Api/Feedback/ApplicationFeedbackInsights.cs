namespace PoolSense.Api.Feedback;

public sealed record ApplicationFeedbackInsights(
    int RangeDays,
    int TotalFeedback,
    int FeedbackLast30Days,
    int PreviousFeedback30Days,
    int UniqueSubmittersLast30Days,
    int PreviousUniqueSubmitters30Days,
    int HelpfulAiFeedbackLast30Days,
    int PreviousHelpfulAiFeedback30Days,
    int TotalAiFeedbackLast30Days,
    int NotHelpfulAiFeedbackLast30Days,
    IReadOnlyList<ApplicationFeedbackTypeSummary> FeedbackTypes,
    IReadOnlyList<ApplicationFeedbackDailyCount> DailyFeedbackCounts,
    IReadOnlyList<ApplicationFeedbackDailyCount> DailyAiFeedbackCounts,
    DateTime GeneratedAtUtc);

public sealed record ApplicationFeedbackTypeSummary(string FeedbackType, int Count);

public sealed record ApplicationFeedbackDailyCount(string Date, int Count);