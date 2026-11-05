namespace Luna.Core.Chat;

/// <summary>What the chat pane shows for the open conversation. The view maps it to a localized line.</summary>
public enum ChatState
{
    /// <summary>No conversation is open.</summary>
    Idle,

    /// <summary>The newest page of the open conversation is being read.</summary>
    Loading,

    /// <summary>The open conversation has messages to show.</summary>
    Loaded,

    /// <summary>The open conversation has no stored messages.</summary>
    Empty,

    /// <summary>The newest page could not be read; the view offers nothing but the message.</summary>
    Error,
}
