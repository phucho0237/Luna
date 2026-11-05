using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luna.Core.Storage;
using Luna.Protocol.Messaging;

namespace Luna.Core.ViewModels;

/// <summary>
/// The conversation list: the stored conversations joined with the cached friend names. It exposes data and flags only;
/// the view maps them to localized text. Refreshes may overlap: each one takes a version number and only the LATEST
/// one may assign <see cref="Items"/>, in one atomic <see cref="IUiDispatcher.Post"/> step together with the flags, so
/// a slow earlier refresh can never overwrite a newer result. A failure never throws: it sets
/// <see cref="HasError"/> and keeps the previous items.
/// </summary>
public sealed partial class ConversationListViewModel : ObservableObject
{
    /// <summary>How many conversations are listed (the store allows at most 500).</summary>
    public const int DefaultLimit = 200;

    private readonly IMessageStore _store;
    private readonly IContactStore _contacts;
    private readonly IUiDispatcher _ui;
    private readonly int _limit;

    private int _version;

    public ConversationListViewModel(IMessageStore store, IContactStore contacts, IUiDispatcher ui, int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 500);
        _store = store;
        _contacts = contacts;
        _ui = ui;
        _limit = limit;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync());
    }

    /// <summary>The conversations, most recently active first. Replaced as a whole, never edited in place.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ConversationItem> Items { get; private set; } = [];

    /// <summary>True once a refresh has succeeded at least once (the view can tell "empty" from "not loaded yet").</summary>
    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    /// <summary>True when the latest refresh could not read the store; the previous items stay.</summary>
    [ObservableProperty]
    public partial bool HasError { get; private set; }

    public IAsyncRelayCommand RefreshCommand { get; }

    /// <summary>Never prints conversations.</summary>
    public override string ToString() => nameof(ConversationListViewModel);

    /// <summary>
    /// Reads the store and the cached names and assigns the result, unless a newer refresh was started meanwhile.
    /// Never throws for storage problems. A cancellation returns quietly and assigns nothing.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        int version = Interlocked.Increment(ref _version);

        IReadOnlyList<ConversationSummary> summaries;
        IReadOnlyDictionary<string, string> names;
        try
        {
            summaries = await _store.ListConversationsAsync(_limit, cancellationToken).ConfigureAwait(false);
            names = await ReadNamesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            // The exception is dropped on purpose: it could carry file paths or data, and HasError is all the view needs.
            Post(version, () => HasError = true);
            return;
        }

        var items = new List<ConversationItem>(summaries.Count);
        foreach (ConversationSummary summary in summaries)
        {
            string? name = null;
            if (summary.ThreadType == ThreadType.User && names.TryGetValue(summary.ThreadId, out string? known))
            {
                name = known;
            }

            items.Add(new ConversationItem(summary.ThreadType, summary.ThreadId, name, summary.LastTimestampMs, summary.MessageCount));
        }

        Post(version, () =>
        {
            Items = items;
            IsLoaded = true;
            HasError = false;
        });
    }

    // Friend names are a nicety: any failure to read them leaves the rows unnamed instead of failing the list.
    private async Task<IReadOnlyDictionary<string, string>> ReadNamesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _contacts.GetContactsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new Dictionary<string, string>();
        }
    }

    // One dispatcher step; dropped when a newer refresh has been started since this one began.
    private void Post(int version, Action apply) =>
        _ui.Post(() =>
        {
            if (version == Volatile.Read(ref _version))
            {
                apply();
            }
        });
}
