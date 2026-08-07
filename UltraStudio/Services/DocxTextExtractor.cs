using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace UltraStudio.Services
{
    /// <summary>
    /// Izvlači ČIST tekst iz .docx za potrebe lekture — bez ambicije da
    /// sačuva formatiranje (za to postoji budući "vrati u dizajn" korak).
    /// .docx je obično zip arhiva sa word/document.xml unutra, pa nam ne
    /// treba nikakva dodatna biblioteka za ovo.
    /// </summary>
    public static class DocxTextExtractor
    {
        private static readonly Regex ParagraphRegex = new("<w:p[ >].*?</w:p>", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex TextRunRegex = new("<w:t[^>]*>(.*?)</w:t>", RegexOptions.Singleline | RegexOptions.Compiled);

        public static string ExtractPlainText(string docxPath)
        {
            using var archive = ZipFile.OpenRead(docxPath);
            var entry = archive.GetEntry("word/document.xml");
            if (entry == null) return "";

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            string xml = reader.ReadToEnd();

            var sb = new StringBuilder();
            foreach (Match p in ParagraphRegex.Matches(xml))
            {
                foreach (Match t in TextRunRegex.Matches(p.Value))
                    sb.Append(WebUtility.HtmlDecode(t.Groups[1].Value));
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
    }
}
