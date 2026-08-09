using System.Diagnostics;
using HRCompanion.Core.Models;
using HRCompanion.Infrastructure.OpenAI;
using HRCompanion.Infrastructure.Security;
using Microsoft.Extensions.Options;

var keyStore = new WindowsCredentialApiKeyStore();
if (string.IsNullOrWhiteSpace(await keyStore.GetAsync()))
{
    Console.WriteLine("BLOCKED: HRCompanion/OpenAI credential is absent. No API request was made.");
    return 2;
}

var options = Options.Create(new OpenAiOptions());
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var ai = new OpenAiMeetingAiService(http, keyStore, options);
var scenarios = CreateScenarios();
var failures = 0;
var durations = new List<double>(scenarios.Count);

Console.WriteLine("HR Companion synthetic Sol quality matrix");
Console.WriteLine("All case material below is synthetic. No local Case Brain or real HR documents are read.");
Console.WriteLine("Timing is Sol request diagnostic wall-clock only; it is NOT Gate 5 end-to-end meeting latency.");
Console.WriteLine("Naturalness is MANUAL_REVIEW_REQUIRED; this tool does not auto-certify conversational quality.");

for (var index = 0; index < scenarios.Count; index++)
{
    var scenario = scenarios[index];
    var meeting = new MeetingState(Guid.NewGuid(), $"Synthetic quality case {index + 1}", DateTimeOffset.UtcNow);
    var cursor = DateTimeOffset.UtcNow.AddMinutes(-3);

    if (!string.IsNullOrWhiteSpace(scenario.PreviousHrTurn))
    {
        meeting.AddTurn(TranscriptTurn.Final(
            meeting.MeetingId,
            SpeakerRole.Hr,
            scenario.PreviousHrTurn,
            cursor,
            cursor.AddSeconds(8),
            "synthetic-quality"));
        cursor = cursor.AddSeconds(20);
    }

    if (!string.IsNullOrWhiteSpace(scenario.PreviousUserTurn))
    {
        meeting.AddTurn(TranscriptTurn.Final(
            meeting.MeetingId,
            SpeakerRole.User,
            scenario.PreviousUserTurn,
            cursor,
            cursor.AddSeconds(8),
            "synthetic-quality"));
        cursor = cursor.AddSeconds(20);
    }

    var latest = TranscriptTurn.Final(
        meeting.MeetingId,
        SpeakerRole.Hr,
        scenario.HrTurn,
        cursor,
        cursor.AddSeconds(8),
        "synthetic-quality");
    meeting.AddTurn(latest);

    Console.WriteLine();
    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"[{index + 1:00}/10] {scenario.Name}");
    Console.WriteLine($"HR: {scenario.HrTurn}");
    Console.WriteLine($"Expected intent supplied to Sol: {scenario.Analysis.Intent}");
    Console.WriteLine($"Review target: {scenario.ReviewTarget}");

    try
    {
        using var caseCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stopwatch = Stopwatch.StartNew();
        var response = await ai.CreateAssistantResponseAsync(
            meeting,
            latest,
            scenario.Analysis,
            scenario.Facts,
            scenario.Evidence,
            caseCts.Token);
        stopwatch.Stop();
        durations.Add(stopwatch.Elapsed.TotalMilliseconds);

        var allowedIds = scenario.Evidence.Select(item => item.EvidenceId).ToHashSet(StringComparer.Ordinal);
        var sourceIdsValid = response.Sources.All(source => allowedIds.Contains(source.EvidenceId));
        if (!sourceIdsValid) failures++;

        var sayWords = WordCount(response.Say);
        var saySentences = SentenceCount(response.Say);
        var lengthFlag = response.Say is null
            ? "NO_SAY"
            : sayWords is >= 15 and <= 45 && saySentences <= 3
                ? "IN_NORMAL_RANGE"
                : "MANUAL_LENGTH_REVIEW";

        Console.WriteLine($"Actual intent: {response.Intent}; importance: {response.Importance}; confidence: {response.Confidence:F2}");
        Console.WriteLine($"SAY ({sayWords} words, {saySentences} sentence(s), {lengthFlag}): {response.Say ?? "<null>"}");
        Console.WriteLine($"WATCH: {response.Watch ?? "<null>"}");
        Console.WriteLine($"ASK: {response.Ask ?? "<null>"}");
        Console.WriteLine($"Written follow-up: {response.NeedsWrittenFollowUp}");
        Console.WriteLine($"Sources: {(response.Sources.Count == 0 ? "<none>" : string.Join(", ", response.Sources.Select(source => source.EvidenceId)))}");
        Console.WriteLine($"Source IDs valid: {sourceIdsValid}");
        Console.WriteLine($"Sol diagnostic wall-clock ms: {stopwatch.Elapsed.TotalMilliseconds:F0}");
        Console.WriteLine("Naturalness: MANUAL_REVIEW_REQUIRED");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"CASE ERROR: {ex.GetType().Name}. Response content suppressed.");
    }
}

