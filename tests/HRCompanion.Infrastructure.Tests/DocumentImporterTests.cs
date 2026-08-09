using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HRCompanion.Infrastructure.Data;
using HRCompanion.Infrastructure.Documents;
using MimeKit;

namespace HRCompanion.Infrastructure.Tests;

public sealed class DocumentImporterTests
{
    [Fact]
    public async Task RecursiveImport_CoversAllFormatsDeduplicationLocatorsAndPerFileIsolation()
    {
        var root = Path.Combine(Path.GetTempPath(), "HRCompanion.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var nested = Path.Combine(source, "nested");
        Directory.CreateDirectory(nested);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "note.txt"), "Synthetic adjustment evidence from plain text.");
            await File.WriteAllTextAsync(Path.Combine(source, "note-copy.txt"), "Synthetic adjustment evidence from plain text.");
            await File.WriteAllTextAsync(Path.Combine(nested, "position.md"), "# Position\nSynthetic redeployment preference.");
            await File.WriteAllTextAsync(Path.Combine(nested, "page.html"), "<html><body><p>Synthetic HTML grievance evidence.</p></body></html>");
            CreateDocx(Path.Combine(source, "letter.docx"));
            await CreateEmlAsync(Path.Combine(source, "message.eml"));
            await File.WriteAllBytesAsync(Path.Combine(nested, "evidence.pdf"), CreatePdf("Synthetic PDF phased return evidence."));
            await File.WriteAllTextAsync(Path.Combine(source, "broken.pdf"), "not a PDF");

            var repository = new SqliteCaseRepository(new AppPaths(Path.Combine(root, "app")));
            await repository.InitializeAsync();
            var result = await new DocumentImporter(repository).ImportPathsAsync([source]);

            Assert.Equal(8, result.FilesSeen);
            Assert.Equal(6, result.Imported);
            Assert.Equal(1, result.SkippedDuplicate);
            Assert.Single(result.Errors);
            Assert.Contains("broken.pdf", result.Errors[0], StringComparison.OrdinalIgnoreCase);

            var pdf = await repository.SearchAsync("phased return");
            Assert.Contains(pdf, item => item.SourceName == "evidence.pdf" && item.SourceLocator == "p.1");
            Assert.Contains(await repository.SearchAsync("redeployment preference"), item => item.SourceName == "position.md");
            Assert.Contains(await repository.SearchAsync("synthetic subject"), item => item.SourceName.Contains("synthetic subject", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void CreateDocx(string path)
    {
        using var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var part = document.AddMainDocumentPart();
        part.Document = new Document(new Body(new Paragraph(new Run(new Text("Synthetic DOCX Occupational Health evidence.")))));
        part.Document.Save();
    }

    private static async Task CreateEmlAsync(string path)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("sender@example.test"));
        message.To.Add(MailboxAddress.Parse("recipient@example.test"));
        message.Subject = "Synthetic subject";
        message.Body = new TextPart("plain") { Text = "Synthetic EML capability evidence." };
        await message.WriteToAsync(path);
    }

    private static byte[] CreatePdf(string text)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {text.Length + 34} >>\nstream\nBT /F1 12 Tf 72 720 Td ({text}) Tj ET\nendstream"
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
