using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace el.Domain.Common;

/// <summary>
/// Immutable, read-only set of currency definitions keyed by code and version.
/// Callers always resolve an explicit version so that historical calculations replay
/// against the definition they used; there is no implicit "latest version" fallback.
/// </summary>
public sealed class CurrencyCatalog
{
    private readonly FrozenDictionary<(CurrencyCode Code, int Version), CurrencyDefinition> _definitions;

    private CurrencyCatalog(
        FrozenDictionary<(CurrencyCode Code, int Version), CurrencyDefinition> definitions,
        ReadOnlyCollection<CurrencyDefinition> ordered)
    {
        _definitions = definitions;
        Definitions = ordered;
    }

    /// <summary>All definitions, ordered by code (ordinal) and then by version.</summary>
    public IReadOnlyList<CurrencyDefinition> Definitions { get; }

    /// <summary>
    /// Creates a catalog from a snapshot of <paramref name="definitions"/>.
    /// Each code/version pair may appear only once; duplicates are rejected, not merged.
    /// </summary>
    public static CurrencyCatalog Create(IEnumerable<CurrencyDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var byKey = new Dictionary<(CurrencyCode Code, int Version), CurrencyDefinition>();

        foreach (var definition in definitions)
        {
            if (definition is null)
            {
                throw new ArgumentException("Currency definitions must not contain null.", nameof(definitions));
            }

            var key = (definition.Code, definition.Version);

            if (byKey.TryGetValue(key, out var existing))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Currency {definition.Code} version {definition.Version} is defined more than once (exponents {existing.Exponent} and {definition.Exponent})."),
                    nameof(definitions));
            }

            byKey.Add(key, definition);
        }

        var ordered = byKey.Values
            .OrderBy(definition => definition.Code.Value, StringComparer.Ordinal)
            .ThenBy(definition => definition.Version)
            .ToArray();

        return new CurrencyCatalog(byKey.ToFrozenDictionary(), Array.AsReadOnly(ordered));
    }

    /// <summary>Returns the definition for exactly this code and version.</summary>
    /// <exception cref="UnknownCurrencyException">The code/version pair is not defined.</exception>
    public CurrencyDefinition Resolve(CurrencyCode code, int version)
    {
        ArgumentNullException.ThrowIfNull(code);

        return TryResolve(code, version, out var definition)
            ? definition
            : throw new UnknownCurrencyException(code, version);
    }

    public bool TryResolve(CurrencyCode code, int version, [NotNullWhen(true)] out CurrencyDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(code);

        return _definitions.TryGetValue((code, version), out definition);
    }
}
