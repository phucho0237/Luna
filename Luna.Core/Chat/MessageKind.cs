namespace Luna.Core.Chat;

/// <summary>What a stored message row is, for display. The view maps it to a localized label.</summary>
public enum MessageKind
{
    /// <summary>A message with text: the text is shown.</summary>
    Text,

    /// <summary>A message the sender recalled.</summary>
    Recalled,

    /// <summary>A sticker.</summary>
    Sticker,

    /// <summary>A photo.</summary>
    Image,

    /// <summary>An attached file.</summary>
    File,

    /// <summary>Any other or unknown message type.</summary>
    Other,
}
