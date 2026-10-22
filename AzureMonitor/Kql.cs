using System.Text;

namespace PPObjectSearch.AzureMonitor;

/// <summary>Building KQL from values that came from elsewhere - names, ids - without letting them change the query.</summary>
public static class Kql
{
    /// <summary>A single-quoted string literal: backslashes, quotes and line breaks escaped.</summary>
    public static string Literal(string value)
    {
        var text = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            text.Append(c switch
            {
                '\\' => @"\\",
                '\'' => @"\'",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                _ => c.ToString()
            });
        }

        return text.Append('\'').ToString();
    }

    /// <summary>"('a', 'b')", for <c>in~</c>.</summary>
    public static string List(IEnumerable<string> values) => "(" + string.Join(", ", values.Select(Literal)) + ")";
}
