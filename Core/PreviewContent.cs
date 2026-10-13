using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SharpCompress.Archives;

namespace CleanTag.Core;

public sealed record ContentPreview(string Text, byte[]? Image = null, string? ImageType = null);

public static class PreviewContent
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".log", ".ini", ".cfg", ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".html", ".css", ".sql", ".toml", ".srt", ".vtt", ".svg", ".bat", ".ps1", ".sh", ".gitignore" };
    public static bool IsText(string extension) => TextExtensions.Contains(extension);
    public static ContentPreview Read(FileEntry file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (file.Extension == ".svg")
        {
            using var reader = XmlReader.Create(file.FullPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
            var document = XDocument.Load(reader);
            if (document.Root?.Name.LocalName != "svg") throw new IOException("Geçersiz SVG.");
            foreach (var node in document.Descendants().ToArray())
            {
                token.ThrowIfCancellationRequested();
                if (node.Name.LocalName is "script" or "foreignObject" or "style" or "image" or "animate" or "set") { node.Remove(); continue; }
                foreach (var attribute in node.Attributes().ToArray())
                    if (attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                        (attribute.Name.LocalName == "href" && !attribute.Value.StartsWith('#')) ||
                        attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase)) attribute.Remove();
            }
            return new("", Encoding.UTF8.GetBytes(document.Root.ToString()), "svg");
        }
        if (file.Extension is ".rar" or ".7z" or ".tar" or ".gz")
        {
            using var archive = ArchiveFactory.OpenArchive(new FileInfo(file.FullPath));
            var lines = new List<string>();
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (lines.Count == 150) { lines.Add("— İlk 150 öğe —"); break; }
                lines.Add($"{entry.Key}    {FileEntry.FormatSize(entry.Size)}{(entry.IsEncrypted ? " · Şifreli" : "")}");
            }
            return new("Arşiv içeriği · Dosyalar çıkartılmadı\n\n" + string.Join('\n', lines));
        }
        if (IsText(file.Extension) || file.Size == 0)
        {
            using var stream = File.Open(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var buffer = new char[64000]; int count = reader.ReadBlock(buffer, 0, buffer.Length);
            string text = new(buffer, 0, count);
            return new(text + (count == buffer.Length ? "\n\n— Önizleme ilk 64.000 karakterle sınırlı —" : ""));
        }
        if (file.Extension is ".psd" or ".psb")
            return new("Photoshop kaynağı · Gömülü önizleme", ReadPhotoshopThumbnail(file.FullPath, token), "jpeg");
        if (file.Extension is ".zip" or ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp" or ".epub")
        {
            using var zip = ZipFile.OpenRead(file.FullPath);
            if (file.Extension == ".zip")
            {
                var names = zip.Entries.Take(150).Select(e => $"{e.FullName}    {FileEntry.FormatSize(e.Length)}");
                return new($"{zip.Entries.Count:N0} arşiv öğesi · İçerik çıkartılmadı\n\n" + string.Join('\n', names) + (zip.Entries.Count > 150 ? "\n\n— İlk 150 öğe —" : ""));
            }
            var thumbnail = zip.GetEntry("docProps/thumbnail.jpeg") ?? zip.GetEntry("docProps/thumbnail.png") ?? zip.GetEntry("Thumbnails/thumbnail.png");
            byte[]? image = thumbnail is { Length: > 0 and < 4_000_000 } ? ReadEntryBytes(thumbnail, 4_000_000) : null;
            var text = new StringBuilder();
            if (file.Extension == ".docx") text.Append(ReadXmlText(zip.GetEntry("word/document.xml"), token));
            else if (file.Extension == ".pptx")
                foreach (var slide in zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).OrderBy(e => SlideNumber(e.Name)).Take(12))
                    text.AppendLine($"\n— {slide.Name} —\n{ReadXmlText(slide, token)}");
            else if (file.Extension == ".xlsx")
            {
                var strings = ReadXmlValues(zip.GetEntry("xl/sharedStrings.xml"), "t", token).Take(10000).ToArray();
                var sheet = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("xl/worksheets/sheet") && e.FullName.EndsWith(".xml"));
                if (sheet != null && sheet.Length <= 4_000_000)
                {
                    using var reader = CreateReader(sheet);
                    int rows = 0;
                    while (reader.Read() && rows < 160)
                    {
                        token.ThrowIfCancellationRequested();
                        if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
                        {
                            var shared = reader.GetAttribute("t") == "s";
                            using var cell = reader.ReadSubtree(); string value = "";
                            while (cell.Read()) if (cell.NodeType == XmlNodeType.Text) value += cell.Value;
                            if (shared && int.TryParse(value, out int i) && i >= 0 && i < strings.Length) value = strings[i];
                            text.Append(value).Append("  |  ");
                        }
                        if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row") { text.AppendLine(); rows++; }
                    }
                }
            }
            else text.Append(ReadXmlText(zip.GetEntry("content.xml"), token));
            string content = text.ToString();
            if (content.Length > 64000) content = content[..64000] + "\n— Önizleme kısaltıldı —";
            if (string.IsNullOrWhiteSpace(content)) content = "Bu belgeden metin önizlemesi alınamadı. Kendi uygulamasında açabilirsiniz.";
            return new(content, image, thumbnail?.Name.EndsWith("png") == true ? "png" : "jpeg");
        }
        return new("");
    }
    private static int SlideNumber(string name) => int.TryParse(new string(name.Where(char.IsDigit).ToArray()), out var n) ? n : 0;
    private static XmlReader CreateReader(ZipArchiveEntry entry)
    {
        if (entry.Length > 4_000_000) throw new IOException("Belge bölümü önizleme sınırından büyük.");
        return XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000, CloseInput = true });
    }
    private static IEnumerable<string> ReadXmlValues(ZipArchiveEntry? entry, string node, CancellationToken token)
    {
        if (entry == null) yield break;
        using var reader = CreateReader(entry);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == node)
            {
                using var part = reader.ReadSubtree(); var text = new StringBuilder();
                while (part.Read()) if (part.NodeType == XmlNodeType.Text) text.Append(part.Value);
                yield return text.ToString();
            }
        }
    }
    private static string ReadXmlText(ZipArchiveEntry? entry, CancellationToken token)
    {
        if (entry == null) return "";
        using var reader = CreateReader(entry); var text = new StringBuilder();
        while (reader.Read() && text.Length < 64000)
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Text) text.Append(reader.Value).Append(' ');
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "p" or "tr") text.AppendLine();
        }
        return text.ToString();
    }
    private static byte[] ReadEntryBytes(ZipArchiveEntry entry, int max)
    {
        using var stream = entry.Open(); var bytes = new byte[Math.Min(entry.Length, max)]; stream.ReadExactly(bytes); return bytes;
    }
    private static byte[]? ReadPhotoshopThumbnail(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[26]; stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("8BPS"u8)) return null;
        uint Read32() { Span<byte> b = stackalloc byte[4]; stream.ReadExactly(b); return BinaryPrimitives.ReadUInt32BigEndian(b); }
        ushort Read16() { Span<byte> b = stackalloc byte[2]; stream.ReadExactly(b); return BinaryPrimitives.ReadUInt16BigEndian(b); }
        var colorLength = Read32(); stream.Seek(colorLength, SeekOrigin.Current);
        long resourcesEnd = stream.Position + 4L + Read32(); resourcesEnd = Math.Min(resourcesEnd, stream.Length);
        int count = 0; Span<byte> signature = stackalloc byte[4];
        while (stream.Position + 12 < resourcesEnd && count++ < 10000)
        {
            token.ThrowIfCancellationRequested();
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual("8BIM"u8)) break;
            var id = Read16(); int nameLength = stream.ReadByte();
            if (nameLength < 0) break;
            stream.Seek(nameLength + ((nameLength + 1) % 2), SeekOrigin.Current);
            uint size = Read32(); long next = stream.Position + size + (size % 2);
            if (next > resourcesEnd) break;
            if (id is 1033 or 1036 && size > 28 && size < 8_000_000)
            {
                uint format = Read32(); stream.Seek(24, SeekOrigin.Current);
                if (format == 1) { var jpeg = new byte[size - 28]; stream.ReadExactly(jpeg); return jpeg; }
            }
            stream.Position = next;
        }
        return null;
    }
}
