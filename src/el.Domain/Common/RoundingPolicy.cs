namespace el.Domain.Common;

/// <summary>
/// The rounding policy recorded in a calculation trace: a stable mode name and the version
/// of the rounding definition. A change to what any mode does requires a new version, so a
/// stored trace can always be replayed against the definition it used.
/// </summary>
public sealed record RoundingPolicy
{
    /// <summary>Version of the rounding definition implemented by <see cref="Rounding"/>.</summary>
    public const int CurrentVersion = 1;

    private RoundingPolicy(RoundingMode mode, string name)
    {
        Mode = mode;
        Name = name;
        Version = CurrentVersion;
    }

    public RoundingMode Mode { get; }

    /// <summary>Stable identifier of the mode: "HalfEven", "HalfAwayFromZero" or "TowardZero".</summary>
    public string Name { get; }

    public int Version { get; }

    /// <exception cref="ArgumentOutOfRangeException">The mode is not a defined rounding mode.</exception>
    public static RoundingPolicy For(RoundingMode mode) => mode switch
    {
        RoundingMode.HalfEven => new RoundingPolicy(mode, "HalfEven"),
        RoundingMode.HalfAwayFromZero => new RoundingPolicy(mode, "HalfAwayFromZero"),
        RoundingMode.TowardZero => new RoundingPolicy(mode, "TowardZero"),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The rounding mode is not defined."),
    };

    public override string ToString() =>
        FormattableString.Invariant($"{Name} v{Version}");
}
