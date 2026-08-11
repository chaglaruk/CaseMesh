using System.Text.RegularExpressions;
using HRCompanion.Core.Models;

namespace HRCompanion.Core.Services;

public sealed partial class DeterministicCueEngine
{
    private static readonly string[] CommitmentTerms =
    [
        "agree", "accept", "confirm", "resign", "withdraw", "sign", "consent",
        "start on", "return on", "return by", "final decision", "settlement", "capability",
        "dismissal", "dismiss", "terminate", "termination"
    ];

    private static readonly (string[] Triggers, string[] RetrievalTerms)[] HrConceptAliases =
    [
        (["capability", "capability process", "capability procedure", "health capability", "ill health capability", "capability hearing",
          "dismissal on the basis of capability", "dismissal on capability", "may result in dismissal", "could result in dismissal"],
         ["health related capability", "capability", "procedure", "medical evidence", "Occupational Health", "reasonable adjustments", "redeployment", "last resort"]),
        (["alternative role", "alternative position", "another role", "redeploy", "redeployment", "suitable role",
          "vacancy", "vacancies", "internal role", "internal application", "apply for", "applied for", "secured an alternative role",
          "successfully secured an alternative role"],
         ["redeployment", "alternative", "role", "suitable", "vacancy", "internal application", "Occupational Health"]),
        (["return to work", "return date", "come back to work", "start back", "phased return", "return to your current role",
          "return to the same role", "return to the same site", "will not return", "won't return", "refuse to return", "refusing to return",
          "resume your role", "resuming your role", "resume work", "resuming work", "when will you be resuming", "intend to return",
          "do not intend to return", "don't intend to return"],
         ["return", "safe return", "not fit for work", "not refusing", "same role", "same environment", "reporting line", "Occupational Health", "fit note"]),
        (["occupational health", "oh report", "oh recommendation", "medical advice", "medical evidence", "medical information"],
         ["Occupational Health", "recommendation", "not fit for work", "redeployment", "phased return", "reasonable adjustments"]),
        (["fit note", "sick note", "fitness for work", "fit for work", "not fit for work", "doctor's note", "doctors note",
          "medical certificate", "certificate", "current fit note", "latest fit note"],
         ["fit note", "not fit for work", "fitness", "work", "current", "Occupational Health", "medical advice"]),
        (["reasonable adjustment", "reasonable adjustments", "adjustment", "adjusted duties", "temporary adjustment"],
         ["reasonable adjustments", "Occupational Health", "work", "role", "location", "reporting line", "phased return"]),
        (["grievance"], ["grievance"]),
        (["settlement", "without prejudice"], ["settlement", "without prejudice"]),
        (["sick pay", "ssp", "statutory sick pay", "company sick pay", "csp"],
         ["company sick pay", "CSP", "service band", "entitlement"]),
        (["payroll", "underpayment", "pay shortfall", "wage deduction", "deduction", "advance payment", "advance deduction",
          "unresolved about your pay", "outstanding about your pay", "still unresolved about your pay"],
         ["payroll", "reconciliation", "payment discrepancy", "payslip", "employer letter", "deduction", "advance", "correction",
          "company sick pay", "CSP", "service band", "entitlement"]),
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
        var containsDirectQuestion = ContainsDirectQuestion(normalized, lower);

        MeetingIntent intent;
        if (potentialCommitment && containsDirectQuestion)
        {
            intent = MeetingIntent.CommitmentRequest;
        }
        else if (containsDirectQuestion)
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

    private static bool ContainsDirectQuestion(string text, string lower) =>
        text.Contains('?') ||
        StartsLikeQuestion(lower) ||
        EmbeddedDirectQuestionRegex().IsMatch(text) ||
        ConversationalQuestionRegex().IsMatch(text);

    private static bool StartsLikeQuestion(string lower) =>
        new[] { "why ", "what ", "when ", "where ", "who ", "how ", "can you", "could you", "would you", "will you", "do you", "are you", "is it", "have you" }
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
        var terms = new List<string>();
        foreach (var (triggers, aliases) in HrConceptAliases)
        {
            if (!triggers.Any(trigger => lower.Contains(trigger, StringComparison.Ordinal))) continue;
            terms.AddRange(aliases);
        }
        terms.AddRange(baseTerms);

        return terms
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    [GeneratedRegex(@"(?:^|[.!;,:]\s+)(?:(?:(?:so|and|but|well)|(?:um+|uh+|erm+|er+|ah+|hmm+))[,\s]+)*(?:why|what|when|where|who|how|can you|could you|would you|will you|do you|are you|is it|have you)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedDirectQuestionRegex();

    [GeneratedRegex(@"\b(?:wondering|wanted to ask|want to ask|need to understand|trying to understand|help me understand)\b.{0,140}?\b(?:why|what|when|where|who|how|whether|if|can you|could you|would you|will you|do you|are you|have you)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConversationalQuestionRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespaceRegex();

    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9'_-]*")]
    private static partial Regex WordRegex();
}
