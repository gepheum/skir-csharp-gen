using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace SkirClient.Internal;

/// <summary>
/// Internal serialization contract for a single type.
/// Implemented by concrete type adapters and wrapped by
/// <see cref="SkirClient.Serializer{T}"/>.
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
