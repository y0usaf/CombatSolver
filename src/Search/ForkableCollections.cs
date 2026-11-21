using System.Collections;

namespace CombatSolver;

internal sealed class ForkableList<T> : IReadOnlyList<T>
{
    private sealed class Storage(List<T> values)
    {
        public List<T> Values { get; } = values;
        public volatile bool Shared;
    }

    private Storage _storage;

    public ForkableList()
        : this(new List<T>())
    {
    }

    public ForkableList(IEnumerable<T> values)
        : this(new List<T>(values))
    {
    }

    private ForkableList(List<T> values)
        => _storage = new Storage(values);

    private ForkableList(Storage storage)
        => _storage = storage;

    public int Count => _storage.Values.Count;
    public T this[int index]
    {
        get => _storage.Values[index];
        set
        {
            EnsureWritable();
            _storage.Values[index] = value;
        }
    }

    public ForkableList<T> Fork()
    {
        _storage.Shared = true;
        return new ForkableList<T>(_storage);
    }

    public void Add(T value)
    {
        EnsureWritable();
        _storage.Values.Add(value);
    }

    public void Insert(int index, T value)
    {
        EnsureWritable();
        _storage.Values.Insert(index, value);
    }

    public bool Remove(T value)
    {
        // 未命中的 Remove 不得触发写时复制，所以只有共享存储才需要先探测一次。
        // 独占存储直接删，省掉 Contains + Remove 的双次线性查找。
        if (_storage.Shared)
        {
            if (!_storage.Values.Contains(value))
                return false;
            EnsureWritable();
        }
        return _storage.Values.Remove(value);
    }

    public void RemoveAt(int index)
    {
        EnsureWritable();
        _storage.Values.RemoveAt(index);
    }

    public int IndexOf(T value) => _storage.Values.IndexOf(value);
    public bool Contains(T value) => _storage.Values.Contains(value);
    public List<T>.Enumerator GetEnumerator() => _storage.Values.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void EnsureWritable()
    {
        if (!_storage.Shared)
            return;
        _storage = new Storage(new List<T>(_storage.Values));
    }
}

internal sealed class ForkableDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
{
    private const int SmallMaxCount = 8;

    internal sealed class Storage
    {
        public readonly IEqualityComparer<TKey>? Comparer;
        public volatile bool Shared;
        public int Count;
        public TKey Key0 = default!;
        public TValue Value0 = default!;
        public KeyValuePair<TKey, TValue>[]? Overflow;
        public Dictionary<TKey, TValue>? Dict;

        public Storage(IEqualityComparer<TKey>? comparer)
        {
            Comparer = comparer;
        }

        public Storage(Storage source)
        {
            Comparer = source.Comparer;
            if (source.Dict is { } dict)
            {
                Dict = new Dictionary<TKey, TValue>(dict, dict.Comparer);
            }
            else
            {
                Count = source.Count;
                Key0 = source.Key0;
                Value0 = source.Value0;
                if (source.Overflow is { } overflow && Count > 1)
                {
                    var copy = new KeyValuePair<TKey, TValue>[overflow.Length];
                    Array.Copy(overflow, copy, Count - 1);
                    Overflow = copy;
                }
            }
        }
    }

    private static readonly Storage SharedEmptyDefault = new(comparer: null) { Shared = true };

    private Storage _storage;

    public ForkableDictionary()
        => _storage = SharedEmptyDefault;

    public ForkableDictionary(IEqualityComparer<TKey> comparer)
        => _storage = ReferenceEquals(comparer, EqualityComparer<TKey>.Default)
            ? SharedEmptyDefault
            : new Storage(comparer);

    private ForkableDictionary(Storage storage)
        => _storage = storage;

    public int Count => _storage.Dict?.Count ?? _storage.Count;
    public IEnumerable<TKey> Keys => _storage.Dict is { } dict ? dict.Keys : EnumerateSmallKeys();
    public IEnumerable<TValue> Values => _storage.Dict is { } dict ? dict.Values : EnumerateSmallValues();
    public IEqualityComparer<TKey> Comparer => _storage.Comparer ?? EqualityComparer<TKey>.Default;

    private static bool KeyEquals(IEqualityComparer<TKey>? comparer, TKey a, TKey b)
        => comparer is null ? EqualityComparer<TKey>.Default.Equals(a, b) : comparer.Equals(a, b);

