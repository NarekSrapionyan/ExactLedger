using System.Text;

namespace el.Domain.Clearing;

/// <summary>Strict RFC 4180: CRLF written; LF or CRLF read; quotes only when needed.</summary>
public static class Rfc4180
{
    private static readonly char[] SpecialCharacters = [',', '"', '\r', '\n'];

    public static string Write(IEnumerable<IReadOnlyList<string>> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var builder = new StringBuilder();

        foreach (var record in records)
        {
            for (var index = 0; index < record.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                AppendField(builder, record[index]);
            }

            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>False plus the 1-based record number of the first malformed record.</summary>
    public static bool TryParse(string text, out IReadOnlyList<IReadOnlyList<string>> records, out int errorRecord)
    {
        ArgumentNullException.ThrowIfNull(text);

        var parsed = new List<IReadOnlyList<string>>();
        errorRecord = Parse(text, parsed);

        if (errorRecord == 0)
        {
            records = parsed;

            return true;
        }

        records = [];

        return false;
    }

    private static void AppendField(StringBuilder builder, string field)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (field.IndexOfAny(SpecialCharacters) < 0)
        {
            builder.Append(field);

            return;
        }

        builder.Append('"').Append(field.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }

    private static int Parse(string text, List<IReadOnlyList<string>> parsed)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var position = 0;

        while (position < text.Length)
        {
            if (text[position] == '"')
            {
                position++;
                var closed = false;

                while (position < text.Length)
                {
                    var current = text[position++];

                    if (current != '"')
                    {
                        field.Append(current);
                    }
                    else if (position < text.Length && text[position] == '"')
                    {
                        field.Append('"');
                        position++;
                    }
                    else
                    {
                        closed = true;
                        break;
                    }
                }

                if (!closed || (position < text.Length && text[position] is not (',' or '\r' or '\n')))
                {
                    return parsed.Count + 1;
                }
            }
            else
            {
                while (position < text.Length && text[position] is not (',' or '\r' or '\n'))
                {
                    if (text[position] == '"')
                    {
                        return parsed.Count + 1;
                    }

                    field.Append(text[position++]);
                }
            }

            fields.Add(field.ToString());
            field.Clear();

            if (position >= text.Length)
            {
                break;
            }

            var separator = text[position++];

            if (separator == ',')
            {
                if (position >= text.Length)
                {
                    fields.Add(string.Empty);
                }

                continue;
            }

            if (separator == '\r')
            {
                if (position >= text.Length || text[position] != '\n')
                {
                    return parsed.Count + 1;
                }

                position++;
            }

            parsed.Add(fields.ToArray());
            fields.Clear();
        }

        if (fields.Count > 0)
        {
            parsed.Add(fields.ToArray());
        }

        return 0;
    }
}
