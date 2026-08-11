using System.Text.RegularExpressions;
using HRCompanion.Core.Models;

namespace HRCompanion.Core.Services;

public sealed partial class DeterministicCueEngine
{
    private static readonly string[] CommitmentTerms =
    [
        "agree", "accept", "confirm", "resign", "withdraw", "sign", "consent",
        "start on", "return on", "final decision", "settlement", "capability"
    ];

    private static readonly (string[] Triggers, string[] RetrievalTerms)[] HrConceptAliases =
    [
        (["alternative role", "alternative position", "another role", "redeploy", "redeployment", "suitable role",
          "vacancy", "vacancies", "internal role", "internal application", "apply for", "applied for"],
         ["redeployment", "alternative", "role", "suitable", "vacancy", "internal application", "Occupational Health"]),
        (["return to work", "return date", "come back to work", "start back", "phased return", "return to your current role",
          "return to the same role", "return to the same site", "will not return", "won't return", "refuse to return", "refusing to return"],
         ["return", "fit note", "phased return", "role", "location", "reporting line", "Occupational Health"]),
        (["occupational health", "oh report", "oh recommendation"],
         ["Occupational Health", "recommendation", "redeployment", "phased return"]),
        (["fit note", "sick note", "fitness for work", "fit for work"],
         ["fit note", "fitness", "work", "Occupational Health"]),
        (["reasonable adjustment", "reasonable adjustments", "adjustment"],
         ["reasonable adjustments", "Occupational Health", "work", "role", "location"]),
        (["capability", "capability process", "capability procedure"],
         ["capability", "procedure", "Occupational Health", "reasonable adjustments", "redeployment"]),
        (["grievance"], ["grievance"]),
        (["settlement", "without prejudice"], ["settlement", "without prejudice"]),
        (["sick pay", "ssp", "statutory sick pay", "company sick pay", "csp"],
         ["sick pay", "SSP", "CSP", "company sick pay"]),
        (["payroll", "underpayment", "pay shortfall", "wage deduction", "deduction", "advance payment", "advance deduction"],
         ["payroll", "underpayment", "deduction", "advance", "reconciliation"]),
        (["reporting line", "line manager", "report to", "manager"],
         ["reporting", "manager", "role"])
    ];

    public MeetingAnalysis Analyze(string text)
    {
        var normalized = CollapseWhitespaceRegex().Replace(text.Trim(), " ");
        if (normalized.Length == 0)
        {
            return new(MeetingIntent.Unknown, AssistantImportance.Low, false, false, false, []);
        }

        var lower = normalized.ToLowerInvariant();
        var potentialCommitment = CommitmentTerms.Any(term => lower.Contains(term, StringComparison.Ordinal));
        var writtenFollowUp = lower.Contains("in writing", StringComparison.Ordinal) ||
                              lower.Contains("come back to you", StringComparison.Ordinal) ||
                              lower.Contains("get back to you", StringComparison.Ordinal);

        MeetingIntent intent;
        if (potentialCommitment && (normalized.EndsWith('?') || lower.StartsWith("can you") || lower.StartsWith("will you")))
        {
            intent = MeetingIntent.CommitmentRequest;
        }
        else if (normalized.EndsWith('?') || StartsLikeQuestion(lower))
        {
            intent = MeetingIntent.Question;
        }
        else if (lower.StartsWith("please ") || lower.StartsWith("we'd like you to") || lower.StartsWith("we would like you to"))
        {
            intent = MeetingIntent.Request;
        }
        else
        {
            intent = MeetingIntent.Information;
        }

        var needsAssistant = intent is MeetingIntent.Question or MeetingIntent.Request or MeetingIntent.CommitmentRequest;
        var importance = potentialCommitment ? AssistantImportance.High : needsAssistant ? AssistantImportance.Normal : AssistantImportance.Low;
        var terms = ExpandRetrievalTerms(normalized, ExtractRetrievalTerms(normalized));

        return new(intent, importance, needsAssistant, potentialCommitment, writtenFollowUp, terms);
    }

    private static bool StartsLikeQuestion(string lower) =>
        new[] { "why ", "what ", "when ", "where ", "who ", "how ", "could you", "would you", "do you", "are you", "is it", "have you" }
            .Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal));

    private static IReadOnlyList<string> ExtractRetrievalTerms(string text)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "that", "this", "with", "from", "have", "your", "you", "we", "they", "would", "could",
            "what", "when", "where", "why", "how", "are", "was", "were", "been", "for", "about", "into", "our"
        };

        return WordRegex().Matches(text)
            .Select(x => x.Value)
            .Where(x => x.Length >= 4 && !stop.Contains(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static IReadOnlyList<string> ExpandRetrievalTerms(string text, IReadOnlyList<string> baseTerms)
    {
        var lower = text.ToLowerInvariant();
        var terms = new List<string>(baseTerms);
        foreach (var (triggers, aliases) in HrConceptAliases)
        {
            if (!triggers.Any(trigger => lower.Contains(trigger, StringComparison.Ordinal))) continue;
            terms.AddRange(aliases);
        }

        return terms
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespaceRegex();

    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9'_-]*")]
    private static partial Regex WordRegex();
}
