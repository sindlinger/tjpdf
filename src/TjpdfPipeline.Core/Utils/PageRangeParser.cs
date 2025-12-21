using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FilterPDF
{
    /// <summary>
    /// Parser de page ranges similar ao qpdf
    /// Suporta:
    /// - Páginas individuais: 1,3,5
    /// - Intervalos: 1-10, 10-1 (reverso)
    /// - Última página: z ou r1
    /// - Contagem reversa: r2 (penúltima), r3 (antepenúltima)
    /// - Exclusões: 1-10,x3-4 (1 a 10 exceto 3 e 4)
    /// - Filtros: 1-10:even, 1-10:odd
    /// </summary>
    public static class PageRangeParser
    {
        public static List<int> Parse(string rangeSpec, int totalPages)
        {
            if (string.IsNullOrWhiteSpace(rangeSpec))
                return new List<int>();
                
            var result = new List<int>();
            var excludePages = new HashSet<int>();
            
            // Dividir por vírgulas
            var parts = rangeSpec.Split(',');
            
            foreach (var part in parts)
            {
                var trimmedPart = part.Trim();
                if (string.IsNullOrEmpty(trimmedPart))
                    continue;
                    
                // Verificar se é exclusão (começa com x)
                bool isExclusion = trimmedPart.StartsWith("x");
                if (isExclusion)
                    trimmedPart = trimmedPart.Substring(1);
                
                // Verificar se tem filtro :odd ou :even
                string? filter = null;
                if (trimmedPart.Contains(":"))
                {
                    var filterParts = trimmedPart.Split(':');
                    trimmedPart = filterParts[0];
                    filter = filterParts[1].ToLower();
                }
                
                // Processar o range
                var pages = ProcessRange(trimmedPart, totalPages);
                
                // Aplicar filtro se houver
                if (filter != null)
                {
                    pages = ApplyFilter(pages, filter);
                }
                
                // Adicionar ou excluir páginas
                if (isExclusion)
                {
                    foreach (var page in pages)
                        excludePages.Add(page);
                }
                else
                {
                    result.AddRange(pages);
                }
            }
            
            // Remover páginas excluídas
            if (excludePages.Count > 0)
            {
                result = result.Where(p => !excludePages.Contains(p)).ToList();
            }
            
            return result;
        }
        
        private static List<int> ProcessRange(string range, int totalPages)
        {
            var result = new List<int>();
            
            // Substituir z pela última página
            range = range.Replace("z", totalPages.ToString());
            
            // Verificar se é um intervalo (contém -)
            if (range.Contains("-"))
            {
                var rangeParts = range.Split('-');
                if (rangeParts.Length == 2)
                {
                    int start = ParsePageNumber(rangeParts[0], totalPages);
                    int end = ParsePageNumber(rangeParts[1], totalPages);
                    
                    if (start <= end)
                    {
                        // Intervalo crescente
                        for (int i = start; i <= end; i++)
                        {
                            if (i >= 1 && i <= totalPages)
                                result.Add(i);
                        }
                    }
                    else
                    {
                        // Intervalo decrescente
                        for (int i = start; i >= end; i--)
                        {
                            if (i >= 1 && i <= totalPages)
                                result.Add(i);
                        }
                    }
                }
            }
            else
            {
                // Página única
                int page = ParsePageNumber(range, totalPages);
                if (page >= 1 && page <= totalPages)
                    result.Add(page);
            }
            
            return result;
        }
        
        private static int ParsePageNumber(string pageSpec, int totalPages)
        {
            pageSpec = pageSpec.Trim();
            
            // Verificar se é contagem reversa (r1, r2, etc.)
            if (pageSpec.StartsWith("r"))
            {
                string numberPart = pageSpec.Substring(1);
                if (int.TryParse(numberPart, out int reverseIndex))
                {
                    return totalPages - reverseIndex + 1;
                }
            }
            
            // Tentar parse normal
            if (int.TryParse(pageSpec, out int pageNumber))
            {
                return pageNumber;
            }
            
            return 0; // Página inválida
        }
        
        private static List<int> ApplyFilter(List<int> pages, string filter)
        {
            var result = new List<int>();
            
            if (filter == "odd")
            {
                // Pegar páginas em posições ímpares (1ª, 3ª, 5ª, etc.)
                for (int i = 0; i < pages.Count; i++)
                {
                    if ((i + 1) % 2 == 1) // Posição ímpar (1-based)
                        result.Add(pages[i]);
                }
            }
            else if (filter == "even")
            {
                // Pegar páginas em posições pares (2ª, 4ª, 6ª, etc.)
                for (int i = 0; i < pages.Count; i++)
                {
                    if ((i + 1) % 2 == 0) // Posição par (1-based)
                        result.Add(pages[i]);
                }
            }
            else
            {
                // Filtro desconhecido, retornar todas
                return pages;
            }
            
            return result;
        }
        
        /// <summary>
        /// Valida se uma especificação de page range é válida
        /// </summary>
        public static bool IsValid(string rangeSpec, int totalPages, out string? error)
        {
            error = null;
            
            try
            {
                var pages = Parse(rangeSpec, totalPages);
                
                if (pages.Count == 0)
                {
                    error = "No valid pages in range";
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        
        /// <summary>
        /// Retorna uma descrição amigável do range
        /// </summary>
        public static string Describe(string rangeSpec, int totalPages)
        {
            try
            {
                var pages = Parse(rangeSpec, totalPages);
                
                if (pages.Count == 0)
                    return "No pages";
                else if (pages.Count == 1)
                    return $"Page {pages[0]}";
                else if (pages.Count <= 10)
                    return $"Pages {string.Join(", ", pages)}";
                else
                    return $"{pages.Count} pages: {string.Join(", ", pages.Take(5))}... and {pages.Count - 5} more";
            }
            catch
            {
                return "Invalid range";
            }
        }
    }
}