using System;
using System.Globalization;
using Luna.Core.ViewModels;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Luna.App;

/// <summary>
/// Maps the login view model state to the Vietnamese status line from Resources.resw. It never throws and never
/// returns a raw resource key: a missing key or a malformed format string falls back to a generic text, and if even
/// that is missing the result is an empty string.
/// </summary>
public static class LoginText
{
    private const string StatePrefix = "Login_State_";
    private const string FailurePrefix = "Login_Failure_";
    private const string FallbackStateKey = StatePrefix + nameof(LoginState.Idle);
    private const string FallbackFailureKey = FailurePrefix + nameof(LoginFailure.Unexpected);

    private static readonly Lazy<ResourceLoader?> Loader = new(CreateLoader);

    /// <summary>The status line for the current login state.</summary>
    public static string Status(LoginState state, LoginFailure failure, int? serverErrorCode)
        => Resolve(Lookup, state, failure, serverErrorCode);

    /// <summary>The resource key for a state; a failed login is described by its failure instead.</summary>
    public static string KeyFor(LoginState state, LoginFailure failure)
        => state == LoginState.Failed
            ? FailurePrefix + failure
            : StatePrefix + state;

    /// <summary>Same as <see cref="Status"/> with an injectable lookup, so the fallback rules do not need WinUI.</summary>
    internal static string Resolve(
        Func<string, string?> lookup, LoginState state, LoginFailure failure, int? serverErrorCode)
    {
        string key = KeyFor(state, failure);
        string? template = Safe(lookup, key);
        if (template is not null)
        {
            string? text = Format(template, failure == LoginFailure.Server && state == LoginState.Failed
                ? serverErrorCode
                : null);
            if (text is not null)
            {
                return text;
            }
        }

        string fallbackKey = state == LoginState.Failed ? FallbackFailureKey : FallbackStateKey;
        string? fallback = Safe(lookup, fallbackKey);
        if (fallback is not null && fallback.Contains("{0}", StringComparison.Ordinal))
        {
            fallback = null;
        }

        return fallback ?? string.Empty;
    }

    /// <summary>Looks one string up by key (for example the window title); an empty string when it is missing.</summary>
    public static string Get(string key) => Safe(Lookup, key) ?? string.Empty;

    private static string? Format(string template, int? code)
    {
        if (code is null)
        {
            return template.Contains("{0}", StringComparison.Ordinal) ? null : template;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, code.Value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Safe(Func<string, string?> lookup, string key)
    {
        try
        {
            string? value = lookup(key);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Lookup(string key)
    {
        ResourceLoader? loader = Loader.Value;
        return loader?.GetString(key);
    }

    private static ResourceLoader? CreateLoader()
    {
        try
        {
            return new ResourceLoader();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
