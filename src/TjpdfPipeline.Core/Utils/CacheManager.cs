using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using FilterPDF.Security;
using FilterPDF.Utils;
using Npgsql;

namespace FilterPDF
{
    /// <summary>
    /// Gerenciador de cache para PDFs carregados
    /// Permite carregar, listar e buscar PDFs por nome
    /// </summary>
    public static class CacheManager
    {
        private static readonly string PgUri = PgDocStore.DefaultPgUri;
        
        // Lock para prevenir race conditions durante operações concorrentes
        private static readonly object indexLock = new object();
        
        /// <summary>
        /// Informações sobre um PDF em cache
        /// </summary>
        public class CacheEntry
        {
            public string OriginalFileName { get; set; } = "";
            public string OriginalPath { get; set; } = "";
            public string CacheFileName { get; set; } = "";
            public string CachePath { get; set; } = "";
            public DateTime CachedDate { get; set; }
            public long OriginalSize { get; set; }
            public long CacheSize { get; set; }
            public string ExtractionMode { get; set; } = "";
            public string Version { get; set; } = "";
        }
        
        /// <summary>
        /// Índice de todos os arquivos em cache
        /// </summary>
        public class CacheIndex
        {
            public Dictionary<string, CacheEntry> Entries { get; set; } = new Dictionary<string, CacheEntry>();
            public DateTime LastUpdated { get; set; } = DateTime.Now;
            public string Version { get; set; } = "2.12.0";
        }
        
        static CacheManager() { }
        
        /// <summary>
        /// Garante que o diretório de cache existe
        /// </summary>
        private static void EnsureDb()
        {
            using var conn = new NpgsqlConnection(PgAnalysisLoader.GetPgUri(PgUri));
            conn.Open();
        }

        public class MetaStats
        {
            public int MetaTitle { get; set; }
            public int MetaAuthor { get; set; }
            public int MetaSubject { get; set; }
            public int MetaKeywords { get; set; }
            public int MetaCreationDate { get; set; }
            public int StatTotalImages { get; set; }
            public int StatTotalFonts { get; set; }
            public int StatBookmarks { get; set; }
            public int ResAttachments { get; set; }
            public int ResEmbeddedFiles { get; set; }
            public int ResJavascript { get; set; }
            public int ResMultimedia { get; set; }
            public int SecIsEncrypted { get; set; }
            public long SumImages { get; set; }
            public long SumBookmarks { get; set; }
            public long SumFonts { get; set; }
            public long SumPages { get; set; }
        }

        public class BookmarkSummaryItem
        {
            public string Title { get; set; } = "";
            public int Count { get; set; }
            public List<string> Samples { get; set; } = new List<string>();
        }

        public class TopValueItem
        {
            public string Value { get; set; } = "";
            public int Count { get; set; }
            public List<string> Samples { get; set; } = new List<string>();
        }

        public static MetaStats GetMetaStats()
        {
            EnsureDb();
            var m = new MetaStats();
            var rows = PgAnalysisLoader.ListProcesses(PgUri);

            // Pré-carrega contagem de bookmarks
            var bookmarkCounts = new Dictionary<long, int>();
            using (var conn = new NpgsqlConnection(PgAnalysisLoader.GetPgUri(PgUri)))
            {
                conn.Open();
                using var cmd = new NpgsqlCommand("SELECT process_id, COUNT(*) FROM bookmarks GROUP BY process_id", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var pid = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                    var cnt = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                    bookmarkCounts[pid] = cnt;
                }
            }

            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(row.MetaTitle)) m.MetaTitle++;
                if (!string.IsNullOrWhiteSpace(row.MetaAuthor)) m.MetaAuthor++;
                if (!string.IsNullOrWhiteSpace(row.MetaSubject)) m.MetaSubject++;
                if (!string.IsNullOrWhiteSpace(row.MetaKeywords)) m.MetaKeywords++;
                // CreationDate não está em ProcessRow; ignoramos

                var imgs = row.TotalImages;
                var fonts = row.TotalFonts;
                var bkm = bookmarkCounts.TryGetValue(row.Id, out var b) ? b : 0;

                m.StatTotalImages += imgs > 0 ? 1 : 0;
                m.StatTotalFonts += fonts > 0 ? 1 : 0;
                m.StatBookmarks += bkm > 0 ? 1 : 0;
                m.SumImages += imgs;
                m.SumFonts += fonts;
                m.SumBookmarks += bkm;
                m.SumPages += row.TotalPages;

