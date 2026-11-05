using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Luna.Core.Chat;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Luna.App;

/// <summary>
/// The chat pane: it only presents <see cref="ChatViewModel"/>. The view keeps its own collection of rows and copies every
/// change of <see cref="ChatViewModel.Items"/> to it one for one (Add becomes an Insert at the same index, Replace stays a
/// Replace), so the list is never rebuilt and keeps its scroll position. Every text comes from Resources.resw through
/// <see cref="ChatText"/>. It makes no server call and has no input box. Attach and Detach are idempotent and safe in any
/// order; after Detach no handler touches an element.
/// </summary>
public sealed partial class ChatView : UserControl
{
    private readonly ObservableCollection<MessageRow> _rows = [];
    private ChatViewModel? _chat;
    private bool _isGroup;
    private bool _scrollPending;

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
        chat.PropertyChanged += OnChatPropertyChanged;
        chat.Items.CollectionChanged += OnItemsChanged;
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

        _rows.Clear();
        _scrollPending = false;
        LoadOlderButton.Visibility = Visibility.Collapsed;
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
        _scrollPending = true;
    }

    /// <summary>Updates the header title only (for example when a friend name arrives).</summary>
    public void SetTitle(string title) => TitleText.Text = title;

    private void OnLoadOlderClick(object sender, RoutedEventArgs e)
    {
        ChatViewModel? chat = _chat;
        if (chat is not null)
        {
            // LoadOlderAsync never throws; the result arrives through Items.
            _ = chat.LoadOlderAsync();
        }
    }

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_chat is null)
        {
            return;
        }

        if (e.PropertyName is null or "" or nameof(ChatViewModel.State) or nameof(ChatViewModel.CanLoadOlder))
        {
            RefreshState();
        }
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
        LoadOlderButton.Visibility = chat.CanLoadOlder ? Visibility.Visible : Visibility.Collapsed;

        if (state == ChatState.Loaded && _scrollPending && _rows.Count > 0)
        {
            _scrollPending = false;
            ScrollToEnd();
        }
        else if (state is ChatState.Empty or ChatState.Error)
        {
            _scrollPending = false;
        }
    }

    // After the first load the newest message is shown. Containers exist only after a layout pass, so the scroll is asked
    // again at low priority.
    private void ScrollToEnd()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        MessageList.ScrollIntoView(_rows[^1]);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_chat is not null && _rows.Count > 0)
            {
                MessageList.ScrollIntoView(_rows[^1]);
            }
        });
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
        if (e.NewItems is not null && e.NewStartingIndex >= 0)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex <= _rows.Count)
            {
                for (int i = 0; i < e.NewItems.Count; i++)
                {
                    _rows.Insert(e.NewStartingIndex + i, new MessageRow((MessageItem)e.NewItems[i]!, _isGroup, now));
                }

                handled = true;
            }
            else if (e.Action == NotifyCollectionChangedAction.Replace && e.NewStartingIndex + e.NewItems.Count <= _rows.Count)
            {
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

        DateTimeOffset now = DateTimeOffset.Now;
        foreach (MessageItem item in chat.Items)
        {
            _rows.Add(new MessageRow(item, _isGroup, now));
        }
    }
}
