namespace el.Domain.Common;

/// <summary>
/// Base for entities: two instances are the same entity when they have the same type and ID,
/// whatever their other state. Value objects such as Money, ScaledPercentage or CurrencyCode
/// are records instead and are equal by value.
/// </summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : struct, IEquatable<TId>
{
    protected Entity(TId id)
    {
        if (id.Equals(default))
        {
            throw new ArgumentException("An entity ID must not be empty.", nameof(id));
        }

        Id = id;
    }

    public TId Id { get; }

    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);

    public bool Equals(Entity<TId>? other) =>
        other is not null && other.GetType() == GetType() && Id.Equals(other.Id);

    public override bool Equals(object? obj) => Equals(obj as Entity<TId>);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
