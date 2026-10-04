using System.Globalization;
using System.Text.Json;

namespace PPObjectSearch.Services;

/// <summary>Checks a value against an environment variable's type before it is written.</summary>
public static class EnvironmentVariableValues
{
    public const int Text = 100000000;
    public const int Number = 100000001;
    public const int Boolean = 100000002;
    public const int Json = 100000003;
    public const int DataSource = 100000004;
    public const int Secret = 100000005;

    /// <summary>
    /// The value as it should be stored, or the reason it cannot be. Booleans are stored as
    /// "yes"/"no", which is what Power Automate and canvas apps read.
    /// </summary>
    public static (string? Value, string? Error) Normalise(int? type, string? input)
    {
        var text = input ?? string.Empty;

        switch (type)
        {
            case Secret:
                return (null, "A secret holds an Azure Key Vault reference and is set in the maker portal, not here.");

            case Number:
                return decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    ? (number.ToString(CultureInfo.InvariantCulture), null)
                    : (null, $"'{text}' is not a number. Use digits, with '.' for decimals.");

            case Boolean:
                return text.Trim().ToLowerInvariant() switch
                {
                    "yes" or "true" or "1" => ("yes", null),
                    "no" or "false" or "0" => ("no", null),
                    _ => (null, $"'{text}' is not a yes/no value.")
                };

            case Json:
                try
                {
                    using var _ = JsonDocument.Parse(text);
                    return (text, null);
                }
                catch (JsonException ex)
                {
                    return (null, "Not valid JSON - " + ex.Message);
                }

            case DataSource:
                return string.IsNullOrWhiteSpace(text)
                    ? (null, "A data source value cannot be empty.")
                    : (text.Trim(), null);

            default:
                return (text, null);
        }
    }
}
