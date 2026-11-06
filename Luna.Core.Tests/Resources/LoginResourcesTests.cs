using System.Xml.Linq;
using Luna.Core.Chat;
using Luna.Core.Friends;
using Luna.Core.Realtime;
using Luna.Core.ViewModels;

namespace Luna.Core.Tests.Resources;

/// <summary>
/// Guards the Vietnamese resource file of Luna.App: every login state and failure, and every live sync state, friends
/// state and friends failure of the home page, has its text, and the file has no duplicate keys, empty values or old
/// brand word. Luna.App cannot be referenced from here, so the file is parsed as XML.
/// </summary>
public class LoginResourcesTests
{
    // Built from pieces so this file never contains the forbidden word literally (same trick as scripts/check-language.ps1).
    private static readonly string Brand = "za" + "lo";

    private static readonly Lazy<List<(string Name, string Value)>> Entries = new(LoadEntries);

    [Fact]
    public void Every_login_state_has_a_text()
    {
        string[] missing = Enum.GetNames<LoginState>()
            .Select(name => "Login_State_" + name)
            .Where(key => Entries.Value.All(e => e.Name != key))
            .ToArray();

        Assert.True(missing.Length == 0, "Missing keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_login_failure_has_a_text()
    {
        string[] missing = Enum.GetNames<LoginFailure>()
            .Select(name => "Login_Failure_" + name)
            .Where(key => Entries.Value.All(e => e.Name != key))
            .ToArray();

        Assert.True(missing.Length == 0, "Missing keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_server_failure_text_has_a_slot_for_the_numeric_code()
    {
        var entry = Entries.Value.Single(e => e.Name == "Login_Failure_Server");

        Assert.Contains("{0}", entry.Value);
    }

    // ---- Home page ----------------------------------------------------------------------------------------------

    [Fact]
    public void Every_live_sync_state_has_a_text_without_a_slot()
    {
        AssertKeysWithoutSlot(Enum.GetNames<LiveSyncState>().Select(name => "Home_Connection_" + name));
    }

    [Fact]
    public void Every_friends_state_has_a_text_and_only_the_waiting_states_take_minutes()
    {
        string[] withMinutes = [nameof(FriendsState.Throttled), nameof(FriendsState.CoolingDown)];

        AssertKeysWithoutSlot(Enum.GetNames<FriendsState>()
            .Where(name => !withMinutes.Contains(name))
            .Select(name => "Home_Friends_" + name));
        AssertKeysWithSlot(withMinutes.Select(name => "Home_Friends_" + name));
    }

    [Fact]
    public void Every_friends_failure_has_a_text_and_only_the_server_failure_takes_the_code()
    {
        AssertKeysWithoutSlot(Enum.GetNames<FriendsFailure>()
            .Where(name => name != nameof(FriendsFailure.Server))
            .Select(name => "Home_FriendsFailure_" + name));
        AssertKeysWithSlot(["Home_FriendsFailure_Server"]);
    }

    [Fact]
    public void The_conversation_fallback_titles_take_the_id_suffix()
    {
        AssertKeysWithSlot(["Home_Conversation_UnknownUser", "Home_Conversation_Group"]);
    }

    [Fact]
    public void The_home_page_has_its_static_texts_and_accessible_names()
    {
        string[] required =
        [
            "Home_Empty",
            "Home_ListError",
            "Home_SaveProblem",
            "Home_Unavailable",
            "HomeBrand.Text",
            "HomePlaceholder.Text",
            "HomeRefreshButton.Content",
            "HomeRefreshButton.AutomationProperties.Name",
            "HomeReconnectButton.Content",
            "HomeReconnectButton.AutomationProperties.Name",
            "HomeConversationList.AutomationProperties.Name",
            "HomeConnectionDot.AutomationProperties.Name",
        ];

        AssertKeysWithoutSlot(required);
    }

    [Fact]
    public void The_duplicate_connection_text_mentions_reconnecting()
    {
        var entry = Entries.Value.Single(e => e.Name == "Home_Connection_DuplicateConnection");
        var button = Entries.Value.Single(e => e.Name == "HomeReconnectButton.Content");

        Assert.Contains(button.Value, entry.Value, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Chat pane ----------------------------------------------------------------------------------------------

    [Fact]
    public void Every_message_kind_except_Text_has_a_text_without_a_slot()
    {
        AssertKeysWithoutSlot(Enum.GetNames<MessageKind>()
            .Where(name => name != nameof(MessageKind.Text))
            .Select(name => "Chat_Kind_" + name));
    }

    [Fact]
    public void Every_chat_state_has_a_text_without_a_slot()
    {
        AssertKeysWithoutSlot(Enum.GetNames<ChatState>().Select(name => "Chat_State_" + name));
    }

    [Fact]
    public void The_empty_chat_text_says_that_only_messages_received_while_the_app_ran_are_shown()
    {
        var entry = Entries.Value.Single(e => e.Name == "Chat_State_Empty");

        Assert.Contains("khi ứng dụng đang chạy", entry.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_group_sender_fallback_takes_the_short_id()
    {
        AssertKeysWithSlot(["Chat_Sender_Unknown"]);
    }

    [Fact]
    public void The_chat_pane_has_its_static_texts_and_accessible_names()
    {
        string[] required =
        [
            "Chat_Sender_Self",
            "Chat_Header_Subtitle",
            "ChatMessageList.AutomationProperties.Name",
            "ChatHeader.AutomationProperties.Name",
        ];

        AssertKeysWithoutSlot(required);
    }

    [Fact]
    public void The_infinite_scroll_indicators_have_texts_and_an_accessible_name()
    {
        string[] required =
        [
            "Chat_LoadingOlder",
            "Chat_StartOfStored",
            "ChatLoadingOlderRing.AutomationProperties.Name",
        ];

        AssertKeysWithoutSlot(required);
    }

    [Fact]
    public void The_load_older_button_strings_are_gone()
    {
        string[] offending = Entries.Value
            .Where(e => e.Name.StartsWith("ChatLoadOlderButton", StringComparison.Ordinal))
            .Select(e => e.Name)
            .ToArray();

        Assert.True(offending.Length == 0, "Keys of the removed button: " + string.Join(", ", offending));
    }

    [Fact]
    public void The_start_of_history_line_says_the_message_is_saved_on_this_computer()
    {
        var entry = Entries.Value.Single(e => e.Name == "Chat_StartOfStored");

        Assert.Contains("trên máy này", entry.Value, StringComparison.Ordinal);
    }

    private static void AssertKeysWithoutSlot(IEnumerable<string> keys)
    {
        string[] bad = keys
            .Where(key => !Entries.Value.Any(e => e.Name == key && !e.Value.Contains("{0}", StringComparison.Ordinal)))
            .ToArray();

        Assert.True(bad.Length == 0, "Missing keys, or keys that must not contain {0}: " + string.Join(", ", bad));
    }

    private static void AssertKeysWithSlot(IEnumerable<string> keys)
    {
        string[] bad = keys
            .Where(key => !Entries.Value.Any(e => e.Name == key && e.Value.Contains("{0}", StringComparison.Ordinal)))
            .ToArray();

        Assert.True(bad.Length == 0, "Missing keys, or keys without {0}: " + string.Join(", ", bad));
    }

    [Fact]
    public void No_key_is_duplicated()
    {
        string[] duplicates = Entries.Value
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.True(duplicates.Length == 0, "Duplicate keys: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void No_value_is_empty()
    {
        string[] empty = Entries.Value
            .Where(e => string.IsNullOrWhiteSpace(e.Value))
            .Select(e => e.Name)
            .ToArray();

        Assert.True(empty.Length == 0, "Empty values: " + string.Join(", ", empty));
    }

    [Fact]
    public void No_key_or_value_contains_the_old_brand_word()
    {
        string[] offending = Entries.Value
            .Where(e => e.Name.Contains(Brand, StringComparison.OrdinalIgnoreCase)
                || e.Value.Contains(Brand, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .ToArray();

        Assert.True(offending.Length == 0, "Brand word in: " + string.Join(", ", offending));
    }

    private static List<(string Name, string Value)> LoadEntries()
    {
        string path = Path.Combine(FindRepositoryRoot(), "Luna.App", "Strings", "vi-VN", "Resources.resw");
        Assert.True(File.Exists(path), "Resource file not found: Luna.App/Strings/vi-VN/Resources.resw");

        XDocument doc = XDocument.Load(path);
        return doc.Root!
            .Elements("data")
            .Select(d => (
                Name: (string?)d.Attribute("name") ?? string.Empty,
                Value: (string?)d.Element("value") ?? string.Empty))
            .ToList();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Luna.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Luna.slnx not found above the test output folder");
        return dir!.FullName;
    }
}
