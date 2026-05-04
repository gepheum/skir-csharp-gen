using System.Text;
using System.Text.Json;
using SkirClient.Internal;

namespace SkirClient;

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

    internal ITypeAdapter<T> Adapter => _adapter;

    /// <summary>
    /// Serializes <paramref name="value"/> to JSON.
    /// <para>
    /// When <paramref name="readable"/> is <c>false</c> (the default) the result is
    /// <em>dense</em> (field-number-based) JSON - safe for persistence and transport.
    /// Renaming a field in the .skir file does not break deserialization of previously
    /// persisted values.
    /// </para>
    /// <para>
    /// When <paramref name="readable"/> is <c>true</c> the result is human-readable,
    /// name-based, indented JSON - use this for debugging only.
    /// </para>
    /// </summary>
    public string ToJson(T value, MustNameArguments _ = default, bool readable = false)
    {
        var sb = new StringBuilder();
        _adapter.ToJson(value, readable ? "\n" : null, sb);
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
        _adapter.Encode(value, output);
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
    public T FromJson(string json, MustNameArguments _ = default, bool keepUnrecognizedValues = false)
    {
        using var doc = JsonDocument.Parse(json);
        return _adapter.FromJson(doc.RootElement, keepUnrecognizedValues);
    }

    /// <summary>
    /// Deserializes a value from binary format.
    /// When <paramref name="keepUnrecognizedValues"/> is <c>true</c>, unrecognized
    /// field data is preserved for round-trip fidelity.
    /// </summary>
    public T FromBytes(byte[] bytes, MustNameArguments _ = default, bool keepUnrecognizedValues = false)
    {
        if (bytes.Length >= 4 &&
            bytes[0] == 's' && bytes[1] == 'k' &&
            bytes[2] == 'i' && bytes[3] == 'r')
        {
            int offset = 4;
            return _adapter.Decode(bytes, ref offset, keepUnrecognizedValues);
        }
        // No magic prefix - treat payload as UTF-8 JSON.
        return FromJson(
            System.Text.Encoding.UTF8.GetString(bytes),
            MustNameArguments.GetDefault(),
            keepUnrecognizedValues: keepUnrecognizedValues);
    }

    /// <summary>Reflection descriptor for this type's schema.</summary>
    public TypeDescriptor TypeDescriptor => _adapter.TypeDescriptor;
}