    private int FindSmallIndex(Storage s, TKey key)
    {
        int count = s.Count;
        if (count == 0)
            return -1;
        IEqualityComparer<TKey>? comparer = s.Comparer;
        if (KeyEquals(comparer, s.Key0, key))
            return 0;
        if (count > 1 && s.Overflow is { } overflow)
        {
            int overflowCount = count - 1;
            for (int i = 0; i < overflowCount; i++)
            {
                if (KeyEquals(comparer, overflow[i].Key, key))
                    return i + 1;
            }
        }
        return -1;
    }

    public TValue this[TKey key]
    {
        get
        {
            Storage s = _storage;
            if (s.Dict is { } dict)
                return dict[key];
            int idx = FindSmallIndex(s, key);
            if (idx == 0)
                return s.Value0;
            if (idx > 0)
                return s.Overflow![idx - 1].Value;
            throw new KeyNotFoundException($"The given key '{key}' was not present in the dictionary.");
        }
        set
        {
            Storage s = _storage;
            if (s.Shared)
            {
                if (TryGetValue(key, out TValue existing)
                    && EqualityComparer<TValue>.Default.Equals(existing, value))
                {
                    return;
                }
                EnsureWritable();
                s = _storage;
            }
            if (s.Dict is { } dict)
            {
                dict[key] = value;
                return;
            }
            int idx = FindSmallIndex(s, key);
            if (idx == 0)
            {
                s.Value0 = value;
                return;
            }
            if (idx > 0)
            {
                s.Overflow![idx - 1] = new KeyValuePair<TKey, TValue>(s.Overflow[idx - 1].Key, value);
                return;
            }
            AddSmallNew(s, key, value);
        }
    }

    public ForkableDictionary<TKey, TValue> Fork()
    {
        _storage.Shared = true;
        return new ForkableDictionary<TKey, TValue>(_storage);
    }

    public void Add(TKey key, TValue value)
    {
        EnsureWritable();
        Storage s = _storage;
        if (s.Dict is { } dict)
        {
            dict.Add(key, value);
            return;
        }
        if (FindSmallIndex(s, key) >= 0)
            throw new ArgumentException($"An item with the same key has already been added. Key: {key}");
        AddSmallNew(s, key, value);
    }

    private static void AddSmallNew(Storage s, TKey key, TValue value)
    {
        int count = s.Count;
        if (count == 0)
        {
            s.Key0 = key;
            s.Value0 = value;
            s.Count = 1;
            return;
        }
        if (count < SmallMaxCount)
        {
            int overflowIdx = count - 1;
            var overflow = s.Overflow;
            if (overflow is null)
            {
                overflow = new KeyValuePair<TKey, TValue>[3];
                s.Overflow = overflow;
            }
            else if (overflowIdx >= overflow.Length)
            {
                Array.Resize(ref overflow, SmallMaxCount - 1);
                s.Overflow = overflow;
            }
            overflow[overflowIdx] = new KeyValuePair<TKey, TValue>(key, value);
            s.Count = count + 1;
            return;
        }
        // Spill to Dictionary
        var dict = new Dictionary<TKey, TValue>(count + 1, s.Comparer);
        dict.Add(s.Key0, s.Value0);
        if (s.Overflow is { } existing)
        {
            for (int i = 0; i < count - 1; i++)
                dict.Add(existing[i].Key, existing[i].Value);
        }
        dict.Add(key, value);
        s.Dict = dict;
        s.Count = 0;
        s.Key0 = default!;
        s.Value0 = default!;
        s.Overflow = null;
    }

    public bool Remove(TKey key)
    {
        Storage s = _storage;
        if (s.Dict is { } dict)
        {
            if (s.Shared)
            {
                if (!dict.ContainsKey(key))
                    return false;
                EnsureWritable();
                dict = _storage.Dict!;
            }
            return dict.Remove(key);
        }
        int idx = FindSmallIndex(s, key);
        if (idx < 0)
            return false;
        if (s.Shared)
        {
            EnsureWritable();
            s = _storage;
        }
        int count = s.Count;
        if (count == 1)
        {
            s.Key0 = default!;
            s.Value0 = default!;
            s.Count = 0;
            return true;
        }
        var overflow = s.Overflow!;
        int overflowCount = count - 1;
        if (idx == 0)
        {
            s.Key0 = overflow[0].Key;
            s.Value0 = overflow[0].Value;
            for (int i = 1; i < overflowCount; i++)
                overflow[i - 1] = overflow[i];
            overflow[overflowCount - 1] = default;
        }
        else
        {
            for (int i = idx; i < overflowCount; i++)
                overflow[i - 1] = overflow[i];
            overflow[overflowCount - 1] = default;
        }
        s.Count = count - 1;
        return true;
    }

