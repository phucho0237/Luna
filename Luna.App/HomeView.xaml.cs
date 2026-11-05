using System;
using System.Collections.Generic;
using System.ComponentModel;
using Luna.Core.Realtime;
using Luna.Core.ViewModels;
using Luna.Protocol.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Luna.App;

/// <summary>
/// The signed-in page: the conversation list on the left, a placeholder for the chat pane on the right. It only presents
/// <see cref="ShellViewModel.Home"/>; every text comes from Resources.resw through <see cref="HomeText"/>, and the only
/// account data it shows is the title of a conversation. It makes no server call of its own: the buttons are bound to the
/// view model commands, and the page runs <see cref="HomeViewModel.StartCommand"/> once per home view model.
/// Attach and Detach are idempotent and safe in any order; after Detach no handler touches an element.
/// </summary>
public sealed partial class HomeView : UserControl
{
    private ShellViewModel? _shell;
    private HomeViewModel? _home;
    private HomeViewModel? _startedHome;

    // The rows currently set as ItemsSource, the user's selected conversation key, and a flag that keeps a selection
    // restored by code from being recorded as a user choice (it also guarantees the handler can never loop or act).
    private IReadOnlyList<ConversationRow> _rows = [];
    private string? _selectedKey;
    private bool _restoringSelection;

    public HomeView()
    {
        InitializeComponent();
        SaveProblemBar.Message = HomeText.Get("Home_SaveProblem");
        ListErrorBar.Message = HomeText.Get("Home_ListError");
        EmptyText.Text = HomeText.Get("Home_Empty");
        UnavailableText.Text = HomeText.Get("Home_Unavailable");
        ShowUnavailable();
    }

    /// <summary>
    /// Starts presenting <paramref name="shell"/>'s home. The same shell twice does nothing; another shell replaces the
    /// first. Call <see cref="Detach"/> when the window closes.
    /// </summary>
    public void Attach(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (ReferenceEquals(_shell, shell))
        {
            return;
        }

        Detach();
        _shell = shell;
        shell.PropertyChanged += OnShellPropertyChanged;
        BindHome(shell.Home);
    }

    /// <summary>Stops presenting: unsubscribes from the shell and the home and releases the rows. Safe to call any time.</summary>
    public void Detach()
    {
        if (_shell is null && _home is null)
        {
            // Never attached, or already detached: nothing to undo and no element to touch.
            return;
        }

        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
            _shell = null;
        }

        UnbindHome();
        _startedHome = null;
        _selectedKey = null;
        ShowUnavailable();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ShellViewModel? shell = _shell;
        if (shell is null || !(e.PropertyName is null or "" or nameof(ShellViewModel.Home)))
        {
            return;
        }

