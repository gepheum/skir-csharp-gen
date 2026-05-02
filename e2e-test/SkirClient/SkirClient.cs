using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SkirClient;

// =============================================================================
// Serializer<T>
// =============================================================================

/// <summary>
/// Serializes and deserializes values of type <typeparamref name="T"/>.
/// Every generated struct and enum exposes a static <c>Serializer</c> property
/// that returns an instance of this class.
/// </summary>
public sealed class Serializer<T>
{
    private readonly ITypeAdapter<T> _adapter;

    internal Serializer(ITypeAdapter<T> adapter)
    {
        _adapter = adapter;
    }

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
    public string ToJson(T value, bool readable = false)
    {
        var sb = new StringBuilder();
        _adapter.ToJsonInternal(value, readable ? "\n" : null, sb);
        return sb.ToString();
    }

    /// <summary>Serializes <paramref name="value"/> to compact binary format.</summary>
    public byte[] ToBytes(T value)
    {
        var output = new List<byte>(5);
        output.Add((byte)'s');
        output.Add((byte)'k');
        output.Add((byte)'i');
        output.Add((byte)'r');
        _adapter.EncodeInternal(value, output);
        return output.ToArray();
    }

    /// <summary>
    /// Deserializes a value from JSON.
    /// Accepts both dense and readable JSON.
    /// When <paramref name="keepUnrecognizedValues"/> is <c>true</c>, field data not
    /// declared in the current schema is preserved in the struct's internal
    /// unrecognized-fields store so that re-serializing the value does not
    /// silently discard forward-compatible fields.
    /// </summary>
    public T FromJson(string json, bool keepUnrecognizedValues = false)
    {
        using var doc = JsonDocument.Parse(json);
        return _adapter.FromJsonInternal(doc.RootElement, keepUnrecognizedValues);
    }

    /// <summary>
    /// Deserializes a value from binary format.
    /// When <paramref name="keepUnrecognizedValues"/> is <c>true</c>, unrecognized
    /// field data is preserved for round-trip fidelity.
    /// </summary>
    public T FromBytes(byte[] bytes, bool keepUnrecognizedValues = false)
    {
        if (bytes.Length >= 4 &&
            bytes[0] == 's' && bytes[1] == 'k' &&
            bytes[2] == 'i' && bytes[3] == 'r')
        {
            int offset = 4;
            return _adapter.DecodeInternal(bytes, ref offset, keepUnrecognizedValues);
        }
        // No magic prefix - treat payload as UTF-8 JSON.
        return FromJson(Encoding.UTF8.GetString(bytes), keepUnrecognizedValues);
    }

    // Internal fast-path hooks used by generated adapters.
    internal bool IsDefaultInternal(T value)
        => _adapter.IsDefaultInternal(value);

    internal void ToJsonInternal(T value, string? eolIndent, StringBuilder output)
        => _adapter.ToJsonInternal(value, eolIndent, output);

    internal T FromJsonInternal(JsonElement json, bool keepUnrecognizedValues)
        => _adapter.FromJsonInternal(json, keepUnrecognizedValues);

    internal void EncodeInternal(T value, List<byte> output)
        => _adapter.EncodeInternal(value, output);

    internal T DecodeInternal(byte[] data, ref int offset, bool keepUnrecognizedValues)
        => _adapter.DecodeInternal(data, ref offset, keepUnrecognizedValues);

    /// <summary>Reflection descriptor for this type's schema.</summary>
    public TypeDescriptor TypeDescriptor => _adapter.TypeDescriptor;
}

// =============================================================================
// ITypeAdapter<T>
// =============================================================================

/// <summary>
/// Internal serialization contract for a single type.
/// Implemented by concrete type adapters and wrapped by
/// <see cref="Serializer{T}"/>.
/// </summary>
internal interface ITypeAdapter<T>
{
    /// <summary>Returns <c>true</c> when <paramref name="input"/> equals the default (zero) value.</summary>
    bool IsDefaultInternal(T input);