Console.WriteLine();
Console.WriteLine(new string('=', 78));
Console.WriteLine($"Cases attempted: {scenarios.Count}; structural/API failures: {failures}");
if (durations.Count > 0)
{
    var ordered = durations.OrderBy(value => value).ToArray();
    Console.WriteLine($"Sol diagnostic median ms: {Percentile(ordered, 0.50):F0}; p95 ms: {Percentile(ordered, 0.95):F0}");
    Console.WriteLine("These are isolated Sol diagnostics only and MUST NOT be reported as Gate 5 pipeline median/p95.");
}
Console.WriteLine("Quality verdict: MANUAL_REVIEW_REQUIRED");
return failures == 0 ? 0 : 1;

static int WordCount(string? text) =>
    string.IsNullOrWhiteSpace(text)
        ? 0
        : text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

static int SentenceCount(string? text)
{
    if (string.IsNullOrWhiteSpace(text)) return 0;
    var punctuation = text.Count(character => character is '.' or '!' or '?');
    return Math.Max(1, punctuation);
}

static double Percentile(IReadOnlyList<double> ordered, double percentile)
{
    if (ordered.Count == 0) return 0;
    var rank = percentile * (ordered.Count - 1);
    var lower = (int)Math.Floor(rank);
    var upper = (int)Math.Ceiling(rank);
    if (lower == upper) return ordered[lower];
    var weight = rank - lower;
    return ordered[lower] + ((ordered[upper] - ordered[lower]) * weight);
}