                if (row.HasAttachments) m.ResAttachments++;
                if (row.HasEmbedded) m.ResEmbeddedFiles++;
                if (row.HasJs) m.ResJavascript++;
                if (row.HasMultimedia) m.ResMultimedia++;
                if (row.IsEncrypted) m.SecIsEncrypted++;
            }
            return m;
        }

        public static List<BookmarkSummaryItem> GetBookmarkSummary(int top, int sampleSize)
        {
            var topItems = GetTopValues("bookmark", top, sampleSize, null, null);
            var list = new List<BookmarkSummaryItem>();
            foreach (var item in topItems)
            {
                list.Add(new BookmarkSummaryItem
                {
                    Title = item.Value,
                    Count = item.Count,
                    Samples = item.Samples
                });
            }
            return list;
        }

        public static List<TopValueItem> GetTopValues(string field, int top, int sampleSize, int? lastCaches = null, DateTime? since = null)
        {
            EnsureDb();
            var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            void AddValue(string? rawValue, string sampleName)
            {
                if (string.IsNullOrWhiteSpace(rawValue)) return;
                var value = rawValue.Trim();
                if (value.Length == 0) return;
                if (!freq.TryGetValue(value, out var count))
                {
                    freq[value] = 1;
                    samples[value] = new List<string> { sampleName };
                }
                else
                {
                    freq[value] = count + 1;
                    if (samples[value].Count < sampleSize && !samples[value].Contains(sampleName))
                        samples[value].Add(sampleName);
                }
            }

            var rows = PgAnalysisLoader.ListProcesses(PgUri);
            if (since.HasValue) rows = rows.Where(r => r.CreatedAt > since.Value).ToList();
            if (lastCaches.HasValue) rows = rows.Take(lastCaches.Value).ToList();

            foreach (var row in rows)
            {
                switch (field?.ToLowerInvariant())
                {
                    case "bookmark":
                        // Sem árvore normalizada, depende do JSON; ignoramos bookmarks para manter PG-only
                        break;
                    case "meta_title":
                        AddValue(row.MetaTitle, row.ProcessNumber);
                        break;
                    case "meta_author":
                        AddValue(row.MetaAuthor, row.ProcessNumber);
                        break;
                    case "meta_subject":
                        AddValue(row.MetaSubject, row.ProcessNumber);
                        break;
                    case "meta_keywords":
                        AddValue(row.MetaKeywords, row.ProcessNumber);
                        break;
                    case "doc_type":
                        // Sem doc_type agregado; ignorar
                        break;
                    case "mode":
                        AddValue("pg", row.ProcessNumber);
                        break;
                    default:
                        throw new ArgumentException($"Campo inválido para top: {field}");
                }
            }

            var result = freq
                .Select(kv => new TopValueItem
                {
                    Value = kv.Key,
                    Count = kv.Value,
                    Samples = samples.TryGetValue(kv.Key, out var s) ? s : new List<string>()
                })
                .OrderByDescending(i => i.Count)
                .ThenBy(i => i.Value, StringComparer.OrdinalIgnoreCase)
                .Take(top)
                .ToList();

            return result;
        }
        
        /// <summary>
        /// Adiciona um PDF ao cache com validação de segurança
        /// </summary>
        public static void AddToCache(string originalPdfPath, string cacheFilePath, string extractionMode = "both")
        {
            // Compat: escrita real é feita pelo SqliteCacheStore.UpsertCache; aqui só garantimos schema
            EnsureDb();
        }
        
        /// <summary>
        /// Busca um PDF no cache por nome (sem extensão) ou índice
        /// </summary>
        public static string? FindCacheFile(string pdfNameOrIndex)
        {
            EnsureDb();
            var rows = PgAnalysisLoader.ListProcesses(PgUri);
            if (int.TryParse(pdfNameOrIndex, out int idx) && idx > 0 && idx <= rows.Count)
            {
                return $"pg://{PgUri}#{rows[idx - 1].ProcessNumber}";
            }
            var match = rows.FirstOrDefault(r => string.Equals(r.ProcessNumber, pdfNameOrIndex, StringComparison.OrdinalIgnoreCase));
            return match != null ? $"pg://{PgUri}#{match.ProcessNumber}" : null;
        }
        
        /// <summary>
        /// Lista todos os PDFs em cache
        /// </summary>
        public static List<CacheEntry> ListCachedPDFs()
        {
            EnsureDb();
            var list = new List<CacheEntry>();
            var rows = PgAnalysisLoader.ListProcesses(PgUri);
            int idx = 1;
            foreach (var row in rows)
            {
                list.Add(new CacheEntry
                {
                    OriginalFileName = string.IsNullOrWhiteSpace(row.Source) ? $"process_{row.ProcessNumber}.pdf" : Path.GetFileName(row.Source),
                    OriginalPath = row.Source,
                    CacheFileName = row.ProcessNumber,
                    CachePath = $"pg://{PgUri}#{row.ProcessNumber}",
                    CachedDate = row.CreatedAt,
                    OriginalSize = 0,
                    CacheSize = 0,
                    ExtractionMode = "pg",
                    Version = "pg"
                });
                idx++;
            }
            return list;
        }
        
        /// <summary>
        /// Remove um PDF do cache
        /// </summary>
        public static bool RemoveFromCache(string pdfName)
        {
            EnsureDb();
            pdfName = Path.GetFileNameWithoutExtension(pdfName);
            using var conn = new NpgsqlConnection(PgAnalysisLoader.GetPgUri(PgUri));
            conn.Open();
            using var cmd = new NpgsqlCommand("DELETE FROM processes WHERE process_number=@n", conn);
            cmd.Parameters.AddWithValue("@n", pdfName);
            var rows = cmd.ExecuteNonQuery();
            return rows > 0;
        }
        
        /// <summary>
        /// Limpa todo o cache
        /// </summary>
        public static void ClearCache()
        {
            EnsureDb();
            using var conn = new NpgsqlConnection(PgAnalysisLoader.GetPgUri(PgUri));
            conn.Open();
            using var cmd = new NpgsqlCommand("DELETE FROM processes", conn);
            cmd.ExecuteNonQuery();
        }
        
        /// <summary>
        /// Verifica se um PDF está em cache
        /// </summary>
        public static bool IsInCache(string pdfName)
        {
            return FindCacheFile(pdfName) != null;
        }
        
        /// <summary>
        /// Obtém informações sobre um PDF por índice ou nome
        /// </summary>
        public static CacheEntry? GetCacheEntry(string pdfNameOrIndex)
        {
            var entries = ListCachedPDFs();
            if (int.TryParse(pdfNameOrIndex, out int cacheIndex) && cacheIndex > 0)
            {
                if (cacheIndex <= entries.Count)
                    return entries[cacheIndex - 1];
                return null;
            }

            var pdfName = Path.GetFileNameWithoutExtension(pdfNameOrIndex);
            return entries.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e.OriginalFileName)
                                                .Equals(pdfName, StringComparison.OrdinalIgnoreCase));
        }
        
        /// <summary>
        /// Obtém estatísticas do cache
        /// </summary>
        public static CacheStats GetCacheStats()
        {
            EnsureDb();
            var rows = PgAnalysisLoader.ListProcesses(PgUri);
            long totalPages = rows.Sum(r => (long)r.TotalPages);
            return new CacheStats
            {
                TotalEntries = rows.Count,
                TotalCacheSize = 0,
                TotalOriginalSize = 0,
                CacheDirectory = PgUri,
                LastUpdated = DateTime.UtcNow,
                TotalPages = totalPages
            };
        }
        
        /// <summary>
        /// Estatísticas do cache
        /// </summary>
        public class CacheStats
        {
            public int TotalEntries { get; set; }
            public long TotalCacheSize { get; set; }
            public long TotalOriginalSize { get; set; }
            public string CacheDirectory { get; set; } = "";
            public DateTime LastUpdated { get; set; }
            public long TotalPages { get; set; }
        }
        
        /// <summary>
        /// Reconstrói o índice a partir dos arquivos de cache existentes
        /// 
        /// Este método é útil quando:
        /// - O processamento paralelo com múltiplos workers causou race conditions
        /// - O arquivo index.json foi corrompido ou deletado
        /// - Existem arquivos de cache órfãos não listados no índice
        /// 
        /// Limitações dos dados reconstruídos:
        /// - ExtractionMode será marcado como "unknown"
        /// - OriginalSize será 0 (tamanho original não é armazenado no cache)
        /// - OriginalPath será apenas o nome do arquivo
        /// - CachedDate será a data de criação do arquivo de cache
        /// </summary>
        /// <returns>Número de entradas reconstruídas no índice</returns>
        public static int RebuildIndexFromFiles()
        {
            Console.WriteLine("Rebuild não é necessário: cache agora é armazenado em SQLite.");
            return 0;
        }
        
        /// <summary>
        /// Normaliza caminhos para o formato correto do sistema operacional atual
        /// Se Windows: formato Windows (C:\path)
        /// Se Linux/WSL: formato Unix (/path)
        /// </summary>
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
                
            // Se é caminho absoluto, normalizar para o sistema atual
            if (Path.IsPathRooted(path))
            {
                // Em Linux/WSL, converter para formato Unix
                if (Environment.OSVersion.Platform == PlatformID.Unix || 
                    Environment.OSVersion.Platform == PlatformID.MacOSX)
                {
                    return path.Replace('\\', '/');
                }
                // No Windows, manter formato Windows
                return path;
            }
            
            // Para caminhos relativos, usar Path.GetFullPath mas normalizar resultado
            string fullPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
            
            // Em Linux/WSL, normalizar para formato Unix
            if (Environment.OSVersion.Platform == PlatformID.Unix || 
                Environment.OSVersion.Platform == PlatformID.MacOSX)
            {
                return fullPath.Replace('\\', '/');
            }
            
            // No Windows, retornar como está
            return fullPath;
        }
    }
}
