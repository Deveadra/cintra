using System.Text.Json;
using MeetingCompanion.Contracts;

namespace MeetingCompanion.Ai;

internal static class VisionOutput
{
    // Intentionally small reviewed grammar: commands cannot contain model-invented resource identifiers or shell operators.
    private static readonly Dictionary<string, string[]> Commands = new(StringComparer.Ordinal)
    {
        ["kubectl version --client"] = [],
        ["docker ps"] = [],
        ["kubectl get pods --namespace <namespace>"] = ["namespace"],
        ["kubectl logs <pod> --namespace <namespace>"] = ["pod", "namespace"]
    };

    public static ReasoningResult Validate(ReasoningResult result, VisionRequest request, Guid[] evidence)
    {
        if (!Enum.IsDefined(result.Outcome)) throw new ContractException("Unknown reasoning outcome.");
        if (result.Outcome != ReasoningOutcome.Completed)
        {
            if (result.Suggestion is not null) throw new ContractException("Failed outcome contains a suggestion.");
            if (result.DiagnosticCode is "vision_authentication_required" or "vision_rate_limited_no_retry" or
                "vision_network_failure_no_retry" or "vision_http_failure_no_retry" or "vision_response_too_large" or
                "vision_response_invalid" or "image_unreadable")
                return VisionResults.Failure(result.DiagnosticCode, result.Outcome);
            // Adapter diagnostics are never surfaced verbatim (could contain secrets, transcripts or provider body).
            return VisionResults.Failure(result.Outcome switch
            {
                ReasoningOutcome.Refused => "vision_refused",
                ReasoningOutcome.TimedOut => "vision_timeout_no_retry",
                ReasoningOutcome.Incomplete => "vision_incomplete",
                _ => "vision_unavailable_no_retry"
            }, result.Outcome);
        }
        var s = result.Suggestion ?? throw new ContractException("Missing suggestion.");
        s.Validate(automatic: true); // Manual screenshot suggestions also use only reviewed read-only diagnostics.
        if (s.Kind != SuggestionKind.ScreenshotAnalysis || s.Body.Length > 3000 || s.Commands.Length > 4 ||
            s.WhatToLookFor.Length > 4 || s.Assumptions.Length > 4 || s.EvidenceEventIds.Any(id => !evidence.Contains(id)) ||
            s.ExpiresAtMs <= request.Snapshot.CapturedAtMs || s.ExpiresAtMs > request.Snapshot.CapturedAtMs + 60_000 ||
            !s.Body.StartsWith("Observed:", StringComparison.Ordinal) || !s.Body.Contains("Possible explanation:", StringComparison.Ordinal))
            throw new ContractException("Invalid vision output.");
        foreach (var c in s.Commands)
            if (!Commands.TryGetValue(c.Text, out var inputs) || c.Risk != CommandRisk.ReadOnly ||
                c.IsTemplate != (inputs.Length > 0) || !inputs.SequenceEqual(c.RequiredInputs))
                throw new ContractException("Command outside reviewed display-only grammar.");
        // Detach from a fake/custom adapter's mutable arrays before handing off to UI.
        return new(ReasoningOutcome.Completed, s with
        {
            EvidenceEventIds = s.EvidenceEventIds.ToArray(),
            Assumptions = s.Assumptions.ToArray(),
            WhatToLookFor = s.WhatToLookFor.ToArray(),
            Commands = s.Commands.Select(c => c with { RequiredInputs = c.RequiredInputs.ToArray() }).ToArray()
        }, null);
    }

    public static ReasoningResult Parse(JsonElement data, VisionRequest request)
    {
        var payload = data.Deserialize<Analysis>(JsonOptions) ?? throw new JsonException();
        if (payload.Observations is not { Length: <= 4 } || payload.Hypotheses is not { Length: <= 4 } ||
            payload.NextSteps is not { Length: <= 4 } || payload.Commands is not { Length: <= 4 }) throw new JsonException();
        if (!payload.Readable) return VisionResults.Failure("image_unreadable", ReasoningOutcome.Incomplete);
        using var summary = JsonDocument.Parse(request.Reasoning.Context.Summary);
        var imageEventId = summary.RootElement.GetProperty("snapshot_event_id").GetGuid();
        foreach (var o in payload.Observations)
        {
            if (o is null || string.IsNullOrWhiteSpace(o.Text) || o.Text.Length > 384 ||
                o.EvidenceEventIds is not { Length: > 0 and <= 8 }) throw new JsonException();
            if (o.Text.StartsWith("Visible in snapshot:", StringComparison.Ordinal))
            { if (!o.EvidenceEventIds.Contains(imageEventId)) throw new JsonException(); }
            else if (o.Text.StartsWith("Stated in call:", StringComparison.Ordinal))
            { if (o.EvidenceEventIds.Any(id => !request.Reasoning.Context.SpeakerTurns.Any(t => t.EventId == id))) throw new JsonException(); }
            else throw new JsonException();
        }
        foreach (var text in payload.Hypotheses.Concat(payload.NextSteps))
            if (string.IsNullOrWhiteSpace(text) || text.Length > 384) throw new JsonException();
        var body = "Observed:\n" + string.Join("\n", payload.Observations.Select(o => o.Text)) +
            "\nPossible explanation:\n" + (payload.Hypotheses.Length == 0 ? "Unknown; more evidence is needed." : string.Join("\n", payload.Hypotheses));
        return new(ReasoningOutcome.Completed, new(Guid.NewGuid(), SuggestionKind.ScreenshotAnalysis, Priority.Normal,
            "Manual snapshot analysis", body, payload.Commands, payload.NextSteps, Confidence.Low,
            payload.Observations.SelectMany(o => o.EvidenceEventIds).Distinct().ToArray(),
            payload.Hypotheses.Length == 0 ? ["Visual interpretation and environment are unverified."] : payload.Hypotheses,
            checked(request.Snapshot.CapturedAtMs + 60_000)), null);
    }

    private sealed record Observation(string Text, Guid[] EvidenceEventIds);
    private sealed record Analysis(bool Readable, Observation[] Observations, string[] Hypotheses, string[] NextSteps, DisplayCommand[] Commands);
    private static readonly JsonSerializerOptions JsonOptions = new(ContractJson.Options) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public const string Schema = """
    {"type":"object","additionalProperties":false,"required":["readable","observations","hypotheses","next_steps","commands"],"properties":{
      "readable":{"type":"boolean"},
      "observations":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["text","evidence_event_ids"],"properties":{"text":{"type":"string"},"evidence_event_ids":{"type":"array","items":{"type":"string"}}}}},
      "hypotheses":{"type":"array","items":{"type":"string"}},"next_steps":{"type":"array","items":{"type":"string"}},
      "commands":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["text","risk","is_template","required_inputs"],"properties":{"text":{"type":"string"},"risk":{"type":"string","enum":["read_only"]},"is_template":{"type":"boolean"},"required_inputs":{"type":"array","items":{"type":"string"}}}}}
    }}
    """;
}
