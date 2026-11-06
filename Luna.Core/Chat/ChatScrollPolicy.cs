namespace Luna.Core.Chat;

/// <summary>How the list should anchor itself when items are added (maps to the panel's items-updating scroll mode).</summary>
public enum ItemsUpdatingMode
{
    /// <summary>Keep what the user is reading where it is (used for inserts at the top and for appends while reading higher up).</summary>
    KeepItemsInView,

    /// <summary>Follow the newest message (used for an append while the list is at the bottom).</summary>
    KeepLastItemInView,
}

/// <summary>
/// Pure scroll decisions of the chat pane, only numbers and flags (device independent pixels), so they are testable without a
/// window. Nothing here reads the view or the data.
/// </summary>
public static class ChatScrollPolicy
{
    /// <summary>How close to the top (in DIPs) the view must be before an older page is asked for.</summary>
    public const double LoadThresholdDips = 400;

    /// <summary>How far from the end (in DIPs) still counts as "at the bottom".</summary>
    public const double BottomToleranceDips = 24;

    /// <summary>
    /// Fuse: most automatic loads in a row (without the user scrolling in between) while the content already fills the
    /// viewport. If the scroll anchoring ever fails, at most this many pages are read instead of the whole conversation.
    /// </summary>
    public const int MaxChainWhenReading = 2;

    /// <summary>
    /// Fuse: most automatic loads in a row while the content does not fill the viewport (the fill case needs several pages
    /// when the messages are short).
    /// </summary>
    public const int MaxChainWhenFilling = 12;

    // The smallest offset change (in DIPs) that counts as a scroll; below it the difference is layout noise.
    private const double OffsetChangeDips = 0.5;

    /// <summary>
    /// True when an older page should be requested now. Never before the initial position is done, never while a load runs
    /// or when nothing older is known, and never for a viewport that has no size yet. Otherwise true when the view is within
    /// <paramref name="thresholdDips"/> of the top, or when the content hardly scrolls at all (a first page that does not
    /// fill the viewport). <paramref name="consecutiveAutoLoads"/> counts the automatic loads since the user last scrolled
    /// (a negative value counts as zero): the answer turns false at <see cref="MaxChainWhenReading"/> when the content
    /// fills the viewport and at <see cref="MaxChainWhenFilling"/> when it does not.
    /// </summary>
    public static bool ShouldLoadOlder(
        bool initialPositionDone,
        double verticalOffset,
        double scrollableHeight,
        double viewportHeight,
        bool canLoadOlder,
        bool isLoadingOlder,
        int consecutiveAutoLoads,
        double thresholdDips = LoadThresholdDips)
    {
        // "!(x > 0)" also rejects NaN.
        if (!initialPositionDone || !canLoadOlder || isLoadingOlder || !(viewportHeight > 0))
        {
            return false;
        }

        bool filling = scrollableHeight <= thresholdDips;
        if (consecutiveAutoLoads >= (filling ? MaxChainWhenFilling : MaxChainWhenReading))
        {
            return false;
        }

        return verticalOffset <= thresholdDips || filling;
    }

    /// <summary>
    /// True when a change of the vertical offset came from the user: the offset really moved (NaN never counts) and no
    /// automatic load or positioning is running. The view resets its chain counter on it.
    /// </summary>
    public static bool IsUserScroll(double previousOffset, double currentOffset, bool automaticWorkInFlight) =>
        !automaticWorkInFlight && Math.Abs(currentOffset - previousOffset) >= OffsetChangeDips;

    /// <summary>True when the view is at the end of the content, within <paramref name="toleranceDips"/>.</summary>
    public static bool IsAtBottom(double verticalOffset, double scrollableHeight, double toleranceDips = BottomToleranceDips) =>
        scrollableHeight - verticalOffset <= toleranceDips;

    /// <summary>
    /// How the list anchors itself for the change about to be mirrored: an insert at the top and an append while reading
    /// higher up keep the items in view; an append while at the bottom follows the last item.
    /// </summary>
    public static ItemsUpdatingMode Decide(bool insertedAtTop, bool wasAtBottom) =>
        !insertedAtTop && wasAtBottom ? ItemsUpdatingMode.KeepLastItemInView : ItemsUpdatingMode.KeepItemsInView;
}
