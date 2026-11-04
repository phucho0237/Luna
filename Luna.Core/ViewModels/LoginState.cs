namespace Luna.Core.ViewModels;

/// <summary>Where the login screen is. The view maps each value to localized text.</summary>
public enum LoginState
{
    Idle,
    Restoring,
    WaitingForScan,
    WaitingForConfirm,
    RefreshingCode,
    SignedIn,
    Declined,
    Cancelled,
    Failed,
}

/// <summary>Why the login ended in <see cref="LoginState.Failed"/>. The view maps each value to localized text.</summary>
public enum LoginFailure
{
    None,
    Network,
    Server,
    QrTimedOut,
    UnexpectedResponse,
    Storage,
    Unexpected,
}
