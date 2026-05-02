using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SkirClient;

/// <summary>
/// A fully functional <see cref="Serializer{T}"/> for a Skir struct type.
/// <para>
/// Usage: construct, call <see cref="AddField{V}"/> for each field and
/// <see cref="AddRemovedNumber"/> for each removed slot, then call
/// <see cref="Finalize_"/> exactly once.
/// </para>
/// </summary>
public sealed class StructAdapter<T> : Serializer<T> where T : struct
{
    // ---- per-field type-erased interface ------------------------------------

    private interface IFieldEntry
    {
        string Name { get; }
        int Number { get; }
        TypeDescriptor FieldType { get; }
        bool IsDefault(T value);
        void ToJson(T value, string? eolIndent, StringBuilder sb);
        T SetFromJson(T value, JsonElement json, bool keep);
        void Encode(T value, List<byte> output);
        T SetFromBytes(T value, byte[] data, ref int offset, bool keep);
    }

    private sealed class TypedField<V>(
        string name, int number, Serializer<V> ser,
        Func<T, V> getter, Func<T, V, T> setter) : IFieldEntry
    {
        public string Name => name;
        public int Number => number;
        public TypeDescriptor FieldType => ser.TypeDescriptor;
        public bool IsDefault(T v) => ser.IsDefaultInternal(getter(v));
        public void ToJson(T v, string? eolIndent, StringBuilder sb) =>
            ser.ToJsonInternal(getter(v), eolIndent, sb);
        public T SetFromJson(T v, JsonElement json, bool keep) =>
            setter(v, ser.FromJsonInternal(json, keep));
        public void Encode(T v, List<byte> output) =>
            ser.EncodeInternal(getter(v), output);
        public T SetFromBytes(T v, byte[] data, ref int offset, bool keep) =>
            setter(v, ser.DecodeInternal(data, ref offset, keep));
    }

    // ---- state -------------------------------------------------------------

    private readonly T _default;
    private readonly string _modulePath;
    private readonly string _qualifiedName;
    private readonly List<IFieldEntry> _orderedFields = [];
    private readonly Dictionary<string, int> _nameToIndex = [];
    private readonly HashSet<int> _removedNumbers = [];
    private List<int?> _slotToIndex = [];
    private readonly StructDescriptor _descriptor;

    public StructAdapter(T defaultValue, string modulePath, string qualifiedName)
    {
        _default = defaultValue;
        _modulePath = modulePath;
        _qualifiedName = qualifiedName;
        _descriptor = new StructDescriptor(modulePath, qualifiedName, "");
    }

    // ---- builder -----------------------------------------------------------

    /// <summary>Registers a struct field. Must be called before <see cref="Finalize_"/>.</summary>
    public void AddField<V>(string name, int number, Serializer<V> serializer,
        Func<T, V> getter, Func<T, V, T> setter)
    {
        int idx = _orderedFields.Count;
        _orderedFields.Add(new TypedField<V>(name, number, serializer, getter, setter));
        _nameToIndex[name] = idx;
    }

    /// <summary>Marks a slot number as removed. Must be called before <see cref="Finalize_"/>.</summary>
    public void AddRemovedNumber(int number) => _removedNumbers.Add(number);

    /// <summary>
    /// Finalizes the adapter. Must be called exactly once, after all
    /// <see cref="AddField{V}"/> and <see cref="AddRemovedNumber"/> calls.
    /// </summary>
    public void Finalize_()
    {
        _orderedFields.Sort((a, b) => a.Number.CompareTo(b.Number));
        _nameToIndex.Clear();
        for (int i = 0; i < _orderedFields.Count; i++)
            _nameToIndex[_orderedFields[i].Name] = i;

        int maxSlot = -1;
        foreach (var f in _orderedFields) if (f.Number > maxSlot) maxSlot = f.Number;
        foreach (int r in _removedNumbers) if (r > maxSlot) maxSlot = r;

        _slotToIndex = new List<int?>(maxSlot + 1);
        for (int i = 0; i <= maxSlot; i++) _slotToIndex.Add(null);
        for (int i = 0; i < _orderedFields.Count; i++)
            _slotToIndex[_orderedFields[i].Number] = i;

        var fields = _orderedFields
            .Select(f => new StructField(f.Name, f.Number, f.FieldType))
            .ToList();
        _descriptor.SetFields(fields);
        _descriptor.SetRemovedNumbers(_removedNumbers);
    }

    // ---- Serializer<T> public API ------------------------------------------

    public override string ToJson(T value, bool readable = false)
    {
        var sb = new StringBuilder();
        ToJsonImpl(value, readable ? "\n" : null, sb);
        return sb.ToString();
    }

    public override byte[] ToBytes(T value)
    {
        var buf = new List<byte>(8) { (byte)'s', (byte)'k', (byte)'i', (byte)'r' };
        EncodeImpl(value, buf);
        return [.. buf];
    }

    public override T FromJson(string json, bool keepUnrecognizedValues = false)
    {
        using var doc = JsonDocument.Parse(json);
        return FromJsonImpl(doc.RootElement, keepUnrecognizedValues);
    }

