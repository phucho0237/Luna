using System.Text.Json;

namespace Luna.Protocol.Auth;

public abstract record QrLoginEvent;

/// <summary>New QR code. Show <paramref name="PngImage"/> to the user to scan with the mobile app.</summary>
public sealed record QrGenerated(string Code, byte[] PngImage) : QrLoginEvent;

/// <summary>The phone has scanned and is waiting for the user to tap confirm.</summary>
public sealed record QrScanned(string DisplayName, string? AvatarUrl) : QrLoginEvent;

public sealed record QrExpired : QrLoginEvent;

public sealed record QrDeclined : QrLoginEvent;

/// <summary>The session cookies are already in <see cref="ClientContext.Cookies"/>.</summary>
public sealed record QrLoginResult(string DisplayName, JsonElement UserInfo);