---
name: serializable-dictionary
description: "Back a runtime Dictionary with parallel SerializeField lists through ISerializationCallbackReceiver so key/value config is editable in the Unity Inspector and persists in scenes, prefabs, and ScriptableObjects. Use when a SerializeField Dictionary silently stays empty, when parallel key/value lists are drifting out of sync, or when a designer needs to edit a lookup table in the Inspector."
metadata:
  category: "performance"
  tags: "unity, serialization, dictionary, inspector, data-structures"
---

# Serializable Dictionary for the Unity Inspector

Unity does not serialize `Dictionary<K, V>`. `SerializableDictionary<TKey, TValue>`
keeps a runtime dictionary alongside two `[SerializeField]` lists and syncs them
in the serialization callbacks, so the data shows in the Inspector and still has
O(1) runtime lookup.

## When to use

- A `[SerializeField] Dictionary<K, V>` field is always empty at runtime.
- Two hand-maintained `List<TKey>` / `List<TValue>` fields are drifting apart.
- A designer needs to edit a lookup table (drop rates, per-enum resistances,
  per-type stats) in the Inspector.
- Storing configuration on a `ScriptableObject` asset that scripts read by key.

## Rules

- Mark the type `[Serializable]` and implement both
  `IDictionary<TKey, TValue>` and `ISerializationCallbackReceiver`. Unity calls
  the callbacks; the `IDictionary` surface is what game code uses.
- Hold exactly three fields: `[SerializeField] private List<TKey> keys`,
  `[SerializeField] private List<TValue> values`, and the private runtime
  `Dictionary<TKey, TValue> dictionary`. The dictionary itself is never a
  serialized field.
- `OnBeforeSerialize` clears both lists and repopulates them from the dictionary.
  `OnAfterDeserialize` clears the dictionary and rebuilds it from the lists.
- Iterate only `Mathf.Min(keys.Count, values.Count)` in `OnAfterDeserialize`.
  The Inspector can leave the two lists at different lengths mid-edit, and
  indexing past the shorter one throws during deserialization.
- Skip null keys and duplicate keys on rebuild rather than letting
  `dictionary.Add` throw. Guard with `!dictionary.ContainsKey(key)` or a
  `HashSet<TKey> seen` and log a warning naming the duplicate key; the first
  entry wins.
- Route every `IDictionary` member to the inner dictionary. Do not implement
  lookups over the lists - the lists exist only for serialization.
- Offer the three constructors the pattern expects: parameterless, one taking
  `IEqualityComparer<TKey>`, and one copying an existing
  `IDictionary<TKey, TValue>`.
- Keep entries under about 1000. Serialization is O(n) on every save and load,
  and the property drawer iterates all entries on every repaint.
- Use enum or string keys. Do not store `UnityEngine.Object` references that can
  become null, and do not use it for runtime-generated data - there is no
  serialization benefit and it costs 3x memory (both lists plus the dictionary).
- The custom drawer is optional and editor-only. Wrap it in `#if UNITY_EDITOR`,
  register it with `[CustomPropertyDrawer(typeof(SerializableDictionary<,>), true)]`
  (the `true` makes it apply to derived types), and find the backing arrays with
  `property.FindPropertyRelative("keys")` and `("values")` - those string names
  are coupled to the field names above, so renaming a field breaks the drawer.
- A drawer that adds or removes an entry must change `keys` and `values`
  together (`arraySize++` on both, `DeleteArrayElementAtIndex(i)` on both), or
  the next `OnAfterDeserialize` truncates to the shorter list.
- `GetPropertyHeight` must return `singleLineHeight` when collapsed and
  `(keys.arraySize + 2) * (singleLineHeight + 2)` when expanded, accounting for
  the header row and the add button.

## References

| Document                                                                                              | Purpose                                                                                                     |
| ----------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------- |
| [serializable-dictionary.md](./references/serializable-dictionary.md)                                 | Full `SerializableDictionary<TKey, TValue>` implementation, callback flow, sorted variant, and cost profile |
| [serializable-dictionary-part-1.md](./references/serializable-dictionary-part-1.md)                   | Do and do-not list plus the duplicate-key handling variant of `OnAfterDeserialize`                          |
| [serializable-dictionary-property-drawer.md](./references/serializable-dictionary-property-drawer.md) | Editor-only `PropertyDrawer` with paired add/remove and height calculation                                  |
| [serializable-dictionary-usage-examples.md](./references/serializable-dictionary-usage-examples.md)   | MonoBehaviour, ScriptableObject, and enum-key call sites                                                    |
