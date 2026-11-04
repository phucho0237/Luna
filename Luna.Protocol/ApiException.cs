namespace Luna.Protocol;

public sealed class ApiException : Exception
{
    public const int InvalidSignature = 18022;
    public const int InvalidEncryptionProtocol = 18060;

    public ApiException(int errorCode, string? serverMessage)
        : base($"The server returned error {errorCode}: {serverMessage}")
    {
        ErrorCode = errorCode;
        ServerMessage = serverMessage;
    }

    public int ErrorCode { get; }

    public string? ServerMessage { get; }
}