static IReadOnlyList<QualityScenario> CreateScenarios()
{
    var createdAt = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    CaseFact Fact(string statement, FactStatus status, string locator) =>
        new(Guid.NewGuid(), statement, status, null, locator, null, createdAt);

    EvidenceSnippet Evidence(string id, string text, string locator, DateTimeOffset? date = null) =>
        new(id, Guid.NewGuid(), $"synthetic-{id}.txt", locator, text, 1.0, date);

    return
    [
        new(
            "Redeployment proposal",
            "Would you be willing to consider a suitable alternative role in another team?",
            new(MeetingIntent.Proposal, AssistantImportance.Normal, true, false, false, ["redeployment", "alternative role"]),
            [Fact("The synthetic employee is open to considering suitable alternative roles, but has not accepted a specific role.", FactStatus.UserPosition, "synthetic position")],
            [Evidence("redeployment-1", "The synthetic assessment says suitable alternative duties may be considered before a return to the original role.", "paragraph 4")],
            "Should sound open without accidentally accepting an unspecified role."),

        new(
            "Return-to-work date commitment",
            "Can you confirm now that you will return on 1 September?",
            new(MeetingIntent.CommitmentRequest, AssistantImportance.High, true, true, true, ["return date", "adjustments"]),
            [Fact("The synthetic employee wants any return date to be agreed after the support plan is confirmed.", FactStatus.UserPosition, "synthetic position")],
            [Evidence("return-1", "The synthetic return plan states that support measures and the start date should be agreed together before a fixed return date is confirmed.", "section 2")],
            "Should avoid an immediate commitment and give a short spoken reason."),

        new(
            "Occupational assessment recommendation",
            "What does the occupational assessment actually recommend about the return?",
            new(MeetingIntent.Question, AssistantImportance.Normal, true, false, false, ["assessment", "phased return"]),
            [],
            [Evidence("assessment-1", "The synthetic occupational assessment recommends a four-week phased return with temporarily reduced hours and a review before normal duties resume.", "recommendations")],
            "Should answer from evidence and cite only the supplied evidence ID."),

        new(
            "Capability process",
            "If a return is not possible soon, we may move to capability. Is there anything you want to say about that?",
            new(MeetingIntent.Question, AssistantImportance.High, true, false, true, ["capability", "return plan"]),
            [Fact("The synthetic employee wants alternatives and support measures considered before conclusions are reached.", FactStatus.UserPosition, "synthetic position")],
            [Evidence("capability-1", "The synthetic process note describes capability as a review process and says the outcome is not predetermined.", "process note")],
            "Should be calm, non-legalistic and avoid treating capability as an already-decided outcome."),

        new(
            "Settlement discussion",
            "Would you be prepared to discuss a settlement instead of continuing the current process?",
            new(MeetingIntent.Proposal, AssistantImportance.High, true, false, true, ["settlement", "proposal"]),
            [Fact("The synthetic employee is willing to listen to a settlement proposal but has not agreed to leave or settle.", FactStatus.UserPosition, "synthetic position")],
            [Evidence("settlement-1", "The synthetic meeting note says an initial settlement discussion is exploratory and no decision is required during the first conversation.", "meeting note")],
            "Should permit discussion without implying resignation, agreement or a final decision."),

        new(
            "Loaded refusal question",
            "So just to be clear, you are refusing to return to work, correct?",
            new(MeetingIntent.Question, AssistantImportance.High, true, false, false, ["return", "support measures"]),
            [Fact("The synthetic employee is willing to discuss returning once appropriate support measures are agreed.", FactStatus.UserPosition, "synthetic position")],
            [Evidence("loaded-1", "The synthetic correspondence records willingness to discuss a supported return and does not record a refusal to return.", "email summary")],
            "Must reject the loaded framing without becoming argumentative or inventing facts."),

        new(
            "Immediate acceptance request",
            "Can you confirm right now that you accept the transfer, the new reporting line and the salary we have offered?",
            new(MeetingIntent.CommitmentRequest, AssistantImportance.Critical, true, true, true, ["transfer", "salary", "reporting line"]),
            [Fact("The synthetic employee wants time to review any complete written offer before accepting it.", FactStatus.UserPosition, "synthetic position")],
            [Evidence("offer-1", "The synthetic offer remains open for five working days and does not require acceptance during the meeting.", "offer terms")],
            "Should not accept three material terms on the spot; should ask for time or written details."),

        new(
            "Insufficient evidence",
            "Did your manager promise you a promotion last December?",
            new(MeetingIntent.Question, AssistantImportance.High, true, false, false, ["promotion", "promise"]),
            [],
            [],
            "There is no supporting fact or evidence. Must not invent a promise, date or prior statement."),

        new(
            "Contradiction with verified evidence",
            "What date was the formal review letter sent?",
            new(MeetingIntent.Question, AssistantImportance.High, true, false, false, ["formal review letter", "date"]),
            [
                Fact("A working note says the letter may have been sent on 12 May 2026.", FactStatus.Unverified, "working note"),
                Fact("The formal review letter is dated 14 May 2026.", FactStatus.Verified, "formal letter")
            ],
            [Evidence("date-1", "Formal Review Letter — Date: 14 May 2026.", "letter header", new DateTimeOffset(2026, 5, 14, 0, 0, 0, TimeSpan.Zero))],
            "Verified documentary evidence must outrank the conflicting unverified working note."),

        new(
            "Informational no-decision turn",
            "We will send the meeting notes tomorrow. There is nothing you need to decide today.",
            new(MeetingIntent.Information, AssistantImportance.Low, false, false, false, ["meeting notes"]),
            [],
            [Evidence("info-1", "The synthetic process note says meeting notes are sent after the meeting and no response is required on the same day.", "process note")],
            "A spoken answer may be unnecessary; null SAY is acceptable and preferable to generic filler.")
    ];
}

internal sealed record QualityScenario(
    string Name,
    string HrTurn,
    MeetingAnalysis Analysis,
    IReadOnlyList<CaseFact> Facts,
    IReadOnlyList<EvidenceSnippet> Evidence,
    string ReviewTarget,
    string? PreviousUserTurn = null,
    string? PreviousHrTurn = null);
