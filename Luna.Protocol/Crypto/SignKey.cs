using System.Text;

namespace Luna.Protocol.Crypto;

/// <summary>
/// <c>signkey = md5("zsecure" + tag + concat(parameter values, in order of the sorted key names))</c>.
/// </summary>
public static class SignKey
{
    public static string Compute(string tag, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        ArgumentNullException.ThrowIfNull(parameters);

        var sb = new StringBuilder("zsecure").Append(tag);
        foreach (string key in parameters.Keys.Order(StringComparer.Ordinal))
        {
            sb.Append(parameters[key]);
        }

        return Md5Hex.Lower(sb.ToString());
    }

    /// <summary>
    /// The tag is the lowercase command name with hyphens removed. For example <c>getLoginInfo</c> becomes <c>getlogininfo</c>.
    /// </summary>
    public static string NormalizeTag(string commandName)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandName);
        return commandName.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
    }
}
