using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using iText.Kernel.Pdf;

namespace FilterPDF.Utils
{
    /// <summary>
    /// Centraliza abertura e reuso de PdfDocument (iText 7) com cache simples por caminho.
    /// Mantém interface semelhante à PdfAccessManager legado, mas para a API nova.
    /// </summary>
    public static class PdfAccessManager7
    {
        private static readonly Dictionary<string, PdfDocument> _openDocs = new Dictionary<string, PdfDocument>();
        private static readonly Dictionary<string, PdfReader> _openReaders = new Dictionary<string, PdfReader>();
        private static readonly object _lock = new object();

        /// <summary>
        /// Obtém ou cria um PdfDocument para o arquivo informado.
        /// O caller NÃO deve fechar o documento retornado; use CloseDocument para soltar do cache.
        /// </summary>
        public static PdfDocument GetDocument(string pdfPath)
        {
            if (string.IsNullOrEmpty(pdfPath))
                throw new ArgumentNullException(nameof(pdfPath));

            string fullPath = pdfPath;

            lock (_lock)
            {
                if (_openDocs.TryGetValue(fullPath, out var existingDoc) && !existingDoc.IsClosed())
                {
                    return existingDoc;
                }

                // Abrir leitor e documento novos
                var reader = CreateReaderWithRecovery(fullPath);
                var doc = new PdfDocument(reader);
                _openReaders[fullPath] = reader;
                _openDocs[fullPath] = doc;
                return doc;
            }
        }

        /// <summary>
        /// Cria um PdfDocument não cacheado; caller é responsável por fechar.
        /// </summary>
        public static PdfDocument CreateTemporaryDocument(string pdfPath)
        {
            if (string.IsNullOrEmpty(pdfPath))
                throw new ArgumentNullException(nameof(pdfPath));

            var reader = CreateReaderWithRecovery(pdfPath);
            return new PdfDocument(reader);
        }

        private static PdfReader CreateReaderWithRecovery(string pdfPath)
        {
            try
            {
                var props = BuildReaderProperties();
                return new PdfReader(pdfPath, props);
            }
            catch
            {
                var reader = new PdfReader(pdfPath);
                reader.SetUnethicalReading(true);
                return reader;
            }
        }

        private static ReaderProperties BuildReaderProperties()
        {
            var props = new ReaderProperties();
            TrySetBool(props, "SetUnethicalReading", true);
            TrySetBool(props, "SetRecoverMode", true);
            TrySetBool(props, "SetRecoveryMode", true);
            TrySetBool(props, "SetRepairMode", true);
            TrySetStrictnessLevel(props);
            return props;
        }

        private static void TrySetBool(object target, string methodName, bool value)
        {
            try
            {
                var method = target.GetType().GetMethod(methodName, new[] { typeof(bool) });
                method?.Invoke(target, new object[] { value });
            }
            catch
            {
                // ignore
            }
        }

        private static void TrySetStrictnessLevel(object target)
        {
            try
            {
                var method = target.GetType().GetMethod("SetStrictnessLevel");
                if (method == null)
                    return;
                var parameters = method.GetParameters();
                if (parameters.Length != 1)
                    return;
                var paramType = parameters[0].ParameterType;
                object? value = null;
                if (paramType.IsEnum)
                {
                    var names = Enum.GetNames(paramType);
                    var pick = names.FirstOrDefault(n => n.Contains("LENIENT", StringComparison.OrdinalIgnoreCase) ||
                                                         n.Contains("LAX", StringComparison.OrdinalIgnoreCase) ||
                                                         n.Contains("RELAX", StringComparison.OrdinalIgnoreCase))
                               ?? names.FirstOrDefault(n => n.Contains("DEFAULT", StringComparison.OrdinalIgnoreCase))
                               ?? names.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(pick))
                        value = Enum.Parse(paramType, pick);
                }
                else if (paramType == typeof(int))
                {
                    value = 0;
                }
                else if (paramType == typeof(string))
                {
                    value = "LENIENT";
                }

                if (value != null)
                    method.Invoke(target, new[] { value });
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// Fecha e remove um documento específico do cache.
        /// </summary>
        public static void CloseDocument(string pdfPath)
        {
            if (string.IsNullOrEmpty(pdfPath))
                return;

            string fullPath = pdfPath;
            lock (_lock)
            {
                if (_openDocs.TryGetValue(fullPath, out var doc))
                {
                    if (!doc.IsClosed())
                        doc.Close();
                    _openDocs.Remove(fullPath);
                }
                if (_openReaders.TryGetValue(fullPath, out var reader))
                {
                    reader.Close();
                    _openReaders.Remove(fullPath);
                }
            }
        }

        /// <summary>
        /// Fecha todos os documentos abertos no cache.
        /// </summary>
        public static void CloseAll()
        {
            lock (_lock)
            {
                foreach (var doc in _openDocs.Values)
                {
                    if (!doc.IsClosed())
                        doc.Close();
                }
                foreach (var reader in _openReaders.Values)
                {
                    reader.Close();
                }
                _openDocs.Clear();
                _openReaders.Clear();
            }
        }

        /// <summary>
        /// Obtém contagem de páginas sem manter documento aberto.
        /// </summary>
        public static int GetPageCount(string pdfPath)
        {
            using (var doc = CreateTemporaryDocument(pdfPath))
            {
                return doc.GetNumberOfPages();
            }
        }

        /// <summary>
        /// Verifica se o PDF está criptografado (usa iText7 reader).
        /// </summary>
        public static bool IsEncrypted(string pdfPath)
        {
            try
            {
                using (var doc = CreateTemporaryDocument(pdfPath))
                {
                    return doc.GetReader()?.IsEncrypted() ?? false;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Retorna info básica sem cache persistente.
        /// </summary>
        public static (int PageCount, bool IsEncrypted, long FileSize) GetBasicInfo(string pdfPath)
        {
            var fileInfo = new FileInfo(pdfPath);
            using (var doc = CreateTemporaryDocument(pdfPath))
            {
                return (doc.GetNumberOfPages(), doc.GetReader()?.IsEncrypted() ?? false, fileInfo.Length);
            }
        }
    }
}
