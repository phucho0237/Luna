using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Luna.Core.Chat;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Luna.App;

/// <summary>
/// The chat pane: it only presents <see cref="ChatViewModel"/>. The view keeps its own collection of rows and copies every
/// change of <see cref="ChatViewModel.Items"/> to it one for one (Add becomes an Insert at the same index, Replace stays a
/// Replace), so the list is never rebuilt and keeps its scroll position. Older messages load by themselves when the user
/// scrolls near the top; every decision comes from <see cref="ChatScrollPolicy"/> and only runs once the view is positioned
/// at the newest message. Every text comes from Resources.resw. It makes no server call and has no input box. Attach and
/// Detach are idempotent and safe in any order; after Detach no handler touches an element.
/// </summary>
public sealed partial class ChatView : UserControl
{
    private readonly ObservableCollection<MessageRow> _rows = [];
    private ChatViewModel? _chat;
    private bool _isGroup;
    private bool _scrollPending;

    // The ScrollViewer inside the ListView and the offset seen by the last ViewChanged.
    private ScrollViewer? _scroller;
    private double _lastOffset;

    // False whenever a conversation is shown, the view is attached or the rows were rebuilt; true once the initial scroll to
    // the newest row has run. Nothing is loaded before that. Each reset bumps the generation, which drops stale callbacks.
    private bool _positioned;
    private int _generation;

    // Fuse against runaway loads: automatic loads since the user last scrolled (see ChatScrollPolicy.MaxChain*).
    private int _autoLoadsSinceUserScroll;
    private bool _wasLoading;
    private bool _pageInserted;
    private bool _settling;
    private bool _followPending;

    public ChatView()
    {
        InitializeComponent();
        MessageList.ItemsSource = _rows;
        SubtitleText.Text = HomeText.Get("Chat_Header_Subtitle");
    }

    /// <summary>Starts presenting <paramref name="chat"/>. The same instance twice does nothing; another one replaces the first.</summary>
    public void Attach(ChatViewModel chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (ReferenceEquals(_chat, chat))
        {
            return;
        }

        Detach();
        _chat = chat;
        _wasLoading = chat.IsLoadingOlder;
        chat.PropertyChanged += OnChatPropertyChanged;
        chat.Items.CollectionChanged += OnItemsChanged;
        if (IsLoaded)
        {
            HookScroller();
        }

        Rebuild();
        RefreshState();
    }

    /// <summary>Stops presenting: unsubscribes and empties the rows. Safe to call any time.</summary>
    public void Detach()
    {
        ChatViewModel? chat = _chat;
        _chat = null;
        if (chat is not null)
        {
            chat.PropertyChanged -= OnChatPropertyChanged;
            chat.Items.CollectionChanged -= OnItemsChanged;
        }

        UnhookScroller();
        _rows.Clear();
        ResetPosition(pending: false);
        _autoLoadsSinceUserScroll = 0;
        _wasLoading = false;
        _pageInserted = false;
        LoadingChip.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        StartText.Visibility = Visibility.Collapsed;
        StateText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Sets the header for the conversation that is being opened and drops the rows of the previous one at once (the view
    /// model clears its items a moment later). Call it before opening the conversation.
    /// </summary>
    public void Show(string title, bool isGroup)
    {
        _isGroup = isGroup;
        TitleText.Text = title;
        _rows.Clear();
        ResetPosition(pending: true);
        _autoLoadsSinceUserScroll = 0;
        _pageInserted = false;
        _lastOffset = 0;
    }

    /// <summary>Updates the header title only (for example when a friend name arrives).</summary>
    public void SetTitle(string title) => TitleText.Text = title;

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_chat is not null)
        {
            UnhookScroller();
            HookScroller();
            EvaluateLoadOlder();
        }
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e) => UnhookScroller();

