using Luna.Core.Chat;

namespace Luna.Core.Tests.Chat;

public class ChatScrollPolicyTests
{
    // Positioned, can load, not loading, a normal viewport: the base case the tests vary one input of.
    private static bool Should(
        double offset = 5000,
        double scrollable = 20000,
        double viewport = 600,
        bool positioned = true,
        bool canLoad = true,
        bool loading = false) =>
        ChatScrollPolicy.ShouldLoadOlder(positioned, offset, scrollable, viewport, canLoad, loading);

    [Fact]
    public void Constants_are_the_agreed_values()
    {
        Assert.Equal(400, ChatScrollPolicy.LoadThresholdDips);
        Assert.Equal(24, ChatScrollPolicy.BottomToleranceDips);
    }

    [Fact]
    public void Far_from_the_top_with_a_long_content_does_not_load()
    {
        Assert.False(Should(offset: 5000, scrollable: 20000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(399.9)]
    [InlineData(400)]
    public void Within_the_threshold_of_the_top_loads(double offset)
    {
        Assert.True(Should(offset: offset, scrollable: 20000));
    }

    [Theory]
    [InlineData(400.1)]
    [InlineData(401)]
    [InlineData(10000)]
    public void Beyond_the_threshold_does_not_load(double offset)
    {
        Assert.False(Should(offset: offset, scrollable: 20000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    [InlineData(400)]
    public void A_first_page_that_does_not_fill_the_viewport_loads_even_when_scrolled(double scrollable)
    {
        // Content hardly scrolls: the offset can be anything within it, the page must still be filled.
        Assert.True(Should(offset: scrollable, scrollable: scrollable));
    }

    [Fact]
    public void Content_that_scrolls_just_over_the_threshold_and_is_read_far_down_does_not_load()
    {
        Assert.False(Should(offset: 450, scrollable: 450.1));
    }

    [Fact]
    public void Nothing_older_to_load_never_loads()
    {
        Assert.False(Should(offset: 0, scrollable: 0, canLoad: false));
        Assert.False(Should(offset: 0, scrollable: 20000, canLoad: false));
    }

    [Fact]
    public void A_load_in_progress_never_loads_again()
    {
        Assert.False(Should(offset: 0, scrollable: 20000, loading: true));
        Assert.False(Should(offset: 0, scrollable: 0, loading: true));
    }

    [Theory]
    [InlineData(0, 20000)]
    [InlineData(0, 0)]
    [InlineData(100, 300)]
    [InlineData(5000, 20000)]
    public void Before_the_initial_position_is_done_nothing_loads(double offset, double scrollable)
    {
        Assert.False(Should(offset: offset, scrollable: scrollable, positioned: false));
    }

    [Fact]
    public void Position_done_behaves_as_without_the_gate()
    {
        Assert.True(Should(offset: 10, scrollable: 20000, positioned: true));
        Assert.False(Should(offset: 5000, scrollable: 20000, positioned: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void A_viewport_without_a_size_never_loads(double viewport)
    {
        // The view has not been laid out yet (or reports nonsense): there is nothing to judge.
        Assert.False(Should(offset: 0, scrollable: 0, viewport: viewport));
    }

    [Theory]
    [InlineData(-5, 20000)]
    [InlineData(0, -10)]
    [InlineData(-1, -1)]
    public void Negative_offsets_and_heights_count_as_the_top(double offset, double scrollable)
    {
        Assert.True(Should(offset: offset, scrollable: scrollable));
    }

    [Fact]
    public void A_custom_threshold_is_used()
    {
        Assert.True(ChatScrollPolicy.ShouldLoadOlder(true, 900, 20000, 600, true, false, thresholdDips: 1000));
        Assert.False(ChatScrollPolicy.ShouldLoadOlder(true, 150, 20000, 600, true, false, thresholdDips: 100));
        Assert.True(ChatScrollPolicy.ShouldLoadOlder(true, 0, 20000, 600, true, false, thresholdDips: 0));
    }

    [Theory]
    [InlineData(20000, 20000)]
    [InlineData(19976, 20000)]
    [InlineData(19999.5, 20000)]
    [InlineData(0, 0)]
    [InlineData(0, 10)]
    [InlineData(-3, -3)]
    public void At_the_bottom_within_the_tolerance(double offset, double scrollable)
    {
        Assert.True(ChatScrollPolicy.IsAtBottom(offset, scrollable));
    }

    [Theory]
    [InlineData(19975.9, 20000)]
    [InlineData(10000, 20000)]
    [InlineData(0, 20000)]
    [InlineData(0, 25)]
    public void Not_at_the_bottom_beyond_the_tolerance(double offset, double scrollable)
    {
        Assert.False(ChatScrollPolicy.IsAtBottom(offset, scrollable));
    }

    [Fact]
    public void A_custom_tolerance_is_used()
    {
        Assert.True(ChatScrollPolicy.IsAtBottom(19900, 20000, toleranceDips: 100));
        Assert.False(ChatScrollPolicy.IsAtBottom(19999, 20000, toleranceDips: 0));
        Assert.True(ChatScrollPolicy.IsAtBottom(20000, 20000, toleranceDips: 0));
    }

    [Fact]
    public void An_offset_past_the_end_is_at_the_bottom()
    {
        Assert.True(ChatScrollPolicy.IsAtBottom(20500, 20000));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void An_insert_at_the_top_keeps_the_items_in_view(bool insertedAtTop, bool wasAtBottom)
    {
        Assert.True(insertedAtTop);
        Assert.Equal(ItemsUpdatingMode.KeepItemsInView, ChatScrollPolicy.Decide(insertedAtTop, wasAtBottom));
    }

    [Fact]
    public void An_append_while_at_the_bottom_follows_the_last_item()
    {
        Assert.Equal(ItemsUpdatingMode.KeepLastItemInView, ChatScrollPolicy.Decide(insertedAtTop: false, wasAtBottom: true));
    }

    [Fact]
    public void An_append_while_reading_higher_up_keeps_the_items_in_view()
    {
        Assert.Equal(ItemsUpdatingMode.KeepItemsInView, ChatScrollPolicy.Decide(insertedAtTop: false, wasAtBottom: false));
    }
}
