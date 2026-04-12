using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SkirClient;

// =============================================================================
// SkirUnrecognized
// =============================================================================

/// <summary>
/// Holds fields encountered during deserialization that are not declared in the
/// current schema version. Preserved on re-serialization, ensuring that a value
/// round-tripped by an older client does not silently drop fields added by a
/// newer schema. Completely opaque to user code: you never construct or inspect
/// this type directly.
/// </summary>
public sealed class SkirUnrecognized
{
    // Intentionally opaque. The SkirClient library constructs instances
    // during deserialization; user code never creates them.
    internal SkirUnrecognized() { }
}

// =============================================================================
// Serializer<T>
// =============================================================================

/// <summary>
/// Serializes and deserializes values of type <typeparamref name="T"/>.
/// Every generated struct and enum exposes a static <c>Serializer</c> property
/// that returns an instance of this class.
/// </summary>
public abstract class Serializer<T>
{
    /// <summary>
    /// Serializes <paramref name="value"/> to JSON.
    /// <para>
    /// When <paramref name="readable"/> is <c>false</c> (the default) the result is
    /// <em>dense</em> (field-number-based) JSON — safe for persistence and transport.
    /// Renaming a field in the .skir file does not break deserialization of previously
    /// persisted values.
    /// </para>
    /// <para>
    /// When <paramref name="readable"/> is <c>true</c> the result is human-readable,
    /// name-based, indented JSON — use this for debugging only.
    /// </para>
    /// </summary>
    public abstract string ToJson(T value, bool readable = false);

    /// <summary>Serializes <paramref name="value"/> to compact binary format.</summary>
    public abstract byte[] ToBytes(T value);

    /// <summary>
    /// Deserializes a value from JSON.
    /// Accepts both dense and readable JSON.
    /// When <paramref name="keepUnrecognizedValues"/> is <c>true</c>, field data not
    /// declared in the current schema is preserved in the struct's internal
    /// unrecognized-fields store so that re-serializing the value does not
    /// silently discard forward-compatible fields.
    /// </summary>
    public abstract T FromJson(string json, bool keepUnrecognizedValues = false);

    /// <summary>
    /// Deserializes a value from binary format.
    /// When <paramref name="keepUnrecognizedValues"/> is <c>true</c>, unrecognized
    /// field data is preserved for round-trip fidelity.
    /// </summary>
    public abstract T FromBytes(byte[] bytes, bool keepUnrecognizedValues = false);

    /// <summary>Reflection descriptor for this type's schema.</summary>
    public abstract TypeDescriptor TypeDescriptor { get; }
}

// =============================================================================
// ITypeAdapter<T>
// =============================================================================

/// <summary>
/// Internal serialization contract for a single type.
/// Implemented by primitive adapters and used by
/// <see cref="TypeAdapterSerializer{T}"/> to back a <see cref="Serializer{T}"/>.
/// </summary>
internal interface ITypeAdapter<T>
{
    /// <summary>Returns <c>true</c> when <paramref name="input"/> equals the default (zero) value.</summary>
    bool IsDefault(T input);

    /// <summary>
    /// Appends the JSON representation of <paramref name="input"/> to
    /// <paramref name="output"/>. When <paramref name="eolIndent"/> is
    /// <c>null</c> the output is dense; otherwise it is readable and
    /// <paramref name="eolIndent"/> is <c>"\n"</c> followed by the
    /// current indentation prefix.
    /// </summary>
    void ToJson(T input, string? eolIndent, StringBuilder output);

    /// <summary>Deserializes a value from a parsed JSON token.</summary>
    T FromJson(JsonElement json, bool keepUnrecognizedValues);

    /// <summary>Appends the binary encoding of <paramref name="input"/> to <paramref name="output"/>.</summary>
    void Encode(T input, List<byte> output);

    /// <summary>
    /// Reads one encoded value from <paramref name="data"/> starting at
    /// <paramref name="offset"/>, advancing <paramref name="offset"/> past
    /// the consumed bytes.
    /// </summary>
    T Decode(byte[] data, ref int offset, bool keepUnrecognizedValues);

    /// <summary>Returns the reflection descriptor for this type.</summary>
    TypeDescriptor TypeDescriptor { get; }
}

// =============================================================================
// TypeAdapterSerializer<T>
// =============================================================================

