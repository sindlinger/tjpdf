using System;
using System.Collections.Concurrent;
using System.IO;
using Newtonsoft.Json;

namespace FilterPDF
{
    /// <summary>
    /// Gerencia cache em memória de arquivos JSON deserializados para evitar recarregamento
    /// </summary>
    public static class CacheMemoryManager
    {
        private static readonly ConcurrentDictionary<string, CachedAnalysis> _memoryCache = new();
        private static readonly object _lockObj = new object();
        
        private class CachedAnalysis
        {
            public PDFAnalysisResult Analysis { get; set; } = new PDFAnalysisResult();
            public DateTime LoadTime { get; set; }
            public string FilePath { get; set; } = string.Empty;
            public long FileSize { get; set; }
        }
        
        /// <summary>
        /// Carrega um arquivo de cache, usando memória se já foi carregado
        /// </summary>
        public static PDFAnalysisResult? LoadCacheFile(string cacheFilePath)
        {
            var cacheKey = cacheFilePath;
            
            // Verificar se já está em memória
            if (_memoryCache.TryGetValue(cacheKey, out var cached))
            {
                Console.Error.WriteLine($"[DEBUG] Using MEMORY CACHE for {Path.GetFileName(cacheFilePath)} (loaded at {cached.LoadTime:HH:mm:ss})");
                return cached.Analysis;
            }
            
            // Carregar do disco ou do SQLite
            lock (_lockObj)
            {
                // Double-check após lock
                if (_memoryCache.TryGetValue(cacheKey, out cached))
                {
                    return cached.Analysis;
                }

                ResolveDbPath(cacheFilePath, out var dbPath, out var cacheName);
                if (string.IsNullOrEmpty(cacheName)) return null;

                var analysis = LoadFromSqlite(dbPath, cacheName);
                if (analysis != null)
                {
                    _memoryCache[cacheKey] = new CachedAnalysis
                    {
                        Analysis = analysis,
                        LoadTime = DateTime.Now,
                        FilePath = cacheFilePath,
                        FileSize = 0
                    };
                }
                return analysis;
            }
        }

        private static PDFAnalysisResult? LoadFromSqlite(string dbPath, string cacheName)
        {
            if (string.IsNullOrEmpty(dbPath) || string.IsNullOrEmpty(cacheName)) return null;
            try
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id FROM caches WHERE name=@n LIMIT 1";
                cmd.Parameters.AddWithValue("@n", cacheName);
                var idObj = cmd.ExecuteScalar();
                if (idObj == null) return null;
                long cacheId = (idObj is long l) ? l : Convert.ToInt64(idObj);

                var pages = new List<PageAnalysis>();
                using var pc = conn.CreateCommand();
                pc.CommandText = "SELECT page_number, text FROM pages WHERE cache_id=@cid ORDER BY page_number";
                pc.Parameters.AddWithValue("@cid", cacheId);
                using var r = pc.ExecuteReader();
                while (r.Read())
                {
                    int num = r.IsDBNull(0) ? 0 : r.GetInt32(0);
                    string text = r.IsDBNull(1) ? "" : r.GetString(1);
                    pages.Add(new PageAnalysis
                    {
                        PageNumber = num,
                        TextInfo = new TextInfo { PageText = text }
                    });
                }

                return new PDFAnalysisResult
                {
                    DocumentInfo = new DocumentInfo { TotalPages = pages.Count },
                    Pages = pages
                };
            }
            catch
            {
                return null;
            }
        }

        private static void ResolveDbPath(string cacheFilePath, out string dbPath, out string cacheName)
        {
            dbPath = FilterPDF.Utils.SqliteCacheStore.DefaultDbPath;
            cacheName = Path.GetFileNameWithoutExtension(cacheFilePath);

            if (cacheFilePath.StartsWith("db://", StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = cacheFilePath.Substring("db://".Length);
                var parts = trimmed.Split('#');
                if (parts.Length == 2)
                {
                    dbPath = parts[0];
                    cacheName = parts[1];
                }
            }
            else if (cacheFilePath.Contains("#"))
            {
                var parts = cacheFilePath.Split('#');
                if (parts.Length == 2)
                {
                    dbPath = parts[0];
                    cacheName = parts[1];
                }
            }
        }
        
        /// <summary>
        /// Limpa o cache em memória
        /// </summary>
        public static void ClearCache()
        {
            _memoryCache.Clear();
            Console.Error.WriteLine("[DEBUG] Memory cache cleared");
        }
        
        /// <summary>
        /// Obtém estatísticas do cache
        /// </summary>
        public static (int FileCount, long TotalSize) GetCacheStats()
        {
            int count = _memoryCache.Count;
            long totalSize = 0;
            
            foreach (var item in _memoryCache.Values)
            {
                totalSize += item.FileSize;
            }
            
            return (count, totalSize);
        }
    }
}
