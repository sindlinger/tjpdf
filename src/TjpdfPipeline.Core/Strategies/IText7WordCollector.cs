using System;
using System.Collections.Generic;
using System.Linq;
using FilterPDF;
using iText.Kernel.Geom;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace FilterPDF.Strategies
{
    /// <summary>
    /// Coleta palavras com fonte/estilo/bbox usando iText7 (TextRenderInfo por caractere).
    /// Útil para mapear campos preenchidos (tokens) na página.
    /// </summary>
    public class IText7WordCollector : IEventListener
    {
        private readonly List<WordInfo> words = new List<WordInfo>();

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT) return;
            var renderInfo = (TextRenderInfo)data;

            var chars = renderInfo.GetCharacterRenderInfos();
            var buffer = new List<TextRenderInfo>();
            float letterGapHint = GetMedianGap(chars);

            foreach (var ch in chars)
            {
                var t = ch.GetText();
                bool isSpace = string.IsNullOrWhiteSpace(t);

                if (isSpace)
                {
                    FlushBuffer(buffer);
                    buffer.Clear();
                    continue;
                }

                if (buffer.Count > 0 && ShouldBreakWord(buffer[buffer.Count - 1], ch, letterGapHint))
                {
                    FlushBuffer(buffer);
                    buffer.Clear();
                }

                buffer.Add(ch);
            }

            FlushBuffer(buffer);
        }

        private void FlushBuffer(List<TextRenderInfo> buffer)
        {
            if (buffer == null || buffer.Count == 0) return;

            string text = string.Concat(buffer.Select(c => c.GetText()));

            var first = buffer.First();
            var font = first.GetFont();
            string fontName = font?.GetFontProgram()?.GetFontNames()?.GetFontName() ?? "";
            float size = Math.Abs(first.GetAscentLine().GetStartPoint().Get(Vector.I2) -
                                  first.GetDescentLine().GetStartPoint().Get(Vector.I2));

            int renderMode = first.GetTextRenderMode();
            bool underline = renderMode == 3 || renderMode == 2; // stroke proxy
            bool bold = fontName.ToLower().Contains("bold") || fontName.ToLower().Contains("black") ||
                        fontName.ToLower().Contains("heavy");
            bool italic = fontName.ToLower().Contains("italic") || fontName.ToLower().Contains("oblique") ||
                          fontName.ToLower().Contains("slant");

            float charSpacing = 0; // iText7 (7.2.5) não expõe direto
            float wordSpacing = first.GetWordSpacing();
            float horizScaling = first.GetHorizontalScaling();
            float rise = first.GetRise();

            float x0 = buffer.Min(c => c.GetDescentLine().GetStartPoint().Get(Vector.I1));
            float x1 = buffer.Max(c => c.GetAscentLine().GetEndPoint().Get(Vector.I1));
            float y0 = buffer.Min(c => c.GetDescentLine().GetStartPoint().Get(Vector.I2));
            float y1 = buffer.Max(c => c.GetAscentLine().GetEndPoint().Get(Vector.I2));

            words.Add(new WordInfo
            {
                Text = text,
                Font = fontName,
                Size = size,
                Bold = bold,
                Italic = italic,
                Underline = underline,
                RenderMode = renderMode,
                CharSpacing = charSpacing,
                WordSpacing = wordSpacing,
                HorizontalScaling = horizScaling,
                Rise = rise,
                X0 = x0,
                Y0 = y0,
                X1 = x1,
                Y1 = y1
            });
        }

        private bool ShouldBreakWord(TextRenderInfo prev, TextRenderInfo curr, float letterGapHint)
        {
            float gap = GetGap(prev, curr);
            if (gap <= 0) return false;
            float threshold = letterGapHint > 0
                ? (IsSingleToken(prev) && IsSingleToken(curr) ? letterGapHint * 2.2f : letterGapHint * 1.15f)
                : GetSpaceThreshold(prev);
            return gap > threshold;
        }

        private float GetGap(TextRenderInfo prev, TextRenderInfo curr)
        {
            float prevEnd = prev.GetAscentLine().GetEndPoint().Get(Vector.I1);
            float currStart = curr.GetBaseline().GetStartPoint().Get(Vector.I1);
            return currStart - prevEnd;
        }

        private float GetMedianGap(IList<TextRenderInfo> chars)
        {
            if (chars == null || chars.Count < 2) return 0;
            var gaps = new List<float>();
            for (int i = 1; i < chars.Count; i++)
            {
                var a = chars[i - 1];
                var b = chars[i];
                var ta = a.GetText();
                var tb = b.GetText();
                if (string.IsNullOrWhiteSpace(ta) || string.IsNullOrWhiteSpace(tb)) continue;
                float gap = GetGap(a, b);
                if (gap > 0) gaps.Add(gap);
            }
            if (gaps.Count == 0) return 0;
            gaps.Sort();
            int mid = gaps.Count / 2;
            return gaps.Count % 2 == 0 ? (gaps[mid - 1] + gaps[mid]) / 2f : gaps[mid];
        }

        private float GetSpaceThreshold(TextRenderInfo info)
        {
            float fontSize = GetFontSize(info);
            float space = 0;
            try { space = info.GetSingleSpaceWidth(); } catch { space = 0; }
            if (space <= 0) space = fontSize * 0.3f;
            return Math.Max(space * 0.9f, fontSize * 0.35f);
        }

        private float GetFontSize(TextRenderInfo info)
        {
            return Math.Abs(info.GetAscentLine().GetStartPoint().Get(Vector.I2) -
                            info.GetDescentLine().GetStartPoint().Get(Vector.I2));
        }

        private bool IsSingleToken(TextRenderInfo info)
        {
            if (info == null) return false;
            var t = info.GetText() ?? "";
            t = t.Trim();
            if (t.Length != 1) return false;
            return !char.IsWhiteSpace(t[0]);
        }

        public ICollection<EventType> GetSupportedEvents()
        {
            return new EventType[] { EventType.RENDER_TEXT };
        }

        public List<WordInfo> GetWords()
        {
            // Ordenar por Y (top->bottom) e depois X
            return words
                .OrderByDescending(w => w.Y0)
                .ThenBy(w => w.X0)
                .ToList();
        }
    }
}
