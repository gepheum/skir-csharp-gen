using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using SkirClient.Internal;

namespace SkirClient;

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

    /// <summary>Serializer for a recursive struct field (<see cref="Recursive{T}"/>).</summary>
    public static Serializer<Recursive<T>> RecursiveSerializer<T>(Serializer<T> inner) where T : struct
        => new(new RecursiveAdapter_<T>(inner));

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
            case 240: return (long)Math.Round(BitConverter.UInt32BitsToSingle(ReadU32(data, ref offset)));
            case 241: return (long)Math.Round(BitConverter.Int64BitsToDouble((long)ReadU64(data, ref offset)));
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
        => BinaryUtils.SkipValue(data, ref offset);

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

    // ---- Remaining primitive adapters ----


    private sealed class Float32Adapter : ITypeAdapter<float>
    {
        public bool IsDefault(float input) => input == 0f;

        public void ToJson(float input, string? eolIndent, StringBuilder output)
        {
            if (float.IsNaN(input)) { WriteJsonString("NaN", output); return; }
            if (float.IsPositiveInfinity(input)) { WriteJsonString("Infinity", output); return; }
            if (float.IsNegativeInfinity(input)) { WriteJsonString("-Infinity", output); return; }
            output.Append(input.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        public float FromJson(JsonElement json, bool keepUnrecognizedValues)
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

        public void Encode(float input, List<byte> output)
        {
            if (input == 0f)
            {
                output.Add(0);
            }
            else
            {
                output.Add(240);
                output.AddRange(LE(BitConverter.GetBytes(BitConverter.SingleToUInt32Bits(input))));
            }
        }

        public float Decode(byte[] data, ref int offset, bool keepUnrecognizedValues)
        {
            byte wire = ReadU8(data, ref offset);
            if (wire == 240)
                return BitConverter.UInt32BitsToSingle(ReadU32(data, ref offset));
            if (wire == 241)
                return (float)BitConverter.Int64BitsToDouble((long)ReadU64(data, ref offset));
            return (float)DecodeNumberBody(wire, data, ref offset);
        }

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Float32);
    }

    private sealed class Float64Adapter : ITypeAdapter<double>
    {
        public bool IsDefault(double input) => input == 0d;

        public void ToJson(double input, string? eolIndent, StringBuilder output)
        {
            if (double.IsNaN(input)) { WriteJsonString("NaN", output); return; }
            if (double.IsPositiveInfinity(input)) { WriteJsonString("Infinity", output); return; }
            if (double.IsNegativeInfinity(input)) { WriteJsonString("-Infinity", output); return; }
            output.Append(input.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        public double FromJson(JsonElement json, bool keepUnrecognizedValues)
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

        public void Encode(double input, List<byte> output)
        {
            if (input == 0d)
            {
                output.Add(0);
            }
            else
            {
                output.Add(241);
                output.AddRange(LE(BitConverter.GetBytes(BitConverter.DoubleToUInt64Bits(input))));
            }
        }

        public double Decode(byte[] data, ref int offset, bool keepUnrecognizedValues)
        {
            byte wire = ReadU8(data, ref offset);
            if (wire == 241)
                return BitConverter.Int64BitsToDouble((long)ReadU64(data, ref offset));
            if (wire == 240)
                return BitConverter.UInt32BitsToSingle(ReadU32(data, ref offset));
            return DecodeNumberBody(wire, data, ref offset);
        }

        public TypeDescriptor TypeDescriptor { get; } = new PrimitiveDescriptor(PrimitiveType.Float64);
    }

    private sealed class BytesAdapter : ITypeAdapter<ImmutableBytes>
    {
        public bool IsDefault(ImmutableBytes input) => input.IsEmpty;

        // Dense: standard base64 with = padding.
        // Readable: "hex:" + lowercase hex string.
        public void ToJson(ImmutableBytes input, string? eolIndent, StringBuilder output)
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

        public ImmutableBytes FromJson(JsonElement json, bool keepUnrecognizedValues)
        {
            if (json.ValueKind != JsonValueKind.String) return ImmutableBytes.Empty;
            string s = json.GetString()!;
            return s.StartsWith("hex:", StringComparison.Ordinal)
                ? ImmutableBytes.CopyFrom(DecodeHex(s.AsSpan(4)))
                : ImmutableBytes.CopyFrom(Convert.FromBase64String(s));
        }

        // empty → wire 244; nonempty → wire 245 + encode_uint32(len) + raw bytes.
        public void Encode(ImmutableBytes input, List<byte> output)
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

        public ImmutableBytes Decode(byte[] data, ref int offset, bool keepUnrecognizedValues)
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

    // ---- Composite serializers ----

    private sealed class OptionalRefAdapter_<T>(Serializer<T> inner) : ITypeAdapter<T?> where T : class
    {
        public bool IsDefault(T? v) => v is null;

        public void ToJson(T? v, string? eolIndent, StringBuilder sb)
        {
            if (v is null) sb.Append("null");
            else inner.ToJson(v, eolIndent, sb);
        }

        public T? FromJson(JsonElement json, bool keep)
        {
            if (json.ValueKind == JsonValueKind.Null) return null;
            var result = inner.FromJson(json, keep);
            return inner.IsDefault(result) ? null : result;
        }

        public void Encode(T? v, List<byte> output)
        {
            if (v is null) output.Add(255);
            else inner.Encode(v, output);
        }

        public T? Decode(byte[] data, ref int offset, bool keep)
        {
            if (offset < data.Length && data[offset] == 255)
            {
                offset++;
                return null;
            }
            var result = inner.Decode(data, ref offset, keep);
            return inner.IsDefault(result) ? null : result;
        }

        public TypeDescriptor TypeDescriptor { get; } = new OptionalDescriptor(inner.TypeDescriptor);
    }

    private sealed class RecursiveAdapter_<T>(Serializer<T> inner) : ITypeAdapter<Recursive<T>> where T : struct
    {
        public bool IsDefault(Recursive<T> r) =>
            r.IsDefaultValue || inner.IsDefault(r.Value);

        public void ToJson(Recursive<T> r, string? eolIndent, StringBuilder sb) =>
            inner.ToJson(r.IsDefaultValue ? default : r.Value!, eolIndent, sb);

        public Recursive<T> FromJson(JsonElement json, bool keep)
        {
            T value = inner.FromJson(json, keep);
            return inner.IsDefault(value) ? Recursive<T>.DefaultValue : Recursive<T>.FromValue(value);
        }

        public void Encode(Recursive<T> r, List<byte> output) =>
            inner.Encode(r.IsDefaultValue ? default : r.Value!, output);

        public Recursive<T> Decode(byte[] data, ref int offset, bool keep)
        {
            T value = inner.Decode(data, ref offset, keep);
            return inner.IsDefault(value) ? Recursive<T>.DefaultValue : Recursive<T>.FromValue(value);
        }

        public TypeDescriptor TypeDescriptor => inner.TypeDescriptor;
    }

    private sealed class OptionalValueAdapter_<T>(Serializer<T> inner) : ITypeAdapter<T?> where T : struct
    {
        public bool IsDefault(T? v) => v is null;

        public void ToJson(T? v, string? eolIndent, StringBuilder sb)
        {
            if (v is null) sb.Append("null");
            else inner.ToJson(v.Value, eolIndent, sb);
        }

        public T? FromJson(JsonElement json, bool keep)
        {
            if (json.ValueKind == JsonValueKind.Null) return null;
            var result = inner.FromJson(json, keep);
            return inner.IsDefault(result) ? null : (T?)result;
        }

        public void Encode(T? v, List<byte> output)
        {
            if (v is null) output.Add(255);
            else inner.Encode(v.Value, output);
        }

        public T? Decode(byte[] data, ref int offset, bool keep)
        {
            if (offset < data.Length && data[offset] == 255)
            {
                offset++;
                return null;
            }
            var result = inner.Decode(data, ref offset, keep);
            return inner.IsDefault(result) ? null : (T?)result;
        }

        public TypeDescriptor TypeDescriptor { get; } = new OptionalDescriptor(inner.TypeDescriptor);
    }

    private sealed class ArrayAdapter_<T>(Serializer<T> inner) : ITypeAdapter<ImmutableList<T>>
    {
        public bool IsDefault(ImmutableList<T> v) => v.Count == 0;

        public void ToJson(ImmutableList<T> v, string? eolIndent, StringBuilder sb)
        {
            sb.Append('[');
            for (int i = 0; i < v.Count; i++)
            {
                if (i > 0) sb.Append(',');
                string? childIndent = eolIndent != null ? eolIndent + "  " : null;
                if (childIndent != null) sb.Append(childIndent);
                inner.ToJson(v[i], childIndent, sb);
            }
            if (eolIndent != null && v.Count > 0) sb.Append(eolIndent);
            sb.Append(']');
        }

        public ImmutableList<T> FromJson(JsonElement json, bool keep)
        {
            if (json.ValueKind != JsonValueKind.Array) return ImmutableList<T>.Empty;
            var builder = ImmutableList.CreateBuilder<T>();
            foreach (var item in json.EnumerateArray()) builder.Add(inner.FromJson(item, keep));
            return builder.ToImmutable();
        }

        public void Encode(ImmutableList<T> v, List<byte> output)
        {
            int n = v.Count;
            if (n == 0) { output.Add(246); return; }
            if (n <= 3) output.Add((byte)(246 + n));
            else { output.Add(250); EncodeUint32((uint)n, output); }
            foreach (var item in v) inner.Encode(item, output);
        }

        public ImmutableList<T> Decode(byte[] data, ref int offset, bool keep)
        {
            if (offset >= data.Length) return ImmutableList<T>.Empty;
            byte wire = ReadU8(data, ref offset);
            if (wire == 0 || wire == 246) return ImmutableList<T>.Empty;

            int count = wire == 250 ? (int)DecodeNumber(data, ref offset) : wire - 246;
            var builder = ImmutableList.CreateBuilder<T>();
            for (int i = 0; i < count; i++) builder.Add(inner.Decode(data, ref offset, keep));
            return builder.ToImmutable();
        }

        public TypeDescriptor TypeDescriptor { get; } = new ArrayDescriptor(inner.TypeDescriptor);
    }
}
