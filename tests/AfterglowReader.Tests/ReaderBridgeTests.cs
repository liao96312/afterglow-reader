using AfterglowReader.Reader;

namespace AfterglowReader.Tests;

public sealed class ReaderBridgeTests
{
    [Fact]
    public void Parse_ReaderPointerEnteredMessage()
    {
        var message = ReaderBridge.Parse("{\"type\":\"readerPointerEntered\"}");

        Assert.IsType<ReaderPointerEnteredMessage>(message);
    }

    [Fact]
    public void Parse_UnknownMessage_IsIgnored()
    {
        var message = ReaderBridge.Parse("{\"type\":\"futureMessage\"}");

        Assert.Null(message);
    }

    [Fact]
    public void Parse_ProgressChangedMessage_IncludesSequence()
    {
        var message = ReaderBridge.Parse("{\"type\":\"progressChanged\",\"paragraphId\":\"ch-2-p-4\",\"offset\":12.5,\"sequence\":42}");

        var progress = Assert.IsType<ProgressChangedMessage>(message);
        Assert.Equal("ch-2-p-4", progress.ParagraphId);
        Assert.Equal(12.5, progress.Offset);
        Assert.Equal(42, progress.Sequence);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"type\":\"requestWindow\",\"direction\":0,\"anchorOffset\":0}")]
    [InlineData("{\"type\":\"requestWindow\",\"direction\":1,\"anchorOffset\":\"bad\"}")]
    [InlineData("{\"type\":\"beginWindowResize\",\"edge\":\"center\"}")]
    [InlineData("{\"type\":\"progressChanged\",\"paragraphId\":\"p\",\"offset\":1e999,\"sequence\":1}")]
    public void Parse_InvalidMessage_IsIgnored(string json)
    {
        Assert.Null(ReaderBridge.Parse(json));
    }

    [Fact]
    public void Parse_RejectsOversizedIdentifiers()
    {
        var oversized = new string('x', 257);

        Assert.Null(ReaderBridge.Parse($"{{\"type\":\"selectChapter\",\"chapterId\":\"{oversized}\"}}"));
    }
}