    public void Clear()
    {
        Storage s = _storage;
        if (s.Dict is null)
        {
            if (s.Count == 0)
                return;
            if (s.Shared)
            {
                _storage = s.Comparer is null ? SharedEmptyDefault : new Storage(s.Comparer);
                return;
            }
            s.Key0 = default!;
            s.Value0 = default!;
            if (s.Overflow is { } overflow && s.Count > 1)
                Array.Clear(overflow, 0, s.Count - 1);
            s.Count = 0;
            return;
        }
        if (s.Dict.Count == 0)
            return;
        if (s.Shared)
        {
            _storage = s.Comparer is null ? SharedEmptyDefault : new Storage(s.Comparer);
            return;
        }
        s.Dict.Clear();
    }

    public bool ContainsKey(TKey key)
    {
        Storage s = _storage;
        return s.Dict is { } dict ? dict.ContainsKey(key) : FindSmallIndex(s, key) >= 0;
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        Storage s = _storage;
        if (s.Dict is { } dict)
            return dict.TryGetValue(key, out value!);
        int idx = FindSmallIndex(s, key);
        if (idx == 0)
        {
            value = s.Value0;
            return true;
        }
        if (idx > 0)
        {
            value = s.Overflow![idx - 1].Value;
            return true;
        }
        value = default!;
        return false;
    }

    public TValue GetValueOrDefault(TKey key)
        => TryGetValue(key, out TValue value) ? value : default!;

    public TValue GetValueOrDefault(TKey key, TValue defaultValue)
        => TryGetValue(key, out TValue value) ? value : defaultValue;

    private IEnumerable<TKey> EnumerateSmallKeys()
    {
        Storage s = _storage;
        if (s.Count > 0)
            yield return s.Key0;
        if (s.Count > 1 && s.Overflow is { } overflow)
        {
            for (int i = 0; i < s.Count - 1; i++)
                yield return overflow[i].Key;
        }
    }

    private IEnumerable<TValue> EnumerateSmallValues()
    {
        Storage s = _storage;
        if (s.Count > 0)
            yield return s.Value0;
        if (s.Count > 1 && s.Overflow is { } overflow)
        {
            for (int i = 0; i < s.Count - 1; i++)
                yield return overflow[i].Value;
        }
    }

    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private readonly Storage _storage;
        private Dictionary<TKey, TValue>.Enumerator _dictEnum;
        private readonly bool _useDict;
        private int _index;
        private KeyValuePair<TKey, TValue> _current;

        internal Enumerator(Storage storage)
        {
            _storage = storage;
            if (storage.Dict is { } dict)
            {
                _useDict = true;
                _dictEnum = dict.GetEnumerator();
                _index = 0;
            }
            else
            {
                _useDict = false;
                _dictEnum = default;
                _index = -1;
            }
            _current = default;
        }

        public KeyValuePair<TKey, TValue> Current => _useDict ? _dictEnum.Current : _current;
        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_useDict)
                return _dictEnum.MoveNext();
            int next = _index + 1;
            if (next >= _storage.Count)
                return false;
            _index = next;
            _current = next == 0
                ? new KeyValuePair<TKey, TValue>(_storage.Key0, _storage.Value0)
                : _storage.Overflow![next - 1];
            return true;
        }

        public void Reset()
        {
            if (_useDict)
                ((IEnumerator)_dictEnum).Reset();
            else
                _index = -1;
        }

        public void Dispose()
        {
            if (_useDict)
                _dictEnum.Dispose();
        }
    }

    public Enumerator GetEnumerator() => new(_storage);

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
        => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void EnsureWritable()
    {
        if (!_storage.Shared)
            return;
        _storage = new Storage(_storage);
    }
}

internal sealed class ForkableSet<T> : ISet<T>, IReadOnlySet<T>
{
    private const int SmallMaxCount = 8;

    internal sealed class Storage
    {
        public readonly IEqualityComparer<T>? Comparer;
        public volatile bool Shared;
        public int Count;
        public T Value0 = default!;
        public T[]? Overflow;
        public HashSet<T>? Set;

        public Storage(IEqualityComparer<T>? comparer)
        {
            Comparer = comparer;
        }

        public Storage(Storage source)
        {
            Comparer = source.Comparer;
            if (source.Set is { } set)
            {
                Set = new HashSet<T>(set, set.Comparer);
            }
            else
            {
                Count = source.Count;
                Value0 = source.Value0;
                if (source.Overflow is { } overflow && Count > 1)
                {
                    var copy = new T[overflow.Length];
                    Array.Copy(overflow, copy, Count - 1);
                    Overflow = copy;
                }
            }
        }
    }

