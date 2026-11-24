using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CaptionCopy
{
    public sealed class CaptionSentence
    {
        public string Text { get; private set; }
        public bool IsDraft { get; private set; }
        public CaptionSentence(string text, bool isDraft) { Text = text; IsDraft = isDraft; }
    }

    // The most recent UIA snapshot maps to a range at the end of _segment.
    // Only that range can be revised; text that has scrolled out remains intact.
    public sealed class TranscriptEngine
    {
        private readonly List<CaptionSentence> _frozen = new List<CaptionSentence>();
        private readonly SortedSet<int> _pauses = new SortedSet<int>();
        private string _segment = "";
        private string _previous = "";
        private int _snapshotStart;
        private int _cutoff;
        private DateTime _changedAt;
        private bool _settled;
        public static readonly TimeSpan PauseDelay = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan ShortPauseDelay = TimeSpan.FromMilliseconds(700);

        public bool Observe(string snapshot, DateTime now)
        {
            string next = Normalize(snapshot);
            // A blank UIA value is often a transition, not an instruction to erase text.
            if (next.Length == 0 || next == _previous) return false;
            if (_previous.Length == 0)
            {
                _snapshotStart = _segment.Length;
                _segment += next;
            }
            else
            {
                // A short display pause must not end the recognition-revision window.
                int offset = FindAlignment(_previous, next, now - _changedAt < PauseDelay);
                if (offset >= 0)
                {
                    _snapshotStart += offset;
                    ReplaceTail(_snapshotStart, next);
                }
                else
                {
                    // No surviving overlap: a new caption block. Preserve the old block.
                    _pauses.Add(_segment.Length);
                    string separator = NeedsSpace(_segment, next) ? " " : "";
                    _snapshotStart = _segment.Length + separator.Length;
                    _segment += separator + next;
                }
            }
            _previous = next;
            _changedAt = now;
            _settled = false;
            return true;
        }

        public bool Tick(DateTime now)
        {
            if (_settled || _previous.Length == 0) return false;
            TimeSpan idle = now - _changedAt;
            if (idle < ShortPauseDelay) return false;
            if (idle < PauseDelay)
            {
                IList<CaptionSentence> rows = CurrentRows();
                if (rows.Count == 0 || !rows[rows.Count - 1].IsDraft ||
                    SentenceSplitter.Measure(rows[rows.Count - 1].Text) < 24) return false;
            }
            _settled = true;
            _pauses.Add(_segment.Length);
            return true;
        }

        public void Clear()
        {
            _frozen.Clear();
            // Retain only the currently readable baseline, not previously cleared history.
            _segment = _previous;
            _snapshotStart = 0;
            _cutoff = _segment.Length;
            _pauses.Clear();
        }

        public void BeginSegment()
        {
            foreach (CaptionSentence row in CurrentRows()) _frozen.Add(new CaptionSentence(row.Text, false));
            _segment = _previous = "";
            _cutoff = _snapshotStart = 0;
            _settled = false;
            _pauses.Clear();
        }

        public IList<CaptionSentence> GetSentences()
        {
            var result = new List<CaptionSentence>(_frozen);
            result.AddRange(CurrentRows());
            return result;
        }

        public string GetCopyText()
        {
            return string.Join(Environment.NewLine, GetSentences().Select(s => s.Text));
        }

        private IList<CaptionSentence> CurrentRows()
        {
            string visible = _segment.Substring(_cutoff);
            var pauses = new HashSet<int>(_pauses.Where(p => p > _cutoff).Select(p => p - _cutoff));
            return SentenceSplitter.Split(visible, pauses);
        }

        private void ReplaceTail(int start, string replacement)
        {
            string before = _segment.Substring(start);
            int common = CommonPrefix(before, 0, replacement);
            int suffix = 0;
            while (suffix < before.Length - common && suffix < replacement.Length - common &&
                before[before.Length - 1 - suffix] == replacement[replacement.Length - 1 - suffix]) suffix++;
            int oldLength = _segment.Length;
            int newLength = start + replacement.Length;
            if (_cutoff > start + common)
            {
                if (_cutoff >= oldLength - suffix) _cutoff = newLength - (oldLength - _cutoff);
                else _cutoff = start + common + Math.Min(_cutoff - start - common, replacement.Length - common - suffix);
            }
            // Map pause boundaries through the revision. Discard pauses inside rewritten text.
            var mapped = new SortedSet<int>();
            foreach (int boundary in _pauses)
            {
                int position;
                if (boundary <= start + common) position = boundary;
                else if (boundary >= oldLength - suffix) position = newLength - (oldLength - boundary);
                else continue;
                // Slow, partial English words must not become separate sentences after a pause.
                if (position > start && position < newLength &&
                    IsLatinWord(replacement[position - start - 1]) && IsLatinWord(replacement[position - start])) continue;
                // Recognition often adds punctuation after the text has already settled.
                // Attach that punctuation to the preceding sentence rather than a new empty row.
                if (position >= start && position < newLength && "。！？.!?…\"'”’）)]】」』".IndexOf(replacement[position - start]) >= 0) continue;
                mapped.Add(position);
            }
            _pauses.Clear();
            foreach (int p in mapped) _pauses.Add(p);
            _segment = _segment.Substring(0, start) + replacement;
            _cutoff = Math.Max(0, Math.Min(_cutoff, _segment.Length));
        }

        private static int FindAlignment(string previous, string next, bool allowRevision)
        {
            if (next.StartsWith(previous, StringComparison.Ordinal) || previous.StartsWith(next, StringComparison.Ordinal)) return 0;
            // Find the longest shared prefix at any position in the old visible block.
            int bestOffset = -1, bestLength = 0;
            for (int offset = 0; offset < previous.Length; offset++)
            {
                if (previous[offset] != next[0]) continue;
                int length = CommonPrefix(previous, offset, next);
                bool exactOverlap = length == previous.Length - offset;
                bool strongAnchor = (allowRevision || exactOverlap) && (length >= 4 || (length >= 2 && IsCjk(next[0])));
                // Avoid aligning an isolated space/letter in unrelated English text.
                bool shortWord = length >= 1 && exactOverlap &&
                    (IsCjk(next[0]) || ((offset == 0 || !IsLatinWord(previous[offset - 1])) &&
                    (length == next.Length || !IsLatinWord(next[length]))));
                bool shortRevision = allowRevision && offset == 0 && !EndsSentence(previous) && length >= 2 &&
                    (char.IsWhiteSpace(next[length - 1]) || length >= Math.Min(previous.Length, next.Length) / 2.0);
                if ((strongAnchor || shortWord || shortRevision) && length > bestLength)
                {
                    bestOffset = offset;
                    bestLength = length;
                }
            }
            if (bestOffset >= 0) return bestOffset;
            // A revision can change the first word too. Accept a substantial shared suffix,
            // but do not join two independently punctuated sentences merely ending in '.'.
            int commonSuffix = 0;
            while (commonSuffix < previous.Length && commonSuffix < next.Length &&
                previous[previous.Length - commonSuffix - 1] == next[next.Length - commonSuffix - 1]) commonSuffix++;
            if (allowRevision && !EndsSentence(previous) && commonSuffix >= 3 && commonSuffix >= Math.Max(previous.Length, next.Length) / 2.0) return 0;
            return -1;
        }

        private static int CommonPrefix(string oldText, int offset, string newText)
        {
            int length = 0;
            while (offset + length < oldText.Length && length < newText.Length && oldText[offset + length] == newText[length]) length++;
            return length;
        }

        private static bool NeedsSpace(string left, string right)
        {
            return left.Length > 0 && right.Length > 0 && IsLatinWord(left[left.Length - 1]) && IsLatinWord(right[0]);
        }

        private static bool IsLatinWord(char c) { return c < 128 && char.IsLetterOrDigit(c); }
        private static bool IsCjk(char c) { return c >= '\u3400' && c <= '\u9fff'; }
        private static bool EndsSentence(string text)
        {
            return Regex.IsMatch(text, "[。！？.!?…][\\\"'”’）)\\]】」』]*$");
        }

        public static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Replace("\u200b", "").Replace("\uFEFF", "");
            value = Regex.Replace(value, @"(?<=[\u3400-\u9fff])[^\S\r\n]*[\r\n]+\s*(?=[\u3400-\u9fff])", "");
            return Regex.Replace(value, @"\s+", " ").Trim();
        }
    }

    public static class SentenceSplitter
    {
        // A Chinese character occupies two units; most Latin characters occupy one.
        // Prefer clauses of at least 18 Chinese characters, and keep unpunctuated
        // speech to roughly 36-52 Chinese characters without waiting for silence.
        public const int MinimumClauseUnits = 36;
        public const int TargetChunkUnits = 72;
        public const int MaximumChunkUnits = 104;
        private static readonly HashSet<string> Abbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "e.g", "i.e", "u.s", "u.k", "a.m", "p.m" };

        public static IList<CaptionSentence> Split(string text, ISet<int> pauses)
        {
            var result = new List<CaptionSentence>();
            int[] widths = new int[text.Length + 1];
            for (int p = 0; p < text.Length; p++) widths[p + 1] = widths[p] + CharacterWidth(text[p]);
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (i > start && pauses.Contains(i)) { Add(result, text, start, i, false); start = i; }
                if (IsEnd(text, i))
                {
                    int end = i + 1;
                    while (end < text.Length && ("。！？.!?…\"'”’）)]】」』".IndexOf(text[end]) >= 0)) end++;
                    Add(result, text, start, end, false);
                    start = end;
                    i = end - 1;
                }
                else if (IsClauseEnd(text, i) && widths[i + 1] - widths[start] >= MinimumClauseUnits)
                {
                    Add(result, text, start, i + 1, false);
                    start = i + 1;
                }
                else if (widths[i + 1] - widths[start] >= MaximumChunkUnits)
                {
                    int end = FindChunkBoundary(text, widths, start, i + 1);
                    if (end <= start) continue; // Keep a single very long English word/URL intact.
                    Add(result, text, start, end, false);
                    start = end;
                    i = end - 1;
                }
            }
            if (start < text.Length) Add(result, text, start, text.Length, !pauses.Contains(text.Length));
            return result;
        }

        public static int Measure(string text)
        {
            int width = 0;
            foreach (char c in text) width += CharacterWidth(c);
            return width;
        }

        private static int CharacterWidth(char c)
        {
            return c >= '\u2e80' && c <= '\uffef' ? 2 : 1;
        }

        private static bool IsClauseEnd(string text, int index)
        {
            if ("，,；;：:".IndexOf(text[index]) < 0) return false;
            if (index > 0 && index + 1 < text.Length && char.IsDigit(text[index - 1]) && char.IsDigit(text[index + 1])) return false;
            // Do not split the scheme of a URL.
            return !(text[index] == ':' && index + 1 < text.Length && text[index + 1] == '/');
        }

        private static int FindChunkBoundary(string text, int[] widths, int start, int limit)
        {
            int beforeTarget = -1, afterTarget = -1;
            for (int boundary = start + 1; boundary <= limit; boundary++)
            {
                int width = widths[boundary] - widths[start];
                if (width < MinimumClauseUnits || !CanBreakAt(text, boundary)) continue;
                if (width <= TargetChunkUnits) beforeTarget = boundary;
                else { afterTarget = boundary; break; }
            }
            return beforeTarget > start ? beforeTarget : afterTarget;
        }

        private static bool CanBreakAt(string text, int boundary)
        {
            // An incomplete last token may still grow on the next UIA snapshot.
            if (boundary >= text.Length) return false;
            char left = text[boundary - 1], right = text[boundary];
            if (char.IsHighSurrogate(left) && char.IsLowSurrogate(right)) return false;
            var category = char.GetUnicodeCategory(right);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark ||
                category == System.Globalization.UnicodeCategory.SpacingCombiningMark ||
                right == '\u200d' || left == '\u200d') return false;
            // Leave punctuation with the preceding text, including decimals and closing quotes.
            if (char.IsPunctuation(right) && "（([【「『“‘".IndexOf(right) < 0) return false;
            if (char.IsWhiteSpace(left) || char.IsWhiteSpace(right)) return true;
            if (IsClauseEnd(text, boundary - 1)) return true;
            bool leftCjk = left >= '\u3400' && left <= '\u9fff';
            bool rightCjk = right >= '\u3400' && right <= '\u9fff';
            return leftCjk || rightCjk;
        }

        private static void Add(List<CaptionSentence> rows, string text, int start, int end, bool draft)
        {
            string sentence = text.Substring(start, end - start).Trim();
            if (sentence.Length > 0 && !sentence.All(c => char.IsPunctuation(c) || char.IsWhiteSpace(c))) rows.Add(new CaptionSentence(sentence, draft));
        }

        private static bool IsEnd(string text, int index)
        {
            char c = text[index];
            if ("。！？!?…".IndexOf(c) >= 0) return true;
            if (c != '.') return false;
            if (index + 1 < text.Length && text[index + 1] == '.') return true;
            if (index > 0 && index + 1 < text.Length && char.IsLetterOrDigit(text[index - 1]) && char.IsLetterOrDigit(text[index + 1])) return false;
            int wordStart = index - 1;
            while (wordStart >= 0 && (char.IsLetter(text[wordStart]) || text[wordStart] == '.')) wordStart--;
            string word = text.Substring(wordStart + 1, index - wordStart - 1);
            if (Abbreviations.Contains(word)) return false;
            // Initials such as J. Smith or U. S. do not end a sentence.
            if (word.Length == 1 && word[0] >= 'A' && word[0] <= 'Z' && index + 1 < text.Length) return false;
            return true;
        }
    }
}
