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
    /// <summary>The least distance from the top (in DIPs) at which an older page is asked for; see <see cref="LoadThreshold"/>.</summary>
    public const double MinLoadThresholdDips = 1200;

    /// <summary>How far from the end (in DIPs) still counts as "at the bottom".</summary>
    public const double BottomToleranceDips = 24;

    /// <summary>
    /// Fuse: most automatic loads in a row (without the user scrolling in between) while the content already fills the
    /// viewport and no pointer is held. If the scroll anchoring ever fails, at most this many pages are read instead of the
    /// whole conversation.
    /// </summary>
    public const int MaxChainWhenReading = 2;

    /// <summary>
    /// Fuse while the user holds the pointer on the list or its scrollbar: a held scrollbar thumb keeps the offset at the top
    /// and so cannot re-arm the chain, so the cap is much higher. Still finite in case the anchoring fails.
    /// </summary>
    public const int MaxChainWhenDragging = 40;

    /// <summary>The page size of the first automatic load of a chain.</summary>
    public const int BasePageSize = 50;

    /// <summary>The largest page size an automatic chain asks for.</summary>
    public const int MaxPageSize = 400;

    /// <summary>
    /// Fuse: most automatic loads in a row while the content does not fill the viewport (the fill case needs several pages
    /// when the messages are short).
    /// </summary>
    public const int MaxChainWhenFilling = 12;

    // The smallest offset change (in DIPs) that counts as a scroll; below it the difference is layout noise.
    private const double OffsetChangeDips = 0.5;

    /// <summary>
    /// How close to the top (in DIPs) the view must be before an older page is asked for, and the scrollable height at or
    /// below which the content counts as not filling the viewport: the larger of <see cref="MinLoadThresholdDips"/> and one
    /// and a half viewports. A viewport that is not a positive number gives the minimum.
    /// </summary>
    public static double LoadThreshold(double viewportHeight) =>
        viewportHeight > 0 ? Math.Max(MinLoadThresholdDips, 1.5 * viewportHeight) : MinLoadThresholdDips;

    /// <summary>
    /// The page size for the next automatic load of a chain: <paramref name="basePageSize"/> doubled once per automatic
    /// load already made, capped at <paramref name="maxPageSize"/>. A negative count counts as zero; any count is safe (no
    /// overflow).
    /// </summary>
    public static int NextPageSize(int consecutiveAutoLoads, int basePageSize = BasePageSize, int maxPageSize = MaxPageSize)
    {
        // Doubling stops as soon as the cap is reached, so the loop is short and a long never overflows for any counter.
        long size = Math.Max(1, basePageSize);
        for (int i = 0; i < consecutiveAutoLoads && size < maxPageSize; i++)
        {
            size *= 2;
        }

        return (int)Math.Min(maxPageSize, size);
    }

    /// <summary>
    /// True when an older page should be requested now. Never before the initial position is done, never while a load runs
    /// or when nothing older is known, and never for a viewport that has no size yet. Otherwise true when the view is within
    /// <see cref="LoadThreshold"/> of the top, or when the content hardly scrolls at all (a first page that does not fill
    /// the viewport). <paramref name="consecutiveAutoLoads"/> counts the automatic loads since the user last scrolled or
    /// released the pointer (a negative value counts as zero): the answer turns false at <see cref="MaxChainWhenReading"/>
    /// when the content fills the viewport (<see cref="MaxChainWhenDragging"/> while <paramref name="userIsDragging"/>) and
    /// at <see cref="MaxChainWhenFilling"/> when it does not.
    /// </summary>
    public static bool ShouldLoadOlder(
        bool initialPositionDone,
        double verticalOffset,
        double scrollableHeight,
        double viewportHeight,
        bool canLoadOlder,
        bool isLoadingOlder,
        int consecutiveAutoLoads,
        bool userIsDragging)
    {
        // "!(x > 0)" also rejects NaN.
        if (!initialPositionDone || !canLoadOlder || isLoadingOlder || !(viewportHeight > 0))
        {
            return false;
        }

        double threshold = LoadThreshold(viewportHeight);
        bool filling = scrollableHeight <= threshold;
        int cap = filling ? MaxChainWhenFilling : userIsDragging ? MaxChainWhenDragging : MaxChainWhenReading;
        if (consecutiveAutoLoads >= cap)
        {
            return false;
        }

        return verticalOffset <= threshold || filling;
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
