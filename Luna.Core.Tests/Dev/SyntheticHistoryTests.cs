using Luna.Core.Dev;
using Luna.Core.Storage;
using Luna.Protocol.Messaging;

namespace Luna.Core.Tests.Dev;

/// <summary>The fake history builder and the guard that keeps fake data away from the real history.</summary>
public class SyntheticHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static string RealPath => MessageStoreDefaults.DefaultPath;

    private static string DataFolder => Path.GetDirectoryName(RealPath)!;

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "luna-seed-test", "fake.db");

    private static List<ChatMessage> Regular(IReadOnlyList<ChatMessage> all) => all.Where(m => !m.IsUndo).ToList();

    private static List<ChatMessage> Notices(IReadOnlyList<ChatMessage> all) => all.Where(m => m.IsUndo).ToList();

    // ---- Build ----

    [Fact]
    public void Small_count_gives_exactly_that_many_regular_messages_and_no_notice()
    {
        IReadOnlyList<ChatMessage> all = SyntheticHistory.Build(5, Now);

        Assert.Equal(5, Regular(all).Count);
        Assert.Empty(Notices(all));
    }

    [Fact]
    public void Large_count_gives_that_many_regular_messages_and_one_notice_per_200()
    {
        IReadOnlyList<ChatMessage> all = SyntheticHistory.Build(20000, Now);

        Assert.Equal(20000, Regular(all).Count);
        Assert.Equal(100, Notices(all).Count);
    }

    [Fact]
    public void Zero_count_is_empty_and_negative_count_throws()
    {
        Assert.Empty(SyntheticHistory.Build(0, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => SyntheticHistory.Build(-1, Now));
    }

    [Fact]
    public void Regular_messages_are_one_minute_apart_in_order_and_end_at_now()
    {
        List<ChatMessage> regular = Regular(SyntheticHistory.Build(1000, Now));

        long[] stamps = regular.Select(m => long.Parse(m.Timestamp, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        for (int i = 1; i < stamps.Length; i++)
        {
            Assert.Equal(60_000, stamps[i] - stamps[i - 1]);
        }

        Assert.Equal(Now.ToUnixTimeMilliseconds(), stamps[^1]);
    }

    [Fact]
    public void Messages_use_three_fake_conversations_with_the_fake_prefix()
    {
        List<ChatMessage> regular = Regular(SyntheticHistory.Build(300, Now));

        string[] threads = regular.Select(m => m.ThreadId).Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            [SyntheticHistory.PrivateThreadA, SyntheticHistory.GroupThread, SyntheticHistory.PrivateThreadB],
            threads);
        Assert.All(threads, t => Assert.StartsWith(SyntheticHistory.ThreadPrefix, t, StringComparison.Ordinal));
        Assert.Equal(ThreadType.Group, regular.First(m => m.ThreadId == SyntheticHistory.GroupThread).ThreadType);
        Assert.Equal(ThreadType.User, regular.First(m => m.ThreadId == SyntheticHistory.PrivateThreadA).ThreadType);
        Assert.Equal(ThreadType.User, regular.First(m => m.ThreadId == SyntheticHistory.PrivateThreadB).ThreadType);
    }

    [Fact]
    public void Ids_are_unique_and_fake()
    {
        List<ChatMessage> regular = Regular(SyntheticHistory.Build(5000, Now));

        Assert.Equal(regular.Count, regular.Select(m => m.MsgId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(regular.Count, regular.Select(m => m.CliMsgId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(regular, m => Assert.StartsWith(SyntheticHistory.ThreadPrefix, m.MsgId, StringComparison.Ordinal));
    }

    [Fact]
    public void Text_and_names_are_ascii_with_multi_line_and_one_very_long_text()
    {
        IReadOnlyList<ChatMessage> all = SyntheticHistory.Build(1000, Now);
        List<ChatMessage> regular = Regular(all);

        Assert.All(regular, m => Assert.True(IsAscii(m.Text ?? ""), "non-ASCII text"));
        Assert.All(regular, m => Assert.True(IsAscii(m.DisplayName), "non-ASCII name"));
        Assert.All(regular, m => Assert.False(string.IsNullOrEmpty(m.Text)));
        Assert.Contains(regular, m => m.Text!.Contains('\n'));
        Assert.Equal(1, regular.Count(m => m.Text!.Length >= 2000));
        Assert.True(regular.Select(m => m.Text!.Length).Distinct().Count() > 5, "texts should vary in length");
        Assert.All(SyntheticHistory.ContactNames, c => Assert.True(IsAscii(c.Name)));
    }

    [Fact]
    public void Both_directions_and_group_senders_appear()
    {
        List<ChatMessage> regular = Regular(SyntheticHistory.Build(600, Now));

        Assert.Contains(regular, m => m.IsSelf);
        Assert.Contains(regular, m => !m.IsSelf);
        var groupSenders = regular
            .Where(m => m.ThreadId == SyntheticHistory.GroupThread && !m.IsSelf)
            .Select(m => m.UidFrom)
            .Distinct()
            .ToList();
        Assert.True(groupSenders.Count >= 2);
        Assert.All(groupSenders, s => Assert.StartsWith(SyntheticHistory.ThreadPrefix, s, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_notice_targets_an_existing_earlier_message_of_the_same_thread_once()
    {
        IReadOnlyList<ChatMessage> all = SyntheticHistory.Build(3000, Now);
        var targets = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < all.Count; i++)
        {
            if (!all[i].IsUndo)
            {
                continue;
            }

            var undo = Assert.IsType<UndoContent>(all[i].Content);
            ChatMessage target = Assert.Single(
                all.Take(i),
                m => !m.IsUndo && m.ThreadId == all[i].ThreadId && m.MsgId == undo.GlobalMsgId);
            Assert.Equal(target.CliMsgId, undo.CliMsgId);
            Assert.True(targets.Add(target.MsgId), "a message is recalled twice");
        }

        Assert.Equal(15, targets.Count);
    }

    [Fact]
    public void Same_count_and_now_give_the_same_history()
    {
        IReadOnlyList<ChatMessage> a = SyntheticHistory.Build(700, Now);
        IReadOnlyList<ChatMessage> b = SyntheticHistory.Build(700, Now);

        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].MsgId, b[i].MsgId);
            Assert.Equal(a[i].ThreadId, b[i].ThreadId);
            Assert.Equal(a[i].Timestamp, b[i].Timestamp);
            Assert.Equal(a[i].IsSelf, b[i].IsSelf);
            Assert.Equal(a[i].Text, b[i].Text);
            Assert.Equal(a[i].IsUndo, b[i].IsUndo);
        }
    }

    [Fact]
    public async Task Saving_the_history_stores_every_message_and_applies_every_notice()
    {
        using var store = new SqliteMessageStore(":memory:");
        IReadOnlyList<ChatMessage> all = SyntheticHistory.Build(1000, Now);

        int inserted = 0;
        int recalled = 0;
        int skipped = 0;
        for (int offset = 0; offset < all.Count; offset += 500)
        {
            StoreResult result = await store.SaveAsync(all.Skip(offset).Take(500).ToList());
            inserted += result.Inserted;
            recalled += result.Recalled;
            skipped += result.Skipped;
        }

        Assert.Equal(1000, inserted);
        Assert.Equal(5, recalled);
        Assert.Equal(0, skipped);
        IReadOnlyList<ConversationSummary> conversations = await store.ListConversationsAsync(50);
        Assert.Equal(SyntheticHistory.ConversationCount, conversations.Count);
    }

    // ---- CheckTarget ----

    private static SeedTargetCheck Check(string? path, bool force = false, bool exists = false) =>
        SyntheticHistory.CheckTarget(path, RealPath, force, _ => exists);

    [Fact]
    public void A_plain_temp_path_is_ok()
    {
        Assert.Equal(SeedTargetCheck.Ok, Check(TempFile()));
    }

    [Fact]
    public void The_exact_default_path_is_refused_with_or_without_force()
    {
        Assert.Equal(SeedTargetCheck.IsDefaultPath, Check(RealPath));
        Assert.Equal(SeedTargetCheck.IsDefaultPath, Check(RealPath, force: true));
        Assert.Equal(SeedTargetCheck.IsDefaultPath, Check(RealPath, force: true, exists: true));
    }

    [Fact]
    public void The_default_path_in_other_spellings_is_still_refused()
    {
        Assert.Equal(SeedTargetCheck.IsDefaultPath, Check(RealPath.ToUpperInvariant()));
        Assert.Equal(SeedTargetCheck.IsDefaultPath, Check(RealPath.ToLowerInvariant()));
        Assert.Equal(SeedTargetCheck.IsDefaultPath, Check(RealPath.Replace('\\', '/')));
        Assert.Equal(SeedTargetCheck.IsDefaultPath, Check(RealPath + " "));
        Assert.Equal(SeedTargetCheck.IsDefaultPath, Check(RealPath + "."));
        Assert.Equal(SeedTargetCheck.IsDefaultPath, Check(RealPath + ". ."));
    }

    [Fact]
    public void A_dot_dot_segment_that_resolves_into_the_data_folder_is_refused()
    {
        string folderName = Path.GetFileName(DataFolder);
        string sneaky = Path.Combine(DataFolder, "..", folderName, "history2.db");

        Assert.Equal(SeedTargetCheck.InsideDataFolder, Check(sneaky));
        Assert.Equal(SeedTargetCheck.IsDefaultPath, Check(Path.Combine(DataFolder, "..", folderName, "history.db")));
    }

    [Fact]
    public void Any_file_inside_the_data_folder_is_refused_even_with_force()
    {
        string sibling = Path.Combine(DataFolder, "history2.db");
        string deeper = Path.Combine(DataFolder, "sub", "fake.db");

        Assert.Equal(SeedTargetCheck.InsideDataFolder, Check(sibling));
        Assert.Equal(SeedTargetCheck.InsideDataFolder, Check(sibling, force: true));
        Assert.Equal(SeedTargetCheck.InsideDataFolder, Check(sibling, force: true, exists: true));
        Assert.Equal(SeedTargetCheck.InsideDataFolder, Check(deeper, force: true));
        Assert.Equal(SeedTargetCheck.InsideDataFolder, Check(sibling.ToUpperInvariant()));
        Assert.Equal(SeedTargetCheck.InsideDataFolder, Check(sibling.Replace('\\', '/')));
    }

    [Fact]
    public void A_relative_path_that_resolves_into_the_data_folder_is_refused()
    {
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(DataFolder, "history2.db"));

        Assert.Equal(SeedTargetCheck.InsideDataFolder, Check(relative));
    }

    [Fact]
    public void A_folder_with_a_similar_name_is_not_the_data_folder()
    {
        string lookalike = DataFolder + "-test" + Path.DirectorySeparatorChar + "fake.db";

        Assert.Equal(SeedTargetCheck.Ok, Check(lookalike));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad*name.db")]
    [InlineData("bad?name.db")]
    [InlineData("bad<name>.db")]
    public void Empty_blank_or_badly_named_paths_are_invalid(string? path)
    {
        Assert.Equal(SeedTargetCheck.Invalid, Check(path));
        Assert.Equal(SeedTargetCheck.Invalid, Check(path, force: true));
    }

    [Fact]
    public void A_path_with_a_nul_character_is_invalid()
    {
        Assert.Equal(SeedTargetCheck.Invalid, Check(Path.Combine(Path.GetTempPath(), "bad\0name.db")));
    }

    [Fact]
    public void A_directory_path_is_invalid()
    {
        string directory = Path.Combine(Path.GetTempPath(), "luna-seed-test-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Equal(SeedTargetCheck.Invalid, Check(directory));
            Assert.Equal(SeedTargetCheck.Invalid, Check(directory + Path.DirectorySeparatorChar));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void An_existing_file_needs_force()
    {
        Assert.Equal(SeedTargetCheck.ExistsWithoutForce, Check(TempFile(), force: false, exists: true));
        Assert.Equal(SeedTargetCheck.Ok, Check(TempFile(), force: true, exists: true));
        Assert.Equal(SeedTargetCheck.Ok, Check(TempFile(), force: false, exists: false));
    }

    [Fact]
    public void The_exists_check_gets_the_normalized_path_and_runs_only_for_acceptable_paths()
    {
        var asked = new List<string>();
        SyntheticHistory.CheckTarget(TempFile(), RealPath, false, p => { asked.Add(p); return false; });
        SyntheticHistory.CheckTarget(RealPath, RealPath, false, p => { asked.Add(p); return false; });

        Assert.Equal([Path.GetFullPath(TempFile())], asked);
    }

    // ---- AreAllThreadsSynthetic ----

    [Fact]
    public void Only_fake_thread_ids_count_as_synthetic()
    {
        Assert.True(SyntheticHistory.AreAllThreadsSynthetic([]));
        Assert.True(SyntheticHistory.AreAllThreadsSynthetic([SyntheticHistory.PrivateThreadA, SyntheticHistory.GroupThread]));
        Assert.False(SyntheticHistory.AreAllThreadsSynthetic([SyntheticHistory.PrivateThreadA, "12345"]));
        Assert.False(SyntheticHistory.AreAllThreadsSynthetic(["70"]));
        Assert.False(SyntheticHistory.AreAllThreadsSynthetic([""]));
        Assert.False(SyntheticHistory.AreAllThreadsSynthetic(["x7000"]));
    }

    private static bool IsAscii(string text) => text.All(c => c < 128);
}
