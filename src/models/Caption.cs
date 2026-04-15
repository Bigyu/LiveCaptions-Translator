using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class SentenceState
    {
        public string OriginalText = string.Empty;
        public string TranslatedText = string.Empty;
        public int Version = 0;
        public bool IsComplete = false;
        public bool IsTranslationPending = false;
    }

    public class Caption : INotifyPropertyChanged
    {
        public const int MAX_CONTEXTS = 10;

        private static Caption? instance = null;
        public event PropertyChangedEventHandler? PropertyChanged;

        private string displayOriginalCaption = string.Empty;
        private string displayTranslatedCaption = string.Empty;
        private string overlayOriginalCaption = " ";
        private string overlayCurrentTranslation = " ";
        private string overlayNoticePrefix = " ";

        public readonly object _sentenceStatesLock = new object();
        public Dictionary<int, SentenceState> SentenceStates { get; } = new();
        public int FirstActiveSentenceIndex { get; set; } = 0;
        public int LastActiveSentenceIndex { get; set; } = 0;

        public string OriginalCaption { get; set; } = string.Empty;
        public string TranslatedCaption { get; set; } = string.Empty;

        public Queue<TranslationHistoryEntry> Contexts { get; } = new(MAX_CONTEXTS);

        public IEnumerable<TranslationHistoryEntry> AwareContexts => GetPreviousContexts(Translator.Setting.NumContexts);
        public string AwareContextsCaption => GetPreviousText(Translator.Setting.NumContexts, TextType.Caption);

        public IEnumerable<TranslationHistoryEntry> DisplayLogCards =>
            GetPreviousContexts(Translator.Setting.DisplaySentences).Reverse();

        public string DisplayOriginalCaption
        {
            get => displayOriginalCaption;
            set
            {
                displayOriginalCaption = value;
                OnPropertyChanged("DisplayOriginalCaption");
            }
        }
        public string DisplayTranslatedCaption
        {
            get => displayTranslatedCaption;
            set
            {
                displayTranslatedCaption = value;
                OnPropertyChanged("DisplayTranslatedCaption");
            }
        }

        public string OverlayOriginalCaption
        {
            get => overlayOriginalCaption;
            set
            {
                overlayOriginalCaption = value;
                OnPropertyChanged("OverlayOriginalCaption");
            }
        }
        public string OverlayNoticePrefix
        {
            get => overlayNoticePrefix;
            set
            {
                overlayNoticePrefix = value;
                OnPropertyChanged("OverlayNoticePrefix");
            }
        }
        public string OverlayCurrentTranslation
        {
            get => overlayCurrentTranslation;
            set
            {
                overlayCurrentTranslation = value;
                OnPropertyChanged("OverlayCurrentTranslation");
            }
        }

        public string OverlayPreviousTranslation =>
            GetOverlayTranslationFromSentenceStates(Translator.Setting.DisplaySentences).previous;

        private Caption()
        {
        }

        public static Caption GetInstance()
        {
            if (instance != null)
                return instance;
            instance = new Caption();
            return instance;
        }

        public string GetPreviousText(int count, TextType textType)
        {
            if (count <= 0 || Contexts.Count == 0)
                return string.Empty;

            var prev = Contexts
                .Reverse().Take(count).Reverse()
                .Select(entry => entry == null || string.CompareOrdinal(entry.TranslatedText, "N/A") == 0 ||
                                 entry.TranslatedText.Contains("[ERROR]") || entry.TranslatedText.Contains("[WARNING]") ?
                    "" : (textType == TextType.Caption ? entry.SourceText : entry.TranslatedText))
                .Aggregate((accu, cur) =>
                {
                    if (!string.IsNullOrEmpty(accu))
                    {
                        if (Array.IndexOf(TextUtil.PUNC_EOS, accu[^1]) == -1)
                            accu += TextUtil.isCJChar(accu[^1]) ? "。" : ". ";
                        else
                            accu += TextUtil.isCJChar(accu[^1]) ? "" : " ";
                    }
                    cur = RegexPatterns.NoticePrefix().Replace(cur, "");
                    return accu + cur;
                });

            if (textType == TextType.Translation)
                prev = RegexPatterns.NoticePrefix().Replace(prev, "");
            if (!string.IsNullOrEmpty(prev) && Array.IndexOf(TextUtil.PUNC_EOS, prev[^1]) == -1)
                prev += TextUtil.isCJChar(prev[^1]) ? "。" : ".";
            if (!string.IsNullOrEmpty(prev) && Encoding.UTF8.GetByteCount(prev[^1].ToString()) < 2)
                prev += " ";
            return prev;
        }

        public IEnumerable<TranslationHistoryEntry> GetPreviousContexts(int count)
        {
            if (count <= 0 || Contexts.Count == 0)
                return [];

            return Contexts
                .Reverse().Take(count).Reverse()
                .Where(entry => entry != null && string.CompareOrdinal(entry.TranslatedText, "N/A") != 0 &&
                                !entry.TranslatedText.Contains("[ERROR]") &&
                                !entry.TranslatedText.Contains("[WARNING]"));
        }

        /// <summary>
        /// Computes Overlay original text from SentenceStates dict.
        /// Returns the last N sentences' OriginalText concatenated, including incomplete sentence.
        /// Falls back to empty string if SentenceStates is empty.
        /// </summary>
        public string GetOverlayOriginalFromSentenceStates(int displayCount, string incompleteSentenceFallback)
        {
            lock (_sentenceStatesLock)
            {
                if (SentenceStates.Count == 0)
                    return incompleteSentenceFallback;

                int lastCompletedIdx = LastActiveSentenceIndex;
                int firstIdx = Math.Max(FirstActiveSentenceIndex, lastCompletedIdx - displayCount + 1);

                var parts = new List<string>();
                for (int i = firstIdx; i <= lastCompletedIdx; i++)
                {
                    if (SentenceStates.TryGetValue(i, out var state) && !string.IsNullOrEmpty(state.OriginalText))
                        parts.Add(state.OriginalText);
                }

                if (!string.IsNullOrEmpty(incompleteSentenceFallback))
                    parts.Add(incompleteSentenceFallback);

                if (parts.Count == 0)
                    return string.Empty;

                return ConcatenateWithPunctuation(parts);
            }
        }

        /// <summary>
        /// Computes Overlay translation text from SentenceStates dict.
        /// Returns (previousTranslations, currentTranslation, noticePrefix) tuple.
        /// previousTranslations = sentences before the last one.
        /// currentTranslation = last sentence's TranslatedText (without NoticePrefix).
        /// noticePrefix = last sentence's NoticePrefix.
        /// </summary>
        public (string previous, string current, string noticePrefix) GetOverlayTranslationFromSentenceStates(int displayCount)
        {
            lock (_sentenceStatesLock)
            {
                if (SentenceStates.Count == 0)
                    return (string.Empty, string.Empty, string.Empty);

                int lastIdx = LastActiveSentenceIndex;
                int firstIdx = Math.Max(FirstActiveSentenceIndex, lastIdx - displayCount + 1);

                var translatedParts = new List<string>();
                for (int i = firstIdx; i <= lastIdx; i++)
                {
                    if (SentenceStates.TryGetValue(i, out var state))
                    {
                        if (!string.IsNullOrEmpty(state.TranslatedText))
                        {
                            if (state.TranslatedText.Contains("[ERROR]") || state.TranslatedText.Contains("[WARNING]"))
                            {
                                if (i == lastIdx)
                                    translatedParts.Add(state.TranslatedText);
                            }
                            else
                                translatedParts.Add(state.TranslatedText);
                        }
                    }
                }

                if (translatedParts.Count == 0)
                    return (string.Empty, string.Empty, string.Empty);

                string currentTranslation = translatedParts[^1];
                string noticePrefix = string.Empty;

                if (!currentTranslation.Contains("[ERROR]") && !currentTranslation.Contains("[WARNING]"))
                {
                    var match = RegexPatterns.NoticePrefixAndTranslation().Match(currentTranslation);
                    noticePrefix = match.Groups[1].Value.Trim();
                    currentTranslation = match.Groups[2].Value.Trim();
                }

                var previousParts = translatedParts.Take(translatedParts.Count - 1)
                    .Select(t => RegexPatterns.NoticePrefix().Replace(t, "").Trim())
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();

                string previousTranslation = previousParts.Count > 0
                    ? ConcatenateWithPunctuation(previousParts)
                    : string.Empty;

                return (previousTranslation, currentTranslation, noticePrefix);
            }
        }

        /// <summary>
        /// Concatenates text parts with appropriate punctuation separators.
        /// Adds EOS punctuation between parts if missing, with CJ-aware spacing.
        /// </summary>
        private static string ConcatenateWithPunctuation(List<string> parts)
        {
            if (parts.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0 && sb.Length > 0)
                {
                    char lastChar = sb[^1];
                    if (Array.IndexOf(TextUtil.PUNC_EOS, lastChar) == -1)
                        sb.Append(TextUtil.isCJChar(lastChar) ? "。" : ". ");
                    else
                        sb.Append(TextUtil.isCJChar(lastChar) ? "" : " ");
                }
                sb.Append(parts[i]);
            }

            if (sb.Length > 0 && Array.IndexOf(TextUtil.PUNC_EOS, sb[^1]) == -1)
                sb.Append(TextUtil.isCJChar(sb[^1]) ? "。" : ".");
            if (sb.Length > 0 && Encoding.UTF8.GetByteCount(sb[^1].ToString()) < 2)
                sb.Append(' ');

            return sb.ToString();
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }

    public enum TextType
    {
        Caption,
        Translation
    }
}