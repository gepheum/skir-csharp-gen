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
    }

    private readonly Box? _box;

    private Recursive(bool isDefaultValue, Box? box)
    {
        IsDefaultValue = isDefaultValue;
        _box = box;
    }

    /// <summary>
    /// True when this instance represents Zig's <c>default_value</c> branch.
    /// </summary>
    public bool IsDefaultValue { get; }

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
    public static Recursive<T> DefaultValue => new(isDefaultValue: true, box: null);

    /// <summary>
    /// Returns Zig's <c>value</c> branch.
    /// </summary>
    public static Recursive<T> FromValue(T value)
    {
        if (value is null)
            throw new global::System.ArgumentNullException(nameof(value));
        return new Recursive<T>(isDefaultValue: false, box: new Box(value));
    }
}
