using MeetingCompanion.Contracts;

namespace MeetingCompanion.Ai;

/// <summary>Explicit consent for this manual action; never inferred from Start or STT consent.</summary>
public sealed record VisionConsent(bool ApiEnabled, bool ImageEnabled, decimal ApprovedEstimateUsd);

public sealed record VisionPolicy
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
    public int MaxImageBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxContextCharacters { get; init; } = 24_000;
    public int MaxRequests { get; init; } = 10;
    public decimal SessionBudgetUsd { get; init; } = 0.10m;

    internal void Validate()
    {
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(1) ||
            MaxImageBytes is < 32 or > 4 * 1024 * 1024 || MaxContextCharacters is < 512 or > 24_000 ||
            MaxRequests is < 1 or > 100 || SessionBudgetUsd <= 0 || SessionBudgetUsd > 10)
            throw new ArgumentOutOfRangeException(nameof(VisionPolicy));
    }
}

internal static class VisionResults
{
    public static ReasoningResult Failure(string code, ReasoningOutcome outcome = ReasoningOutcome.Unavailable) => new(outcome, null, code);
}
