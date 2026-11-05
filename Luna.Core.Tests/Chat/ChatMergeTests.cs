using Luna.Core.Chat;

namespace Luna.Core.Tests.Chat;

public sealed class ChatMergeTests
{
    private static readonly IReadOnlySet<MessageKey> None = new HashSet<MessageKey>();

    private static MessageKey K(long ts, long id) => new(ts, id);

    private static IReadOnlySet<MessageKey> Changed(params MessageKey[] keys) => new HashSet<MessageKey>(keys);

    // Applies a plan the way the view model does: in order, on a plain list.
    private static List<MessageKey> Apply(IReadOnlyList<MessageKey> current, ChatMergePlan plan)
    {
        Assert.False(plan.IsReset, "A reset plan cannot be applied in place.");
        var list = new List<MessageKey>(current);
        foreach (ChatMergeOperation op in plan.Operations)
        {
            if (op.Kind == ChatMergeKind.Replace)
            {
                Assert.Equal(op.Key, list[op.Index]);
            }
            else
            {
                list.Insert(op.Index, op.Key);
            }
        }

        return list;
    }

    [Fact]
    public void One_new_message_is_appended_at_the_end()
    {
        MessageKey[] current = [K(100, 1), K(200, 2)];
        MessageKey[] page = [K(100, 1), K(200, 2), K(300, 3)];

        ChatMergePlan plan = ChatMerge.Plan(current, page, None);

        Assert.False(plan.IsReset);
        Assert.Equal([new ChatMergeOperation(ChatMergeKind.Insert, 2, K(300, 3))], plan.Operations);
    }

    [Fact]
    public void Several_new_messages_are_appended_in_order()
    {
        MessageKey[] current = [K(100, 1), K(200, 2)];
        MessageKey[] page = [K(200, 2), K(300, 3), K(400, 4), K(500, 5)];

        ChatMergePlan plan = ChatMerge.Plan(current, page, None);

        Assert.Equal(
            [
                new ChatMergeOperation(ChatMergeKind.Insert, 2, K(300, 3)),
                new ChatMergeOperation(ChatMergeKind.Insert, 3, K(400, 4)),
                new ChatMergeOperation(ChatMergeKind.Insert, 4, K(500, 5)),
            ],
            plan.Operations);
        Assert.Equal([K(100, 1), K(200, 2), K(300, 3), K(400, 4), K(500, 5)], Apply(current, plan));
    }

    [Fact]
    public void A_late_message_with_an_older_timestamp_and_the_highest_id_is_inserted_in_the_middle()
    {
        MessageKey[] current = [K(100, 1), K(200, 2), K(300, 3)];
        MessageKey[] page = [K(100, 1), K(150, 9), K(200, 2), K(300, 3)];

        ChatMergePlan plan = ChatMerge.Plan(current, page, None);

        Assert.Equal([new ChatMergeOperation(ChatMergeKind.Insert, 1, K(150, 9))], plan.Operations);
    }

    [Fact]
    public void Equal_timestamps_are_ordered_by_id()
    {
        MessageKey[] current = [K(100, 1), K(100, 5)];
        MessageKey[] page = [K(100, 1), K(100, 3), K(100, 5), K(100, 7)];

        ChatMergePlan plan = ChatMerge.Plan(current, page, None);

        Assert.Equal(
            [
                new ChatMergeOperation(ChatMergeKind.Insert, 1, K(100, 3)),
                new ChatMergeOperation(ChatMergeKind.Insert, 3, K(100, 7)),
            ],
            plan.Operations);
        Assert.Equal(page, Apply(current, plan));
    }

    [Fact]
    public void A_changed_message_is_replaced_in_place_without_moving()
    {
        MessageKey[] current = [K(100, 1), K(200, 2), K(300, 3)];

        ChatMergePlan plan = ChatMerge.Plan(current, current, Changed(K(200, 2)));

        Assert.Equal([new ChatMergeOperation(ChatMergeKind.Replace, 1, K(200, 2))], plan.Operations);
        Assert.Equal(current, Apply(current, plan));
    }

    [Fact]
    public void A_replace_and_an_insert_in_one_plan_use_the_indexes_after_the_earlier_steps()
    {
        MessageKey[] current = [K(100, 1), K(200, 2)];
        MessageKey[] page = [K(50, 9), K(100, 1), K(200, 2), K(300, 3)];

        ChatMergePlan plan = ChatMerge.Plan(current, page, Changed(K(200, 2)));

        Assert.Equal(
            [
                new ChatMergeOperation(ChatMergeKind.Insert, 0, K(50, 9)),
                new ChatMergeOperation(ChatMergeKind.Replace, 2, K(200, 2)),
                new ChatMergeOperation(ChatMergeKind.Insert, 3, K(300, 3)),
            ],
            plan.Operations);
        Assert.Equal(page, Apply(current, plan));
    }

