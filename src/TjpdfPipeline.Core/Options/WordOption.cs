using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FilterPDF.Options
{
    /// <summary>
    /// Centralizes word search logic for all filter commands
    /// Supports operators: & (AND), | (OR), * (wildcard), ? (single char)
    /// Default: case-insensitive + accent-insensitive.
    /// Markers:
    ///   ~word~   => force normalized (compat)
    ///   !word    => EXACT (case/accents) without normalization
    ///   "word" / 'word' => EXACT (case/accents) without normalization (aspas envolvem todo o termo)
    ///   term com espaço (frase) => tratado como exato por padrão (como se estivesse entre aspas)
    /// </summary>
    public static class WordOption
    {
        /// <summary>
        /// Checks if text contains the search pattern
        /// </summary>
        public static bool Matches(string text, string searchPattern)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(searchPattern))
                return false;

            // Handle OR operator (|)
            if (searchPattern.Contains("|"))
            {
                string[] orTerms = searchPattern.Split('|');
                foreach (string term in orTerms)
                {
                    string trimmedTerm = term.Trim();
                    if (string.IsNullOrEmpty(trimmedTerm))
                        continue;

                    if (ProcessSingleTerm(text, trimmedTerm))
                        return true;
                }
                return false;
            }
            // Handle AND operator (&)
            else if (searchPattern.Contains("&"))
            {
                string[] andTerms = searchPattern.Split('&');
                foreach (string term in andTerms)
                {
                    string trimmedTerm = term.Trim();
                    if (string.IsNullOrEmpty(trimmedTerm))
                        continue;

                    if (!ProcessSingleTerm(text, trimmedTerm))
                        return false;
                }
                return true;
            }
            // Single term
            else
            {
                return ProcessSingleTerm(text, searchPattern);
            }
        }

        /// <summary>
        /// Process a single search term (may include wildcards and normalization)
        /// </summary>
        private static bool ProcessSingleTerm(string text, string searchTerm)
        {
            var (mode, cleanTerm) = DetectMode(searchTerm);

            if (mode == MatchMode.Exact)
            {
                // Case/acentos sensível
                if (cleanTerm.Contains("*") || cleanTerm.Contains("?"))
                    return MatchesWildcard(text, cleanTerm, RegexOptions.None);
                return text.Contains(cleanTerm);
            }
            else
            {
                // Normalizado (padrão)
                string normalizedText = NormalizeText(text);
                string normalizedSearchTerm = NormalizeText(cleanTerm);

                if (cleanTerm.Contains("*") || cleanTerm.Contains("?"))
                {
                    return MatchesWildcard(normalizedText, normalizedSearchTerm, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                else
                {
                    return normalizedText.Contains(normalizedSearchTerm);
                }
            }
        }

        /// <summary>
        /// Normalize text by removing diacritics and converting to lowercase
        /// </summary>
        public static string NormalizeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var sb = new StringBuilder();
            foreach (var c in text.Normalize(NormalizationForm.FormD).ToLowerInvariant())
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>
        /// Check if a word should be normalized (indicated by ~word~)
        /// </summary>
        private static (bool shouldNormalize, string word) CheckNormalizationSyntax(string word)
        {
            word = word.Trim();
            if (word.StartsWith("~") && word.EndsWith("~") && word.Length > 2)
            {
                return (true, word.Substring(1, word.Length - 2));
            }
            return (false, word);
        }

        /// <summary>
        /// Check if text matches wildcard pattern
        /// </summary>
        private static bool MatchesWildcard(string text, string pattern, RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
                return false;

            // Convert wildcard pattern to regex
            string regexPattern = WildcardToRegex(pattern);
            
            try
            {
                return Regex.IsMatch(text, regexPattern, options);
            }
            catch
            {
                // If regex fails, fall back to simple contains
                return options.HasFlag(RegexOptions.IgnoreCase)
                    ? text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0
                    : text.Contains(pattern);
            }
        }

        public enum MatchMode { Normalized, Exact }

        public static (MatchMode mode, string term) DetectMode(string raw)
        {
            var (normFlag, term) = CheckNormalizationSyntax(raw);
            if (string.IsNullOrWhiteSpace(term))
                return (MatchMode.Normalized, term);

            // Aspas simples ou duplas envolvendo todo o termo => modo exato
            if ((term.Length >= 2 && term.StartsWith("\"") && term.EndsWith("\"")) ||
                (term.Length >= 2 && term.StartsWith("'") && term.EndsWith("'")))
            {
                return (MatchMode.Exact, term.Substring(1, term.Length - 2));
            }
            if (term.StartsWith("!"))
                return (MatchMode.Exact, term.Substring(1));

            // Se o termo contém espaço, consideramos frase exata (equivalente a ter aspas)
            if (term.Any(char.IsWhiteSpace))
                return (MatchMode.Exact, term);

            if (normFlag)
                return (MatchMode.Normalized, term);
            return (MatchMode.Normalized, term);
        }

        /// <summary>
        /// Convert wildcard pattern to regex
        /// </summary>
        private static string WildcardToRegex(string pattern)
        {
            // Escape special regex characters except * and ?
            pattern = Regex.Escape(pattern);
            // Convert wildcards to regex
            pattern = pattern.Replace("\\*", ".*");
            pattern = pattern.Replace("\\?", ".");
            return pattern;
        }

        /// <summary>
        /// Get a description of the search pattern for display
        /// </summary>
        public static string GetSearchDescription(string searchPattern)
        {
            if (searchPattern.Contains("&"))
            {
                string[] words = searchPattern.Split('&');
                return $"all words: {string.Join(" AND ", words.Select(w => $"'{w.Trim()}'"))}";
            }
            else if (searchPattern.Contains("|"))
            {
                string[] words = searchPattern.Split('|');
                return $"any word: {string.Join(" OR ", words.Select(w => $"'{w.Trim()}'"))}";
            }
            else
            {
                return $"text: '{searchPattern}'";
            }
        }
    }
}
