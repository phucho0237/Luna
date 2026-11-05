using Luna.Protocol.Messaging;

namespace Luna.Core.Chat;

/// <summary>Pure classification of a stored message row into a <see cref="MessageKind"/>.</summary>
public static class MessageKinds
{
    /// <summary>
    /// A recalled row is <see cref="MessageKind.Recalled"/> whatever its type; otherwise a row with text is
    /// <see cref="MessageKind.Text"/>; otherwise the type decides (sticker, photo, file); anything else, including a
    /// null or unknown type, is <see cref="MessageKind.Other"/>.
    /// </summary>
    public static MessageKind Classify(string msgType, bool recalled, bool hasText)
    {
        if (recalled)
        {
            return MessageKind.Recalled;
        }

        if (hasText)
        {
            return MessageKind.Text;
        }

        return msgType switch
        {
            MessageTypes.Sticker => MessageKind.Sticker,
            MessageTypes.Photo => MessageKind.Image,
            MessageTypes.File => MessageKind.File,
            _ => MessageKind.Other,
        };
    }
}
