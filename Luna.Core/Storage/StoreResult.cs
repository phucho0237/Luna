namespace Luna.Core.Storage;

/// <summary>Counts for one <see cref="IMessageStore.SaveAsync"/> call. Numbers only, safe to print.</summary>
/// <param name="Inserted">Messages written as new rows.</param>
/// <param name="Duplicates">Messages ignored because the thread already holds a message with the same dedupe key.</param>
/// <param name="Recalled">Stored messages marked as recalled by a recall notice.</param>
/// <param name="Skipped">Messages that could not be stored (bad timestamp, unmatched recall notice, no raw JSON).</param>
public sealed record StoreResult(int Inserted, int Duplicates, int Recalled, int Skipped);
