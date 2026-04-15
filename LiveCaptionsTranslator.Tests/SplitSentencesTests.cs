using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests;

/// <summary>
/// Tests for the sentence splitting logic used in SyncLoop.
/// SplitSentences is a private static method in Translator, so we test
/// the underlying TextUtil helpers and replicate the splitting algorithm
/// to verify correctness.
/// </summary>
public class SplitSentencesTests
{
    /// <summary>
    /// Replicates the private SplitSentences algorithm from Translator.cs (lines 290-308)
    /// for testing purposes. The algorithm splits text at EOS punctuation characters.
    /// </summary>
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

    [Fact]
    public void SplitSentences_NormalInput_SplitsCompletedAndIncomplete()
    {
        // "Hello. How are you." → completed=["Hello.", "How are you."], incomplete=""
        var (completed, incomplete) = SplitSentences("Hello. How are you.");

        Assert.Equal(2, completed.Count);
        Assert.Equal("Hello.", completed[0]);
        Assert.Equal("How are you.", completed[1]);
        Assert.Equal(string.Empty, incomplete);
    }

    [Fact]
    public void SplitSentences_IncompleteSentence_ReturnsIncompletePart()
    {
        // "Hello. How are you" → completed=["Hello."], incomplete="How are you"
        var (completed, incomplete) = SplitSentences("Hello. How are you");

        Assert.Single(completed);
        Assert.Equal("Hello.", completed[0]);
        Assert.Equal("How are you", incomplete);
    }

    [Fact]
    public void SplitSentences_EmptyInput_ReturnsEmptyLists()
    {
        // "" → completed=[], incomplete=""
        var (completed, incomplete) = SplitSentences("");

        Assert.Empty(completed);
        Assert.Equal(string.Empty, incomplete);
    }

    [Fact]
    public void SplitSentences_ShortSentenceBelowThreshold_IsStillSplit()
    {
        // Short sentences (< SHORT_THRESHOLD=10 bytes) are still split correctly.
        // The SHORT_THRESHOLD logic is applied AFTER splitting, in the correction/new-sentence blocks.
        // "Hi. OK." → completed=["Hi.", "OK."], incomplete=""
        var (completed, incomplete) = SplitSentences("Hi. OK.");

        Assert.Equal(2, completed.Count);
        Assert.Equal("Hi.", completed[0]);
        Assert.Equal("OK.", completed[1]);
        Assert.Equal(string.Empty, incomplete);
    }

    [Fact]
    public void SplitSentences_CJPunctuation_SplitsAtCJEOS()
    {
        // Chinese/Japanese EOS punctuation (。？！) should also trigger splits
        var (completed, incomplete) = SplitSentences("你好。怎么样？");

        Assert.Equal(2, completed.Count);
        Assert.Equal("你好。", completed[0]);
        Assert.Equal("怎么样？", completed[1]);
        Assert.Equal(string.Empty, incomplete);
    }

    [Fact]
    public void SplitSentences_MultipleEOSInSequence_SplitsEach()
    {
        // "Really?! Yes." → completed=["Really?!","Yes."], incomplete=""
        var (completed, incomplete) = SplitSentences("Really?! Yes.");

        Assert.Equal(2, completed.Count);
        Assert.Equal("Really?!", completed[0]);
        Assert.Equal("Yes.", completed[1]);
    }

    [Fact]
    public void PUNC_EOS_ContainsAllExpectedCharacters()
    {
        // Verify PUNC_EOS contains . ? ! 。 ？ ！
        Assert.Contains('.', TextUtil.PUNC_EOS);
        Assert.Contains('?', TextUtil.PUNC_EOS);
        Assert.Contains('!', TextUtil.PUNC_EOS);
        Assert.Contains('。', TextUtil.PUNC_EOS);
        Assert.Contains('？', TextUtil.PUNC_EOS);
        Assert.Contains('！', TextUtil.PUNC_EOS);
        Assert.Equal(7, TextUtil.PUNC_EOS.Length);
    }

    [Fact]
    public void SHORT_THRESHOLD_Is10Bytes()
    {
        Assert.Equal(10, TextUtil.SHORT_THRESHOLD);
    }

    [Fact]
    public void LONG_THRESHOLD_Is160Bytes()
    {
        Assert.Equal(160, TextUtil.LONG_THRESHOLD);
    }
}