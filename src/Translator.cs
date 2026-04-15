using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Automation;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
        private static AutomationElement? window = null;
        private static Caption? caption = null;
        private static Setting? setting = null;

        private static readonly Queue<TranslationRequest> pendingTextQueue = new();
        private static readonly Dictionary<int, int> sentenceVersionCounters = new();
        private static readonly TranslationTaskQueue translationTaskQueue = new();

        private static List<string> trackedSentences = new();
        private static string lastEnqueuedIncomplete = string.Empty;

        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
        public static Caption? Caption => caption;
        public static Setting? Setting => setting;

        public static bool LogOnlyFlag { get; set; } = false;
        public static bool FirstUseFlag { get; set; } = false;

        public static event Action? TranslationLogged;

        static Translator()
        {
            window = LiveCaptionsHandler.LaunchLiveCaptions();
            LiveCaptionsHandler.FixLiveCaptions(Window);
            LiveCaptionsHandler.HideLiveCaptions(Window);

            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), models.Setting.FILENAME)))
                FirstUseFlag = true;

            caption = Caption.GetInstance();
            setting = Setting.Load();
        }

        public static void SyncLoop()
        {
            while (true)
            {
                if (Window == null)
                {
                    Thread.Sleep(2000);
                    continue;
                }

                string fullText = string.Empty;
                try
                {
                    // Check LiveCaptions.exe still alive
                    var info = Window.Current;
                    var name = info.Name;
                    // Get the text recognized by LiveCaptions (10-20ms)
                    fullText = LiveCaptionsHandler.GetCaptions(Window);
                }
                catch (ElementNotAvailableException)
                {
                    Window = null;
                    continue;
                }
                if (string.IsNullOrEmpty(fullText))
                    continue;

                // Preprocess
                fullText = RegexPatterns.Acronym().Replace(fullText, "$1$2");
                fullText = RegexPatterns.AcronymWithWords().Replace(fullText, "$1 $2");
                fullText = RegexPatterns.PunctuationSpace().Replace(fullText, "$1 ");
                fullText = RegexPatterns.CJPunctuationSpace().Replace(fullText, "$1");
                // Note: For certain languages (such as Japanese), LiveCaptions excessively uses `\n`.
                // Replace redundant `\n` within sentences with comma-level punctuation.
                fullText = TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);

                var (completedSentences, incompleteSentence) = SplitSentences(fullText);

                // Clear stale contexts when first sentence hasn't completed yet
                if (completedSentences.Count == 0 && Caption.Contexts.Count > 0)
                    ClearContexts();

                // Align tracked sentences with current: old sentences scroll off the front
                int scrollOff = Math.Max(0, trackedSentences.Count - completedSentences.Count);
                var alignedTracked = trackedSentences.Skip(scrollOff).ToList();
                int compareCount = Math.Min(alignedTracked.Count, completedSentences.Count);

                // Reset if most aligned sentences don't match (LiveCaptions refreshed)
                if (compareCount > 0)
                {
                    int matchCount = 0;
                    for (int i = 0; i < compareCount; i++)
                    {
                        if (TextUtil.Similarity(alignedTracked[i], completedSentences[i])
                            > TextUtil.SIM_THRESHOLD)
                            matchCount++;
                    }
                    if (matchCount < compareCount / 2)
                    {
                        trackedSentences.Clear();
                        ClearContexts();
                        alignedTracked = [];
                        compareCount = 0;
                    }
                }

                for (int i = 0; i < compareCount; i++)
                {
                    if (string.CompareOrdinal(alignedTracked[i], completedSentences[i]) != 0)
                    {
                        string sentence = completedSentences[i];
                        if (Encoding.UTF8.GetByteCount(sentence) < TextUtil.SHORT_THRESHOLD && i > 0)
                            sentence = completedSentences[i - 1] + " " + sentence;
                        int version = GetNextVersion(i);
                        pendingTextQueue.Enqueue(new TranslationRequest
                        {
                            OriginalText = sentence,
                            SentenceIndex = i,
                            IsCorrection = true,
                            ExpectedVersion = version
                        });
                    }
                }

                for (int i = alignedTracked.Count; i < completedSentences.Count; i++)
                {
                    string sentence = completedSentences[i];
                    if (Encoding.UTF8.GetByteCount(sentence) < TextUtil.SHORT_THRESHOLD && i > 0)
                        sentence = completedSentences[i - 1] + " " + sentence;
                    int version = GetNextVersion(i);
                    pendingTextQueue.Enqueue(new TranslationRequest
                    {
                        OriginalText = sentence,
                        SentenceIndex = i,
                        IsCorrection = false,
                        ExpectedVersion = version
                    });
                    lastEnqueuedIncomplete = string.Empty;
                }

                trackedSentences = completedSentences.ToList();

                // Fallback for very long incomplete sentences
                if (incompleteSentence.Length > 0
                    && Encoding.UTF8.GetByteCount(incompleteSentence) >= TextUtil.LONG_THRESHOLD
                    && string.CompareOrdinal(lastEnqueuedIncomplete, incompleteSentence) != 0)
                {
                    int sentenceIndex = completedSentences.Count;
                    int version = GetNextVersion(sentenceIndex);
                    pendingTextQueue.Enqueue(new TranslationRequest
                    {
                        OriginalText = incompleteSentence,
                        SentenceIndex = sentenceIndex,
                        IsPreTranslation = true,
                        ExpectedVersion = version
                    });
                    lastEnqueuedIncomplete = incompleteSentence;
                }

                // ── Display ──

                int displayCount = Math.Min(Setting.DisplaySentences, Caption.Contexts.Count);
                int eosSeen = 0;
                int overlayStart = 0;
                for (int i = fullText.Length - 1; i >= 0; i--)
                {
                    if (Array.IndexOf(TextUtil.PUNC_EOS, fullText[i]) != -1)
                    {
                        eosSeen++;
                        if (eosSeen > displayCount)
                        {
                            overlayStart = i + 1;
                            break;
                        }
                    }
                }
                while (overlayStart < fullText.Length && fullText[overlayStart] == ' ')
                    overlayStart++;
                Caption.OverlayOriginalCaption = overlayStart < fullText.Length
                    ? fullText[overlayStart..]
                    : fullText;

                // Display current incomplete sentence, or last completed if no incomplete
                string displaySentence = incompleteSentence.Length > 0
                    ? incompleteSentence
                    : (completedSentences.Count > 0 ? completedSentences[^1] : fullText);
                if (string.CompareOrdinal(Caption.DisplayOriginalCaption, displaySentence) != 0)
                {
                    Caption.DisplayOriginalCaption = displaySentence;
                    Caption.DisplayOriginalCaption =
                        TextUtil.ShortenDisplaySentence(Caption.DisplayOriginalCaption, TextUtil.VERYLONG_THRESHOLD);
                }

                Thread.Sleep(25);
            }
        }

        private static (List<string> completed, string incomplete) SplitSentences(string fullText)
        {
            var completed = new List<string>();
            int start = 0;
            for (int i = 0; i < fullText.Length; i++)
            {
                if (Array.IndexOf(TextUtil.PUNC_EOS, fullText[i]) != -1)
                {
                    string sentence = fullText[start..(i + 1)].Trim();
                    if (sentence.Length > 0)
                        completed.Add(sentence);
                    start = i + 1;
                }
            }
            string incomplete = start < fullText.Length
                ? fullText[start..].Trim()
                : string.Empty;
            return (completed, incomplete);
        }

        private static int GetNextVersion(int sentenceIndex)
        {
            if (!sentenceVersionCounters.ContainsKey(sentenceIndex))
                sentenceVersionCounters[sentenceIndex] = 1;
            else
                sentenceVersionCounters[sentenceIndex]++;
            return sentenceVersionCounters[sentenceIndex];
        }

        public static async Task TranslateLoop()
        {
            while (true)
            {
                // Check LiveCaptions.exe still alive
                if (Window == null)
                {
                    Caption.DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                    Window = LiveCaptionsHandler.LaunchLiveCaptions();
                    Caption.DisplayTranslatedCaption = "";
                }

                // Translate
                if (pendingTextQueue.Count > 0)
                {
                    var originalSnapshot = pendingTextQueue.Dequeue();

                    if (LogOnlyFlag)
                    {
                        bool isOverwrite = await IsOverwrite(originalSnapshot.OriginalText);
                        await LogOnly(originalSnapshot.OriginalText, isOverwrite);
                    }
                    else
                    {
                        translationTaskQueue.Enqueue(token => Task.Run(
                            () => Translate(originalSnapshot.OriginalText, token), token),
                            originalSnapshot.OriginalText,
                            originalSnapshot.SentenceIndex,
                            originalSnapshot.ExpectedVersion);
                    }
                }

                Thread.Sleep(40);
            }
        }

        public static async Task DisplayLoop()
        {
            while (true)
            {
                var (translatedText, isChoke) = translationTaskQueue.Output;

                if (LogOnlyFlag)
                {
                    Caption.TranslatedCaption = string.Empty;
                    Caption.DisplayTranslatedCaption = "[Paused]";
                    Caption.OverlayNoticePrefix = "[Paused]";
                    Caption.OverlayCurrentTranslation = string.Empty;
                }
                else if (!string.IsNullOrEmpty(RegexPatterns.NoticePrefix().Replace(
                             translatedText, string.Empty).Trim()) &&
                         string.CompareOrdinal(Caption.TranslatedCaption, translatedText) != 0)
                {
                    // Main page
                    Caption.TranslatedCaption = translatedText;
                    Caption.DisplayTranslatedCaption =
                        TextUtil.ShortenDisplaySentence(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);

                    // Overlay window
                    if (Caption.TranslatedCaption.Contains("[ERROR]") || Caption.TranslatedCaption.Contains("[WARNING]"))
                        Caption.OverlayCurrentTranslation = Caption.TranslatedCaption;
                    else
                    {
                        var match = RegexPatterns.NoticePrefixAndTranslation().Match(Caption.TranslatedCaption);
                        Caption.OverlayNoticePrefix = match.Groups[1].Value.Trim();
                        Caption.OverlayCurrentTranslation = match.Groups[2].Value.Trim();
                    }
                }

                // If the original sentence is a complete sentence, choke for better visual experience.
                if (isChoke)
                    Thread.Sleep(720);
                Thread.Sleep(40);
            }
        }

        public static async Task<(string, bool)> Translate(string text, CancellationToken token = default)
        {
            string translatedText;
            bool isChoke = Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1;

            try
            {
                var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;

                if (Setting.ContextAware && !TranslateAPI.IsLLMBased)
                {
                    translatedText = await TranslateAPI.TranslateFunction($"{Caption.AwareContextsCaption} 🔤 {text} 🔤", token);
                    translatedText = RegexPatterns.TargetSentence().Match(translatedText).Groups[1].Value;
                }
                else
                {
                    translatedText = await TranslateAPI.TranslateFunction(text, token);
                    translatedText = translatedText.Replace("🔤", "");
                }

                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds,4} ms] " + translatedText;
                }
            }
            catch (OperationCanceledException ex)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ($"[ERROR] Translation Failed: {ex.Message}", isChoke);
            }

            return (translatedText, isChoke);
        }

        public static async Task Log(string originalText, string translatedText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            string targetLanguage, apiName;
            if (Setting != null)
            {
                targetLanguage = Setting.TargetLanguage;
                apiName = Setting.ApiName;
            }
            else
            {
                targetLanguage = "N/A";
                apiName = "N/A";
            }

            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, translatedText, targetLanguage, apiName);
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task LogOnly(string originalText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, "N/A", "N/A", "LogOnly");
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task AddContexts(CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
            if (lastLog == null)
                return;

            if (Caption?.Contexts.Count >= Caption.MAX_CONTEXTS)
                Caption.Contexts.Dequeue();
            Caption?.Contexts.Enqueue(lastLog);

            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        public static void ClearContexts()
        {
            Caption?.Contexts.Clear();

            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        // If this text is too similar to the last one, overwrite it when logging.
        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
            if (lastOriginalText == null)
                return false;

            int minLen = Math.Min(originalText.Length, lastOriginalText.Length);
            originalText = originalText.Substring(0, minLen);
            lastOriginalText = lastOriginalText.Substring(0, minLen);

            double similarity = TextUtil.Similarity(originalText, lastOriginalText);
            return similarity > TextUtil.SIM_THRESHOLD;
        }
    }
}
