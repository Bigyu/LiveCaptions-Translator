using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests;

public class CaptionTests
{
    [Fact]
    public void GetInstance_ShouldNotBeNull()
    {
        var caption = Caption.GetInstance();
        Assert.NotNull(caption);
    }
}