/// <summary>
/// A <see cref="Serializer{T}"/> that delegates all work to an
/// <see cref="ITypeAdapter{T}"/>. Used for primitive and composite types.
/// </summary>
internal sealed class TypeAdapterSerializer<T> : Serializer<T>
{
    private readonly ITypeAdapter<T> _adapter;

    internal TypeAdapterSerializer(ITypeAdapter<T> adapter) => _adapter = adapter;

    public override string ToJson(T value, bool readable = false)
    {
        var sb = new StringBuilder();
        _adapter.ToJson(value, readable ? "\n" : null, sb);
        return sb.ToString();
    }

    public override byte[] ToBytes(T value)
    {
        var output = new List<byte>(5);
        output.Add((byte)'s');
        output.Add((byte)'k');
        output.Add((byte)'i');
        output.Add((byte)'r');
        _adapter.Encode(value, output);
        return output.ToArray();
    }

    public override T FromJson(string json, bool keepUnrecognizedValues = false)
    {
        using var doc = JsonDocument.Parse(json);
        return _adapter.FromJson(doc.RootElement, keepUnrecognizedValues);
    }

    public override T FromBytes(byte[] bytes, bool keepUnrecognizedValues = false)
    {
        if (bytes.Length >= 4 &&
            bytes[0] == 's' && bytes[1] == 'k' &&
            bytes[2] == 'i' && bytes[3] == 'r')
        {
            int offset = 4;
            return _adapter.Decode(bytes, ref offset, keepUnrecognizedValues);
        }
        // No magic prefix — treat payload as UTF-8 JSON.
        return FromJson(Encoding.UTF8.GetString(bytes), keepUnrecognizedValues);
    }

    public override TypeDescriptor TypeDescriptor => _adapter.TypeDescriptor;
}

// =============================================================================
// Serializers — primitives and composites
// =============================================================================

/// <summary>
/// Factory for primitive and composite serializers.
/// </summary>
public static class Serializers
{
    public static Serializer<bool>     Bool      { get; } = new TypeAdapterSerializer<bool>(new BoolAdapter());
    public static Serializer<int>      Int32     { get; } = new Int32Serializer_();
    public static Serializer<long>     Int64     { get; } = new Int64Serializer_();
    public static Serializer<ulong>    Hash64    { get; } = new Hash64Serializer_();
    public static Serializer<float>    Float32   { get; } = new Float32Serializer_();
    public static Serializer<double>   Float64   { get; } = new Float64Serializer_();
    public static Serializer<string>   String    { get; } = new StringSerializer_();
    public static Serializer<byte[]>   Bytes     { get; } = new BytesSerializer_();
    public static Serializer<DateTime> Timestamp { get; } = new TimestampSerializer_();

    /// <summary>Serializer for an optional (nullable) reference type.</summary>
    public static Serializer<T?> Optional<T>(Serializer<T> inner) where T : class
        => new OptionalRefSerializer_<T>(inner);

    /// <summary>Serializer for an optional (nullable) value type.</summary>
    public static Serializer<T?> OptionalValue<T>(Serializer<T> inner) where T : struct
        => new OptionalValueSerializer_<T>(inner);

    /// <summary>Serializer for a read-only list of values.</summary>
    public static Serializer<IReadOnlyList<T>> Array<T>(Serializer<T> inner)
        => new ArraySerializer_<T>(inner);

    // ---- BoolAdapter ----

    private sealed class BoolAdapter : ITypeAdapter<bool>
    {
        public bool IsDefault(bool input) => !input;

        public void ToJson(bool input, string? eolIndent, StringBuilder output)
        {
            if (eolIndent != null)
                output.Append(input ? "true" : "false");
            else
                output.Append(input ? '1' : '0');
        }

        public bool FromJson(JsonElement json, bool keepUnrecognizedValues)
        {
            return json.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number =>
                    json.TryGetInt64(out long i) ? i != 0 : json.GetDouble() != 0.0,
                JsonValueKind.String => json.GetString() != "0",
                _ => false,
            };
        }

        public void Encode(bool input, List<byte> output) =>
            output.Add(input ? (byte)1 : (byte)0);