    private static readonly Storage SharedEmptyDefault = new(comparer: null) { Shared = true };

    private Storage _storage;

    public ForkableSet()
        => _storage = SharedEmptyDefault;

    public ForkableSet(IEqualityComparer<T> comparer)
        => _storage = ReferenceEquals(comparer, EqualityComparer<T>.Default)
            ? SharedEmptyDefault
            : new Storage(comparer);

    public ForkableSet(IEnumerable<T> values)
    {
        _storage = new Storage(comparer: null);
        foreach (T value in values)
            AddCore(_storage, value);
    }

    private ForkableSet(Storage storage)
        => _storage = storage;

    public int Count => _storage.Set?.Count ?? _storage.Count;
    public bool IsReadOnly => false;
    public IEqualityComparer<T> Comparer => _storage.Comparer ?? EqualityComparer<T>.Default;

    private static bool ItemEquals(IEqualityComparer<T>? comparer, T a, T b)
        => comparer is null ? EqualityComparer<T>.Default.Equals(a, b) : comparer.Equals(a, b);

    private static int FindSmallIndex(Storage s, T item)
    {
        int count = s.Count;
        if (count == 0)
            return -1;
        IEqualityComparer<T>? comparer = s.Comparer;
        if (ItemEquals(comparer, s.Value0, item))
            return 0;
        if (count > 1 && s.Overflow is { } overflow)
        {
            int overflowCount = count - 1;
            for (int i = 0; i < overflowCount; i++)
            {
                if (ItemEquals(comparer, overflow[i], item))
                    return i + 1;
            }
        }
        return -1;
    }

    private static bool AddCore(Storage s, T value)
    {
        if (s.Set is { } set)
            return set.Add(value);
        if (FindSmallIndex(s, value) >= 0)
            return false;
        int count = s.Count;
        if (count == 0)
        {
            s.Value0 = value;
            s.Count = 1;
            return true;
        }
        if (count < SmallMaxCount)
        {
            int overflowIdx = count - 1;
            var overflow = s.Overflow;
            if (overflow is null)
            {
                overflow = new T[3];
                s.Overflow = overflow;
            }
            else if (overflowIdx >= overflow.Length)
            {
                Array.Resize(ref overflow, SmallMaxCount - 1);
                s.Overflow = overflow;
            }
            overflow[overflowIdx] = value;
            s.Count = count + 1;
            return true;
        }
        var hs = new HashSet<T>(count + 1, s.Comparer);
        hs.Add(s.Value0);
        if (s.Overflow is { } existing)
        {
            for (int i = 0; i < count - 1; i++)
                hs.Add(existing[i]);
        }
        hs.Add(value);
        s.Set = hs;
        s.Count = 0;
        s.Value0 = default!;
        s.Overflow = null;
        return true;
    }

    public ForkableSet<T> Fork()
    {
        _storage.Shared = true;
        return new ForkableSet<T>(_storage);
    }

    public bool Add(T value)
    {
        Storage s = _storage;
        if (s.Shared)
        {
            if (Contains(value))
                return false;
            EnsureWritable();
            s = _storage;
        }
        return AddCore(s, value);
    }

    void ICollection<T>.Add(T item) => Add(item);

    public bool Remove(T value)
    {
        Storage s = _storage;
        if (s.Set is { } set)
        {
            if (s.Shared)
            {
                if (!set.Contains(value))
                    return false;
                EnsureWritable();
                set = _storage.Set!;
            }
            return set.Remove(value);
        }
        int idx = FindSmallIndex(s, value);
        if (idx < 0)
            return false;
        if (s.Shared)
        {
            EnsureWritable();
            s = _storage;
        }
        int count = s.Count;
        if (count == 1)
        {
            s.Value0 = default!;
            s.Count = 0;
            return true;
        }
        var overflow = s.Overflow!;
        int overflowCount = count - 1;
        if (idx == 0)
        {
            s.Value0 = overflow[0];
            for (int i = 1; i < overflowCount; i++)
                overflow[i - 1] = overflow[i];
            overflow[overflowCount - 1] = default!;
        }
        else
        {
            for (int i = idx; i < overflowCount; i++)
                overflow[i - 1] = overflow[i];
            overflow[overflowCount - 1] = default!;
        }
        s.Count = count - 1;
        return true;
    }

