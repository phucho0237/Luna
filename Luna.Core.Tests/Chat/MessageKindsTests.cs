using Luna.Core.Chat;
using Luna.Protocol.Messaging;

namespace Luna.Core.Tests.Chat;

public sealed class MessageKindsTests
{
    [Fact]
    public void A_text_row_is_Text()
    {
        Assert.Equal(MessageKind.Text, MessageKinds.Classify(MessageTypes.Text, recalled: false, hasText: true));
    }

    [Theory]
    [InlineData(MessageTypes.Text)]
    [InlineData(MessageTypes.Sticker)]
    [InlineData(MessageTypes.Photo)]
    [InlineData(MessageTypes.File)]
    [InlineData(MessageTypes.Undo)]
    [InlineData("something.new")]
    [InlineData("")]
    public void A_recalled_row_is_Recalled_whatever_its_type_or_text(string msgType)
    {
        Assert.Equal(MessageKind.Recalled, MessageKinds.Classify(msgType, recalled: true, hasText: false));
        Assert.Equal(MessageKind.Recalled, MessageKinds.Classify(msgType, recalled: true, hasText: true));
    }

    [Theory]
    [InlineData(MessageTypes.Sticker)]
    [InlineData(MessageTypes.Photo)]
    [InlineData(MessageTypes.File)]
    [InlineData("something.new")]
    public void A_row_with_text_is_Text_whatever_its_type(string msgType)
    {
        Assert.Equal(MessageKind.Text, MessageKinds.Classify(msgType, recalled: false, hasText: true));
    }

    [Fact]
    public void A_sticker_without_text_is_Sticker()
    {
        Assert.Equal(MessageKind.Sticker, MessageKinds.Classify(MessageTypes.Sticker, recalled: false, hasText: false));
    }

    [Fact]
    public void A_photo_without_text_is_Image()
    {
        Assert.Equal(MessageKind.Image, MessageKinds.Classify(MessageTypes.Photo, recalled: false, hasText: false));
    }

    [Fact]
    public void A_file_without_text_is_File()
    {
        Assert.Equal(MessageKind.File, MessageKinds.Classify(MessageTypes.File, recalled: false, hasText: false));
    }

    [Theory]
    [InlineData(MessageTypes.Text)]
    [InlineData(MessageTypes.Undo)]
    [InlineData("something.new")]
    [InlineData("")]
    [InlineData("CHAT.STICKER")]
    public void Unknown_types_and_text_types_without_text_are_Other(string msgType)
    {
        Assert.Equal(MessageKind.Other, MessageKinds.Classify(msgType, recalled: false, hasText: false));
    }

    [Fact]
    public void A_null_type_is_Other_and_does_not_throw()
    {
        Assert.Equal(MessageKind.Other, MessageKinds.Classify(null!, recalled: false, hasText: false));
    }

    [Fact]
    public void MessageItem_keeps_its_values()
    {
        var item = new MessageItem(7, true, "Fake Name", "ab12", 1700000000000, MessageKind.Text, "fake text");

        Assert.Equal(7, item.Id);
        Assert.True(item.IsSelf);
        Assert.Equal("Fake Name", item.SenderName);
        Assert.Equal("ab12", item.SenderShortId);
        Assert.Equal(1700000000000, item.TimestampMs);
        Assert.Equal(MessageKind.Text, item.Kind);
        Assert.Equal("fake text", item.Text);
    }

    [Fact]
    public void MessageItem_ToString_never_prints_the_text_or_the_names()
    {
        var item = new MessageItem(7, false, "Fake Name Marker", "ab12", 1, MessageKind.Text, "fake-text-marker");

        string text = item.ToString();

        Assert.DoesNotContain("fake-text-marker", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Fake Name Marker", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ab12", text, StringComparison.Ordinal);
    }
}
