namespace PoolSense.Api.Options;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string JwtSecret { get; set; } = string.Empty;

    public bool AllowInsecurePasswordFallback { get; set; }

    public int SessionHours { get; set; } = 8;

    public int RememberMeDays { get; set; } = 4;

    // Sliding session window: a session only expires after this many days with no activity.
    public int InactivityTimeoutDays { get; set; } = 14;
}