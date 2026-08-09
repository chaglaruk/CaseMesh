using HRCompanion.Core.Models;

namespace HRCompanion.Infrastructure.Documents;

public sealed class TextChunker
{
    public TextChunker(int targetCharacters = 1800, int overlapCharacters = 220)
    {
        if (targetCharacters < 400) throw new ArgumentOutOfRangeException(nameof(targetCharacters));
        if (overlapCharacters < 0 || overlapCharacters >= targetCharacters / 2) throw new ArgumentOutOfRangeException(nameof(overlapCharacters));
        TargetCharacters = targetCharacters;
        OverlapCharacters = overlapCharacters;
    }

    public int TargetCharacters { get; }
    public int OverlapCharacters { get; }

    public IReadOnlyList<DocumentChunk> Chunk(Guid documentId, IReadOnlyList<LocatedText> sections)
    {
        var result = new List<DocumentChunk>();
        var ordinal = 0;

        foreach (var section in sections)
        {
            var text = Normalize(section.Text);
            if (text.Length == 0) continue;

            var cursor = 0;
            while (cursor < text.Length)
            {
                var desiredEnd = Math.Min(text.Length, cursor + TargetCharacters);
                var end = FindNaturalBreak(text, cursor, desiredEnd);
                if (end <= cursor) end = desiredEnd;

                var chunkText = text[cursor..end].Trim();
                if (chunkText.Length > 0)
                {
                    result.Add(new(Guid.NewGuid(), documentId, ordinal++, chunkText, section.Locator));
                }

                if (end >= text.Length) break;
                cursor = Math.Max(cursor + 1, end - OverlapCharacters);
            }
        }

        return result;
    }

    private static int FindNaturalBreak(string text, int start, int desiredEnd)
    {
        if (desiredEnd >= text.Length) return text.Length;
        var floor = Math.Max(start + 1, desiredEnd - 350);
        for (var i = desiredEnd; i >= floor; i--)
        {
            if (i < text.Length && (text[i] == '\n' || (char.IsWhiteSpace(text[i]) && i > 0 && ".!?".Contains(text[i - 1]))))
            {
                return i;
            }
        }
        return desiredEnd;
    }

    private static string Normalize(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));
}
