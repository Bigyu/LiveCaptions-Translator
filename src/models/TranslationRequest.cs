namespace LiveCaptionsTranslator.models
{
    public class TranslationRequest
    {
        public string OriginalText = string.Empty;
        public int SentenceIndex = 0;
        public bool IsCorrection = false;
        public bool IsPreTranslation = false;
        public int ExpectedVersion = 0;
    }
}
