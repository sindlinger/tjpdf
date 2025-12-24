using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using FilterPDF;
using FilterPDF.TjpbDespachoExtractor.Utils;
using iText.Signatures;
using Newtonsoft.Json;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Extrai rodapé/assinatura e datas por página para validação.
    /// Uso: tjpdf-cli footer --input file.pdf [--page N] [--all] [--tail-lines N] [--json]
    /// </summary>
    public class FooterCommand : Command
    {
        public override string Name => "footer";
        public override string Description => "Extrai rodapé/assinatura por página (signer/signed_at/date_footer)";

        private readonly string[] GenericOrigins = new[]
        {
            "PODER JUDICIÁRIO",
            "PODER JUDICIARIO",
            "TRIBUNAL DE JUSTIÇA",
            "TRIBUNAL DE JUSTICA",
            "MINISTÉRIO PÚBLICO",
            "MINISTERIO PUBLICO",
            "DEFENSORIA PÚBLICA",
            "DEFENSORIA PUBLICA",
            "PROCURADORIA",
            "ESTADO DA PARAÍBA",
            "ESTADO DA PARAIBA",
            "GOVERNO DO ESTADO"
        };

        public override void Execute(string[] args)
        {
            string inputFile = "";
            bool json = false;
            bool all = false;
            int? page = null;
            int tailLines = 12;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--input":
                        if (i + 1 < args.Length) inputFile = args[++i];
                        break;
                    case "--page":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) { page = p; i++; }
                        break;
                    case "--all":
                        all = true;
                        break;
                    case "--json":
                        json = true;
                        break;
                    case "--tail-lines":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var tl)) { tailLines = tl; i++; }
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(inputFile))
            {
                Console.WriteLine("Informe --input <file.pdf>");
                ShowHelp();
                return;
            }

            var analyzer = new PDFAnalyzer(inputFile);
            var analysis = analyzer.AnalyzeFull();
            int total = analysis.DocumentInfo.TotalPages;

            var pages = new List<int>();
            if (page.HasValue)
                pages.Add(Math.Max(1, Math.Min(total, page.Value)));
            else if (all)
                pages.AddRange(Enumerable.Range(1, total));
            else
                pages.Add(total); // default: última página

            var outputs = new List<Dictionary<string, object>>();
            foreach (var p in pages)
            {
                var pageInfo = analysis.Pages[p - 1];
                var pageText = pageInfo.TextInfo.PageText ?? "";
                var footer = pageInfo.TextInfo.Footers?.FirstOrDefault() ?? "";
                var footerSignatureRaw = ExtractFooterSignatureRaw(pageText, tailLines);
                var signer = ExtractSigner(pageText, footer, footerSignatureRaw, analysis.Signatures);
                var signedAt = ExtractSignedAt(pageText, footer, footerSignatureRaw);
                var dateFooter = ExtractDateFromFooter(pageText, footer, footerSignatureRaw);

                outputs.Add(new Dictionary<string, object>
                {
                    ["page"] = p,
                    ["footer"] = footer,
                    ["footer_signature_raw"] = footerSignatureRaw,
                    ["signer"] = signer,
                    ["signed_at"] = signedAt,
                    ["date_footer"] = dateFooter
                });
            }

            if (json)
            {
                if (outputs.Count == 1)
                    Console.WriteLine(JsonConvert.SerializeObject(outputs[0], Formatting.Indented));
                else
                    Console.WriteLine(JsonConvert.SerializeObject(outputs, Formatting.Indented));
            }
            else
            {
                foreach (var obj in outputs)
                {
                    var signedAt = FormatDatePtBr(obj.TryGetValue("signed_at", out var sa) ? sa?.ToString() ?? "" : "");
                    var dateFooter = FormatDatePtBr(obj.TryGetValue("date_footer", out var df) ? df?.ToString() ?? "" : "");
                    Console.WriteLine($"Página {obj["page"]}");
                    Console.WriteLine($"Assinante: {obj["signer"]}");
                    Console.WriteLine($"AssinadoEm: {signedAt}");
                    Console.WriteLine($"DataRodape: {dateFooter}");
                    Console.WriteLine("RodapeAssinaturaRaw:");
                    Console.WriteLine(obj["footer_signature_raw"] ?? "");
                    Console.WriteLine(new string('-', 80));
                }
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli footer --input arquivo.pdf [--page N] [--all] [--tail-lines N] [--json]");
            Console.WriteLine("Padrão: última página. Use --all para imprimir todas.");
        }

        private bool IsGeneric(string text)
        {
            var t = (text ?? "").Trim();
            return GenericOrigins.Any(g => string.Equals(g, t, StringComparison.OrdinalIgnoreCase));
        }

        private string ExtractFooterSignatureRaw(string lastPageText, int tailLines)
        {
            if (string.IsNullOrWhiteSpace(lastPageText)) return "";
            var lines = lastPageText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) return "";

            int tail = tailLines > 0 ? tailLines : 12;
            var sigRegex = new Regex(@"(assinado|assinatura|assinado\s+eletronicamente\s+por:|documento\s+\d+\s+p[aá]gina\s+\d+\s+assinado|verificador|crc|c[oó]digo)", RegexOptions.IgnoreCase);

            int lastSigIdx = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (sigRegex.IsMatch(lines[i]))
                {
                    lastSigIdx = i;
                    break;
                }
            }

            if (lastSigIdx >= 0)
            {
                int start = lastSigIdx;
                int end = Math.Min(lines.Count, lastSigIdx + 2); // linha de assinatura + linha seguinte
                return string.Join("\n", lines.Skip(start).Take(end - start));
            }

            int fallbackStart = Math.Max(0, lines.Count - tail);
            return string.Join("\n", lines.Skip(fallbackStart));
        }

        private string ExtractSignerFromSignatureLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return "";
            var matches = Regex.Matches(line, @"[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇçãõâêîôûäëïöüàèìòùÿ'`\-]+(?:\s+(?:de|da|do|dos|das|e|d')\s+)?[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇçãõâêîôûäëïöüàèìòùÿ'`\-]+(?:\s+[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇçãõâêîôûäëïöüàèìòùÿ'`\-]+){0,4}");
            if (matches.Count == 0) return "";
            var candidate = matches[matches.Count - 1].Value.Trim();
            if (candidate.Length < 6) return "";
            if (!Regex.IsMatch(candidate, @"[A-ZÁÉÍÓÚÂÊÔÃÕÇ]")) return "";
            if (IsGenericSignerLabel(candidate)) return "";
            if (IsGeneric(candidate)) return "";
            if (candidate.Any(char.IsDigit)) return "";
            return candidate;
        }

        private bool IsGenericSignerLabel(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = TextUtils.NormalizeWhitespace(text).ToLowerInvariant();
            if (t == "assinatura" || t == "assinado" || t == "documento") return true;
            if (t.StartsWith("assinatura ") || t.StartsWith("assinado ") || t.StartsWith("documento "))
                return true;
            return false;
        }

        private string ExtractSigner(string lastPageText, string footer, string footerSignatureRaw, List<DigitalSignature> signatures)
        {
            string signatureBlock = footerSignatureRaw ?? "";
            string[] sources =
            {
                $"{lastPageText}\n{footer}\n{signatureBlock}",
                ReverseText($"{lastPageText}\n{footer}\n{signatureBlock}")
            };

            foreach (var source in sources)
            {
                var docPageSigned = Regex.Match(source, @"documento\s+\d+[^\n]{0,120}?assinado[^\n]{0,60}?por\s*[:\-]?\s*([\p{L} .'’\-]+?)(?=\s*(?:,|\(|\bcpf\b|\bem\b|\n|$|-\s*\d))", RegexOptions.IgnoreCase);
                if (docPageSigned.Success) return docPageSigned.Groups[1].Value.Trim();

                var docSigned = Regex.Match(source, @"documento\s+assinado\s+eletronicamente\s+por\s*[:\-]?\s*([\p{L} .'’\-]+?)(?=\s*(?:,|\(|\bcpf\b|\bem\b|\n|$|-\s*\d))", RegexOptions.IgnoreCase);
                if (docSigned.Success) return docSigned.Groups[1].Value.Trim();

                var match = Regex.Match(source, @"assinado(?:\s+digitalmente|\s+eletronicamente)?\s+por\s*[:\-]?\s*([\p{L} .'’\-]+?)(?=\s*(?:,|\(|\bcpf\b|\bem\b|\n|$|-\s*\d))", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();

                var sigMatch = Regex.Match(source, @"Assinatura(?:\s+)?:(.+)", RegexOptions.IgnoreCase);
                if (sigMatch.Success) return sigMatch.Groups[1].Value.Trim();
            }

            if (!string.IsNullOrWhiteSpace(signatureBlock))
            {
                var sigLines = signatureBlock.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                for (int i = 0; i < sigLines.Count; i++)
                {
                    var line = sigLines[i];
                    if (!Regex.IsMatch(line, @"assinad|assinatura", RegexOptions.IgnoreCase)) continue;
                    var val = ExtractSignerFromSignatureLine(line);
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                    if (i + 1 < sigLines.Count)
                    {
                        val = ExtractSignerFromSignatureLine(sigLines[i + 1]);
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                }
            }

            if (signatures != null && signatures.Count > 0)
            {
                var sigName = signatures.Select(s => s.SignerName).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                if (!string.IsNullOrWhiteSpace(sigName)) return sigName.Trim();
                var sigField = signatures.Select(s => s.Name).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                if (!string.IsNullOrWhiteSpace(sigField)) return sigField.Trim();
            }

            return "";
        }

        private string ReverseText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var arr = text.ToCharArray();
            Array.Reverse(arr);
            return new string(arr);
        }

        private string ExtractSignedAt(string lastPagesText, string footer, string footerSignatureRaw)
        {
            string signatureBlock = footerSignatureRaw ?? "";
            var source = $"{lastPagesText}\n{footer}\n{signatureBlock}";
            var preferredSources = new List<string>();
            if (!string.IsNullOrWhiteSpace(signatureBlock)) preferredSources.Add(signatureBlock);
            preferredSources.Add(source);

            foreach (var src in preferredSources)
            {
                if (string.IsNullOrWhiteSpace(src)) continue;
                var windowMatch = Regex.Match(src, @"assinado[\s\S]{0,200}?(\d{1,2}[\/-]\d{1,2}[\/-]\d{2,4})", RegexOptions.IgnoreCase);
                if (windowMatch.Success)
                {
                    var val = NormalizeDate(windowMatch.Groups[1].Value);
                    if (!string.IsNullOrEmpty(val)) return val;
                }
                var emMatch = Regex.Match(src, @"\bem\s+(\d{1,2}[\/-]\d{1,2}[\/-]\d{2,4})(?:\s+\d{1,2}:\d{2})?", RegexOptions.IgnoreCase);
                if (emMatch.Success)
                {
                    var val = NormalizeDate(emMatch.Groups[1].Value);
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }

            var extensoMatch = Regex.Match(source, @"\b(\d{1,2})\s+de\s+(janeiro|fevereiro|març|marco|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)\s+de\s+(\d{4})\b", RegexOptions.IgnoreCase);
            if (extensoMatch.Success)
            {
                var val = NormalizeDateExtenso(extensoMatch.Groups[1].Value, extensoMatch.Groups[2].Value, extensoMatch.Groups[3].Value);
                if (!string.IsNullOrEmpty(val)) return val;
            }

            var match = Regex.Match(source, @"\b(\d{1,2}[\/-]\d{1,2}[\/-]\d{2,4})\b");
            if (match.Success)
            {
                var val = NormalizeDate(match.Groups[1].Value);
                if (!string.IsNullOrEmpty(val)) return val;
            }

            return "";
        }

        private string ExtractDateFromFooter(string lastPagesText, string footer, string footerSignatureRaw)
        {
            var source = $"{footerSignatureRaw}\n{footer}\n{lastPagesText}";
            var dates = new List<DateTime>();

            var signedAt = ExtractSignedAt(lastPagesText, footer, footerSignatureRaw);
            if (!string.IsNullOrWhiteSpace(signedAt) && DateTime.TryParse(signedAt, out var dtSigned))
                dates.Add(dtSigned);

            var extensoMatches = Regex.Matches(source, @"\b(\d{1,2})\s+de\s+(janeiro|fevereiro|març|marco|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)\s+de\s+(\d{4})\b", RegexOptions.IgnoreCase);
            foreach (Match m in extensoMatches)
            {
                var val = NormalizeDateExtenso(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                if (DateTime.TryParse(val, out var dt)) dates.Add(dt);
            }

            var numericMatches = Regex.Matches(source, @"\b(\d{1,2}[\/-]\d{1,2}[\/-]\d{2,4})\b");
            foreach (Match m in numericMatches)
            {
                var val = NormalizeDate(m.Groups[1].Value);
                if (DateTime.TryParse(val, out var dt)) dates.Add(dt);
            }

            if (dates.Count == 0) return "";
            if (!string.IsNullOrWhiteSpace(signedAt) && DateTime.TryParse(signedAt, out var dtSignedFirst))
                return dtSignedFirst.ToString("yyyy-MM-dd");

            var latest = dates.OrderByDescending(d => d).First();
            return latest.ToString("yyyy-MM-dd");
        }

        private string NormalizeDate(string raw)
        {
            string[] formats = { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yy", "d/M/yy", "dd-MM-yy", "d-M-yy" };
            if (DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                int year = dt.Year;
                if (year < 1990 || year > 2100) return "";
                return dt.ToString("yyyy-MM-dd");
            }
            return "";
        }

        private string NormalizeDateExtenso(string day, string month, string year)
        {
            var months = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["janeiro"] = 1,
                ["fevereiro"] = 2,
                ["março"] = 3,
                ["marco"] = 3,
                ["abril"] = 4,
                ["maio"] = 5,
                ["junho"] = 6,
                ["julho"] = 7,
                ["agosto"] = 8,
                ["setembro"] = 9,
                ["outubro"] = 10,
                ["novembro"] = 11,
                ["dezembro"] = 12
            };
            if (!months.TryGetValue(month.Trim().ToLowerInvariant(), out var mm)) return "";
            if (!int.TryParse(day, out var dd)) return "";
            if (!int.TryParse(year, out var yy)) return "";
            try
            {
                var dt = new DateTime(yy, mm, dd);
                return dt.ToString("yyyy-MM-dd");
            }
            catch
            {
                return "";
            }
        }

        private string FormatDatePtBr(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            if (DateTime.TryParse(raw, out var dt))
                return dt.ToString("dd/MM/yyyy", new CultureInfo("pt-BR"));
            return raw;
        }
    }
}
