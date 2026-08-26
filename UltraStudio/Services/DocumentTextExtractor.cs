using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Xml.Linq;
using NPOI.POIFS.FileSystem;
using NPOI.XWPF.Extractor;
using NPOI.XWPF.UserModel;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace UltraStudio.Services
{
    /// <summary>
    /// Univerzalni ekstraktor teksta za lekturu — podržava:
    /// - Word dokumente: .docx (novi XML) i .doc (stari Word 97-2003 OLE2 binarni)
    /// - PDF dokumente: .pdf (PdfPig layout analiza po stranicama)
    /// - Rich Text: .rtf (WPF TextRange nativno)
    /// - OpenDocument Text: .odt (LibreOffice / OpenOffice content.xml)
    /// - eKnjige: .epub (raspored poglavlja iz zip arhive)
    /// - Web stranice: .html, .htm, .xhtml (dekodiranje entiteta i skidanje tagova)
    /// - Titlove: .srt, .vtt (čišćenje vremenskih kodova i brojeva frejmova)
    /// - Običan tekst i Markdown: .txt, .md, .csv, .log, .json, .xml itd. (auto BOM/enkoding)
    /// </summary>
    public static class DocumentTextExtractor
    {
        public static string ExtractText(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException($"Fajl nije pronađen: {filePath}");

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".docx" => ExtractDocx(filePath),
                ".doc" => ExtractDoc(filePath),
                ".pdf" => ExtractPdf(filePath),
                ".rtf" => ExtractRtf(filePath),
                ".odt" => ExtractOdt(filePath),
                ".epub" => ExtractEpub(filePath),
                ".html" or ".htm" or ".xhtml" => ExtractHtml(filePath),
                ".srt" or ".vtt" => ExtractSubtitles(filePath),
                _ => ExtractPlainText(filePath)
            };
        }

        // ════════════════════════════════════════════════════════════════
        // 1. WORD DOCX (.docx)
        // ════════════════════════════════════════════════════════════════
        public static string ExtractDocx(string filePath)
        {
            try
            {
                using var fs = File.OpenRead(filePath);
                var doc = new XWPFDocument(fs);
                var extractor = new XWPFWordExtractor(doc);
                string text = extractor.Text;
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            catch (Exception ex)
            {
                DebugLog.Write($"DocumentTextExtractor: NPOI DOCX greška ({ex.Message}), prelazim na XML zip fallback...");
            }

            // Fallback: direktno čitanje word/document.xml iz zip-a
            return DocxTextExtractor.ExtractPlainText(filePath);
        }

        // ════════════════════════════════════════════════════════════════
        // 2. LEGACY WORD DOC (.doc, Word 97-2003 OLE2 Binarni)
        // ════════════════════════════════════════════════════════════════
        public static string ExtractDoc(string filePath)
        {
            try
            {
                using var fs = File.OpenRead(filePath);
                var poifs = new POIFSFileSystem(fs);
                var root = poifs.Root;

                if (!root.HasEntry("WordDocument"))
                    throw new InvalidDataException("Nije pronađen WordDocument strim u OLE2 fajlu.");

                byte[] wordDocBytes = ReadStreamBytes(root, "WordDocument");
                if (wordDocBytes.Length < 0x0200)
                    throw new InvalidDataException("WordDocument strim je premali za validan FIB.");

                // FIB zaglavlje: proveri table stream (0Table ili 1Table)
                ushort flags = BitConverter.ToUInt16(wordDocBytes, 0x000A);
                bool is1Table = (flags & 0x0200) != 0;
                string tableName = is1Table ? "1Table" : "0Table";

                if (root.HasEntry(tableName))
                {
                    byte[] tableBytes = ReadStreamBytes(root, tableName);
                    int fcClx = BitConverter.ToInt32(wordDocBytes, 0x01A2);
                    int lcbClx = BitConverter.ToInt32(wordDocBytes, 0x01A6);

                    if (fcClx >= 0 && lcbClx > 0 && fcClx + lcbClx <= tableBytes.Length)
                    {
                        string pieceText = ParseDocPieceTable(wordDocBytes, tableBytes, fcClx, lcbClx);
                        if (!string.IsNullOrWhiteSpace(pieceText))
                            return CleanWordText(pieceText);
                    }
                }

                // Fallback: heurističko čitanje Unicode i ANSI nizova iz WordDocument strima
                return ExtractStringsFromBytes(wordDocBytes);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"DocumentTextExtractor.ExtractDoc greška: {ex.Message}");
                // Krajnji fallback: čitaj ceo fajl kao tekstualne karaktere
                return ExtractStringsFromFile(filePath);
            }
        }

        private static byte[] ReadStreamBytes(DirectoryEntry dir, string name)
        {
            using var entryStream = new DocumentInputStream((DocumentEntry)dir.GetEntry(name));
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            return ms.ToArray();
        }

        private static string ParseDocPieceTable(byte[] wordDoc, byte[] table, int fcClx, int lcbClx)
        {
            int offset = fcClx;
            int end = fcClx + lcbClx;
            var sb = new StringBuilder();

            while (offset < end)
            {
                byte clxt = table[offset++];
                if (clxt == 0x01) // Grpprl
                {
                    int cb = BitConverter.ToUInt16(table, offset);
                    offset += 2 + cb;
                }
                else if (clxt == 0x02) // PlcPcd (Piece Table)
                {
                    int lcb = BitConverter.ToInt32(table, offset);
                    offset += 4;
                    int pcdCount = (lcb - 4) / 12; // (lcb - 4) / (4 + 8)

                    int cpOffset = offset;
                    int pcdOffset = offset + (pcdCount + 1) * 4;

                    for (int i = 0; i < pcdCount; i++)
                    {
                        int cpStart = BitConverter.ToInt32(table, cpOffset + i * 4);
                        int cpEnd = BitConverter.ToInt32(table, cpOffset + (i + 1) * 4);
                        int charCount = cpEnd - cpStart;
                        if (charCount <= 0) continue;

                        int fc = BitConverter.ToInt32(table, pcdOffset + i * 8 + 2);
                        bool isAnsi = (fc & 0x40000000) != 0;
                        int rawFc = fc & ~0x40000000;

                        if (isAnsi)
                        {
                            int byteOffset = rawFc / 2;
                            if (byteOffset + charCount <= wordDoc.Length)
                            {
                                string text = Encoding.GetEncoding("windows-1250").GetString(wordDoc, byteOffset, charCount);
                                sb.Append(text);
                            }
                        }
                        else
                        {
                            int byteOffset = rawFc;
                            if (byteOffset + charCount * 2 <= wordDoc.Length)
                            {
                                string text = Encoding.Unicode.GetString(wordDoc, byteOffset, charCount * 2);
                                sb.Append(text);
                            }
                        }
                    }
                    break;
                }
                else break;
            }

            return sb.ToString();
        }

        private static string CleanWordText(string raw)
        {
            var sb = new StringBuilder();
            foreach (char c in raw)
            {
                if (c == '\r' || c == '\n' || c == '\t' || (c >= 32 && c != 0x07 && c != 0x08 && c != 0x0C && c != 0x01 && c != 0x13 && c != 0x14 && c != 0x15))
                    sb.Append(c);
                else if (c == 0x0B || c == 0x07) // Line break / cell break u Wordu
                    sb.AppendLine();
            }
            return sb.ToString().Trim();
        }

        private static string ExtractStringsFromBytes(byte[] bytes)
        {
            var sb = new StringBuilder();
            // Traži Unicode karaktere
            for (int i = 0x0200; i < bytes.Length - 1; i += 2)
            {
                char c = (char)(bytes[i] | (bytes[i + 1] << 8));
                if (c == '\r' || c == '\n' || c == '\t' || (c >= 32 && c < 0xD800))
                    sb.Append(c);
            }
            string res = sb.ToString().Trim();
            return res.Length > 50 ? res : Encoding.GetEncoding("windows-1250").GetString(bytes);
        }

        private static string ExtractStringsFromFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return ExtractStringsFromBytes(bytes);
        }

        // ════════════════════════════════════════════════════════════════
        // 3. PDF DOKUMENTI (.pdf)
        // ════════════════════════════════════════════════════════════════
        public static string ExtractPdf(string filePath)
        {
            using var document = PdfDocument.Open(filePath);
            var sb = new StringBuilder();
            int pageNum = 1;

            foreach (var page in document.GetPages())
            {
                string text = "";
                try { text = ContentOrderTextExtractor.GetText(page); }
                catch { text = page.Text; }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (document.NumberOfPages > 1)
                        sb.AppendLine($"--- [Stranica {pageNum}] ---");
                    sb.AppendLine(text.Trim());
                    sb.AppendLine();
                }
                pageNum++;
            }

            return sb.ToString().Trim();
        }

        // ════════════════════════════════════════════════════════════════
        // 4. RICH TEXT FORMAT (.rtf)
        // ════════════════════════════════════════════════════════════════
        public static string ExtractRtf(string filePath)
        {
            var flowDoc = new FlowDocument();
            var range = new TextRange(flowDoc.ContentStart, flowDoc.ContentEnd);
            using var stream = File.OpenRead(filePath);
            range.Load(stream, DataFormats.Rtf);
            return range.Text.Trim();
        }

        // ════════════════════════════════════════════════════════════════
        // 5. OPENDOCUMENT TEXT (.odt)
        // ════════════════════════════════════════════════════════════════
        public static string ExtractOdt(string filePath)
        {
            using var archive = ZipFile.OpenRead(filePath);
            var entry = archive.GetEntry("content.xml");
            if (entry == null) return "";

            using var stream = entry.Open();
            var xdoc = XDocument.Load(stream);
            XNamespace textNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

            var paragraphs = xdoc.Descendants(textNs + "p")
                .Concat(xdoc.Descendants(textNs + "h"))
                .Select(p => string.Concat(p.DescendantNodes().OfType<XText>().Select(t => t.Value)).Trim())
                .Where(p => !string.IsNullOrEmpty(p));

            return string.Join(Environment.NewLine, paragraphs).Trim();
        }

        // ════════════════════════════════════════════════════════════════
        // 6. EPUB E-KNJIGE (.epub)
        // ════════════════════════════════════════════════════════════════
        public static string ExtractEpub(string filePath)
        {
            using var archive = ZipFile.OpenRead(filePath);
            var sb = new StringBuilder();

            var entries = archive.Entries
                .Where(e => e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                            e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
                            e.FullName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName);

            foreach (var entry in entries)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                string html = reader.ReadToEnd();
                string plain = StripHtml(html);
                if (!string.IsNullOrWhiteSpace(plain))
                {
                    sb.AppendLine(plain);
                    sb.AppendLine();
                }
            }

            return sb.ToString().Trim();
        }

        // ════════════════════════════════════════════════════════════════
        // 7. HTML / WEB STRANICE (.html, .htm, .xhtml)
        // ════════════════════════════════════════════════════════════════
        public static string ExtractHtml(string filePath)
        {
            string html = File.ReadAllText(filePath, DetectEncoding(filePath));
            return StripHtml(html);
        }

        private static string StripHtml(string html)
        {
            html = Regex.Replace(html, "<(script|style|head)[^>]*?>.*?</\\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<(br|p|div|h[1-6]|li|tr)[^>]*>", Environment.NewLine, RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<[^>]+>", " ");
            string decoded = WebUtility.HtmlDecode(html);

            var lines = decoded.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(l => l.Trim())
                               .Where(l => !string.IsNullOrWhiteSpace(l));
            return string.Join(Environment.NewLine, lines);
        }

        // ════════════════════════════════════════════════════════════════
        // 8. TITLOVI (.srt, .vtt)
        // ════════════════════════════════════════════════════════════════
        public static string ExtractSubtitles(string filePath)
        {
            string[] rawLines = File.ReadAllLines(filePath, DetectEncoding(filePath));
            var sb = new StringBuilder();
            var timeRegex = new Regex(@"(\d{2}:\d{2}:\d{2}[\.,]\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}[\.,]\d{3})|WEBVTT", RegexOptions.Compiled);

            foreach (var line in rawLines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                if (int.TryParse(trimmed, out _)) continue; // Redni broj titla
                if (timeRegex.IsMatch(trimmed)) continue;  // Vremenski kod

                string cleanLine = Regex.Replace(trimmed, @"<[^>]+>", ""); // skini <i>, <b> itd.
                sb.AppendLine(cleanLine);
            }

            return sb.ToString().Trim();
        }

        // ════════════════════════════════════════════════════════════════
        // 9. OBIČAN TEKST & MARKDOWN (.txt, .md, .csv, .log, itd.)
        // ════════════════════════════════════════════════════════════════
        public static string ExtractPlainText(string filePath)
        {
            return File.ReadAllText(filePath, DetectEncoding(filePath)).Trim();
        }

        public static Encoding DetectEncoding(string filePath)
        {
            try
            {
                using var fs = File.OpenRead(filePath);
                if (fs.Length >= 4)
                {
                    byte[] bom = new byte[4];
                    fs.Read(bom, 0, 4);
                    if (bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf) return Encoding.UTF8;
                    if (bom[0] == 0xff && bom[1] == 0xfe) return Encoding.Unicode;
                    if (bom[0] == 0xfe && bom[1] == 0xff) return Encoding.BigEndianUnicode;
                    if (bom[0] == 0 && bom[1] == 0 && bom[2] == 0xfe && bom[3] == 0xff) return Encoding.UTF32;
                }
            }
            catch { }
            return Encoding.UTF8;
        }

        // ════════════════════════════════════════════════════════════════
        // ČUVANJE REZULTATA LEKTURE U RAZLIČITIM FORMATIMA
        // ════════════════════════════════════════════════════════════════
        public static void SaveDocument(string text, string outputPath)
        {
            string ext = Path.GetExtension(outputPath).ToLowerInvariant();
            switch (ext)
            {
                case ".docx":
                {
                    using var doc = new XWPFDocument();
                    string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    foreach (var line in lines)
                    {
                        var p = doc.CreateParagraph();
                        var r = p.CreateRun();
                        r.SetText(line);
                    }
                    using var fs = File.Create(outputPath);
                    doc.Write(fs);
                    break;
                }
                case ".rtf":
                {
                    var flowDoc = new FlowDocument();
                    string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    foreach (var line in lines)
                        flowDoc.Blocks.Add(new Paragraph(new Run(line)));
                    var range = new TextRange(flowDoc.ContentStart, flowDoc.ContentEnd);
                    using var fs = File.Create(outputPath);
                    range.Save(fs, DataFormats.Rtf);
                    break;
                }
                default:
                {
                    File.WriteAllText(outputPath, text, Encoding.UTF8);
                    break;
                }
            }
        }
    }
}
