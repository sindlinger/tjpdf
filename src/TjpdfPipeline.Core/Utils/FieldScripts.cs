using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FilterPDF.Utils
{
    public class FieldScript
    {
        public string Name { get; set; } = "";
        public List<string> Fields { get; set; } = new List<string>();
        public double Priority { get; set; } = 1.0;
        public List<FieldScriptSource> Sources { get; set; } = new List<FieldScriptSource>();
        public List<FieldScriptPattern> Patterns { get; set; } = new List<FieldScriptPattern>();
        public List<string> Clean { get; set; } = new List<string>();
        public List<string> Validate { get; set; } = new List<string>();
        public List<string> Fallback { get; set; } = new List<string>();
    }

    public class FieldScriptSource
    {
        public string Bucket { get; set; } = "";
        public List<string> NameMatches { get; set; } = new List<string>();
    }

    public class FieldScriptPattern
    {
        public string Type { get; set; } = "regex";
        public string Label { get; set; } = "";
        public string Pattern { get; set; } = "";
        public string Field { get; set; } = "";
        public double Weight { get; set; } = 1.0;
        public string? Value { get; set; }
        public FieldScriptCompose? Compose { get; set; }
    }

    public class FieldScriptCompose
    {
        public string? SecondField { get; set; }
    }

    public static class FieldScripts
    {
        public static List<FieldScript> LoadScripts(string path)
        {
            var scripts = new List<FieldScript>();
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return scripts;

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            foreach (var file in Directory.GetFiles(path, "*.yml"))
            {
                try
                {
                    using var reader = new StreamReader(file);
                    var script = deserializer.Deserialize<FieldScript>(reader) ?? new FieldScript();
                    script.Name = Path.GetFileName(file);
                    scripts.Add(script);
                }
                catch
                {
                    // ignore malformed script
                }
            }

            return scripts;
        }

        public static List<Dictionary<string, object>> RunScripts(List<FieldScript> scripts, string pdfName, string fullText, int startPage, string bucket)
        {
            return RunScripts(scripts, pdfName, fullText, new List<Dictionary<string, object>>(), startPage, bucket);
        }

        public static List<Dictionary<string, object>> RunScripts(List<FieldScript> scripts, string pdfName, string fullText, List<Dictionary<string, object>> words, int startPage, string bucket)
        {
            var results = new List<Dictionary<string, object>>();
            if (scripts == null || scripts.Count == 0) return results;
            var text = fullText ?? string.Empty;
            var textLower = text.ToLowerInvariant();

            foreach (var script in scripts)
            {
                if (!ShouldRun(script, pdfName, bucket)) continue;
                foreach (var pat in script.Patterns)
                {
                    if (string.Equals(pat.Type, "regex", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(pat.Pattern)) continue;
                        var rx = new Regex(pat.Pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        var matches = rx.Matches(text);
                        foreach (Match m in matches)
                        {
                            if (!m.Success) continue;
                            var value = ExtractValue(pat, m);
                            if (string.IsNullOrWhiteSpace(value)) continue;
                            var fieldName = NormalizeFieldName(pat.Field);
                            AddResult(results, fieldName, value, pat, m, words, startPage);
                            if (pat.Compose != null && !string.IsNullOrWhiteSpace(pat.Compose.SecondField) && m.Groups.Count >= 3)
                            {
                                var v2 = m.Groups[2].Value?.Trim();
                                if (!string.IsNullOrWhiteSpace(v2))
                                {
                                    var field2 = NormalizeFieldName(pat.Compose.SecondField!);
                                    AddResult(results, field2, v2, pat, m, words, startPage);
                                }
                            }
                        }
                    }
                    else if (string.Equals(pat.Type, "keyword", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(pat.Pattern)) continue;
                        if (!textLower.Contains(pat.Pattern.ToLowerInvariant())) continue;
                        var value = !string.IsNullOrWhiteSpace(pat.Value) ? pat.Value! : pat.Pattern;
                        var fieldName = NormalizeFieldName(pat.Field);
                        AddResult(results, fieldName, value, pat, null, words, startPage);
                    }
                }
            }

            return results;
        }

        private static bool ShouldRun(FieldScript script, string pdfName, string bucket)
        {
            if (script.Sources == null || script.Sources.Count == 0) return true;
            foreach (var src in script.Sources)
            {
                if (!string.IsNullOrWhiteSpace(src.Bucket) &&
                    !string.Equals(src.Bucket, bucket, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (src.NameMatches == null || src.NameMatches.Count == 0)
                    return true;
                foreach (var pattern in src.NameMatches)
                {
                    if (WildcardMatch(pdfName, pattern)) return true;
                }
            }
            return false;
        }

        private static bool WildcardMatch(string input, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(input ?? "", rx, RegexOptions.IgnoreCase);
        }

        private static string ExtractValue(FieldScriptPattern pat, Match m)
        {
            if (!string.IsNullOrWhiteSpace(pat.Value))
                return pat.Value!.Trim();
            if (m.Groups.Count > 1)
                return m.Groups[1].Value.Trim();
            return m.Value.Trim();
        }

        private static void AddResult(List<Dictionary<string, object>> results, string fieldName, string value, FieldScriptPattern pat, Match? match,
            List<Dictionary<string, object>> words, int startPage)
        {
            if (string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrWhiteSpace(value)) return;
            var (page, bbox) = FindBBoxForValue(words, value, startPage);
            var item = new Dictionary<string, object>
            {
                ["name"] = fieldName,
                ["value"] = value.Trim(),
                ["pattern"] = pat.Label,
                ["weight"] = pat.Weight,
                ["page"] = page,
                ["method"] = string.Equals(pat.Type, "keyword", StringComparison.OrdinalIgnoreCase) ? "keyword_script" : "regex_script"
            };
            item["bbox"] = bbox ?? new Dictionary<string, double>();
            results.Add(item);
        }

        private static (int page, Dictionary<string, double>? bbox) FindBBoxForValue(List<Dictionary<string, object>> words, string value, int startPage)
        {
            if (words == null || words.Count == 0 || string.IsNullOrWhiteSpace(value))
                return (startPage, null);
            var tokens = Tokenize(value);
            if (tokens.Count == 0) return (startPage, null);
            var wordTokens = words.Select(w => NormalizeToken(w.TryGetValue("text", out var tv) ? tv?.ToString() ?? "" : "")).ToList();

            for (int i = 0; i < words.Count; i++)
            {
                if (!string.Equals(wordTokens[i], tokens[0], StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryGetPage(words[i], out var page)) page = startPage;

                int k = 1;
                for (; k < tokens.Count; k++)
                {
                    if (i + k >= words.Count) break;
                    if (!TryGetPage(words[i + k], out var pcur) || pcur != page) break;
                    if (!string.Equals(wordTokens[i + k], tokens[k], StringComparison.OrdinalIgnoreCase)) break;
                }
                if (k == tokens.Count)
                {
                    var slice = words.Skip(i).Take(k).ToList();
                    var bbox = ComputeBBox(slice);
                    return (page, bbox);
                }
            }
            return (startPage, null);
        }

        private static List<string> Tokenize(string value)
        {
            return value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(NormalizeToken)
                        .Where(t => t.Length > 0)
                        .ToList();
        }

        private static string NormalizeToken(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            var cleaned = Regex.Replace(input, @"[^\p{L}\p{N}]+", "");
            if (cleaned.Length == 0) return "";
            var normalized = cleaned.Normalize(NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }

        private static bool TryGetPage(Dictionary<string, object> w, out int page)
        {
            page = 0;
            if (!w.TryGetValue("page", out var pv)) return false;
            return int.TryParse(pv?.ToString(), out page);
        }

        private static Dictionary<string, double>? ComputeBBox(List<Dictionary<string, object>> words)
        {
            if (words == null || words.Count == 0) return null;
            var nx0s = new List<double>();
            var ny0s = new List<double>();
            var nx1s = new List<double>();
            var ny1s = new List<double>();
            foreach (var w in words)
            {
                if (TryGetDouble(w, "nx0", out var nx0)) nx0s.Add(nx0);
                if (TryGetDouble(w, "ny0", out var ny0)) ny0s.Add(ny0);
                if (TryGetDouble(w, "nx1", out var nx1)) nx1s.Add(nx1);
                if (TryGetDouble(w, "ny1", out var ny1)) ny1s.Add(ny1);
            }
            if (nx0s.Count == 0 || ny0s.Count == 0 || nx1s.Count == 0 || ny1s.Count == 0) return null;
            return new Dictionary<string, double>
            {
                ["nx0"] = nx0s.Min(),
                ["ny0"] = ny0s.Min(),
                ["nx1"] = nx1s.Max(),
                ["ny1"] = ny1s.Max()
            };
        }

        private static bool TryGetDouble(Dictionary<string, object> w, string key, out double val)
        {
            val = 0;
            if (!w.TryGetValue(key, out var v)) return false;
            return double.TryParse(v?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out val);
        }

        private static string NormalizeFieldName(string field)
        {
            if (string.IsNullOrWhiteSpace(field)) return field ?? "";
            var f = field.Trim();
            var upper = f.ToUpperInvariant();
            return upper switch
            {
                "PROCESSO JUDICIAL" => "PROCESSO_JUDICIAL",
                "PROCESSO ADMINISTRATIVO" => "PROCESSO_ADMINISTRATIVO",
                "PROMOVENTE" => "PROMOVENTE",
                "PROMOVIDO" => "PROMOVIDO",
                "CPF/CNPJ" => "CPF_PERITO",
                "PROFISSÃO" => "ESPECIALIDADE",
                "ESPÉCIE DE PERÍCIA" => "ESPECIE_DA_PERICIA",
                "ESPECIE DE PERICIA" => "ESPECIE_DA_PERICIA",
                "JUÍZO" => "VARA",
                "JUIZO" => "VARA",
                "COMARCA" => "COMARCA",
                "VALOR ARBITRADO - JZ" => "VALOR_ARBITRADO_JZ",
                "VALOR ARBITRADO - DE" => "VALOR_ARBITRADO_DE",
                "VALOR ARBITRADO - CM" => "VALOR_ARBITRADO_CM",
                "DATA DA AUTORIZACAO DA DESPESA" => "DATA",
                "DATA DA AUTORIZAÇÃO DA DESPESA" => "DATA",
                "ADIANTAMENTO" => "ADIANTAMENTO",
                "VALOR TABELADO ANEXO I - TABELA I" => "VALOR_TABELADO_ANEXO_I",
                _ => f.Replace(" ", "_")
            };
        }
    }
}