    /// <summary>
    /// Appends the JSON representation of <paramref name="input"/> to
    /// <paramref name="output"/>. When <paramref name="eolIndent"/> is
    /// <c>null</c> the output is dense; otherwise it is readable and
    /// <paramref name="eolIndent"/> is <c>"\n"</c> followed by the
    /// current indentation prefix.
    /// </summary>
    void ToJsonInternal(T input, string? eolIndent, StringBuilder output);

    /// <summary>Deserializes a value from a parsed JSON token.</summary>
    T FromJsonInternal(JsonElement json, bool keepUnrecognizedValues);

    /// <summary>Appends the binary encoding of <paramref name="input"/> to <paramref name="output"/>.</summary>
    void EncodeInternal(T input, List<byte> output);

    /// <summary>
    /// Reads one encoded value from <paramref name="data"/> starting at
    /// <paramref name="offset"/>, advancing <paramref name="offset"/> past
    /// the consumed bytes.
    /// </summary>
    T DecodeInternal(byte[] data, ref int offset, bool keepUnrecognizedValues);

    /// <summary>Returns the reflection descriptor for this type.</summary>
    TypeDescriptor TypeDescriptor { get; }
}

// =============================================================================
// Serializers — primitives and composites
// =============================================================================

/// <summary>
/// Factory for primitive and composite serializers.
/// </summary>
public static class Serializers
{
    public static Serializer<bool>     Bool      { get; } = new(new BoolAdapter());
    public static Serializer<int>      Int32     { get; } = new(new Int32Adapter());
    public static Serializer<long>     Int64     { get; } = new(new Int64Adapter());
    public static Serializer<ulong>    Hash64    { get; } = new(new Hash64Adapter());
    public static Serializer<float>    Float32   { get; } = new(new Float32Adapter());
    public static Serializer<double>   Float64   { get; } = new(new Float64Adapter());
    public static Serializer<string>   String    { get; } = new(new StringAdapter());
    public static Serializer<ImmutableBytes> Bytes { get; } = new(new BytesAdapter());
    public static Serializer<DateTimeOffset> Timestamp { get; } = new(new TimestampAdapter());

    /// <summary>Serializer for an optional (nullable) reference type.</summary>
    public static Serializer<T?> Optional<T>(Serializer<T> inner) where T : class
        => new(new OptionalRefAdapter_<T>(inner));

    /// <summary>Serializer for an optional (nullable) value type.</summary>
    public static Serializer<T?> OptionalValue<T>(Serializer<T> inner) where T : struct
        => new(new OptionalValueAdapter_<T>(inner));

    /// <summary>Serializer for a read-only list of values.</summary>
    public static Serializer<ImmutableList<T>> Array<T>(Serializer<T> inner)
        => new(new ArrayAdapter_<T>(inner));

    // ---- BoolAdapter ----

    private sealed class BoolAdapter : ITypeAdapter<bool>
    {
        public bool IsDefaultInternal(bool input) => !input;

        public void ToJsonInternal(bool input, string? eolIndent, StringBuilder output)
        {
            if (eolIndent != null)
                output.Append(input ? "true" : "false");
            else
                output.Append(input ? '1' : '0');
        }

        public bool FromJsonInternal(JsonElement json, bool keepUnrecognizedValues)
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

        public void EncodeInternal(bool input, List<byte> output) =>
            output.Add(input ? (byte)1 : (byte)0);

