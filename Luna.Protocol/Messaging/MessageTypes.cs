namespace Luna.Protocol.Messaging;

/// <summary>The <c>msgType</c> values seen on the live server (see PROTOCOL.md).</summary>
public static class MessageTypes
{
    /// <summary>Text message.</summary>
    public const string Text = "webchat";

    /// <summary>Sticker.</summary>
    public const string Sticker = "chat.sticker";

    /// <summary>Photo.</summary>
    public const string Photo = "chat.photo";

    /// <summary>Attached file.</summary>
    public const string File = "share.file";

    /// <summary>Message recall notice.</summary>
    public const string Undo = "chat.undo";
}
