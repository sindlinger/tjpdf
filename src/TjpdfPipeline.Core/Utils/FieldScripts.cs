using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            var page = FindPageForValue(words, value, startPage);
            results.Add(new Dictionary<string, object>
            {
                ["name"] = fieldName,
                ["value"] = value.Trim(),
                ["pattern"] = pat.Label,
                ["weight"] = pat.Weight,
                ["page"] = page,
                ["method"] = string.Equals(pat.Type, "keyword", StringComparison.OrdinalIgnoreCase) ? "keyword_script" : "regex_script"
            });
        }

        private static int FindPageForValue(List<Dictionary<string, object>> words, string value, int startPage)
        {
            if (words == null || words.Count == 0 || string.IsNullOrWhiteSpace(value))
                return startPage;
            var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                              .Select(t => t.Trim().ToLowerInvariant())
                              .ToList();
            if (tokens.Count == 0) return startPage;
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                var wt = w.TryGetValue("text", out var tv) ? tv?.ToString() ?? "" : "";
                if (!string.Equals(wt, tokens[0], StringComparison.OrdinalIgnoreCase)) continue;
                bool ok = true;
                for (int k = 1; k < tokens.Count; k++)
                {
                    if (i + k >= words.Count) { ok = false; break; }
                    var wk = words[i + k];
                    var wkt = wk.TryGetValue("text", out var tvk) ? tvk?.ToString() ?? "" : "";
                    if (!string.Equals(wkt, tokens[k], StringComparison.OrdinalIgnoreCase)) { ok = false; break; }
                }
                if (ok && words[i].TryGetValue("page", out var pv) && int.TryParse(pv?.ToString(), out var p))
                    return p;
            }
            return startPage;
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
