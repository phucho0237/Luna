namespace Luna.Core.Chat;

/// <summary>The kind of one step of a <see cref="ChatMergePlan"/>.</summary>
public enum ChatMergeKind
{
    /// <summary>The row at the index is the same message with new content: replace it in place.</summary>
    Replace,

    /// <summary>A message that is not shown yet: insert it at the index.</summary>
    Insert,
}

/// <summary>
/// One step. <see cref="Index"/> is the position in the list AFTER the earlier steps of the same plan were applied, so the
/// steps are applied in order, one after the other.
/// </summary>
public readonly record struct ChatMergeOperation(ChatMergeKind Kind, int Index, MessageKey Key)
{
    /// <summary>Prints the kind and the index only (the key is two numbers, but the guard wants no data in a ToString).</summary>
    public override string ToString() => $"{Kind} at {Index}";
}

/// <summary>
/// What to do with the shown list: apply <see cref="Operations"/> in order, or, when <see cref="IsReset"/> is true,
/// throw the list away and load again (the steps are then empty).
/// </summary>
public sealed record ChatMergePlan(bool IsReset, IReadOnlyList<ChatMergeOperation> Operations)
{
    /// <summary>The reset plan.</summary>
    public static ChatMergePlan Reset { get; } = new(true, []);

    /// <summary>Prints the reset flag and the number of steps only.</summary>
    public override string ToString() => $"ChatMergePlan {{ IsReset = {IsReset}, Operations = {Operations.Count} }}";
}

/// <summary>
/// Pure merge of the newest page of a conversation into the list that is already shown. It works on the store sort key
/// (timestamp, id), never on ids alone: a late message with an older timestamp belongs in the middle.
/// </summary>
public static class ChatMerge
{
    /// <summary>
    /// Plans the merge of <paramref name="page"/> (the newest page) into <paramref name="current"/>; both are ascending by
    /// key without duplicates. <paramref name="changed"/> holds the keys whose content differs from what is shown; only
    /// those are replaced, so an unchanged row costs no operation.
    /// </summary>
    /// <remarks>
    /// Rules: a key of the page that is not shown is inserted at its sorted position (the page is the newest slice of the
    /// store, so everything newer than its oldest key is in it and nothing can be missing between). A page that shares no
    /// key with a NON-EMPTY list and lies entirely after its last key may hide a gap (a burst larger than one page): the
    /// plan is a reset. A page that lies entirely before the first shown key cannot be placed without a possible gap and
    /// the store cannot hold newer messages than it: no operation. Removed messages are never removed from the list.
    /// </remarks>
    public static ChatMergePlan Plan(
        IReadOnlyList<MessageKey> current, IReadOnlyList<MessageKey> page, IReadOnlySet<MessageKey> changed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(changed);

        if (page.Count == 0)
        {
            return new ChatMergePlan(false, []);
        }

        if (current.Count > 0)
        {
            HashSet<MessageKey> shown = [.. current];
            bool overlap = false;
            foreach (MessageKey key in page)
            {
                if (shown.Contains(key))
                {
                    overlap = true;
                    break;
                }
            }

            if (!overlap)
            {
                if (page[0] > current[^1])
                {
                    // Everything in the page is newer than everything shown: messages between may be missing.
                    return ChatMergePlan.Reset;
                }

                if (page[^1] < current[0])
                {
                    // The store cannot hold newer messages than the list shows; do not guess.
                    return new ChatMergePlan(false, []);
                }
            }
        }

        // Merge the two ascending lists. "position" is the index in the list as it is after the steps already planned.
        var operations = new List<ChatMergeOperation>();
        int currentIndex = 0;
        int position = 0;
        foreach (MessageKey key in page)
        {
            while (currentIndex < current.Count && current[currentIndex] < key)
            {
                currentIndex++;
                position++;
            }

            if (currentIndex < current.Count && current[currentIndex] == key)
            {
                if (changed.Contains(key))
                {
                    operations.Add(new ChatMergeOperation(ChatMergeKind.Replace, position, key));
                }

                currentIndex++;
            }
            else
            {
                operations.Add(new ChatMergeOperation(ChatMergeKind.Insert, position, key));
            }

            position++;
        }

        return new ChatMergePlan(false, operations);
    }
}
