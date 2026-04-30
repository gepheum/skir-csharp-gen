using System;
using System.Collections;
using System.Collections.Generic;
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
    public static Serializer<int>      Int32     { get; } = new TypeAdapterSerializer<int>(new Int32Adapter());
    public static Serializer<long>     Int64     { get; } = new TypeAdapterSerializer<long>(new Int64Adapter());
    public static Serializer<ulong>    Hash64    { get; } = new TypeAdapterSerializer<ulong>(new Hash64Adapter());
    public static Serializer<float>    Float32   { get; } = new Float32Serializer_();
    public static Serializer<double>   Float64   { get; } = new Float64Serializer_();
    public static Serializer<string>   String    { get; } = new TypeAdapterSerializer<string>(new StringAdapter());
    public static Serializer<byte[]>   Bytes     { get; } = new TypeAdapterSerializer<byte[]>(new BytesAdapter());
    public static Serializer<DateTimeOffset> Timestamp { get; } = new TypeAdapterSerializer<DateTimeOffset>(new TimestampAdapter());

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

    // ---- Binary wire-format helpers ----

    // Returns bytes in little-endian order regardless of host endianness.
    private static byte[] LE(byte[] bytes)
    {
        if (!BitConverter.IsLittleEndian) System.Array.Reverse(bytes);
        return bytes;
    }

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

    private static ushort ReadU16(byte[] data, ref int offset)
    {
        if (offset + 2 > data.Length) throw new InvalidOperationException("Unexpected end of input");
        var v = BitConverter.ToUInt16(data, offset);
        offset += 2;
        return v;
    }

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

    // Encodes bytes as a lowercase hexadecimal string.
    private static string EncodeHex(byte[] bytes)
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
        public bool IsDefault(int input) => input == 0;

        // Same in both dense and readable modes — always a JSON number.
        public void ToJson(int input, string? eolIndent, StringBuilder output) =>
            output.Append(input);

        public int FromJson(JsonElement json, bool keepUnrecognizedValues)
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

        public void Encode(int input, List<byte> output) => EncodeI32(input, output);

        public int Decode(byte[] data, ref int offset, bool keepUnrecognizedValues) =>
            (int)DecodeNumber(data, ref offset);

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Int32);
    }

    // Values within [-MAX_SAFE_INT, MAX_SAFE_INT] are emitted as JSON numbers;
    // larger values are quoted strings, matching JS Number.MAX_SAFE_INTEGER.
    private const long MaxSafeInt64Json = 9_007_199_254_740_991L;

    private sealed class Int64Adapter : ITypeAdapter<long>
    {
        public bool IsDefault(long input) => input == 0;

        public void ToJson(long input, string? eolIndent, StringBuilder output)
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

        public long FromJson(JsonElement json, bool keepUnrecognizedValues)
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

        public void Encode(long input, List<byte> output)
        {
            if (input >= int.MinValue && input <= int.MaxValue)
                EncodeI32((int)input, output);
            else
            {
                output.Add(238);
                output.AddRange(LE(BitConverter.GetBytes(input)));
            }
        }

        public long Decode(byte[] data, ref int offset, bool keepUnrecognizedValues) =>
            DecodeNumber(data, ref offset);

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Int64);
    }

    private const ulong MaxSafeHash64Json = 9_007_199_254_740_991UL;

    private sealed class Hash64Adapter : ITypeAdapter<ulong>
    {
        public bool IsDefault(ulong input) => input == 0;

        public void ToJson(ulong input, string? eolIndent, StringBuilder output)
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

        public ulong FromJson(JsonElement json, bool keepUnrecognizedValues)
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

        public void Encode(ulong input, List<byte> output)
        {
            if (input <= 231) { output.Add((byte)input); }
            else if (input <= 65535) { output.Add(232); output.AddRange(LE(BitConverter.GetBytes((ushort)input))); }
            else if (input <= 4_294_967_295UL) { output.Add(233); output.AddRange(LE(BitConverter.GetBytes((uint)input))); }
            else { output.Add(234); output.AddRange(LE(BitConverter.GetBytes(input))); }
        }

        public ulong Decode(byte[] data, ref int offset, bool keepUnrecognizedValues) =>
            (ulong)DecodeNumber(data, ref offset);

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Hash64);
    }

    // ---- Remaining primitive stubs ----


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

    private sealed class TimestampAdapter : ITypeAdapter<DateTimeOffset>
    {
        public bool IsDefault(DateTimeOffset input) => DateTimeOffsetToMillis(input) == 0;

        // Dense: unix millis as a JSON number.
        // Readable: {"unix_millis": N, "formatted": "<ISO-8601>"} with indentation.
        public void ToJson(DateTimeOffset input, string? eolIndent, StringBuilder output)
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

        public DateTimeOffset FromJson(JsonElement json, bool keepUnrecognizedValues)
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
                        return FromJson(field, false);
                    ms = 0;
                    break;
                default:
                    ms = 0;
                    break;
            }
            return MillisToDateTimeOffset(ms);
        }

        // ms == 0 → wire 0; else → wire 239 + i64 LE.
        public void Encode(DateTimeOffset input, List<byte> output)
        {
            long ms = DateTimeOffsetToMillis(input);
            if (ms == 0) { output.Add(0); }
            else { output.Add(239); output.AddRange(LE(BitConverter.GetBytes(ms))); }
        }

        public DateTimeOffset Decode(byte[] data, ref int offset, bool keepUnrecognizedValues) =>
            MillisToDateTimeOffset(DecodeNumber(data, ref offset));

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Timestamp);
    }

    private sealed class StringAdapter : ITypeAdapter<string>
    {
        public bool IsDefault(string input) => input.Length == 0;

        // Same in both dense and readable modes — always a JSON string.
        public void ToJson(string input, string? eolIndent, StringBuilder output) =>
            WriteJsonString(input, output);

        public string FromJson(JsonElement json, bool keepUnrecognizedValues)
        {
            return json.ValueKind switch
            {
                JsonValueKind.String => json.GetString()!,
                _ => "",
            };
        }

        // empty → wire 242; nonempty → wire 243 + encode_uint32(len) + UTF-8 bytes.
        public void Encode(string input, List<byte> output)
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

        public string Decode(byte[] data, ref int offset, bool keepUnrecognizedValues)
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

    private sealed class BytesAdapter : ITypeAdapter<byte[]>
    {
        public bool IsDefault(byte[] input) => input.Length == 0;

        // Dense: standard base64 with = padding.
        // Readable: "hex:" + lowercase hex string.
        public void ToJson(byte[] input, string? eolIndent, StringBuilder output)
        {
            output.Append('"');
            if (eolIndent != null)
            {
                output.Append("hex:");
                output.Append(EncodeHex(input));
            }
            else
            {
                output.Append(Convert.ToBase64String(input));
            }
            output.Append('"');
        }

        public byte[] FromJson(JsonElement json, bool keepUnrecognizedValues)
        {
            if (json.ValueKind != JsonValueKind.String) return [];
            string s = json.GetString()!;
            return s.StartsWith("hex:", StringComparison.Ordinal)
                ? DecodeHex(s.AsSpan(4))
                : Convert.FromBase64String(s);
        }

        // empty → wire 244; nonempty → wire 245 + encode_uint32(len) + raw bytes.
        public void Encode(byte[] input, List<byte> output)
        {
            if (input.Length == 0)
            {
                output.Add(244);
            }
            else
            {
                output.Add(245);
                EncodeUint32((uint)input.Length, output);
                output.AddRange(input);
            }
        }

        public byte[] Decode(byte[] data, ref int offset, bool keepUnrecognizedValues)
        {
            byte wire = ReadU8(data, ref offset);
            if (wire == 0 || wire == 244) return [];
            int n = (int)DecodeNumber(data, ref offset);
            if (offset + n > data.Length) throw new InvalidOperationException("Unexpected end of input");
            byte[] result = new byte[n];
            System.Array.Copy(data, offset, result, 0, n);
            offset += n;
            return result;
        }

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Bytes);
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
