using System.Collections;

namespace SkirClient;

/// <summary>
/// An owned immutable byte sequence.
/// <para>
/// Unlike <see cref="byte[]"/>, instances copy incoming data on construction
/// and never expose a mutable buffer, which makes generated structs deeply
/// immutable.
/// </para>
/// </summary>
public readonly struct ImmutableBytes : IReadOnlyList<byte>, IEquatable<ImmutableBytes>
{
    private readonly byte[]? _bytes;

    private ImmutableBytes(byte[] bytes, bool takeOwnership)
    {
        _bytes = takeOwnership ? bytes : (byte[])bytes.Clone();
    }

    /// <summary>The empty immutable byte sequence singleton.</summary>
    public static readonly ImmutableBytes Empty = new(global::System.Array.Empty<byte>(), takeOwnership: true);

    /// <summary>Copies <paramref name="bytes"/> into a new immutable instance.</summary>
    public static ImmutableBytes CopyFrom(byte[] bytes)
    {
        if (bytes is null)
            throw new global::System.ArgumentNullException(nameof(bytes));
        return bytes.Length == 0 ? Empty : new ImmutableBytes(bytes, takeOwnership: false);
    }

    /// <summary>Copies <paramref name="bytes"/> into a new immutable instance.</summary>
    public static ImmutableBytes CopyFrom(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 0 ? Empty : new ImmutableBytes(bytes.ToArray(), takeOwnership: true);

    /// <summary>The number of bytes in the sequence.</summary>
    public int Length => _bytes?.Length ?? 0;

    /// <summary>True when the sequence is empty.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>A read-only span over the stored bytes.</summary>
    public ReadOnlySpan<byte> Span => _bytes ?? global::System.Array.Empty<byte>();

    /// <summary>Returns a mutable copy of the underlying bytes.</summary>
    public byte[] ToArray() => Span.ToArray();

    public byte this[int index] => Span[index];

    int IReadOnlyCollection<byte>.Count => Length;

    public bool Equals(ImmutableBytes other) => Span.SequenceEqual(other.Span);

    public override bool Equals(object? obj) => obj is ImmutableBytes other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new global::System.HashCode();
        foreach (var b in Span)
            hash.Add(b);
        return hash.ToHashCode();
    }

    public static bool operator ==(ImmutableBytes left, ImmutableBytes right) => left.Equals(right);

    public static bool operator !=(ImmutableBytes left, ImmutableBytes right) => !left.Equals(right);

    public IEnumerator<byte> GetEnumerator() => ((IEnumerable<byte>)(Span.ToArray())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
