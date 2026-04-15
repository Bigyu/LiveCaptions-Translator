using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests;

/// <summary>
/// Tests for SentenceState→Overlay computation:
/// GetOverlayOriginalFromSentenceStates and GetOverlayTranslationFromSentenceStates.
/// </summary>
public class OverlayComputationTests
{
    [Fact]
    public void GetOverlayOriginal_EmptySentenceStates_ReturnsFallback()
    {
        var caption = Caption.GetInstance();
        lock (caption._sentenceStatesLock)
        {
            caption.SentenceStates.Clear();
            caption.FirstActiveSentenceIndex = 0;
            caption.LastActiveSentenceIndex = 0;
        }

        string result = caption.GetOverlayOriginalFromSentenceStates(3, "Hello world");
        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void GetOverlayOriginal_SingleSentence_ReturnsThatSentence()
    {
        var caption = Caption.GetInstance();
        lock (caption._sentenceStatesLock)
        {
            caption.SentenceStates.Clear();
            caption.FirstActiveSentenceIndex = 0;
            caption.LastActiveSentenceIndex = 0;

            caption.SentenceStates[0] = new SentenceState
            {
                OriginalText = "Hello.",
                Version = 1
            };
        }

        string result = caption.GetOverlayOriginalFromSentenceStates(3, "");
        Assert.Equal("Hello. ", result);
    }

    [Fact]
    public void GetOverlayOriginal_MultipleSentences_ConcatenatesWithPunctuation()
    {
        var caption = Caption.GetInstance();
        lock (caption._sentenceStatesLock)
        {
            caption.SentenceStates.Clear();
            caption.FirstActiveSentenceIndex = 0;
            caption.LastActiveSentenceIndex = 2;

            caption.SentenceStates[0] = new SentenceState { OriginalText = "Hello." };
            caption.SentenceStates[1] = new SentenceState { OriginalText = "How are you" };
            caption.SentenceStates[2] = new SentenceState { OriginalText = "I am fine." };
        }

        // displayCount=3, incompleteSentenceFallback=""
        string result = caption.GetOverlayOriginalFromSentenceStates(3, "");
        // "Hello." has EOS, "How are you" lacks EOS → gets ". " appended, "I am fine." has EOS
        Assert.Contains("Hello.", result);
        Assert.Contains("How are you", result);
        Assert.Contains("I am fine.", result);
    }

    [Fact]
    public void GetOverlayOriginal_WithIncompleteFallback_AppendsFallback()
    {
        var caption = Caption.GetInstance();
        lock (caption._sentenceStatesLock)
        {
            caption.SentenceStates.Clear();
            caption.FirstActiveSentenceIndex = 0;
            caption.LastActiveSentenceIndex = 0;

            caption.SentenceStates[0] = new SentenceState { OriginalText = "Hello." };
        }

        string result = caption.GetOverlayOriginalFromSentenceStates(3, "How are you");
        Assert.Contains("Hello.", result);
        Assert.Contains("How are you", result);
    }

    [Fact]
    public void GetOverlayTranslation_EmptySentenceStates_ReturnsEmptyTuple()
    {
        var caption = Caption.GetInstance();
        lock (caption._sentenceStatesLock)
        {
            caption.SentenceStates.Clear();
            caption.FirstActiveSentenceIndex = 0;
            caption.LastActiveSentenceIndex = 0;
        }

        var (previous, current, noticePrefix) = caption.GetOverlayTranslationFromSentenceStates(3);
        Assert.Equal(string.Empty, previous);
        Assert.Equal(string.Empty, current);
        Assert.Equal(string.Empty, noticePrefix);
    }

    [Fact]
    public void GetOverlayTranslation_SingleSentence_ReturnsCurrentOnly()
    {
        var caption = Caption.GetInstance();
        lock (caption._sentenceStatesLock)
        {
            caption.SentenceStates.Clear();
            caption.FirstActiveSentenceIndex = 0;
            caption.LastActiveSentenceIndex = 0;

            caption.SentenceStates[0] = new SentenceState
            {
                OriginalText = "Hello.",
                TranslatedText = "你好。",
                Version = 1
            };
        }

        var (previous, current, noticePrefix) = caption.GetOverlayTranslationFromSentenceStates(3);
        Assert.Equal(string.Empty, previous);
        Assert.Equal("你好。", current);
        Assert.Equal(string.Empty, noticePrefix);
    }

    [Fact]
    public void GetOverlayTranslation_MultipleSentences_SplitsPreviousAndCurrent()
    {
        var caption = Caption.GetInstance();
        lock (caption._sentenceStatesLock)
        {
            caption.SentenceStates.Clear();
            caption.FirstActiveSentenceIndex = 0;
            caption.LastActiveSentenceIndex = 2;

            caption.SentenceStates[0] = new SentenceState
            {
                OriginalText = "Hello.",
                TranslatedText = "你好。",
                Version = 1
            };
            caption.SentenceStates[1] = new SentenceState
            {
                OriginalText = "How are you.",
                TranslatedText = "你好吗。",
                Version = 1
            };
            caption.SentenceStates[2] = new SentenceState
            {
                OriginalText = "I am fine.",
                TranslatedText = "我很好。",
                Version = 1
            };
        }

        var (previous, current, noticePrefix) = caption.GetOverlayTranslationFromSentenceStates(3);
        Assert.Contains("你好。", previous);
        Assert.Contains("你好吗。", previous);
        Assert.Equal("我很好。", current);
    }

    [Fact]
    public void GetOverlayTranslation_WithNoticePrefix_SplitsPrefixAndTranslation()
    {
        var caption = Caption.GetInstance();
        lock (caption._sentenceStatesLock)
        {
            caption.SentenceStates.Clear();
            caption.FirstActiveSentenceIndex = 0;
            caption.LastActiveSentenceIndex = 0;

            caption.SentenceStates[0] = new SentenceState
            {
                OriginalText = "Hello.",
                TranslatedText = "[120 ms] 你好。",
                Version = 1
            };
        }

        var (previous, current, noticePrefix) = caption.GetOverlayTranslationFromSentenceStates(3);
        Assert.Equal(string.Empty, previous);
        Assert.Equal("你好。", current);
        Assert.Equal("[120 ms]", noticePrefix);
    }

    [Fact]
    public void GetOverlayTranslation_CorrectionUpdate_ReflectsNewTranslation()
    {
        var caption = Caption.GetInstance();
        lock (caption._sentenceStatesLock)
        {
            caption.SentenceStates.Clear();
            caption.FirstActiveSentenceIndex = 0;
            caption.LastActiveSentenceIndex = 1;

            caption.SentenceStates[0] = new SentenceState
            {
                OriginalText = "Hello.",
                TranslatedText = "你好。",
                Version = 1
            };
            caption.SentenceStates[1] = new SentenceState
            {
                OriginalText = "How are you doing.",
                TranslatedText = "你好吗？",
                Version = 2
            };
        }

        var (previous, current, noticePrefix) = caption.GetOverlayTranslationFromSentenceStates(3);
        Assert.Equal("你好。 ", previous);
        Assert.Equal("你好吗？", current);
    }

    [Fact]
    public void GetOverlayTranslation_ErrorTranslation_IncludedInCurrent()
    {
        var caption = Caption.GetInstance();
        lock (caption._sentenceStatesLock)
        {
            caption.SentenceStates.Clear();
            caption.FirstActiveSentenceIndex = 0;
            caption.LastActiveSentenceIndex = 0;

            caption.SentenceStates[0] = new SentenceState
            {
                OriginalText = "Hello.",
                TranslatedText = "[ERROR] Translation Failed: timeout",
                Version = 1
            };
        }

        var (previous, current, noticePrefix) = caption.GetOverlayTranslationFromSentenceStates(3);
        Assert.Equal(string.Empty, previous);
        Assert.Equal("[ERROR] Translation Failed: timeout", current);
    }
}