        BindHome(shell.Home);
    }

    // The home may be null, appear later or be replaced. The same instance twice changes nothing.
    private void BindHome(HomeViewModel? home)
    {
        if (home is not null && ReferenceEquals(home, _home))
        {
            return;
        }

        UnbindHome();
        if (home is null)
        {
            ShowUnavailable();
            return;
        }

        _home = home;
        home.Conversations.PropertyChanged += OnHomePartChanged;
        home.Friends.PropertyChanged += OnHomePartChanged;
        home.LiveSync.PropertyChanged += OnHomePartChanged;
        ChatPane.Attach(home.Chat);
        RefreshButton.Command = home.RefreshFriendsCommand;
        ReconnectButton.Command = home.ReconnectCommand;

        UnavailableText.Visibility = Visibility.Collapsed;
        Columns.Visibility = Visibility.Visible;
        RefreshList();
        RefreshStatus();

        // Once per instance. StartAsync is also single-run by itself; this only avoids asking twice.
        if (!ReferenceEquals(_startedHome, home))
        {
            _startedHome = home;
            home.StartCommand.Execute(null);
        }
    }

    private void UnbindHome()
    {
        HomeViewModel? home = _home;
        _home = null;
        if (home is not null)
        {
            home.Conversations.PropertyChanged -= OnHomePartChanged;
            home.Friends.PropertyChanged -= OnHomePartChanged;
            home.LiveSync.PropertyChanged -= OnHomePartChanged;
        }

        RefreshButton.Command = null;
        ReconnectButton.Command = null;
        ChatPane.Detach();
        SetItems([]);
        ShowChatPane(false);
    }

    // The right column shows the chat pane while a conversation is chosen and the placeholder otherwise.
    private void ShowChatPane(bool open)
    {
        ChatPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderText.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    }

    // A user choice: show the header, then open the conversation (or close the pane for no choice). The view model call
    // never throws and posts its changes through the dispatcher.
    private void OpenChat(ConversationRow? row)
    {
        HomeViewModel? home = _home;
        if (home is null)
        {
            return;
        }

        if (row is null)
        {
            ShowChatPane(false);
            _ = home.SelectConversationAsync(null);
            return;
        }

        ChatPane.Show(row.Title, row.ThreadType == ThreadType.Group);
        ShowChatPane(true);
        _ = home.SelectConversationAsync(row.Item);
    }

    private void ShowUnavailable()
    {
        Columns.Visibility = Visibility.Collapsed;
        UnavailableText.Visibility = Visibility.Visible;
    }

    private void OnHomePartChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The view models post their changes to the UI thread; this only protects against a caller that does not.
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnHomePartChanged(sender, e));
            return;
        }

        HomeViewModel? home = _home;
        if (home is null)
        {
            return;
        }

        if (ReferenceEquals(sender, home.Conversations)
            && e.PropertyName is null or "" or nameof(ConversationListViewModel.Items))
        {
            RefreshList();
        }

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        HomeViewModel? home = _home;
        if (home is null)
        {
            return;
        }

        LiveSyncService live = home.LiveSync;
        ConnectionText.Text = HomeText.Connection(live.State);
        ShowDot(live.State);
        FriendsText.Text = HomeText.Friends(
            home.Friends.State, home.Friends.Failure, home.Friends.RetryMinutes, home.Friends.ServerErrorCode);
        ReconnectButton.Visibility = live.CanReconnect ? Visibility.Visible : Visibility.Collapsed;
        SaveProblemBar.IsOpen = live.SaveFailures > 0 || live.DroppedBatches > 0;
        ListErrorBar.IsOpen = home.Conversations.HasError;
        EmptyText.Visibility = home.Conversations.IsLoaded && home.Conversations.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowDot(LiveSyncState state)
    {
        DotConnected.Visibility = state == LiveSyncState.Connected ? Visibility.Visible : Visibility.Collapsed;
        DotBusy.Visibility = state is LiveSyncState.Connecting or LiveSyncState.DuplicateConnection
            ? Visibility.Visible
            : Visibility.Collapsed;
        DotDown.Visibility = state == LiveSyncState.Disconnected ? Visibility.Visible : Visibility.Collapsed;
        DotIdle.Visibility = state == LiveSyncState.Stopped ? Visibility.Visible : Visibility.Collapsed;
    }

    // Builds the rows (the time texts are computed now, so they are not live-updated when the day changes; the next
    // refresh corrects them) and replaces the list in one step.
    private void RefreshList()
    {
        HomeViewModel? home = _home;
        if (home is null)
        {
            return;
        }

        IReadOnlyList<ConversationItem> items = home.Conversations.Items;
        DateTimeOffset now = DateTimeOffset.Now;
        var rows = new List<ConversationRow>(items.Count);
        foreach (ConversationItem item in items)
        {
            rows.Add(new ConversationRow(item, now));
        }

        SetItems(rows);
    }

    // Replaces ItemsSource as one step and keeps the user's place: the selected conversation is remembered by key before
    // the replacement and selected again afterwards if it still exists; otherwise the selection is cleared. Selection
    // changes made here are not recorded as user choices and trigger nothing.
    private void SetItems(IReadOnlyList<ConversationRow> rows)
    {
        string? key = (ConversationList.SelectedItem as ConversationRow)?.Key ?? _selectedKey;

        _restoringSelection = true;
        try
        {
            _rows = rows;
            ConversationList.ItemsSource = rows;

            int index = ConversationSelection.IndexOf(rows, key);
            if (index >= 0)
            {
                ConversationList.SelectedIndex = index;
                // Minimal scrolling: nothing happens when the row is already fully visible.
                ConversationList.ScrollIntoView(rows[index], ScrollIntoViewAlignment.Default);
                _selectedKey = key;
                // The chat stays open as it is (no reload); only a name that arrived later may change the header.
                ChatPane.SetTitle(rows[index].Title);
            }
            else
            {
                bool hadSelection = key is not null;
                ConversationList.SelectedItem = null;
                _selectedKey = null;
                if (hadSelection)
                {
                    // The chosen conversation is no longer listed: close its chat pane.
                    OpenChat(null);
                }
            }
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    // Records the user's choice and opens that conversation in the chat pane. A selection restored by code after a list
    // refresh never gets here (the restore flag), and the chat view model ignores the conversation that is already open.
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection || _home is null)
        {
            return;
        }

        var row = ConversationList.SelectedItem as ConversationRow;
        _selectedKey = row?.Key;
        OpenChat(row);
    }
}
