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
}
