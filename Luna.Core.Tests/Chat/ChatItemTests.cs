using Luna.Core.Chat;

namespace Luna.Core.Tests.Chat;

/// <summary>Small pure helpers of the chat pane: short ids, content comparison, keys and the merge lower bound.</summary>
public sealed class ChatItemTests
{
    [Theory]
    [InlineData("fake-uid-1234", "1234")]
    [InlineData("ab", "ab")]
    [InlineData("abcd", "abcd")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ShortId_is_the_last_four_characters(string? id, string expected)
    {
        Assert.Equal(expected, ChatIds.ShortId(id));
    }

    [Fact]
    public void ShortId_never_cuts_a_surrogate_pair_in_half()
    {
        // The start index (3) falls on the low surrogate of the pair, so the result starts after it.
        string cut = ChatIds.ShortId("ab😀cde");
        // The start index (3) falls on a plain character.
        string whole = ChatIds.ShortId("abc😀de");

        Assert.Equal("cde", cut);
        Assert.Equal("😀de", whole);
    }

    private static MessageItem Item(
        long id = 1, bool self = false, string? name = "n", string shortId = "ab", long ts = 100, MessageKind kind = MessageKind.Text,
        string? text = "t") => new(id, self, name, shortId, ts, kind, text);

    [Fact]
    public void Equal_values_in_different_objects_have_the_same_content()
    {
        Assert.True(Item().HasSameContentAs(Item()));
    }

    [Fact]
    public void Every_shown_value_counts_for_the_content()
    {
        Assert.False(Item().HasSameContentAs(Item(id: 2)));
        Assert.False(Item().HasSameContentAs(Item(self: true)));
        Assert.False(Item().HasSameContentAs(Item(name: "other")));
        Assert.False(Item().HasSameContentAs(Item(name: null)));
        Assert.False(Item().HasSameContentAs(Item(shortId: "cd")));
        Assert.False(Item().HasSameContentAs(Item(ts: 101)));
        Assert.False(Item().HasSameContentAs(Item(kind: MessageKind.Recalled)));
        Assert.False(Item().HasSameContentAs(Item(text: "other")));
        Assert.False(Item().HasSameContentAs(Item(text: null)));
        Assert.False(Item().HasSameContentAs(null));
    }

    [Fact]
    public void The_key_is_the_timestamp_and_the_id()
    {
        Assert.Equal(new MessageKey(100, 7), Item(id: 7, ts: 100).Key);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 0)]
    [InlineData(100, 0)]
    [InlineData(150, 1)]
    [InlineData(300, 2)]
    [InlineData(301, 3)]
    public void LowerBound_is_the_first_index_not_before_the_key(long ts, int expected)
    {
        MessageKey[] list = [new(100, 1), new(200, 2), new(300, 3)];

        Assert.Equal(expected, ChatMerge.LowerBound(list, new MessageKey(ts, 0)));
    }

    [Fact]
    public void LowerBound_orders_equal_timestamps_by_id_and_handles_an_empty_list()
    {
        MessageKey[] list = [new(100, 1), new(100, 5)];

        Assert.Equal(1, ChatMerge.LowerBound(list, new MessageKey(100, 2)));
        Assert.Equal(1, ChatMerge.LowerBound(list, new MessageKey(100, 5)));
        Assert.Equal(0, ChatMerge.LowerBound([], new MessageKey(100, 5)));
    }
}
