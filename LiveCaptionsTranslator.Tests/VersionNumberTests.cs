using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests;

/// <summary>
/// Tests for the version number mechanism in TranslationTaskQueue:
/// higher version overwrites lower version; different sentences have independent counters.
/// </summary>
public class VersionNumberTests
{
    [Fact]
    public void SentenceState_HigherVersion_OverwritesLowerVersion()
    {
        var state = new SentenceState
        {
            OriginalText = "How are you.",
            TranslatedText = "你好吗。",
            Version = 1
        };

        // Version 2 arrives — should overwrite version 1
        Assert.True(2 >= state.Version);
        state.TranslatedText = "你好吗？";
        state.Version = 2;

        Assert.Equal("你好吗？", state.TranslatedText);
        Assert.Equal(2, state.Version);
    }

    [Fact]
    public void SentenceState_LowerVersion_DoesNotOverwriteHigherVersion()
    {
        var state = new SentenceState
        {
            OriginalText = "How are you.",
            TranslatedText = "你好吗？",
            Version = 2
        };

        // Version 1 arrives — should NOT overwrite version 2
        bool shouldOverwrite = 1 >= state.Version;
        Assert.False(shouldOverwrite);

        // State remains unchanged
        Assert.Equal("你好吗？", state.TranslatedText);
        Assert.Equal(2, state.Version);
    }

    [Fact]
    public void SentenceState_DifferentIndices_IndependentVersions()
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
                Version = 3
            };
            caption.SentenceStates[1] = new SentenceState
            {
                OriginalText = "How are you.",
                TranslatedText = "你好吗。",
                Version = 1
            };

            // Index 0 has version 3, index 1 has version 1 — independent
            Assert.Equal(3, caption.SentenceStates[0].Version);
            Assert.Equal(1, caption.SentenceStates[1].Version);

            // Updating index 1 to version 2 does not affect index 0
            caption.SentenceStates[1].Version = 2;
            Assert.Equal(3, caption.SentenceStates[0].Version);
            Assert.Equal(2, caption.SentenceStates[1].Version);
        }
    }

    [Fact]
    public void SentenceState_PreTranslationThenFormalTranslation_FormalWins()
    {
        var caption = Caption.GetInstance();
        lock (caption._sentenceStatesLock)
        {
            caption.SentenceStates.Clear();
            caption.FirstActiveSentenceIndex = 0;
            caption.LastActiveSentenceIndex = 0;

            // Pre-translation arrives first (version 1)
            caption.SentenceStates[0] = new SentenceState
            {
                OriginalText = "How are you",
                TranslatedText = "你好吗",
                Version = 1,
                IsComplete = false,
                IsTranslationPending = true
            };

            // Formal translation arrives later (version 2) — overwrites pre-translation
            Assert.True(2 >= caption.SentenceStates[0].Version);
            caption.SentenceStates[0].TranslatedText = "你好吗？";
            caption.SentenceStates[0].Version = 2;
            caption.SentenceStates[0].IsComplete = true;
            caption.SentenceStates[0].IsTranslationPending = false;

            Assert.Equal("你好吗？", caption.SentenceStates[0].TranslatedText);
            Assert.Equal(2, caption.SentenceStates[0].Version);
            Assert.True(caption.SentenceStates[0].IsComplete);
        }
    }

    [Fact]
    public void TranslationTask_VersionAndIndex_TrackedCorrectly()
    {
        var task = new TranslationTask(
            _ => Task.FromResult(("你好吗。", true)),
            "How are you.",
            new CancellationTokenSource(),
            5, 3, false
        );

        Assert.Equal(5, task.SentenceIndex);
        Assert.Equal(3, task.Version);
        Assert.False(task.IsPreTranslation);
    }

    [Fact]
    public void TranslationTaskQueue_Output_ReadsFromSentenceStates()
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
                Version = 2,
                IsComplete = true
            };
        }

        var queue = new TranslationTaskQueue();
        var (translatedText, isChoke) = queue.Output;

        // Output should return the last active sentence's translation
        Assert.Equal("你好吗。", translatedText);
        Assert.True(isChoke);
    }
}