        public bool DecodeInternal(byte[] data, ref int offset, bool keepUnrecognizedValues)
        {
            if (offset >= data.Length)
                throw new InvalidOperationException("Unexpected end of input");
            return data[offset++] != 0;
        }

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Bool);
    }

    // ---- Binary wire-format helpers ----

        // Returns bytes in little-endian order regardless of host endianness.
        private static byte[] LE(byte[] bytes)
        {
            if (!BitConverter.IsLittleEndian) System.Array.Reverse(bytes);
            return bytes;
        }
        internal static byte[] LE_(byte[] bytes) => LE(bytes);

        // Encodes an i32 using the skir variable-length wire format.
        //   0..=231         → single byte
        //   232..=65535     → wire 232 + u16 LE
        //   65536..=i32.MAX → wire 233 + u32 LE
        //   -256..=-1       → wire 235 + u8(v+256)
        //   -65536..=-257   → wire 236 + u16 LE(v+65536)
        //   i32.MIN..=-65537→ wire 237 + i32 LE
        private static void EncodeI32(int v, List<byte> output)
        {
            if (v >= 0)
            {
                if (v <= 231) { output.Add((byte)v); }
                else if (v <= 65535) { output.Add(232); output.AddRange(LE(BitConverter.GetBytes((ushort)v))); }
                else { output.Add(233); output.AddRange(LE(BitConverter.GetBytes((uint)v))); }
            }
            else
            {
                if (v >= -256) { output.Add(235); output.Add((byte)(v + 256)); }
                else if (v >= -65536) { output.Add(236); output.AddRange(LE(BitConverter.GetBytes((ushort)(v + 65536)))); }
                else { output.Add(237); output.AddRange(LE(BitConverter.GetBytes(v))); }
            }
        }

    // Decodes the body of a variable-length number given the already-consumed wire byte.
    private static long DecodeNumberBody(byte wire, byte[] data, ref int offset)
    {
        switch (wire)
        {
            case <= 231: return wire;
            case 232: return ReadU16(data, ref offset);
            case 233: return ReadU32(data, ref offset);
            case 234: return (long)ReadU64(data, ref offset); // reinterpret bits
            case 235: return ReadU8(data, ref offset) - 256L;
            case 236: return ReadU16(data, ref offset) - 65536L;
            case 237: return (int)ReadU32(data, ref offset);
            case 238:
            case 239: return (long)ReadU64(data, ref offset);
            default: return 0;
        }
    }
    internal static void EncodeI32_(int v, List<byte> output) => EncodeI32(v, output);
    internal static long DecodeNumberBody_(byte wire, byte[] data, ref int offset) =>
        DecodeNumberBody(wire, data, ref offset);

    internal static long DecodeNumber(byte[] data, ref int offset)
    {
        byte wire = ReadU8(data, ref offset);
        return DecodeNumberBody(wire, data, ref offset);
    }

    private static byte ReadU8(byte[] data, ref int offset)
    {
        if (offset >= data.Length) throw new InvalidOperationException("Unexpected end of input");
        return data[offset++];
    }
    internal static byte ReadU8_(byte[] data, ref int offset) => ReadU8(data, ref offset);

    private static ushort ReadU16(byte[] data, ref int offset)
    {
        if (offset + 2 > data.Length) throw new InvalidOperationException("Unexpected end of input");
        var v = BitConverter.ToUInt16(data, offset);
        offset += 2;
        return v;
    }
    internal static ushort ReadU16_(byte[] data, ref int offset) => ReadU16(data, ref offset);

    private static uint ReadU32(byte[] data, ref int offset)
    {
        if (offset + 4 > data.Length) throw new InvalidOperationException("Unexpected end of input");
        var v = BitConverter.ToUInt32(data, offset);
        offset += 4;
        return v;
    }

    private static ulong ReadU64(byte[] data, ref int offset)
    {
        if (offset + 8 > data.Length) throw new InvalidOperationException("Unexpected end of input");
        var v = BitConverter.ToUInt64(data, offset);
        offset += 8;
        return v;
    }

    // ---- Timestamp / String / Bytes helpers ----

    private const long MinTimestampMillis = -8_640_000_000_000_000L;
    private const long MaxTimestampMillis =  8_640_000_000_000_000L;

    // Converts a DateTimeOffset to unix milliseconds, clamped to the Skir wire range.
    private static long DateTimeOffsetToMillis(DateTimeOffset dto) =>
        Math.Clamp(dto.ToUnixTimeMilliseconds(), MinTimestampMillis, MaxTimestampMillis);

    // Creates a DateTimeOffset (UTC, offset zero) from unix milliseconds.
    // Values outside DateTimeOffset's representable range are clamped.
    private static DateTimeOffset MillisToDateTimeOffset(long ms)
    {
        ms = Math.Clamp(ms, MinTimestampMillis, MaxTimestampMillis);
        ms = Math.Clamp(ms,
            DateTimeOffset.MinValue.ToUnixTimeMilliseconds(),
            DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }

    // Converts unix milliseconds to an ISO-8601 UTC string, e.g. "2009-02-13T23:31:30.000Z".
    private static string MillisToIso8601(long ms)
    {
        ms = Math.Clamp(ms,
            DateTimeOffset.MinValue.ToUnixTimeMilliseconds(),
            DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
        return DateTimeOffset.FromUnixTimeMilliseconds(ms)
            .ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "Z";
    }

    // Encodes a non-negative length using the skir variable-length uint32 scheme.
    //   0..=231   → single byte
    //   232..=65535 → wire 232 + u16 LE
    //   else      → wire 233 + u32 LE
    private static void EncodeUint32(uint n, List<byte> output)
    {
        if (n <= 231) { output.Add((byte)n); }
        else if (n <= 65535) { output.Add(232); output.AddRange(LE(BitConverter.GetBytes((ushort)n))); }
        else { output.Add(233); output.AddRange(LE(BitConverter.GetBytes(n))); }
    }
    internal static void EncodeUint32_(uint n, List<byte> output) => EncodeUint32(n, output);

    // Writes s as a JSON string literal (with surrounding quotes) using Skir escaping rules.
    private static void WriteJsonString(string s, StringBuilder output)
    {
        output.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                default:
                    if (c < '\x20' || c == '\x7F')
                        output.Append($"\\u{(int)c:x4}");
                    else
                        output.Append(c);
                    break;
            }
        }
        output.Append('"');
    }
    internal static void WriteJsonString_(string s, StringBuilder output) => WriteJsonString(s, output);

    // Skips one encoded value at the current offset (used for removed/unknown slots).
    internal static void SkipValue_(byte[] data, ref int offset)
    {
        if (offset >= data.Length) return;
        byte wire = data[offset++];
        if (wire <= 231) return;

        switch (wire)
        {
            case 232: offset += 2; break;
            case 233: offset += 4; break;
            case 234:
            case 238:
            case 239: offset += 8; break;
            case 235: offset += 1; break;
            case 236: offset += 2; break;
            case 237: offset += 4; break;
            case 242:
            case 244: break;
            case 243:
            case 245:
            {
                long n = DecodeNumber(data, ref offset);
                offset += (int)n;
                break;
            }
            case 246: break;
            case 247:
                SkipValue_(data, ref offset);
                break;
            case 248:
                SkipValue_(data, ref offset);
                SkipValue_(data, ref offset);
                break;
            case 249:
                SkipValue_(data, ref offset);
                SkipValue_(data, ref offset);
                SkipValue_(data, ref offset);
                break;
            case 250:
            {
                long n = DecodeNumber(data, ref offset);
                for (long i = 0; i < n; i++) SkipValue_(data, ref offset);
                break;
            }
            case 251:
            case 252:
            case 253:
            case 254:
                SkipValue_(data, ref offset);
                break;
        }
    }

    // Encodes bytes as a lowercase hexadecimal string.
    private static string EncodeHex(ReadOnlySpan<byte> bytes)
    {
        const string HexChars = "0123456789abcdef";
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(HexChars[b >> 4]);
            sb.Append(HexChars[b & 0xF]);
        }
        return sb.ToString();
    }

    // Decodes a lowercase or uppercase hexadecimal string.
    private static byte[] DecodeHex(ReadOnlySpan<char> s)
    {
        if (s.Length % 2 != 0)
            throw new ArgumentException($"Odd hex string length: {s.Length}");
        byte[] result = new byte[s.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = byte.Parse(s.Slice(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
        return result;
    }

    // ---- Primitive adapters ----

    private sealed class Int32Adapter : ITypeAdapter<int>
    {
        public bool IsDefaultInternal(int input) => input == 0;

        // Same in both dense and readable modes — always a JSON number.
        public void ToJsonInternal(int input, string? eolIndent, StringBuilder output) =>
            output.Append(input);

        public int FromJsonInternal(JsonElement json, bool keepUnrecognizedValues)
        {
            return json.ValueKind switch
            {
                JsonValueKind.Number =>
                    json.TryGetInt64(out long i) ? (int)i : (int)json.GetDouble(),
                // Mirrors TypeScript: +(json as string) | 0
                JsonValueKind.String =>
                    double.TryParse(json.GetString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double d)
                        ? (int)d : 0,
                _ => 0,
            };
        }

        public void EncodeInternal(int input, List<byte> output) => EncodeI32(input, output);

        public int DecodeInternal(byte[] data, ref int offset, bool keepUnrecognizedValues) =>
            (int)DecodeNumber(data, ref offset);

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Int32);
    }

    // Values within [-MAX_SAFE_INT, MAX_SAFE_INT] are emitted as JSON numbers;
    // larger values are quoted strings, matching JS Number.MAX_SAFE_INTEGER.
    private const long MaxSafeInt64Json = 9_007_199_254_740_991L;

    private sealed class Int64Adapter : ITypeAdapter<long>
    {
        public bool IsDefaultInternal(long input) => input == 0;

        public void ToJsonInternal(long input, string? eolIndent, StringBuilder output)
        {
            if (input >= -MaxSafeInt64Json && input <= MaxSafeInt64Json)
                output.Append(input);
            else
            {
                output.Append('"');
                output.Append(input);
                output.Append('"');
            }
        }

        public long FromJsonInternal(JsonElement json, bool keepUnrecognizedValues)
        {
            return json.ValueKind switch
            {
                JsonValueKind.Number =>
                    json.TryGetInt64(out long i) ? i : (long)Math.Round(json.GetDouble()),
                JsonValueKind.String =>
                    long.TryParse(json.GetString(), out long l) ? l : 0L,
                _ => 0L,
            };
        }

        public void EncodeInternal(long input, List<byte> output)
        {
            if (input >= int.MinValue && input <= int.MaxValue)
                EncodeI32((int)input, output);
            else
            {
                output.Add(238);
                output.AddRange(LE(BitConverter.GetBytes(input)));
            }
        }

        public long DecodeInternal(byte[] data, ref int offset, bool keepUnrecognizedValues) =>
            DecodeNumber(data, ref offset);

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Int64);
    }

    private const ulong MaxSafeHash64Json = 9_007_199_254_740_991UL;

    private sealed class Hash64Adapter : ITypeAdapter<ulong>
    {
        public bool IsDefaultInternal(ulong input) => input == 0;

        public void ToJsonInternal(ulong input, string? eolIndent, StringBuilder output)
        {
            if (input <= MaxSafeHash64Json)
                output.Append(input);
            else
            {
                output.Append('"');
                output.Append(input);
                output.Append('"');
            }
        }

        public ulong FromJsonInternal(JsonElement json, bool keepUnrecognizedValues)
        {
            return json.ValueKind switch
            {
                JsonValueKind.Number =>
                    json.TryGetUInt64(out ulong u) ? u :
                    json.TryGetDouble(out double d) ? (d < 0.0 ? 0UL : (ulong)Math.Round(d)) : 0UL,
                JsonValueKind.String =>
                    ulong.TryParse(json.GetString(), out ulong u) ? u : 0UL,
                _ => 0UL,
            };
        }

        public void EncodeInternal(ulong input, List<byte> output)
        {
            if (input <= 231) { output.Add((byte)input); }
            else if (input <= 65535) { output.Add(232); output.AddRange(LE(BitConverter.GetBytes((ushort)input))); }
            else if (input <= 4_294_967_295UL) { output.Add(233); output.AddRange(LE(BitConverter.GetBytes((uint)input))); }
            else { output.Add(234); output.AddRange(LE(BitConverter.GetBytes(input))); }
        }

        public ulong DecodeInternal(byte[] data, ref int offset, bool keepUnrecognizedValues) =>
            (ulong)DecodeNumber(data, ref offset);

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Hash64);
    }

    // ---- Remaining primitive adapters ----


    private sealed class Float32Adapter : ITypeAdapter<float>
    {
        public bool IsDefaultInternal(float input) => input == 0f;

        public void ToJsonInternal(float input, string? eolIndent, StringBuilder output) =>
            output.Append(input.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        public float FromJsonInternal(JsonElement json, bool keepUnrecognizedValues)
        {
            return json.ValueKind switch
            {
                JsonValueKind.Number =>
                    json.TryGetSingle(out float f) ? f : (float)json.GetDouble(),
                JsonValueKind.String =>
                    float.TryParse(json.GetString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out float f)
                        ? f : 0f,
                _ => 0f,
            };
        }

        public void EncodeInternal(float input, List<byte> output)
        {
            if (input == 0f)
            {
                output.Add(0);
            }
            else
            {
                output.Add(233);
                output.AddRange(LE(BitConverter.GetBytes(BitConverter.SingleToUInt32Bits(input))));
            }
        }

        public float DecodeInternal(byte[] data, ref int offset, bool keepUnrecognizedValues)
            => BitConverter.UInt32BitsToSingle((uint)DecodeNumber(data, ref offset));

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Float32);
    }

    private sealed class Float64Adapter : ITypeAdapter<double>
    {
        public bool IsDefaultInternal(double input) => input == 0d;

        public void ToJsonInternal(double input, string? eolIndent, StringBuilder output) =>
            output.Append(input.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        public double FromJsonInternal(JsonElement json, bool keepUnrecognizedValues)
        {
            return json.ValueKind switch
            {
                JsonValueKind.Number => json.GetDouble(),
                JsonValueKind.String =>
                    double.TryParse(json.GetString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double d)
                        ? d : 0d,
                _ => 0d,
            };
        }

        public void EncodeInternal(double input, List<byte> output)
        {
            if (input == 0d)
            {
                output.Add(0);
            }
            else
            {
                output.Add(234);
                output.AddRange(LE(BitConverter.GetBytes(BitConverter.DoubleToUInt64Bits(input))));
            }
        }

        public double DecodeInternal(byte[] data, ref int offset, bool keepUnrecognizedValues)
            => BitConverter.Int64BitsToDouble((long)DecodeNumber(data, ref offset));

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Float64);
    }

    private sealed class BytesAdapter : ITypeAdapter<ImmutableBytes>
    {
        public bool IsDefaultInternal(ImmutableBytes input) => input.IsEmpty;

        // Dense: standard base64 with = padding.
        // Readable: "hex:" + lowercase hex string.
        public void ToJsonInternal(ImmutableBytes input, string? eolIndent, StringBuilder output)
        {
            output.Append('"');
            if (eolIndent != null)
            {
                output.Append("hex:");
                output.Append(EncodeHex(input.Span));
            }
            else
            {
                output.Append(Convert.ToBase64String(input.Span));
            }
            output.Append('"');
        }

        public ImmutableBytes FromJsonInternal(JsonElement json, bool keepUnrecognizedValues)
        {
            if (json.ValueKind != JsonValueKind.String) return ImmutableBytes.Empty;
            string s = json.GetString()!;
            return s.StartsWith("hex:", StringComparison.Ordinal)
                ? ImmutableBytes.CopyFrom(DecodeHex(s.AsSpan(4)))
                : ImmutableBytes.CopyFrom(Convert.FromBase64String(s));
        }

        // empty → wire 244; nonempty → wire 245 + encode_uint32(len) + raw bytes.
        public void EncodeInternal(ImmutableBytes input, List<byte> output)
        {
            if (input.IsEmpty)
            {
                output.Add(244);
            }
            else
            {
                output.Add(245);
                EncodeUint32((uint)input.Length, output);
                output.AddRange(input.ToArray());
            }
        }

        public ImmutableBytes DecodeInternal(byte[] data, ref int offset, bool keepUnrecognizedValues)
        {
            byte wire = ReadU8(data, ref offset);
            if (wire == 0 || wire == 244) return ImmutableBytes.Empty;
            int n = (int)DecodeNumber(data, ref offset);
            if (offset + n > data.Length) throw new InvalidOperationException("Unexpected end of input");
            byte[] result = new byte[n];
            System.Array.Copy(data, offset, result, 0, n);
            offset += n;
            return ImmutableBytes.CopyFrom(result);
        }

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Bytes);
    }

    private sealed class TimestampAdapter : ITypeAdapter<DateTimeOffset>
    {
        public bool IsDefaultInternal(DateTimeOffset input) => DateTimeOffsetToMillis(input) == 0;

        // Dense: unix millis as a JSON number.
        // Readable: {"unix_millis": N, "formatted": "<ISO-8601>"} with indentation.
        public void ToJsonInternal(DateTimeOffset input, string? eolIndent, StringBuilder output)
        {
            long ms = DateTimeOffsetToMillis(input);
            if (eolIndent != null)
            {
                string child = eolIndent + "  ";
                output.Append('{');
                output.Append(child);
                output.Append("\"unix_millis\": ");
                output.Append(ms);
                output.Append(',');
                output.Append(child);
                output.Append("\"formatted\": \"");
                output.Append(MillisToIso8601(ms));
                output.Append('"');
                output.Append(eolIndent);
                output.Append('}');
            }
            else
            {
                output.Append(ms);
            }
        }

        public DateTimeOffset FromJsonInternal(JsonElement json, bool keepUnrecognizedValues)
        {
            long ms;
            switch (json.ValueKind)
            {
                case JsonValueKind.Number:
                    ms = json.TryGetInt64(out long i) ? i : (long)Math.Round(json.GetDouble());
                    break;
                case JsonValueKind.String:
                    ms = double.TryParse(json.GetString(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double d)
                        ? (long)Math.Round(d) : 0L;
                    break;
                case JsonValueKind.Object:
                    if (json.TryGetProperty("unix_millis", out JsonElement field))
                        return FromJsonInternal(field, false);
                    ms = 0;
                    break;
                default:
                    ms = 0;
                    break;
            }
            return MillisToDateTimeOffset(ms);
        }

        // ms == 0 → wire 0; else → wire 239 + i64 LE.
        public void EncodeInternal(DateTimeOffset input, List<byte> output)
        {
            long ms = DateTimeOffsetToMillis(input);
            if (ms == 0) { output.Add(0); }
            else { output.Add(239); output.AddRange(LE(BitConverter.GetBytes(ms))); }
        }

        public DateTimeOffset DecodeInternal(byte[] data, ref int offset, bool keepUnrecognizedValues) =>
            MillisToDateTimeOffset(DecodeNumber(data, ref offset));

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Timestamp);
    }

    private sealed class StringAdapter : ITypeAdapter<string>
    {
        public bool IsDefaultInternal(string input) => input.Length == 0;

        // Same in both dense and readable modes — always a JSON string.
        public void ToJsonInternal(string input, string? eolIndent, StringBuilder output) =>
            WriteJsonString(input, output);

        public string FromJsonInternal(JsonElement json, bool keepUnrecognizedValues)
        {
            return json.ValueKind switch
            {
                JsonValueKind.String => json.GetString()!,
                _ => "",
            };
        }

        // empty → wire 242; nonempty → wire 243 + encode_uint32(len) + UTF-8 bytes.
        public void EncodeInternal(string input, List<byte> output)
        {
            if (input.Length == 0)
            {
                output.Add(242);
            }
            else
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(input);
                output.Add(243);
                EncodeUint32((uint)utf8.Length, output);
                output.AddRange(utf8);
            }
        }

        public string DecodeInternal(byte[] data, ref int offset, bool keepUnrecognizedValues)
        {
            byte wire = ReadU8(data, ref offset);
            if (wire == 0 || wire == 242) return "";
            int n = (int)DecodeNumber(data, ref offset);
            if (offset + n > data.Length) throw new InvalidOperationException("Unexpected end of input");
            string s = Encoding.UTF8.GetString(data, offset, n);
            offset += n;
            return s;
        }

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.String);
    }

    // ---- Composite serializers ----

    private sealed class OptionalRefAdapter_<T>(Serializer<T> inner) : ITypeAdapter<T?> where T : class
    {
        public bool IsDefaultInternal(T? v) => v is null;

        public void ToJsonInternal(T? v, string? eolIndent, StringBuilder sb)
        {
            if (v is null) sb.Append("null");
            else inner.ToJsonInternal(v, eolIndent, sb);
        }

        public T? FromJsonInternal(JsonElement json, bool keep)
        {
            if (json.ValueKind == JsonValueKind.Null) return null;
            var result = inner.FromJsonInternal(json, keep);
            return inner.IsDefaultInternal(result) ? null : result;
        }

        public void EncodeInternal(T? v, List<byte> output)
        {
            if (v is null) output.Add(0);
            else inner.EncodeInternal(v, output);
        }

        public T? DecodeInternal(byte[] data, ref int offset, bool keep)
        {
            var result = inner.DecodeInternal(data, ref offset, keep);
            return inner.IsDefaultInternal(result) ? null : result;
        }

        public TypeDescriptor TypeDescriptor { get; } = new OptionalDescriptor(inner.TypeDescriptor);
    }

    private sealed class OptionalValueAdapter_<T>(Serializer<T> inner) : ITypeAdapter<T?> where T : struct
    {
        public bool IsDefaultInternal(T? v) => v is null;

        public void ToJsonInternal(T? v, string? eolIndent, StringBuilder sb)
        {
            if (v is null) sb.Append("null");
            else inner.ToJsonInternal(v.Value, eolIndent, sb);
        }

        public T? FromJsonInternal(JsonElement json, bool keep)
        {
            if (json.ValueKind == JsonValueKind.Null) return null;
            var result = inner.FromJsonInternal(json, keep);
            return inner.IsDefaultInternal(result) ? null : (T?)result;
        }

        public void EncodeInternal(T? v, List<byte> output)
        {
            if (v is null) output.Add(0);
            else inner.EncodeInternal(v.Value, output);
        }

        public T? DecodeInternal(byte[] data, ref int offset, bool keep)
        {
            var result = inner.DecodeInternal(data, ref offset, keep);
            return inner.IsDefaultInternal(result) ? null : (T?)result;
        }

        public TypeDescriptor TypeDescriptor { get; } = new OptionalDescriptor(inner.TypeDescriptor);
    }

    private sealed class ArrayAdapter_<T>(Serializer<T> inner) : ITypeAdapter<ImmutableList<T>>
    {
        public bool IsDefaultInternal(ImmutableList<T> v) => v.Count == 0;

        public void ToJsonInternal(ImmutableList<T> v, string? eolIndent, StringBuilder sb)
        {
            sb.Append('[');
            for (int i = 0; i < v.Count; i++)
            {
                if (i > 0) sb.Append(',');
                string? childIndent = eolIndent != null ? eolIndent + "  " : null;
                if (childIndent != null) sb.Append(childIndent);
                inner.ToJsonInternal(v[i], childIndent, sb);
            }
            if (eolIndent != null && v.Count > 0) sb.Append(eolIndent);
            sb.Append(']');
        }

        public ImmutableList<T> FromJsonInternal(JsonElement json, bool keep)
        {
            if (json.ValueKind != JsonValueKind.Array) return ImmutableList<T>.Empty;
            var builder = ImmutableList.CreateBuilder<T>();
            foreach (var item in json.EnumerateArray()) builder.Add(inner.FromJsonInternal(item, keep));
            return builder.ToImmutable();
        }

        public void EncodeInternal(ImmutableList<T> v, List<byte> output)
        {
            int n = v.Count;
            if (n == 0) { output.Add(246); return; }
            if (n <= 3) output.Add((byte)(246 + n));
            else { output.Add(250); EncodeUint32((uint)n, output); }
            foreach (var item in v) inner.EncodeInternal(item, output);
        }

        public ImmutableList<T> DecodeInternal(byte[] data, ref int offset, bool keep)
        {
            if (offset >= data.Length) return ImmutableList<T>.Empty;
            byte wire = ReadU8(data, ref offset);
            if (wire == 0 || wire == 246) return ImmutableList<T>.Empty;

            int count = wire == 250 ? (int)DecodeNumber(data, ref offset) : wire - 246;
            var builder = ImmutableList.CreateBuilder<T>();
            for (int i = 0; i < count; i++) builder.Add(inner.DecodeInternal(data, ref offset, keep));
            return builder.ToImmutable();
        }

        public TypeDescriptor TypeDescriptor { get; } = new ArrayDescriptor(inner.TypeDescriptor);
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