    public void Clear()
    {
        Storage s = _storage;
        if (s.Set is null)
        {
            if (s.Count == 0)
                return;
            if (s.Shared)
            {
                _storage = s.Comparer is null ? SharedEmptyDefault : new Storage(s.Comparer);
                return;
            }
            s.Value0 = default!;
            if (s.Overflow is { } overflow && s.Count > 1)
                Array.Clear(overflow, 0, s.Count - 1);
            s.Count = 0;
            return;
        }
        if (s.Set.Count == 0)
            return;
        if (s.Shared)
        {
            _storage = s.Comparer is null ? SharedEmptyDefault : new Storage(s.Comparer);
            return;
        }
        s.Set.Clear();
    }

    public bool Contains(T item)
    {
        Storage s = _storage;
        return s.Set is { } set ? set.Contains(item) : FindSmallIndex(s, item) >= 0;
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        Storage s = _storage;
        if (s.Set is { } set)
        {
            set.CopyTo(array, arrayIndex);
            return;
        }
        if (s.Count == 0)
            return;
        array[arrayIndex] = s.Value0;
        if (s.Count > 1 && s.Overflow is { } overflow)
            Array.Copy(overflow, 0, array, arrayIndex + 1, s.Count - 1);
    }

    private HashSet<T> EnsureHashSetWritable()
    {
        EnsureWritable();
        Storage s = _storage;
        if (s.Set is { } existing)
            return existing;
        var hs = new HashSet<T>(s.Count, s.Comparer);
        if (s.Count > 0)
        {
            hs.Add(s.Value0);
            if (s.Overflow is { } overflow)
            {
                for (int i = 0; i < s.Count - 1; i++)
                    hs.Add(overflow[i]);
            }
        }
        s.Set = hs;
        s.Count = 0;
        s.Value0 = default!;
        s.Overflow = null;
        return hs;
    }

    private HashSet<T> ToTempHashSet()
    {
        Storage s = _storage;
        if (s.Set is { } existing)
            return existing;
        var hs = new HashSet<T>(s.Count, s.Comparer);
        if (s.Count > 0)
        {
            hs.Add(s.Value0);
            if (s.Overflow is { } overflow)
            {
                for (int i = 0; i < s.Count - 1; i++)
                    hs.Add(overflow[i]);
            }
        }
        return hs;
    }

    public void ExceptWith(IEnumerable<T> other) => EnsureHashSetWritable().ExceptWith(other);
    public void IntersectWith(IEnumerable<T> other) => EnsureHashSetWritable().IntersectWith(other);
    public bool IsProperSubsetOf(IEnumerable<T> other) => ToTempHashSet().IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<T> other) => ToTempHashSet().IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<T> other) => ToTempHashSet().IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<T> other) => ToTempHashSet().IsSupersetOf(other);
    public bool Overlaps(IEnumerable<T> other) => ToTempHashSet().Overlaps(other);
    public bool SetEquals(IEnumerable<T> other) => ToTempHashSet().SetEquals(other);
    public void SymmetricExceptWith(IEnumerable<T> other) => EnsureHashSetWritable().SymmetricExceptWith(other);
    public void UnionWith(IEnumerable<T> other) => EnsureHashSetWritable().UnionWith(other);

    public struct Enumerator : IEnumerator<T>
    {
        private readonly Storage _storage;
        private HashSet<T>.Enumerator _setEnum;
        private readonly bool _useSet;
        private int _index;
        private T _current;

        internal Enumerator(Storage storage)
        {
            _storage = storage;
            if (storage.Set is { } set)
            {
                _useSet = true;
                _setEnum = set.GetEnumerator();
                _index = 0;
            }
            else
            {
                _useSet = false;
                _setEnum = default;
                _index = -1;
            }
            _current = default!;
        }

        public T Current => _useSet ? _setEnum.Current : _current;
        object? IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_useSet)
                return _setEnum.MoveNext();
            int next = _index + 1;
            if (next >= _storage.Count)
                return false;
            _index = next;
            _current = next == 0 ? _storage.Value0 : _storage.Overflow![next - 1];
            return true;
        }

        public void Reset()
        {
            if (_useSet)
                ((IEnumerator)_setEnum).Reset();
            else
                _index = -1;
        }

        public void Dispose()
        {
            if (_useSet)
                _setEnum.Dispose();
        }
    }

    public Enumerator GetEnumerator() => new(_storage);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void EnsureWritable()
    {
        if (!_storage.Shared)
            return;
        _storage = new Storage(_storage);
    }
}
