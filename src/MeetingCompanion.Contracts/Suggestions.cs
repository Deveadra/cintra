namespace MeetingCompanion.Contracts;

/// <summary>Display/copy only. There is intentionally no command execution interface.</summary>
public sealed record DisplayCommand(string Text, CommandRisk Risk, bool IsTemplate, string[] RequiredInputs);
public sealed record Suggestion(Guid SuggestionId, SuggestionKind Kind, Priority Priority, string Headline,
    string Body, DisplayCommand[] Commands, string[] WhatToLookFor, Confidence Confidence,
    Guid[] EvidenceEventIds, string[] Assumptions, long ExpiresAtMs)
{
    public void Validate(bool automatic = false)
    {
        Require.Id(SuggestionId); Require.Enum(Kind); Require.Enum(Priority); Require.Enum(Confidence);
        Require.Text(Headline, 160); Require.Text(Body, 4096);
        Require.That(ExpiresAtMs >= 0 && Commands is { Length: <= 8 } && WhatToLookFor is { Length: <= 16 }
            && EvidenceEventIds is { Length: <= 128 } && Assumptions is { Length: <= 16 }, "Invalid suggestion limits.");
        Require.That(EvidenceEventIds!.Length > 0 || Assumptions!.Length > 0, "Suggestion needs evidence or explicit assumptions.");
        foreach (var id in EvidenceEventIds) Require.Id(id);
        foreach (var item in WhatToLookFor!) Require.Text(item, 1024);
        foreach (var item in Assumptions!) Require.Text(item, 1024);
        foreach (var command in Commands!)
        {
            Require.That(command is not null, "Command required.");
            Require.Text(command!.Text, 2048); Require.Enum(command.Risk);
            Require.That(command.RequiredInputs is { Length: <= 16 }, "Invalid command inputs.");
            foreach (var input in command.RequiredInputs!) Require.Text(input, 128);
            Require.That(command.RequiredInputs.Length == 0 || command.IsTemplate, "Unresolved inputs require a template.");
            Require.That(!automatic || command.Risk == CommandRisk.ReadOnly, "Automatic suggestions must be read-only.");
        }
    }
}

public sealed record SuggestionRequest(Guid RequestId, TriggerKind Trigger, bool Automatic, ConversationContext Context);
public sealed record VisionRequest(SuggestionRequest Reasoning, SnapshotMetadata Snapshot, ReadOnlyMemory<byte> Image, string MediaType);
public enum ReasoningOutcome { Completed, Refused, TimedOut, Unavailable, Incomplete }
public sealed record ReasoningResult(ReasoningOutcome Outcome, Suggestion? Suggestion, string? DiagnosticCode);
