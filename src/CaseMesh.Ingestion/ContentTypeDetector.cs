using System.IO.Compression;
using System.Text;

namespace CaseMesh.Ingestion;

public static class ContentTypeDetector
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static EvidenceMediaType Detect(string path, IngestionLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> prefix = stackalloc byte[12];
        var read = stream.Read(prefix);
        var bytes = prefix[..read];

        if (bytes.StartsWith("%PDF-"u8)) return EvidenceMediaType.Pdf;
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            ValidatePngDimensions(stream, limits);
            return EvidenceMediaType.Png;
        }
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            ValidateJpegDimensions(stream, limits);
            return EvidenceMediaType.Jpeg;
        }
        if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B)
            return DetectDocx(stream, limits);

        stream.Position = 0;
        string text;
        try
        {
            using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException exception)
        {
            throw new EvidenceIngestionException(
                IngestionFailureKind.UnsupportedMedia,
                "unsupported-signature",
                "The evidence signature is not in the supported allowlist.",
                exception);
        }

        if (text.IndexOf('\0') >= 0 || text.Any(character =>
                char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new EvidenceIngestionException(
                IngestionFailureKind.UnsupportedMedia,
                "unsupported-binary",
                "The evidence does not match a supported textual media type.");
        }

        return LooksLikeEml(text) ? EvidenceMediaType.Eml : EvidenceMediaType.PlainText;
    }

    private static EvidenceMediaType DetectDocx(Stream stream, IngestionLimits limits)
    {
        try
        {
            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > limits.MaximumPackageEntries)
            {
                throw ResourceLimit("package-entry-limit");
            }

            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.Length > limits.MaximumExpandedPackageBytes - expanded)
                {
                    throw ResourceLimit("package-expanded-byte-limit");
                }
                expanded += entry.Length;
                if ((entry.Length > 0 && entry.CompressedLength == 0) ||
                    entry.CompressedLength > 0 &&
                    (decimal)entry.Length / entry.CompressedLength > limits.MaximumPackageCompressionRatio)
                {
                    throw ResourceLimit("package-compression-ratio-limit");
                }
            }

            var hasTypes = archive.GetEntry("[Content_Types].xml") is not null;
            var hasDocument = archive.GetEntry("word/document.xml") is not null;
            if (!hasTypes || !hasDocument)
            {
                throw new EvidenceIngestionException(
                    IngestionFailureKind.UnsupportedMedia,
                    "unsupported-zip-container",
                    "ZIP containers are not an accepted evidence type; only DOCX packages are allowed.");
            }

            return EvidenceMediaType.Docx;
        }
        catch (InvalidDataException exception)
        {
            throw new EvidenceIngestionException(
                IngestionFailureKind.MalformedMedia,
                "malformed-docx-container",
                "The DOCX package is malformed.",
                exception);
        }
    }

    private static bool LooksLikeEml(string text)
    {
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0) headerEnd = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (headerEnd <= 0) return false;
        var headers = text[..headerEnd];
        return headers.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Any(line => line.StartsWith("From:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Date:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Message-Id:", StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidatePngDimensions(Stream stream, IngestionLimits limits)
    {
        if (stream.Length < 24) throw MalformedImage("malformed-png");
        Span<byte> dimensions = stackalloc byte[8];
        stream.Position = 16;
        if (stream.Read(dimensions) != dimensions.Length) throw MalformedImage("malformed-png");
        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(dimensions[..4]);
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(dimensions[4..]);
        ValidateImageDimensions(width, height, limits);
    }

    private static void ValidateJpegDimensions(Stream stream, IngestionLimits limits)
    {
        stream.Position = 2;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        while (stream.Position + 4 <= stream.Length)
        {
            if (reader.ReadByte() != 0xFF) throw MalformedImage("malformed-jpeg");
            byte marker;
            do { marker = reader.ReadByte(); } while (marker == 0xFF && stream.Position < stream.Length);
            if (marker is 0xD8 or 0xD9) continue;
            if (marker == 0xDA) break;
            var length = ReadBigEndianUInt16(reader);
            if (length < 2 || stream.Position + length - 2 > stream.Length) throw MalformedImage("malformed-jpeg");
            if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
            {
                if (length < 7) throw MalformedImage("malformed-jpeg");
                _ = reader.ReadByte();
                var height = ReadBigEndianUInt16(reader);
                var width = ReadBigEndianUInt16(reader);
                ValidateImageDimensions(width, height, limits);
                return;
            }
            stream.Position += length - 2;
        }
        throw MalformedImage("malformed-jpeg");
    }

    private static ushort ReadBigEndianUInt16(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[2];
        if (reader.Read(bytes) != 2) throw MalformedImage("malformed-jpeg");
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static void ValidateImageDimensions(uint width, uint height, IngestionLimits limits)
    {
        if (width == 0 || height == 0) throw MalformedImage("invalid-image-dimensions");
        if (width > limits.MaximumImageDimension || height > limits.MaximumImageDimension ||
            (ulong)width * height > (ulong)limits.MaximumImagePixels)
            throw ResourceLimit("image-pixel-limit");
    }

    private static EvidenceIngestionException MalformedImage(string code) => new(
        IngestionFailureKind.MalformedMedia, code, "The image header is malformed.");

    private static EvidenceIngestionException ResourceLimit(string code) => new(
        IngestionFailureKind.ResourceLimit,
        code,
        "The evidence package exceeds a configured resource limit.");
}
