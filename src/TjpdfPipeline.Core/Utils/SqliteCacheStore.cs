using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FilterPDF.Utils
{
    /// <summary>
    /// Helper util to persist and read cache data in SQLite.
    /// Single source of truth for cache (replaces JSON files/index).
    /// </summary>
    public static class SqliteCacheStore
    {
        public static readonly string DefaultDbPath = Path.Combine("data", "sqlite", "sqlite-mcp.db");

        public static void EnsureDatabase(string dbPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".");
            using var conn = new SqliteConnection($"Data Source={dbPath};");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;

                CREATE TABLE IF NOT EXISTS caches (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT UNIQUE,
                    source TEXT,
                    created_at TEXT,
                    mode TEXT,
                    size_bytes INTEGER,
                    process_number TEXT
                );

                CREATE TABLE IF NOT EXISTS processes (
                    id TEXT PRIMARY KEY,
                    sei TEXT,
                    origem TEXT,
                    created_at TEXT
                );

                CREATE TABLE IF NOT EXISTS documents (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    process_id TEXT REFERENCES processes(id) ON DELETE CASCADE,
                    name TEXT,
                    doc_type TEXT,
                    start_page INT,
                    end_page INT
                );

                CREATE TABLE IF NOT EXISTS pages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    cache_id INTEGER NOT NULL REFERENCES caches(id) ON DELETE CASCADE,
                    document_id INTEGER REFERENCES documents(id) ON DELETE CASCADE,
                    page_number INTEGER,
                    text TEXT,
                    word_count INTEGER GENERATED ALWAYS AS (
                        (length(text) - length(replace(text, ' ', '')) + 1)
                    ) VIRTUAL,
                    header TEXT,
                    footer TEXT,
                    type TEXT,
                    has_money INTEGER DEFAULT 0,
                    has_cpf INTEGER DEFAULT 0,
                    fonts TEXT,
                    UNIQUE(cache_id, page_number)
                );

                CREATE VIRTUAL TABLE IF NOT EXISTS page_fts USING fts5(
                    cache_id UNINDEXED,
                    page_number UNINDEXED,
                    text,
                    tokenize='unicode61'
                );

                CREATE TRIGGER IF NOT EXISTS pages_ai AFTER INSERT ON pages
                BEGIN
                    INSERT INTO page_fts(rowid, cache_id, page_number, text)
                    VALUES (new.id, new.cache_id, new.page_number, new.text);
                END;

                CREATE TRIGGER IF NOT EXISTS pages_ad AFTER DELETE ON pages
                BEGIN
                    DELETE FROM page_fts WHERE rowid = old.id;
                END;

                CREATE TRIGGER IF NOT EXISTS pages_au AFTER UPDATE OF text ON pages
                BEGIN
                    UPDATE page_fts SET text = new.text WHERE rowid = new.id;
                END;

                CREATE INDEX IF NOT EXISTS idx_pages_cache_page ON pages(cache_id, page_number);
                CREATE INDEX IF NOT EXISTS idx_documents_process ON documents(process_id);
            ";
            cmd.ExecuteNonQuery();

            // Migração leve: garantir colunas novas sem quebrar bases existentes
            void TryAlter(string sql)
            {
                try
                {
                    using var c = conn.CreateCommand();
                    c.CommandText = sql;
                    c.ExecuteNonQuery();
                }
                catch
                {
                    // ignora se já existe
                }
            }
            TryAlter("ALTER TABLE pages ADD COLUMN has_money INTEGER DEFAULT 0;");
            TryAlter("ALTER TABLE pages ADD COLUMN has_cpf INTEGER DEFAULT 0;");
            TryAlter("ALTER TABLE pages ADD COLUMN fonts TEXT;");
            TryAlter("ALTER TABLE pages ADD COLUMN image_count INTEGER DEFAULT 0;");
            TryAlter("ALTER TABLE pages ADD COLUMN annotation_count INTEGER DEFAULT 0;");
            TryAlter("ALTER TABLE caches ADD COLUMN process_number TEXT;");
            TryAlter("ALTER TABLE caches ADD COLUMN json TEXT;");
            TryAlter("ALTER TABLE caches ADD COLUMN meta_title TEXT;");
            TryAlter("ALTER TABLE caches ADD COLUMN meta_author TEXT;");
            TryAlter("ALTER TABLE caches ADD COLUMN meta_subject TEXT;");
            TryAlter("ALTER TABLE caches ADD COLUMN meta_keywords TEXT;");
            TryAlter("ALTER TABLE caches ADD COLUMN meta_creator TEXT;");
            TryAlter("ALTER TABLE caches ADD COLUMN meta_producer TEXT;");
            TryAlter("ALTER TABLE caches ADD COLUMN meta_creation_date TEXT;");
            TryAlter("ALTER TABLE caches ADD COLUMN meta_mod_date TEXT;");
            TryAlter("ALTER TABLE caches ADD COLUMN meta_pdf_version TEXT;");
            TryAlter("ALTER TABLE caches ADD COLUMN meta_is_tagged INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN doc_total_pages INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN stat_total_images INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN stat_total_fonts INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN stat_bookmarks INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN res_attachments INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN res_embedded_files INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN res_javascript INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN res_multimedia INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN sec_is_encrypted INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN sec_encryption_type INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN sec_can_print INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN sec_can_modify INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN sec_can_copy INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN sec_can_annotate INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN sec_can_fill_forms INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN sec_can_extract INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN sec_can_assemble INTEGER;");
            TryAlter("ALTER TABLE caches ADD COLUMN sec_can_print_hq INTEGER;");
        }

        public static bool CacheExists(string dbPath, string cacheName)
        {
            EnsureDatabase(dbPath);
            using var conn = new SqliteConnection($"Data Source={dbPath};");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM caches WHERE name=@n LIMIT 1";
            cmd.Parameters.AddWithValue("@n", cacheName);
            using var reader = cmd.ExecuteReader();
            return reader.Read();
        }

        public static void UpsertCache(string dbPath, string cacheName, string sourcePath, PDFAnalysisResult analysis, string mode)
        {
            EnsureDatabase(dbPath);
            using var conn = new SqliteConnection($"Data Source={dbPath};");
            conn.Open();
            using var tx = conn.BeginTransaction();

            var inferredProcess = InferProcessFromName(cacheName);
            var metaJson = SerializeMetadata(analysis);
            var meta = FlattenMeta(analysis);
            long cacheId = InsertOrUpdateCache(conn, cacheName, sourcePath, mode, analysis?.FileSize ?? 0, inferredProcess, metaJson, meta);

            // Clear old pages for idempotency
            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM pages WHERE cache_id=@cid";
                del.Parameters.AddWithValue("@cid", cacheId);
                del.ExecuteNonQuery();
            }

            if (analysis?.Pages != null)
            {
                foreach (var page in analysis.Pages)
                {
                    var text = page?.TextInfo?.PageText ?? "";
                    var header = string.Join("\n", text.Split('\n').Take(5));
                    var footer = string.Join("\n", text.Split('\n').Reverse().Take(5).Reverse());
                    var fonts = page?.TextInfo?.Fonts != null ? string.Join("|", page.TextInfo.Fonts.Select(f => f.Name)) : "";
                    var imgCount = page?.Resources?.Images?.Count ?? 0;
                    var annCount = page?.Annotations?.Count ?? 0;
                    var hasMoney = HasMoney(text) ? 1 : 0;
                    var hasCpf = HasCpf(text) ? 1 : 0;
                    using var ins = conn.CreateCommand();
                    ins.CommandText = @"INSERT INTO pages(cache_id,page_number,text,header,footer,has_money,has_cpf,fonts,image_count,annotation_count)
                                        VALUES (@c,@p,@t,@h,@f,@m,@cpf,@fonts,@img,@ann)";
                    ins.Parameters.AddWithValue("@c", cacheId);
                    ins.Parameters.AddWithValue("@p", page?.PageNumber ?? 0);
                    ins.Parameters.AddWithValue("@t", text);
                    ins.Parameters.AddWithValue("@h", header);
                    ins.Parameters.AddWithValue("@f", footer);
                    ins.Parameters.AddWithValue("@m", hasMoney);
                    ins.Parameters.AddWithValue("@cpf", hasCpf);
                    ins.Parameters.AddWithValue("@fonts", fonts);
                    ins.Parameters.AddWithValue("@img", imgCount);
                    ins.Parameters.AddWithValue("@ann", annCount);
                    ins.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }

        public static string? GetCacheJson(string dbPath, string cacheName)
        {
            return null; // JSON legacy removido
        }

        public static List<string> ListCacheNames(string dbPath)
        {
            var list = new List<string>();
            if (!File.Exists(dbPath)) return list;
            using var conn = new SqliteConnection($"Data Source={dbPath};");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM caches ORDER BY name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }
            return list;
        }

        private static long InsertOrUpdateCache(SqliteConnection conn, string cacheName, string sourcePath, string mode, long sizeBytes, string? processNumber, string? metaJson, CacheMeta meta)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO caches(name, source, created_at, mode, size_bytes, json, process_number,
                                meta_title, meta_author, meta_subject, meta_keywords, meta_creator, meta_producer,
                                meta_creation_date, meta_mod_date, meta_pdf_version, meta_is_tagged,
                                doc_total_pages, stat_total_images, stat_total_fonts, stat_bookmarks,
                                res_attachments, res_embedded_files, res_javascript, res_multimedia,
                                sec_is_encrypted, sec_encryption_type, sec_can_print, sec_can_modify, sec_can_copy,
                                sec_can_annotate, sec_can_fill_forms, sec_can_extract, sec_can_assemble, sec_can_print_hq)
                                VALUES (@n,@s,@c,@m,@sz,@j,@p,
                                @mtit,@maut,@msub,@mkey,@mcre,@mprod,@mcd,@mmd,@mpdf,@mtag,
                                @dtp,@sti,@stf,@stb,@ra,@ref,@rjs,@rmm,
                                @sei,@set,@scp,@scm,@scc,@sca,@scf,@sce,@sca2,@scphq)
                                ON CONFLICT(name) DO UPDATE SET
                                  source=excluded.source,
                                  created_at=excluded.created_at,
                                  mode=excluded.mode,
                                  size_bytes=excluded.size_bytes,
                                  json=excluded.json,
                                  process_number=COALESCE(excluded.process_number, caches.process_number),
                                  meta_title=excluded.meta_title,
                                  meta_author=excluded.meta_author,
                                  meta_subject=excluded.meta_subject,
                                  meta_keywords=excluded.meta_keywords,
                                  meta_creator=excluded.meta_creator,
                                  meta_producer=excluded.meta_producer,
                                  meta_creation_date=excluded.meta_creation_date,
                                  meta_mod_date=excluded.meta_mod_date,
                                  meta_pdf_version=excluded.meta_pdf_version,
                                  meta_is_tagged=excluded.meta_is_tagged,
                                  doc_total_pages=excluded.doc_total_pages,
                                  stat_total_images=excluded.stat_total_images,
                                  stat_total_fonts=excluded.stat_total_fonts,
                                  stat_bookmarks=excluded.stat_bookmarks,
                                  res_attachments=excluded.res_attachments,
                                  res_embedded_files=excluded.res_embedded_files,
                                  res_javascript=excluded.res_javascript,
                                  res_multimedia=excluded.res_multimedia,
                                  sec_is_encrypted=excluded.sec_is_encrypted,
                                  sec_encryption_type=excluded.sec_encryption_type,
                                  sec_can_print=excluded.sec_can_print,
                                  sec_can_modify=excluded.sec_can_modify,
                                  sec_can_copy=excluded.sec_can_copy,
                                  sec_can_annotate=excluded.sec_can_annotate,
                                  sec_can_fill_forms=excluded.sec_can_fill_forms,
                                  sec_can_extract=excluded.sec_can_extract,
                                  sec_can_assemble=excluded.sec_can_assemble,
                                  sec_can_print_hq=excluded.sec_can_print_hq
                                ;
                                SELECT id FROM caches WHERE name=@n LIMIT 1;";
            cmd.Parameters.AddWithValue("@n", cacheName);
            cmd.Parameters.AddWithValue("@s", sourcePath);
            cmd.Parameters.AddWithValue("@c", DateTime.UtcNow.ToString("s"));
            cmd.Parameters.AddWithValue("@m", mode);
            cmd.Parameters.AddWithValue("@sz", sizeBytes);
            cmd.Parameters.AddWithValue("@j", (object?)metaJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p", (object?)processNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mtit", (object?)meta.MetaTitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@maut", (object?)meta.MetaAuthor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@msub", (object?)meta.MetaSubject ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mkey", (object?)meta.MetaKeywords ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mcre", (object?)meta.MetaCreator ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mprod", (object?)meta.MetaProducer ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mcd", (object?)meta.MetaCreationDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mmd", (object?)meta.MetaModDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mpdf", (object?)meta.MetaPdfVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mtag", meta.MetaIsTagged.HasValue ? meta.MetaIsTagged.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@dtp", meta.DocTotalPages.HasValue ? meta.DocTotalPages.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@sti", meta.StatTotalImages.HasValue ? meta.StatTotalImages.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@stf", meta.StatTotalFonts.HasValue ? meta.StatTotalFonts.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@stb", meta.StatBookmarks.HasValue ? meta.StatBookmarks.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@ra", meta.ResAttachments.HasValue ? meta.ResAttachments.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@ref", meta.ResEmbeddedFiles.HasValue ? meta.ResEmbeddedFiles.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@rjs", meta.ResJavascript.HasValue ? meta.ResJavascript.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@rmm", meta.ResMultimedia.HasValue ? meta.ResMultimedia.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@sei", meta.SecIsEncrypted.HasValue ? meta.SecIsEncrypted.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@set", meta.SecEncryptionType.HasValue ? meta.SecEncryptionType.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@scp", meta.SecCanPrint.HasValue ? meta.SecCanPrint.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@scm", meta.SecCanModify.HasValue ? meta.SecCanModify.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@scc", meta.SecCanCopy.HasValue ? meta.SecCanCopy.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@sca", meta.SecCanAnnotate.HasValue ? meta.SecCanAnnotate.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@scf", meta.SecCanFillForms.HasValue ? meta.SecCanFillForms.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@sce", meta.SecCanExtract.HasValue ? meta.SecCanExtract.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@sca2", meta.SecCanAssemble.HasValue ? meta.SecCanAssemble.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@scphq", meta.SecCanPrintHq.HasValue ? meta.SecCanPrintHq.Value : (object)DBNull.Value);
            var result = cmd.ExecuteScalar();
            return (result is long l) ? l : Convert.ToInt64(result);
        }

        private static string? SerializeMetadata(PDFAnalysisResult analysis)
        {
            if (analysis == null) return null;
            try
            {
                var meta = new
                {
                    analysis.Metadata,
                    analysis.XMPMetadata,
                    analysis.DocumentInfo,
                    analysis.Resources,
                    analysis.Statistics,
                    analysis.Accessibility,
                    analysis.Security,
                    analysis.Bookmarks,
                    analysis.PDFACompliance
                };
                return JsonConvert.SerializeObject(meta);
            }
            catch
            {
                return null;
            }
        }

        private static CacheMeta FlattenMeta(PDFAnalysisResult analysis)
        {
            var m = new CacheMeta();
            var meta = analysis?.Metadata;
            var stats = analysis?.Statistics;
            var res = analysis?.Resources;
            var doc = analysis?.DocumentInfo;
            var sec = analysis?.SecurityInfo ?? analysis?.Security;
            var bookmarks = analysis?.Bookmarks;

            m.MetaTitle = meta?.Title;
            m.MetaAuthor = meta?.Author;
            m.MetaSubject = meta?.Subject;
            m.MetaKeywords = meta?.Keywords;
            m.MetaCreator = meta?.Creator;
            m.MetaProducer = meta?.Producer;
            m.MetaCreationDate = meta?.CreationDate?.ToString("o");
            m.MetaModDate = meta?.ModificationDate?.ToString("o");
            m.MetaPdfVersion = meta?.PDFVersion;
            m.MetaIsTagged = meta?.IsTagged == true ? 1 : 0;

            m.DocTotalPages = doc?.TotalPages;

            m.StatTotalImages = stats?.TotalImages;
            m.StatTotalFonts = stats?.UniqueFonts;
            m.StatBookmarks = bookmarks?.TotalCount;

            m.ResAttachments = res?.AttachmentCount;
            m.ResEmbeddedFiles = res?.EmbeddedFiles?.Count;
            m.ResJavascript = res?.JavaScriptCount;
            m.ResMultimedia = res?.HasMultimedia == true ? 1 : 0;

            m.SecIsEncrypted = sec?.IsEncrypted == true ? 1 : 0;
            m.SecEncryptionType = sec?.EncryptionType;
            m.SecCanPrint = sec?.CanPrint == true ? 1 : 0;
            m.SecCanModify = sec?.CanModify == true ? 1 : 0;
            m.SecCanCopy = sec?.CanCopy == true ? 1 : 0;
            m.SecCanAnnotate = sec?.CanAnnotate == true ? 1 : 0;
            m.SecCanFillForms = sec?.CanFillForms == true ? 1 : 0;
            m.SecCanExtract = sec?.CanExtractContent == true ? 1 : 0;
            m.SecCanAssemble = sec?.CanAssemble == true ? 1 : 0;
            m.SecCanPrintHq = sec?.CanPrintHighQuality == true ? 1 : 0;

            return m;
        }

        private class CacheMeta
        {
            public string? MetaTitle { get; set; }
            public string? MetaAuthor { get; set; }
            public string? MetaSubject { get; set; }
            public string? MetaKeywords { get; set; }
            public string? MetaCreator { get; set; }
            public string? MetaProducer { get; set; }
            public string? MetaCreationDate { get; set; }
            public string? MetaModDate { get; set; }
            public string? MetaPdfVersion { get; set; }
            public int? MetaIsTagged { get; set; }

            public int? DocTotalPages { get; set; }

            public int? StatTotalImages { get; set; }
            public int? StatTotalFonts { get; set; }
            public int? StatBookmarks { get; set; }

            public int? ResAttachments { get; set; }
            public int? ResEmbeddedFiles { get; set; }
            public int? ResJavascript { get; set; }
            public int? ResMultimedia { get; set; }

            public int? SecIsEncrypted { get; set; }
            public int? SecEncryptionType { get; set; }
            public int? SecCanPrint { get; set; }
            public int? SecCanModify { get; set; }
            public int? SecCanCopy { get; set; }
            public int? SecCanAnnotate { get; set; }
            public int? SecCanFillForms { get; set; }
            public int? SecCanExtract { get; set; }
            public int? SecCanAssemble { get; set; }
            public int? SecCanPrintHq { get; set; }
        }

        private static int ComputeWordCount(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            return text.Split(new[] { ' ', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static string? InferProcessFromName(string cacheName)
        {
            if (string.IsNullOrEmpty(cacheName)) return null;
            // Padrão SEI: 000121-44.2024.8.15.
            var m = System.Text.RegularExpressions.Regex.Match(cacheName, @"\d{6}-\d{2}\.\d{4}\.\d\.\d{2}");
            if (m.Success) return m.Value;
            return null;
        }

        private static IEnumerable<(int pageNumber, string text)> ExtractPages(string jsonString)
        {
            var list = new List<(int, string)>();
            if (string.IsNullOrWhiteSpace(jsonString)) return list;
            try
            {
                var root = JToken.Parse(jsonString);
                var pages = root["Pages"] ?? root["pages"];
                if (pages == null || pages.Type != JTokenType.Array) return list;
                int idx = 0;
                foreach (var p in pages)
                {
                    idx++;
                    int pageNumber = p["PageNumber"]?.Value<int?>() ?? idx;
                    string text =
                        p["TextInfo"]?["PageText"]?.Value<string>() ??
                        p["pageText"]?.Value<string>() ??
                        p["text"]?.Value<string>() ?? "";
                    list.Add((pageNumber, text));
                }
            }
            catch
            {
                // swallow; return what we have
            }
            return list;
        }

        private static bool HasMoney(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(text, @"R\$ ?[0-9]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static bool HasCpf(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(text, @"\b\\d{3}\\.\\d{3}\\.\\d{3}-\\d{2}\\b|\b\\d{11}\\b");
        }
    }
}
