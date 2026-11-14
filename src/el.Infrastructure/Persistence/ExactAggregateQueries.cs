using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace el.Infrastructure.Persistence;

/// <summary>
/// Exact aggregate reads. PostgreSQL sums BIGINT columns as NUMERIC, but mapping the result back
/// to a 64-bit integer can overflow. These helpers read the NUMERIC as text and parse it into a
/// BigInteger, so totals stay exact at any size.
/// </summary>
public static class ExactAggregateQueries
{
    /// <summary>
    /// Runs a query that returns one integer aggregate, such as <c>SELECT sum(amount_minor) FROM ...</c>.
    /// Interpolated values become parameters. No rows (NULL) is zero.
    /// </summary>
    /// <exception cref="InvalidOperationException">The aggregate is not an integer.</exception>
    public static async Task<BigInteger> QueryExactIntegerAsync(
        this DatabaseFacade database,
        FormattableString aggregateSql,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(aggregateSql);

        var wrapped = FormattableStringFactory.Create(
            "SELECT ((" + aggregateSql.Format + "))::numeric::text AS \"Value\"",
            aggregateSql.GetArguments());

        var text = await database.SqlQuery<string?>(wrapped).SingleAsync(cancellationToken);

        if (text is null)
        {
            return BigInteger.Zero;
        }

        return BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException("The aggregate is not an integer, so it cannot be read as exact minor units.");
    }
}
