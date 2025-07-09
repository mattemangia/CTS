//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System.Collections.Generic;

namespace CTS
{
    // LRU Cache implementation
    public class LRUCache<TKey, TValue>
    {
        private readonly int _capacity;

        private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cacheMap =
            new Dictionary<TKey, LinkedListNode<CacheItem>>();

        private readonly LinkedList<CacheItem> _lruList =
            new LinkedList<CacheItem>();

        public LRUCache(int capacity) => _capacity = capacity;

        public TValue Get(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                return node.Value.Value;
            }
            return default;
        }

        public List<TKey> GetKeys()
        {
            return new List<TKey>(_cacheMap.Keys);
        }

        public void Add(TKey key, TValue val)
        {
            if (_cacheMap.Count >= _capacity)
            {
                var last = _lruList.Last;
                _cacheMap.Remove(last.Value.Key);
                _lruList.RemoveLast();
            }

            var item = new CacheItem(key, val);
            var node = new LinkedListNode<CacheItem>(item);
            _lruList.AddFirst(node);
            _cacheMap[key] = node;
        }

        private class CacheItem
        {
            public TKey Key { get; }
            public TValue Value { get; }

            public CacheItem(TKey k, TValue v) => (Key, Value) = (k, v);
        }
    }
}