    public override T FromBytes(byte[] bytes, bool keepUnrecognizedValues = false)
    {
        if (bytes.Length >= 4 &&
            bytes[0] == 's' && bytes[1] == 'k' && bytes[2] == 'i' && bytes[3] == 'r')
        {
            int offset = 4;
            return DecodeImpl(bytes, ref offset, keepUnrecognizedValues);
        }
        return FromJson(Encoding.UTF8.GetString(bytes), keepUnrecognizedValues);
    }

    public override TypeDescriptor TypeDescriptor => _descriptor;

    // ---- internal virtuals -------------------------------------------------

    internal override bool IsDefaultInternal(T value)
    {
        foreach (var f in _orderedFields)
            if (!f.IsDefault(value)) return false;
        return true;
    }

    internal override void ToJsonInternal(T value, string? eolIndent, StringBuilder output) =>
        ToJsonImpl(value, eolIndent, output);

    internal override T FromJsonInternal(JsonElement json, bool keepUnrecognized) =>
        FromJsonImpl(json, keepUnrecognized);

    internal override void EncodeInternal(T value, List<byte> output) =>
        EncodeImpl(value, output);

    internal override T DecodeInternal(byte[] data, ref int offset, bool keepUnrecognized) =>
        DecodeImpl(data, ref offset, keepUnrecognized);

    // ---- JSON impl ---------------------------------------------------------

    private void ToJsonImpl(T value, string? eolIndent, StringBuilder sb)
    {
        if (eolIndent != null)
        {
            sb.Append('{');
            string childIndent = eolIndent + "  ";
            bool first = true;
            foreach (var f in _orderedFields)
            {
                if (f.IsDefault(value)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(childIndent);
                Serializers.WriteJsonString_(f.Name, sb);
                sb.Append(": ");
                f.ToJson(value, childIndent, sb);
            }
            if (!first) sb.Append(eolIndent);
            sb.Append('}');
        }
        else
        {
            sb.Append('[');
            int slotCount = GetSlotCount(value);
            for (int i = 0; i < slotCount; i++)
            {
                if (i > 0) sb.Append(',');
                if (i < _slotToIndex.Count && _slotToIndex[i] is int idx)
                    _orderedFields[idx].ToJson(value, null, sb);
                else
                    sb.Append('0');
            }
            sb.Append(']');
        }
    }

    private T FromJsonImpl(JsonElement json, bool keep) =>
        json.ValueKind switch
        {
            JsonValueKind.Number => _default,
            JsonValueKind.Array => FromDenseJson(json, keep),
            JsonValueKind.Object => FromReadableJson(json),
            _ => _default,
        };

    private T FromDenseJson(JsonElement arr, bool keep)
    {
        T t = _default;
        int i = 0;
        int fill = Math.Min(arr.GetArrayLength(), _slotToIndex.Count);
        foreach (var item in arr.EnumerateArray())
        {
            if (i >= fill) break;
            if (_slotToIndex[i] is int idx)
                t = _orderedFields[idx].SetFromJson(t, item, keep);
            i++;
        }
        return t;
    }

    private T FromReadableJson(JsonElement obj)
    {
        T t = _default;
        foreach (var prop in obj.EnumerateObject())
            if (_nameToIndex.TryGetValue(prop.Name, out int idx))
                t = _orderedFields[idx].SetFromJson(t, prop.Value, false);
        return t;
    }

    // ---- Binary encode / decode --------------------------------------------

    private void EncodeImpl(T value, List<byte> output)
    {
        int slotCount = GetSlotCount(value);
        if (slotCount == 0) { output.Add(246); return; }
        if (slotCount <= 3) output.Add((byte)(246 + slotCount));
        else { output.Add(250); Serializers.EncodeUint32_((uint)slotCount, output); }

        int recognized = _slotToIndex.Count;
        for (int i = 0; i < slotCount; i++)
        {
            if (i < recognized && _slotToIndex[i] is int idx)
                _orderedFields[idx].Encode(value, output);
            else
                output.Add(0);
        }
    }

    private T DecodeImpl(byte[] data, ref int offset, bool keep)
    {
        if (offset >= data.Length) return _default;
        byte wire = Serializers.ReadU8_(data, ref offset);
        if (wire == 0 || wire == 246) return _default;

        int encodedSlotCount = wire == 250
            ? (int)Serializers.DecodeNumber(data, ref offset)
            : wire - 246;

        T t = _default;
        int recognized = _slotToIndex.Count;
        int fill = Math.Min(encodedSlotCount, recognized);

        for (int i = 0; i < fill; i++)
        {
            if (_slotToIndex[i] is int idx)
                t = _orderedFields[idx].SetFromBytes(t, data, ref offset, keep);
            else
                Serializers.SkipValue_(data, ref offset);
        }
        for (int i = fill; i < encodedSlotCount; i++)
            Serializers.SkipValue_(data, ref offset);

        return t;
    }

    private int GetSlotCount(T value)
    {
        for (int i = _orderedFields.Count - 1; i >= 0; i--)
            if (!_orderedFields[i].IsDefault(value))
                return _orderedFields[i].Number + 1;
        return 0;
    }
}
