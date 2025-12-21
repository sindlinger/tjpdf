using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FilterPDF.Utils
{
    public class LaudoHashDbEntry
    {
        public string Hash { get; set; } = "";
        public string Especie { get; set; } = "";
        public string Natureza { get; set; } = "";
        public string Autor { get; set; } = "";
        public string Arquivo { get; set; } = "";
        public string Quesitos { get; set; } = "";
    }

    /// <summary>
    /// Banco simples de hashes de laudos (CSV) para lookup em tempo de execução.
    /// Espera colunas: hash_sha1, especie, natureza, autor, arquivo, quesitos
    /// </summary>
    public class LaudoHashDb
    {
        private readonly Dictionary<string, LaudoHashDbEntry> _byHash = new(StringComparer.OrdinalIgnoreCase);

        public static LaudoHashDb? LoadCsv(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var db = new LaudoHashDb();
            try
            {
                using var reader = new StreamReader(path, Encoding.UTF8, true);
                string? line;
                bool headerSkipped = false;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var cols = SplitCsvLine(line);
                    if (cols.Count == 0) continue;
                    if (!headerSkipped)
                    {
                        headerSkipped = true;
                        if (cols[0].Trim().Equals("hash_sha1", StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    var entry = new LaudoHashDbEntry
                    {
                        Hash = cols.Count > 0 ? cols[0].Trim() : "",
                        Especie = cols.Count > 1 ? cols[1].Trim() : "",
                        Natureza = cols.Count > 2 ? cols[2].Trim() : "",
                        Autor = cols.Count > 3 ? cols[3].Trim() : "",
                        Arquivo = cols.Count > 4 ? cols[4].Trim() : "",
                        Quesitos = cols.Count > 5 ? cols[5].Trim() : ""
                    };
                    if (string.IsNullOrWhiteSpace(entry.Hash)) continue;
                    db._byHash[entry.Hash] = entry;
                }
                return db;
            }
            catch
            {
                return null;
            }
        }

        public bool TryGet(string hash, out LaudoHashDbEntry? entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(hash)) return false;
            if (_byHash.TryGetValue(hash, out var e))
            {
                entry = e;
                return true;
            }
            return false;
        }

        private static List<string> SplitCsvLine(string line)
        {
            var result = new List<string>();
            if (line == null) return result;
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                    continue;
                }
                if (c == ',' && !inQuotes)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    continue;
                }
                sb.Append(c);
            }
            result.Add(sb.ToString());
            return result;
        }
    }
}
