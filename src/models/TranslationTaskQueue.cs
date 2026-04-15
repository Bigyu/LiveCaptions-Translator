namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();
        private readonly List<TranslationTask> tasks;

        public (string translatedText, bool isChoke) Output
        {
            get
            {
                var caption = Caption.GetInstance();
                lock (caption._sentenceStatesLock)
                {
                    for (int i = caption.LastActiveSentenceIndex; i >= caption.FirstActiveSentenceIndex; i--)
                    {
                        if (caption.SentenceStates.TryGetValue(i, out var state)
                            && !string.IsNullOrEmpty(state.TranslatedText))
                            return (state.TranslatedText, state.IsComplete);
                    }
                }
                return (string.Empty, false);
            }
        }

        public TranslationTaskQueue()
        {
            tasks = new List<TranslationTask>();
        }

        public void Enqueue(Func<CancellationToken, Task<(string, bool)>> worker,
            string originalText)
        {
            Enqueue(worker, originalText, 0, 0);
        }

        public void Enqueue(Func<CancellationToken, Task<(string, bool)>> worker,
            string originalText, int sentenceIndex, int version, bool isPreTranslation = false)
        {
            var newTranslationTask = new TranslationTask(
                worker, originalText, new CancellationTokenSource(), sentenceIndex, version, isPreTranslation
            );
            lock (_lock)
            {
                tasks.Add(newTranslationTask);
            }
            // Run `OnTaskCompleted` in a new thread.
            newTranslationTask.Task.ContinueWith(
                task => OnTaskCompleted(newTranslationTask),
                TaskContinuationOptions.OnlyOnRanToCompletion
            );
        }

        private async Task OnTaskCompleted(TranslationTask translationTask)
        {
            lock (_lock)
            {
                tasks.Remove(translationTask);
            }

            var (translatedText, isComplete) = translationTask.Task.Result;

            var caption = Caption.GetInstance();
            lock (caption._sentenceStatesLock)
            {
                if (caption.SentenceStates.TryGetValue(translationTask.SentenceIndex, out var state))
                {
                    if (translationTask.Version >= state.Version)
                    {
                        state.TranslatedText = translatedText;
                        state.Version = translationTask.Version;
                        state.IsTranslationPending = false;
                        state.IsComplete = isComplete;
                    }
                }
            }

            if (!translationTask.IsPreTranslation && isComplete)
            {
                bool isOverwrite = await Translator.IsOverwrite(translationTask.OriginalText);
                if (!isOverwrite)
                    await Translator.AddContexts();
                await Translator.Log(translationTask.OriginalText, translatedText, isOverwrite);
            }
        }
    }

    public class TranslationTask
    {
        public Task<(string, bool)> Task { get; }
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }
        public int SentenceIndex { get; }
        public int Version { get; }
        public bool IsPreTranslation { get; }

        public TranslationTask(Func<CancellationToken, Task<(string, bool)>> worker,
            string originalText, CancellationTokenSource cts, int sentenceIndex, int version, bool isPreTranslation = false)
        {
            Task = worker(cts.Token);
            OriginalText = originalText;
            CTS = cts;
            SentenceIndex = sentenceIndex;
            Version = version;
            IsPreTranslation = isPreTranslation;
        }
    }
}
