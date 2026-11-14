using System.Globalization;

namespace el.Infrastructure.Persistence.Configurations;

/// <summary>Small helpers for CHECK constraint SQL built from enum definitions.</summary>
internal static class Sql
{
    /// <summary>"column IN (1, 2, 3)" using the enum's stored integer values.</summary>
    internal static string InList<TEnum>(string column, IEnumerable<TEnum> values)
        where TEnum : struct, Enum =>
        column + " IN (" + string.Join(", ", values.Select(value => Convert.ToInt32(value, CultureInfo.InvariantCulture))) + ")";
}
