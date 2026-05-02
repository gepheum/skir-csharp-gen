using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace SkirClient;

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

