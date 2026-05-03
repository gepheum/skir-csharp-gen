using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace SkirClient.Internal;

/// <summary>
/// Builds and caches per-list index maps.
/// </summary>
public sealed class Indexer<TValue, TKey> where TKey : notnull
{
    private readonly Func<TValue, TKey> _keySelector;
    private readonly ConditionalWeakTable<ImmutableList<TValue>, ImmutableDictionary<TKey, TValue>> _cache = [];
    private readonly System.Threading.Lock _mutex = new();

    public Indexer(Func<TValue, TKey> keySelector)
    {
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
    }

    public ImmutableDictionary<TKey, TValue> Index(ImmutableList<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        lock (_mutex)
        {
            if (_cache.TryGetValue(values, out var cached))
            {
                return cached;
            }

            var map = BuildIndex(values);
            _cache.Add(values, map);
            return map;
        }
    }

    private ImmutableDictionary<TKey, TValue> BuildIndex(ImmutableList<TValue> values)
    {
        var builder = ImmutableDictionary.CreateBuilder<TKey, TValue>();
        foreach (var value in values)
        {
            builder[_keySelector(value)] = value;
        }

        return builder.ToImmutable();
    }
}