    private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_chat is null)
        {
            return;
        }

        HookScroller();
        EvaluateLoadOlder();
    }

    // ---- scroll viewer ----

    private void HookScroller()
    {
        if (_scroller is not null)
        {
            return;
        }

        ScrollViewer? scroller = FindScrollViewer(MessageList);
        if (scroller is null)
        {
            return;
        }

        _scroller = scroller;
        _lastOffset = scroller.VerticalOffset;
        scroller.ViewChanged += OnScrollViewChanged;
    }

    private void UnhookScroller()
    {
        ScrollViewer? scroller = _scroller;
        _scroller = null;
        if (scroller is not null)
        {
            scroller.ViewChanged -= OnScrollViewChanged;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller)
            {
                return scroller;
            }

            ScrollViewer? nested = FindScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ChatViewModel? chat = _chat;
        ScrollViewer? scroller = _scroller;
        if (chat is null || scroller is null)
        {
            return;
        }

        // A real scroll by the user (not the initial positioning, a load or the anchoring after it) re-arms the fuse.
        double offset = scroller.VerticalOffset;
        bool automatic = !_positioned || _settling || chat.IsLoadingOlder;
        if (ChatScrollPolicy.IsUserScroll(_lastOffset, offset, automatic))
        {
            _autoLoadsSinceUserScroll = 0;
        }

        _lastOffset = offset;
        EvaluateLoadOlder();
    }

    // The only place that starts an automatic load. LoadOlderAsync is single flight and never throws; the result arrives
    // through Items.
    private void EvaluateLoadOlder()
    {
        ChatViewModel? chat = _chat;
        if (chat is null || !_positioned || chat.State != ChatState.Loaded)
        {
            return;
        }

        if (_scroller is null)
        {
            HookScroller();
        }

        ScrollViewer? scroller = _scroller;
        if (scroller is null)
        {
            return;
        }

        if (ChatScrollPolicy.ShouldLoadOlder(
                _positioned,
                scroller.VerticalOffset,
                scroller.ScrollableHeight,
                scroller.ViewportHeight,
                chat.CanLoadOlder,
                chat.IsLoadingOlder,
                _autoLoadsSinceUserScroll))
        {
            _ = chat.LoadOlderAsync();
        }
    }

    // ---- view model ----

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ChatViewModel? chat = _chat;
        if (chat is null)
        {
            return;
        }

        if (e.PropertyName is null or ""
            or nameof(ChatViewModel.State)
            or nameof(ChatViewModel.CanLoadOlder)
            or nameof(ChatViewModel.IsLoadingOlder)
            or nameof(ChatViewModel.ReachedStart))
        {
            RefreshState();
        }

        if (e.PropertyName is null or "" or nameof(ChatViewModel.IsLoadingOlder))
        {
            OnLoadingChanged(chat);
        }
    }

    // A load starts: it counts for the fuse. A load ends: once the page has been laid out (low priority, after the anchoring),
    // look again, but only if rows were really inserted; a failed load adds none, so it is never retried by itself.
    private void OnLoadingChanged(ChatViewModel chat)
    {
        bool loading = chat.IsLoadingOlder;
        if (loading == _wasLoading)
        {
            return;
        }

        _wasLoading = loading;
        if (loading)
        {
            _autoLoadsSinceUserScroll++;
            _pageInserted = false;
            return;
        }

        bool inserted = _pageInserted;
        _pageInserted = false;
        _settling = true;
        int generation = _generation;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (generation != _generation || _chat is null)
            {
                return;
            }

            _settling = false;
            _lastOffset = _scroller?.VerticalOffset ?? _lastOffset;
            if (inserted)
            {
                EvaluateLoadOlder();
            }
        });
    }

    private void RefreshState()
    {
        ChatViewModel? chat = _chat;
        if (chat is null)
        {
            return;
        }

        ChatState state = chat.State;
        StateText.Text = ChatText.State(state);
        StateText.Visibility = state is ChatState.Loading or ChatState.Empty or ChatState.Error
            ? Visibility.Visible
            : Visibility.Collapsed;

        bool loading = chat.IsLoadingOlder;
        if (loading && LoadingChip.Visibility != Visibility.Visible)
        {
            // The text is set again each time the chip appears so the polite live region announces it.
            LoadingText.Text = HomeText.Get("Chat_LoadingOlder");
        }

        LoadingRing.IsActive = loading;
        LoadingChip.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;

        bool atStart = chat.ReachedStart;
        if (atStart)
        {
            StartText.Text = HomeText.Get("Chat_StartOfStored");
        }

        StartText.Visibility = atStart ? Visibility.Visible : Visibility.Collapsed;

        UpdatePosition();
    }

    // ---- initial position ----

    private void ResetPosition(bool pending)
    {
        _generation++;
        _positioned = false;
        _scrollPending = pending;
        _settling = false;
        _followPending = false;
    }

    // Decides whether the initial scroll to the newest row has to run now. Empty and Error need no scroll, so they count as
    // positioned at once; so does a Loaded state without rows (the scroll stays pending for the first rows).
    private void UpdatePosition()
    {
        ChatViewModel? chat = _chat;
        if (chat is null)
        {
            return;
        }

        ChatState state = chat.State;
        if (state is ChatState.Empty or ChatState.Error)
        {
            _scrollPending = false;
            _positioned = true;
            return;
        }

        if (state != ChatState.Loaded)
        {
            return;
        }

        if (_rows.Count == 0)
        {
            _positioned = true;
            return;
        }

        if (_scrollPending)
        {
            _scrollPending = false;
            ScrollToEnd();
        }
    }

    // After the first load the newest message is shown. Containers exist only after a layout pass, so the scroll is asked
    // again at low priority; only after that second scroll has run (and the layout was updated so the scroll viewer reports
    // its real numbers) is the view positioned and the policy allowed to look at it.
    private void ScrollToEnd()
    {
        _positioned = false;
        if (_rows.Count > 0)
        {
            MessageList.ScrollIntoView(_rows[^1]);
        }

        int generation = _generation;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (generation != _generation || _chat is null)
            {
                return;
            }

            if (_rows.Count > 0)
            {
                MessageList.ScrollIntoView(_rows[^1]);
                MessageList.UpdateLayout();
            }

            _positioned = true;
            _lastOffset = _scroller?.VerticalOffset ?? 0;
            EvaluateLoadOlder();
        });
    }

    // An append while the user was at the bottom: the panel mode already follows the newest row; this one low-priority scroll
    // asks for the same place and is coalesced. It is never used while the user reads higher up.
    private void FollowBottom()
    {
        if (_followPending)
        {
            return;
        }

        _followPending = true;
        int generation = _generation;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (generation != _generation || _chat is null)
            {
                return;
            }

            _followPending = false;
            if (_rows.Count > 0)
            {
                MessageList.ScrollIntoView(_rows[^1]);
            }
        });
    }

    // ---- rows ----

    private bool WasAtBottom()
    {
        ScrollViewer? scroller = _scroller;
        return scroller is null || ChatScrollPolicy.IsAtBottom(scroller.VerticalOffset, scroller.ScrollableHeight);
    }

    // Must run before the change is mirrored: the panel reads the mode while it handles the change.
    private void ApplyUpdatingMode(bool insertedAbove, bool wasAtBottom)
    {
        if (MessageList.ItemsPanelRoot is ItemsStackPanel panel)
        {
            panel.ItemsUpdatingScrollMode = ChatScrollPolicy.Decide(insertedAbove, wasAtBottom) == ItemsUpdatingMode.KeepLastItemInView
                ? ItemsUpdatingScrollMode.KeepLastItemInView
                : ItemsUpdatingScrollMode.KeepItemsInView;
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ChatViewModel? chat = _chat;
        if (chat is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        bool handled = false;
        bool follow = false;
        if (e.NewItems is not null && e.NewStartingIndex >= 0)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex <= _rows.Count)
            {
                // Anything before the end is "above" what the user reads (ApplyOlder inserts one item at a time at 0, 1, 2...).
                bool above = e.NewStartingIndex < _rows.Count;
                bool wasAtBottom = WasAtBottom();
                ApplyUpdatingMode(above, wasAtBottom);
                for (int i = 0; i < e.NewItems.Count; i++)
                {
                    _rows.Insert(e.NewStartingIndex + i, new MessageRow((MessageItem)e.NewItems[i]!, _isGroup, now));
                }

                if (above && _wasLoading)
                {
                    _pageInserted = true;
                }

                follow = !above && wasAtBottom && _positioned && !_scrollPending;
                handled = true;
            }
            else if (e.Action == NotifyCollectionChangedAction.Replace && e.NewStartingIndex + e.NewItems.Count <= _rows.Count)
            {
                ApplyUpdatingMode(insertedAbove: true, wasAtBottom: false);
                for (int i = 0; i < e.NewItems.Count; i++)
                {
                    _rows[e.NewStartingIndex + i] = new MessageRow((MessageItem)e.NewItems[i]!, _isGroup, now);
                }

                handled = true;
            }
        }

        // Clear (reset) and anything unexpected, or a row count that drifted: rebuild from the view model.
        if (!handled || _rows.Count != chat.Items.Count)
        {
            Rebuild();
            follow = false;
        }

        // A reset while the state stays Loaded raises no State change, so position here too.
        UpdatePosition();
        if (follow)
        {
            FollowBottom();
        }
    }

    private void Rebuild()
    {
        _rows.Clear();
        ChatViewModel? chat = _chat;
        if (chat is null)
        {
            return;
        }

        ResetPosition(pending: true);
        DateTimeOffset now = DateTimeOffset.Now;
        foreach (MessageItem item in chat.Items)
        {
            _rows.Add(new MessageRow(item, _isGroup, now));
        }
    }
}
