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
        bool loading = false,
        int chain = 0) =>
        ChatScrollPolicy.ShouldLoadOlder(positioned, offset, scrollable, viewport, canLoad, loading, chain);

    [Fact]
    public void Constants_are_the_agreed_values()
    {
        Assert.Equal(400, ChatScrollPolicy.LoadThresholdDips);
        Assert.Equal(24, ChatScrollPolicy.BottomToleranceDips);
        Assert.Equal(2, ChatScrollPolicy.MaxChainWhenReading);
        Assert.Equal(12, ChatScrollPolicy.MaxChainWhenFilling);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1000, false)]
    public void A_filled_viewport_stops_after_the_reading_chain_cap(int chain, bool expected)
    {
        // Far above the threshold of scrollable height: the viewport is filled, the user is near the top.
        Assert.Equal(expected, Should(offset: 10, scrollable: 20000, chain: chain));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(11, true)]
    [InlineData(12, false)]
    [InlineData(13, false)]
    [InlineData(int.MaxValue, false)]
    public void Content_that_does_not_fill_the_viewport_stops_after_the_filling_chain_cap(int chain, bool expected)
    {
        Assert.Equal(expected, Should(offset: 0, scrollable: 0, chain: chain));
        Assert.Equal(expected, Should(offset: 200, scrollable: 400, chain: chain));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(int.MinValue)]
    public void A_negative_chain_counts_as_zero(int chain)
    {
        Assert.True(Should(offset: 10, scrollable: 20000, chain: chain));
        Assert.True(Should(offset: 0, scrollable: 0, chain: chain));
        Assert.False(Should(offset: 5000, scrollable: 20000, chain: chain));
    }

    [Fact]
    public void The_caps_do_not_open_other_branches()
    {
        // At the cap nothing loads; below it the other conditions still decide.
        Assert.False(Should(offset: 5000, scrollable: 20000, chain: 0));
        Assert.False(Should(offset: 0, scrollable: 0, chain: 0, canLoad: false));
        Assert.False(Should(offset: 0, scrollable: 0, chain: 0, loading: true));
        Assert.False(Should(offset: 0, scrollable: 0, chain: 0, positioned: false));
        Assert.False(Should(offset: 0, scrollable: 0, chain: 0, viewport: 0));
    }

    [Theory]
    [InlineData(0, 100, false, true)]
    [InlineData(100, 0, false, true)]
    [InlineData(100, 100.5, false, true)]
    [InlineData(-5, 5, false, true)]
    [InlineData(100, 100, false, false)]
    [InlineData(0, 100, true, false)]
    [InlineData(100, 0, true, false)]
    [InlineData(double.NaN, 100, false, false)]
    [InlineData(100, double.NaN, false, false)]
    [InlineData(double.NaN, double.NaN, false, false)]
    public void A_user_scroll_is_a_real_offset_change_with_nothing_automatic_running(
        double previous,
        double current,
        bool automaticInFlight,
        bool expected)
    {
        Assert.Equal(expected, ChatScrollPolicy.IsUserScroll(previous, current, automaticInFlight));
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
        Assert.True(ChatScrollPolicy.ShouldLoadOlder(true, 900, 20000, 600, true, false, 0, thresholdDips: 1000));
        Assert.False(ChatScrollPolicy.ShouldLoadOlder(true, 150, 20000, 600, true, false, 0, thresholdDips: 100));
        Assert.True(ChatScrollPolicy.ShouldLoadOlder(true, 0, 20000, 600, true, false, 0, thresholdDips: 0));
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
