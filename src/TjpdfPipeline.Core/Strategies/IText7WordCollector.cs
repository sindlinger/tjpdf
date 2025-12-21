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
