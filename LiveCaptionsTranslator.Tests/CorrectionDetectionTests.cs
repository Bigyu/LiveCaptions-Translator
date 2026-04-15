using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests;

/// <summary>
/// Tests for correction detection logic: how SentenceStates are updated
/// when tracked sentences differ from current sentences (corrections vs new sentences vs reset).
/// </summary>
public class CorrectionDetectionTests
{
    [Fact]
    public void SentenceState_OriginalText_UpdatedOnCorrection()
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
                Version = 1,
                IsComplete = true
            };
            caption.SentenceStates[1] = new SentenceState
            {
                OriginalText = "How are you.",
                TranslatedText = "你好吗。",
                Version = 1,
                IsComplete = true
            };

            // Correction: sentence at index 1 changes from "How are you." to "How are you doing."
            caption.SentenceStates[1].OriginalText = "How are you doing.";

            Assert.Equal("How are you doing.", caption.SentenceStates[1].OriginalText);
        }
    }

    [Fact]
    public void SentenceState_NewSentence_AddedToDictionary()
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
                Version = 1,
                IsComplete = true
            };

            // New sentence appears at index 1
            caption.SentenceStates[1] = new SentenceState
            {
                OriginalText = "I am fine.",
                Version = 0,
                IsComplete = false
            };
            caption.LastActiveSentenceIndex = 1;

            Assert.Equal(2, caption.SentenceStates.Count);
            Assert.Equal("I am fine.", caption.SentenceStates[1].OriginalText);
            Assert.Equal(1, caption.LastActiveSentenceIndex);
        }
    }

    [Fact]
    public void SentenceState_Reset_ClearsAllStates()
    {
        var caption = Caption.GetInstance();
        lock (caption._sentenceStatesLock)
        {
            caption.SentenceStates.Clear();
            caption.FirstActiveSentenceIndex = 0;
            caption.LastActiveSentenceIndex = 2;

            for (int i = 0; i <= 2; i++)
            {
                caption.SentenceStates[i] = new SentenceState
                {
                    OriginalText = $"Sentence {i}.",
                    TranslatedText = $"翻译 {i}。",
                    Version = 1,
                    IsComplete = true
                };
            }

            // Reset scenario: LiveCaptions refreshed, all states cleared
            caption.SentenceStates.Clear();
            caption.FirstActiveSentenceIndex = 0;
            caption.LastActiveSentenceIndex = 0;

            Assert.Empty(caption.SentenceStates);
        }
    }

    [Fact]
    public void Similarity_BelowThreshold_IndicatesCorrection()
    {
        // "How are you." vs "How are you doing." — similar but different
        double sim = TextUtil.Similarity("How are you.", "How are you doing.");
        Assert.True(sim > TextUtil.SIM_THRESHOLD, "Similar sentences should exceed threshold");

        // "Hello." vs "Goodbye." — very different
        double lowSim = TextUtil.Similarity("Hello.", "Goodbye.");
        Assert.True(lowSim < TextUtil.SIM_THRESHOLD, "Dissimilar sentences should be below threshold");
    }

    [Fact]
    public void Similarity_PrefixMatch_Returns1()
    {
        // One string is prefix of the other → similarity = 1.0
        double sim = TextUtil.Similarity("Hello world", "Hello world how are you");
        Assert.Equal(1.0, sim);
    }

    [Fact]
    public void TranslationRequest_IsCorrection_FlagSet()
    {
        var request = new TranslationRequest
        {
            OriginalText = "How are you doing.",
            SentenceIndex = 1,
            IsCorrection = true,
            IsPreTranslation = false,
            ExpectedVersion = 2
        };

        Assert.True(request.IsCorrection);
        Assert.False(request.IsPreTranslation);
        Assert.Equal(2, request.ExpectedVersion);
        Assert.Equal(1, request.SentenceIndex);
    }
}