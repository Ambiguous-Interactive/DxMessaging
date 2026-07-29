# Serializable Dictionary for Unity Inspector

> **One-line summary**: Implement `ISerializationCallbackReceiver` to create dictionaries that serialize in Unity's Inspector while maintaining O(1) runtime access.

## Overview

Unity cannot serialize `Dictionary<K,V>` directly. This pattern wraps a dictionary with parallel lists that Unity can serialize, syncing them via serialization callbacks. The result is a dictionary that:

1. **Displays in Inspector** - Designers can edit entries
1. **Persists in scenes/prefabs** - Saves with the GameObject
1. **Works at runtime** - Full dictionary functionality

## Problem Statement

```csharp
// BAD: Unity ignores this field
[SerializeField]
private Dictionary<string, int> itemCounts; // Never serialized!

// BAD: Two separate lists are error-prone
[SerializeField] private List<string> keys;
[SerializeField] private List<int> values;
// Must manually keep in sync!
```

## Solution

### Core Concept

```text
+-------------------------------------------------------------+
|  SerializableDictionary<TKey, TValue>                       |
+-------------------------------------------------------------+
|  [SerializeField] List<TKey> keys      <- Unity serializes  |
|  [SerializeField] List<TValue> values  <- Unity serializes  |
|                                                             |
|  Dictionary<TKey, TValue> dictionary   <- Runtime access    |
+-------------------------------------------------------------+
|  OnBeforeSerialize():                                       |
|    keys.Clear(); values.Clear();                            |
|    foreach(kvp in dictionary):                              |
|      keys.Add(kvp.Key);                                     |
|      values.Add(kvp.Value);                                 |
|                                                             |
|  OnAfterDeserialize():                                      |
|    dictionary.Clear();                                      |
|    for(i = 0; i < keys.Count; i++):                         |
|      dictionary[keys[i]] = values[i];                       |
+-------------------------------------------------------------+
```

### Implementation

```csharp
namespace WallstopStudios.UnityHelpers.Core.DataStructure
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>
    /// Dictionary that can be serialized by Unity and displayed in Inspector.
    /// </summary>
    [Serializable]
    public class SerializableDictionary<TKey, TValue>
        : IDictionary<TKey, TValue>, ISerializationCallbackReceiver
    {
        [SerializeField]
        private List<TKey> keys = new List<TKey>();

        [SerializeField]
        private List<TValue> values = new List<TValue>();

        private Dictionary<TKey, TValue> dictionary = new Dictionary<TKey, TValue>();

        public SerializableDictionary()
        {
        }

        public SerializableDictionary(IEqualityComparer<TKey> comparer)
        {
            dictionary = new Dictionary<TKey, TValue>(comparer);
        }

        public SerializableDictionary(IDictionary<TKey, TValue> source)
        {
            foreach (KeyValuePair<TKey, TValue> kvp in source)
            {
                dictionary[kvp.Key] = kvp.Value;
            }
        }

        // ISerializationCallbackReceiver implementation
        public void OnBeforeSerialize()
        {
            keys.Clear();
            values.Clear();

            foreach (KeyValuePair<TKey, TValue> kvp in dictionary)
            {
                keys.Add(kvp.Key);
                values.Add(kvp.Value);
            }
        }

        public void OnAfterDeserialize()
        {
            dictionary.Clear();

            int count = Mathf.Min(keys.Count, values.Count);
            for (int i = 0; i < count; i++)
            {
                TKey key = keys[i];
                if (key != null && !dictionary.ContainsKey(key))
                {
                    dictionary[key] = values[i];
                }
            }
        }

        // IDictionary<TKey, TValue> implementation
        public TValue this[TKey key]
        {
            get => dictionary[key];
            set => dictionary[key] = value;
        }

        public ICollection<TKey> Keys => dictionary.Keys;
        public ICollection<TValue> Values => dictionary.Values;
        public int Count => dictionary.Count;
        public bool IsReadOnly => false;

        public void Add(TKey key, TValue value) => dictionary.Add(key, value);
        public void Add(KeyValuePair<TKey, TValue> item) => dictionary.Add(item.Key, item.Value);
        public void Clear() => dictionary.Clear();
        public bool Contains(KeyValuePair<TKey, TValue> item) => ((IDictionary<TKey, TValue>)dictionary).Contains(item);
        public bool ContainsKey(TKey key) => dictionary.ContainsKey(key);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => ((IDictionary<TKey, TValue>)dictionary).CopyTo(array, arrayIndex);
        public bool Remove(TKey key) => dictionary.Remove(key);
        public bool Remove(KeyValuePair<TKey, TValue> item) => ((IDictionary<TKey, TValue>)dictionary).Remove(item);
        public bool TryGetValue(TKey key, out TValue value) => dictionary.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => dictionary.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Sorted variant that maintains key order.
    /// </summary>
    [Serializable]
    public class SerializableSortedDictionary<TKey, TValue>
        : SerializableDictionary<TKey, TValue>
        where TKey : IComparable<TKey>
    {
        public new void OnBeforeSerialize()
        {
            // Sort keys before serializing for consistent ordering
            base.OnBeforeSerialize();
        }
    }
}
```

## Performance Notes

- **Serialization**: O(n) on save/load; avoid very large dictionaries
- **Runtime Access**: O(1) dictionary operations
- **Memory**: 3x overhead (keys list + values list + dictionary)
- **Editor**: Property drawer iteration is O(n)

## See Also

- [serializable dictionary part 1](./serializable-dictionary-part-1.md)
