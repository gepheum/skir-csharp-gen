namespace SkirClient;

/// <summary>
/// Wraps a recursive struct field value.
/// <para>
/// Mirrors Zig's <c>Recursive(T)</c> union shape:
/// <c>default_value | value: *const T</c>.
/// </para>
/// <para>
/// Treat <see cref="DefaultValue"/> the same as the default value of
/// <typeparamref name="T"/>.
/// </para>
/// </summary>
public readonly struct Recursive<T>
{
    // Keep wrapped values behind a reference so Recursive<Foo> can be used
    // even when Foo is a struct (breaks value-type layout cycles).
    private sealed class Box(T value)
    {
        public T Value { get; } = value;

        public override bool Equals(object? obj) =>
            obj is Box other && global::System.Collections.Generic.EqualityComparer<T>.Default.Equals(Value, other.Value);

        public override int GetHashCode() =>
            global::System.Collections.Generic.EqualityComparer<T>.Default.GetHashCode(Value!);

        public override string ToString() => Value?.ToString() ?? "";
    }

    private readonly Box? _box;

    private Recursive(Box? box)
    {
        _box = box;
    }

    /// <summary>
    /// True when this instance represents Zig's <c>default_value</c> branch.
    /// </summary>
    public bool IsDefaultValue => _box is null;

    /// <summary>
    /// True when this instance represents Zig's <c>value</c> branch.
    /// </summary>
    public bool HasValue => !IsDefaultValue;

    /// <summary>
    /// The wrapped value for the <c>value</c> branch.
    /// Throws when called on <see cref="DefaultValue"/>.
    /// </summary>
    public T Value =>
        HasValue
            ? _box!.Value
            : throw new global::System.InvalidOperationException(
                "Recursive value is default_value and has no wrapped value.");

    /// <summary>
    /// Returns Zig's <c>default_value</c> branch.
    /// </summary>
    public static readonly Recursive<T> DefaultValue = new(box: null);

    /// <summary>
    /// Returns Zig's <c>value</c> branch.
    /// </summary>
    public static Recursive<T> FromValue(T value)
    {
        if (value is null)
            throw new global::System.ArgumentNullException(nameof(value));
        return new Recursive<T>(box: new Box(value));
    }
}
