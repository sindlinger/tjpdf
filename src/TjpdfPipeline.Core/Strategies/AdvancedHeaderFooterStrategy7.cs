using System;
using System.Collections.Generic;
using System.Text;
using iText.Kernel.Geom;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace FilterPDF.Strategies
{
    /// <summary>
    /// iText7 listener to collect header or footer text based on Y position.
    /// </summary>
    public class AdvancedHeaderFooterStrategy7 : IEventListener
    {
        private readonly bool _extractHeader;
        private readonly float _pageHeight;
        private readonly List<TextChunk> _chunks = new();

        public AdvancedHeaderFooterStrategy7(bool extractHeader, float pageHeight)
        {
            _extractHeader = extractHeader;
            _pageHeight = pageHeight;
        }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT) return;
            var renderInfo = (TextRenderInfo)data;
            var text = renderInfo.GetText();
            if (string.IsNullOrWhiteSpace(text)) return;

            LineSegment baseline = renderInfo.GetBaseline();
            Vector start = baseline.GetStartPoint();
            float x = start.Get(Vector.I1);
            float y = start.Get(Vector.I2);

            bool inRegion = _extractHeader
                ? y > (_pageHeight * 0.90f)
                : y < (_pageHeight * 0.10f);

            if (!inRegion) return;

            _chunks.Add(new TextChunk(text, x, y));
        }

        public ICollection<EventType> GetSupportedEvents()
        {
            return new HashSet<EventType> { EventType.RENDER_TEXT };
        }

        public string GetResultantText()
        {
            if (_chunks.Count == 0) return string.Empty;

            _chunks.Sort((a, b) =>
            {
                int yComp = b.Y.CompareTo(a.Y); // top-down
                return yComp != 0 ? yComp : a.X.CompareTo(b.X);
            });

            var sb = new StringBuilder();
            float lastY = float.MaxValue;
            foreach (var chunk in _chunks)
            {
                if (Math.Abs(lastY - chunk.Y) > 5f && sb.Length > 0)
                    sb.AppendLine();

                sb.Append(chunk.Text);
                if (!chunk.Text.EndsWith(" ")) sb.Append(' ');
                lastY = chunk.Y;
            }
            return sb.ToString().Trim();
        }

        private class TextChunk
        {
            public string Text { get; }
            public float X { get; }
            public float Y { get; }
            public TextChunk(string text, float x, float y)
            {
                Text = text;
                X = x;
                Y = y;
            }
        }
    }
}
