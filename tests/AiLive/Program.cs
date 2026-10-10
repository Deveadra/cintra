using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using MeetingCompanion.Ai;
using MeetingCompanion.Contracts;
using MeetingCompanion.Core;

// Explicit standalone operator smoke test. Never run in CI, never creates image/transcript files.
if (args.Length != 4 || args[0] != "--acknowledge-api-and-image-spend" || args[1] != "--manual-png" ||
    !decimal.TryParse(args[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var budget) || budget is <= 0 or > 0.10m)
{
    Console.Error.WriteLine("Usage: --acknowledge-api-and-image-spend --manual-png <path-to-manually-created-PNG> <approved-estimate-USD<=0.10>");
    return 2;
}
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
{
    Console.Error.WriteLine("OPENAI_API_KEY is required; no request sent."); return 2;
}
byte[]? image = null;
try
{
    var file = new FileInfo(args[2]);
    if (!file.Exists || file.Length is < 33 or > 4 * 1024 * 1024) { Console.Error.WriteLine("Image missing or outside byte limits; no request sent."); return 2; }
    image = await File.ReadAllBytesAsync(args[2]);
    var width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(16, 4)));
    var height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(20, 4)));
    var metadata = new SnapshotMetadata(Guid.NewGuid(), 0, SnapshotSource.SelectedWindow, "Operator manual fixture", null, width, height);
    var store = new RollingConversationStore(Guid.NewGuid(), new(0, 1000, DateTimeOffset.UtcNow));
    using var client = OpenAiVisionReasoner.CreateClient();
    var provider = new OpenAiVisionReasoner(client, () => Environment.GetEnvironmentVariable("OPENAI_API_KEY"), operatorEnabled: true);
    using var bridge = new ManualVisionBridge(store, provider, new() { MaxRequests = 1, SessionBudgetUsd = budget });
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); bridge.Stop(); };
    var result = await bridge.AnalyzeManualAsync(metadata, image, new(true, true, budget), cancellation.Token);
    Console.WriteLine($"Outcome={result.Outcome}; Diagnostic={result.DiagnosticCode ?? "none"}; InputTokens={provider.LastInputTokens}; OutputTokens={provider.LastOutputTokens}; ReservedEstimateUsd={bridge.ReservedEstimateUsd.ToString(CultureInfo.InvariantCulture)}");
    // Operator explicitly invoked this harness to see the result. No file logger or screenshot retention.
    if (result.Suggestion is not null) Console.WriteLine(result.Suggestion.Body);
    bridge.Stop(); store.Stop();
    return result.Outcome == ReasoningOutcome.Completed ? 0 : 1;
}
catch (Exception)
{
    Console.Error.WriteLine("Manual vision smoke test failed; details suppressed to protect file paths and credentials."); return 1;
}
finally { if (image is not null) CryptographicOperations.ZeroMemory(image); }
