using System.Collections.Immutable;

namespace SkirClient.Internal;

/// <summary>
/// Builds and caches per-array index maps.
/// </summary>
public sealed class Indexer<TValue, TKey> where TKey : notnull
{
    private readonly Func<TValue, TKey> _keySelector;
    private readonly Dictionary<ImmutableArray<TValue>, ImmutableDictionary<TKey, TValue>> _cache = [];
    private readonly System.Threading.Lock _mutex = new();

    public Indexer(Func<TValue, TKey> keySelector)
    {
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
    }

    public ImmutableDictionary<TKey, TValue> Index(ImmutableArray<TValue> values)
    {
        lock (_mutex)
        {
            if (_cache.TryGetValue(values, out var cached))
            {
                return cached;
            }

            var map = BuildIndex(values);
            _cache[values] = map;
            return map;
        }
    }

    private ImmutableDictionary<TKey, TValue> BuildIndex(ImmutableArray<TValue> values)
    {
        var builder = ImmutableDictionary.CreateBuilder<TKey, TValue>();
        foreach (var value in values)
        {
            builder[_keySelector(value)] = value;
        }

        return builder.ToImmutable();
    }
}