using HRCompanion.Infrastructure.Documents;

namespace HRCompanion.Infrastructure.Tests;

public sealed class TextChunkerTests
{
    [Fact]
    public void PreservesLocatorAcrossChunks()
    {
        var text = string.Join(' ', Enumerable.Repeat("redeployment context sentence.", 120));
        var chunker = new TextChunker(500, 80);
        var chunks = chunker.Chunk(Guid.NewGuid(), [new LocatedText(text, "p.7")]);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.Equal("p.7", chunk.Locator));
    }
}