        public bool Decode(byte[] data, ref int offset, bool keepUnrecognizedValues)
        {
            if (offset >= data.Length)
                throw new InvalidOperationException("Unexpected end of input");
            return data[offset++] != 0;
        }

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Bool);
    }

    // ---- Primitive stubs ----


    private sealed class Int32Serializer_ : Serializer<int>
    {
        public override string ToJson(int value, bool readable = false) => value.ToString();
        public override byte[] ToBytes(int value)  => [];
        public override int FromJson(string json, bool keepUnrecognizedValues = false)  => int.Parse(json);
        public override int FromBytes(byte[] bytes, bool keepUnrecognizedValues = false) => 0;
        public override TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor("int32");
    }

    private sealed class Int64Serializer_ : Serializer<long>
    {
        // int64 is encoded as a quoted string in JSON to preserve precision for
        // JavaScript clients (which use 64-bit floats).
        public override string ToJson(long value, bool readable = false) => $"\"{value}\"";
        public override byte[] ToBytes(long value)  => [];
        public override long FromJson(string json, bool keepUnrecognizedValues = false)  => long.Parse(json.Trim('"'));
        public override long FromBytes(byte[] bytes, bool keepUnrecognizedValues = false) => 0L;
        public override TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor("int64");
    }

    private sealed class Hash64Serializer_ : Serializer<ulong>
    {
        // hash64 is also encoded as a quoted string for the same reason as int64.
        public override string ToJson(ulong value, bool readable = false) => $"\"{value}\"";
        public override byte[] ToBytes(ulong value)  => [];
        public override ulong FromJson(string json, bool keepUnrecognizedValues = false)  => ulong.Parse(json.Trim('"'));
        public override ulong FromBytes(byte[] bytes, bool keepUnrecognizedValues = false) => 0UL;
        public override TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor("hash64");
    }

    private sealed class Float32Serializer_ : Serializer<float>
    {
        public override string ToJson(float value, bool readable = false) => value.ToString("G");
        public override byte[] ToBytes(float value)  => [];
        public override float FromJson(string json, bool keepUnrecognizedValues = false)  => float.Parse(json);
        public override float FromBytes(byte[] bytes, bool keepUnrecognizedValues = false) => 0f;
        public override TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor("float32");
    }

    private sealed class Float64Serializer_ : Serializer<double>
    {
        public override string ToJson(double value, bool readable = false) => value.ToString("G");
        public override byte[] ToBytes(double value)  => [];
        public override double FromJson(string json, bool keepUnrecognizedValues = false)  => double.Parse(json);
        public override double FromBytes(byte[] bytes, bool keepUnrecognizedValues = false) => 0d;
        public override TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor("float64");
    }

    private sealed class StringSerializer_ : Serializer<string>
    {
        public override string ToJson(string value, bool readable = false) => $"\"{value}\"";
        public override byte[] ToBytes(string value)   => [];
        public override string FromJson(string json, bool keepUnrecognizedValues = false)   => json.Trim('"');
        public override string FromBytes(byte[] bytes, bool keepUnrecognizedValues = false) => "";
        public override TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor("string");
    }

    private sealed class BytesSerializer_ : Serializer<byte[]>
    {
        public override string ToJson(byte[] value, bool readable = false)
            => $"\"{Convert.ToBase64String(value)}\"";
        public override byte[] ToBytes(byte[] value)   => [];
        public override byte[] FromJson(string json, bool keepUnrecognizedValues = false)   => Convert.FromBase64String(json.Trim('"'));
        public override byte[] FromBytes(byte[] bytes, bool keepUnrecognizedValues = false) => [];
        public override TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor("bytes");
    }

    private sealed class TimestampSerializer_ : Serializer<DateTime>
    {
        public override string ToJson(DateTime value, bool readable = false)
        {
            var unixMillis = (long)(value.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
            if (readable)
                return $"{{\"unix_millis\":{unixMillis},\"formatted\":\"{value.ToUniversalTime():O}\"}}";
            return unixMillis.ToString();
        }
        public override byte[] ToBytes(DateTime value)  => [];
        public override DateTime FromJson(string json, bool keepUnrecognizedValues = false)  => DateTime.UnixEpoch;
        public override DateTime FromBytes(byte[] bytes, bool keepUnrecognizedValues = false) => DateTime.UnixEpoch;
        public override TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor("timestamp");
    }

    // ---- Composite stubs ----

    private sealed class OptionalRefSerializer_<T>(Serializer<T> inner) : Serializer<T?> where T : class
    {
        public override string ToJson(T? value, bool readable = false)
            => value is null ? "null" : inner.ToJson(value, readable);
        public override byte[] ToBytes(T? value) => [];
        public override T? FromJson(string json, bool keepUnrecognizedValues = false) => json == "null" ? null : inner.FromJson(json, keepUnrecognizedValues);
        public override T? FromBytes(byte[] bytes, bool keepUnrecognizedValues = false) => null;
        public override TypeDescriptor TypeDescriptor { get; } = new OptionalDescriptor(inner.TypeDescriptor);
    }

    private sealed class OptionalValueSerializer_<T>(Serializer<T> inner) : Serializer<T?> where T : struct
    {
        public override string ToJson(T? value, bool readable = false)
            => value is null ? "null" : inner.ToJson(value.Value, readable);
        public override byte[] ToBytes(T? value) => [];
        public override T? FromJson(string json, bool keepUnrecognizedValues = false) => json == "null" ? null : inner.FromJson(json, keepUnrecognizedValues);
        public override T? FromBytes(byte[] bytes, bool keepUnrecognizedValues = false) => null;
        public override TypeDescriptor TypeDescriptor { get; } = new OptionalDescriptor(inner.TypeDescriptor);
    }

    private sealed class ArraySerializer_<T>(Serializer<T> inner) : Serializer<IReadOnlyList<T>>
    {
        public override string ToJson(IReadOnlyList<T> value, bool readable = false)
            => "[" + string.Join(",", value.Select(v => inner.ToJson(v, readable))) + "]";
        public override byte[] ToBytes(IReadOnlyList<T> value) => [];
        public override IReadOnlyList<T> FromJson(string json, bool keepUnrecognizedValues = false) => [];
        public override IReadOnlyList<T> FromBytes(byte[] bytes, bool keepUnrecognizedValues = false) => [];
        public override TypeDescriptor TypeDescriptor { get; } = new ArrayDescriptor(inner.TypeDescriptor);
    }
}

