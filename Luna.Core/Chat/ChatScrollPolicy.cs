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
    /// True when an older page should be requested now. Never before the initial position is done, never while a load runs
    /// or when nothing older is known, and never for a viewport that has no size yet. Otherwise true when the view is within
    /// <paramref name="thresholdDips"/> of the top, or when the content hardly scrolls at all (a first page that does not
    /// fill the viewport).
    /// </summary>
    public static bool ShouldLoadOlder(
        bool initialPositionDone,
        double verticalOffset,
        double scrollableHeight,
        double viewportHeight,
        bool canLoadOlder,
        bool isLoadingOlder,
        double thresholdDips = LoadThresholdDips)
    {
        // "!(x > 0)" also rejects NaN.
        if (!initialPositionDone || !canLoadOlder || isLoadingOlder || !(viewportHeight > 0))
        {
            return false;
        }

        return verticalOffset <= thresholdDips || scrollableHeight <= thresholdDips;
    }

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
