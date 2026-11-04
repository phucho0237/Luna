namespace Luna.Core.Storage;

/// <summary>Keyset pagination position: the (timestamp, row id) of the oldest message of the previous page.</summary>
public sealed record MessageCursor(long TimestampMs, long Id);
