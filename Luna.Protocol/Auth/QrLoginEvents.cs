using System.Text.Json;

namespace Luna.Protocol.Auth;

public abstract record QrLoginEvent;

/// <summary>New QR code. Show <paramref name="PngImage"/> to the user to scan with the mobile app.</summary>
public sealed record QrGenerated(string Code, byte[] PngImage) : QrLoginEvent
{
    /// <summary>Never prints <c>Code</c> (the QR content) or the <c>PngImage</c> bytes to logs.</summary>
    public override string ToString() => "QrGenerated";
}

/// <summary>The phone has scanned and is waiting for the user to tap confirm.</summary>
public sealed record QrScanned(string DisplayName, string? AvatarUrl) : QrLoginEvent
{
    /// <summary>Never prints <c>DisplayName</c> or <c>AvatarUrl</c> to logs.</summary>
    public override string ToString() => "QrScanned";
}

public sealed record QrExpired : QrLoginEvent;

public sealed record QrDeclined : QrLoginEvent;

/// <summary>The session cookies are already in <see cref="ClientContext.Cookies"/>.</summary>
public sealed record QrLoginResult(string DisplayName, JsonElement UserInfo)
{
    /// <summary>Never prints <c>DisplayName</c> or the <c>UserInfo</c> JSON (uid, phone number...) to logs.</summary>
    public override string ToString() => "QrLoginResult";
}