// =============================================================================
// SkirKeyedList<TItem, TKey>
// =============================================================================

/// <summary>
/// An immutable list that also supports O(1) lookup by a key field.
/// Generated for array fields declared as <c>[Item|key_field]</c> in the .skir file.
/// The index is built lazily on the first lookup call and cached for subsequent calls.
/// </summary>
public sealed class SkirKeyedList<TItem, TKey> : IReadOnlyList<TItem>
    where TKey : notnull
{
    private readonly IReadOnlyList<TItem> _items;
    private readonly Func<TItem, TKey> _keyExtractor;
    private readonly Func<TItem> _defaultFactory;
    private Dictionary<TKey, TItem>? _index;

    internal SkirKeyedList(IEnumerable<TItem> items, Func<TItem, TKey> keyExtractor,
        Func<TItem> defaultFactory)
    {
        _items = items.ToList();
        _keyExtractor = keyExtractor;
        _defaultFactory = defaultFactory;
    }

    private Dictionary<TKey, TItem> Index => _index ??= BuildIndex();

    private Dictionary<TKey, TItem> BuildIndex()
    {
        var index = new Dictionary<TKey, TItem>();
        // Last element with a given key wins (matches other language implementations).
        foreach (var item in _items)
            index[_keyExtractor(item)] = item;
        return index;
    }

    /// <summary>
    /// Returns the last element whose key equals <paramref name="key"/>,
    /// or <c>null</c> / the default value if no element has that key.
    /// The first call is O(n); subsequent calls are O(1).
    /// </summary>
    public TItem? FindByKey(TKey key)
        => Index.TryGetValue(key, out var item) ? item : default;

    /// <summary>
    /// Returns the last element whose key equals <paramref name="key"/>,
    /// or the zero-value element if not found. Useful when you want to avoid a
    /// null check and just read default field values.
    /// </summary>
    public TItem FindByKeyOrDefault(TKey key)
        => Index.TryGetValue(key, out var item) ? item : _defaultFactory();

    public int Count => _items.Count;
    public TItem this[int index] => _items[index];
    public IEnumerator<TItem> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_items).GetEnumerator();
}

// =============================================================================
// Method<TRequest, TResponse>
// =============================================================================

/// <summary>
/// Represents a service method declared with the <c>method</c> keyword in the
/// .skir file. Carries metadata (name, number, documentation) and the
/// request/response serializers needed for routing and encoding RPC calls.
/// </summary>
public sealed record Method<TRequest, TResponse>(
    string Name,
    int Number,
    string Doc,
    Serializer<TRequest> RequestSerializer,
    Serializer<TResponse> ResponseSerializer);