    [Fact]
    public void A_changed_key_that_is_not_shown_is_inserted_not_replaced()
    {
        MessageKey[] current = [K(100, 1)];
        MessageKey[] page = [K(100, 1), K(200, 2)];

        ChatMergePlan plan = ChatMerge.Plan(current, page, Changed(K(200, 2)));

        Assert.Equal([new ChatMergeOperation(ChatMergeKind.Insert, 1, K(200, 2))], plan.Operations);
    }

    [Fact]
    public void Nothing_new_and_nothing_changed_is_no_operation()
    {
        MessageKey[] current = [K(100, 1), K(200, 2)];

        ChatMergePlan plan = ChatMerge.Plan(current, current, None);

        Assert.False(plan.IsReset);
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void An_empty_page_is_no_operation()
    {
        ChatMergePlan plan = ChatMerge.Plan([K(100, 1)], [], None);

        Assert.False(plan.IsReset);
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void An_empty_current_list_takes_the_whole_page_without_a_reset()
    {
        MessageKey[] page = [K(100, 1), K(200, 2)];

        ChatMergePlan plan = ChatMerge.Plan([], page, None);

        Assert.False(plan.IsReset);
        Assert.Equal(
            [
                new ChatMergeOperation(ChatMergeKind.Insert, 0, K(100, 1)),
                new ChatMergeOperation(ChatMergeKind.Insert, 1, K(200, 2)),
            ],
            plan.Operations);
    }

    [Fact]
    public void Both_lists_empty_is_no_operation()
    {
        ChatMergePlan plan = ChatMerge.Plan([], [], None);

        Assert.False(plan.IsReset);
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void A_burst_larger_than_one_page_is_a_reset()
    {
        MessageKey[] current = [K(100, 1), K(200, 2)];
        MessageKey[] page = [K(300, 3), K(400, 4)];

        ChatMergePlan plan = ChatMerge.Plan(current, page, None);

        Assert.True(plan.IsReset);
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void A_page_that_starts_right_after_the_last_key_with_no_overlap_is_still_a_reset()
    {
        // Exactly one page of new messages: it cannot be told apart from a larger burst, so it is not trusted.
        ChatMergePlan plan = ChatMerge.Plan([K(100, 1)], [K(100, 2), K(100, 3)], None);

        Assert.True(plan.IsReset);
    }

    [Fact]
    public void A_page_entirely_older_than_the_shown_list_is_no_operation()
    {
        MessageKey[] current = [K(300, 3), K(400, 4)];
        MessageKey[] page = [K(100, 1), K(200, 2)];

        ChatMergePlan plan = ChatMerge.Plan(current, page, None);

        Assert.False(plan.IsReset);
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void A_page_with_no_overlap_that_falls_inside_the_shown_range_is_inserted()
    {
        MessageKey[] current = [K(100, 1), K(400, 4)];
        MessageKey[] page = [K(200, 2), K(300, 3)];

        ChatMergePlan plan = ChatMerge.Plan(current, page, None);

        Assert.False(plan.IsReset);
        Assert.Equal([K(100, 1), K(200, 2), K(300, 3), K(400, 4)], Apply(current, plan));
    }

    [Fact]
    public void Random_inputs_always_give_the_sorted_union_or_one_of_the_two_documented_exceptions()
    {
        var random = new Random(20261105);
        for (int round = 0; round < 2000; round++)
        {
            var universe = new List<MessageKey>();
            int size = random.Next(1, 40);
            for (int i = 0; i < size; i++)
            {
                // Few distinct timestamps so that ties are common; the id is unique.
                universe.Add(K(random.Next(1, 8) * 100, i + 1));
            }

            universe.Sort();
            MessageKey[] current = universe.Where(_ => random.Next(3) > 0).ToArray();
            MessageKey[] page = universe.Where(_ => random.Next(3) > 0).ToArray();
            IReadOnlySet<MessageKey> changed = new HashSet<MessageKey>(page.Where(_ => random.Next(4) == 0));

            ChatMergePlan plan = ChatMerge.Plan(current, page, changed);

            bool overlap = current.Intersect(page).Any();
            if (plan.IsReset)
            {
                Assert.True(current.Length > 0 && page.Length > 0 && !overlap && page[0] > current[^1], "Unexpected reset in round " + round);
                continue;
            }

            List<MessageKey> result = Apply(current, plan);
            bool entirelyOlder = current.Length > 0 && page.Length > 0 && !overlap && page[^1] < current[0];
            MessageKey[] expected = entirelyOlder ? current : current.Union(page).Order().ToArray();
            Assert.True(expected.SequenceEqual(result), "Wrong result in round " + round);

            // Only shown AND changed keys are replaced, and no step ever removes anything.
            foreach (ChatMergeOperation op in plan.Operations.Where(o => o.Kind == ChatMergeKind.Replace))
            {
                Assert.Contains(op.Key, changed);
                Assert.Contains(op.Key, current);
            }
        }
    }
}
