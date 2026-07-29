---
name: test-fixtures-and-cleanup
description: "Test fixture lifecycle for Unity tests: a CommonTestBase that tracks GameObjects, disposables, and scenes for automatic destruction; the Track/TrackDisposable/CreateGameObject/CreateTestScene helpers; DeferAssetCleanupToOneTimeTearDown for expensive shared assets; and reference-counted static fixtures with paired Acquire/Release in OneTimeSetUp/OneTimeTearDown. Use when a test leaks GameObjects or textures, when tests pollute each other, when a fixture is expensive enough to share across test classes, or when writing a new test base class."
metadata:
  category: "testing"
  tags: "testing, fixtures, performance, shared-state, reference-counting"
---

# Test Fixtures and Cleanup

Two cooperating patterns keep Unity tests isolated and fast: a base class that tracks and destroys everything a test creates, and reference-counted static fixtures that build an expensive resource once and tear it down after the last consumer releases it.

## When to use

- A test creates GameObjects, components, textures, scenes, or disposables.
- Tests pass in isolation but fail when run together, or leak Unity objects between runs.
- A fixture costs enough (large textures, asset bundles, scenes) that per-class creation is wasteful.
- Writing or extending a test base class, or overriding its SetUp/TearDown.

## Rules

### Track everything; never destroy by hand

- Create objects through `CreateGameObject(name)`, `CreateGameObject(name, params Type[] components)`, or `Track(obj)`; wrap non-Unity resources in `TrackDisposable(d)`; create scenes with `CreateTestScene(name)`. `Track` and `TrackDisposable` return their argument, so they chain.
- Do NOT manually destroy a tracked object - that produces a double-destroy error. Do not skip tracking "temporary" objects; untracked objects leak.
- The base class destroys tracked objects in REVERSE creation order so children die before parents, uses `Object.Destroy` in play mode and `Object.DestroyImmediate` otherwise, and yields one frame afterward to let deferred destruction flush.
- Disposal is best-effort: each `Dispose` is wrapped so one throwing disposable cannot abort cleanup of the rest.
- The lists are allocated once in `[OneTimeSetUp]`; per-object tracking overhead is about 1 us.

### Lifecycle hooks and overriding

- The hooks are `CommonOneTimeSetUp` (`[OneTimeSetUp]`), `CommonSetUp` (`[SetUp]`), `TearDown` (`[TearDown]`, disposables), `UnityTearDown` (`[UnityTearDown]`, tracked objects plus the drain frame), and `CommonOneTimeTearDown` (`[OneTimeTearDown]`, scenes plus deferred assets).
- Every override MUST call its base method. An override that forgets `base.CommonOneTimeSetUp()` leaves the tracking lists null.
- Set `protected override bool DeferAssetCleanupToOneTimeTearDown => true` when several tests in one fixture share an expensive asset created in `[OneTimeSetUp]`. With the flag on, per-test cleanup is skipped entirely and both disposables and objects are cleaned in `CommonOneTimeTearDown`.
- Objects created in `[OneTimeSetUp]` must still be tracked.

### Reference-counted shared fixtures

- Shape: a static holder with a `syncLock`, an `int refCount`, `Acquire`/`Release` (or `AcquireFixtures`/`ReleaseFixtures`), private `CreateFixtures`/`DestroyFixtures`, and property getters that check acquisition. Create on the transition to `refCount == 1`, destroy on the transition back to `0`.
- Every access path takes the lock. A `Release` that drives `refCount` below zero is a bug: log it and clamp to `0`.
- Property getters must either warn or throw `InvalidOperationException` when read before `Acquire`, so a missing acquire fails loudly instead of returning null.
- Acquire and release in pairs, from `[OneTimeSetUp]` and `[OneTimeTearDown]` respectively, around the base call: acquire AFTER `base.CommonOneTimeSetUp()`, release BEFORE `yield return base.CommonOneTimeTearDown()`.
- When more than one shared fixture exists, derive them from a generic `SharedFixtures<T> where T : SharedFixtures<T>, new()` base that owns the lock, the count, and the `Instance` accessor, and implement only `Create()` and `Destroy()`.
- Treat shared fixtures as immutable. Mutating one between tests reintroduces exactly the cross-test pollution the pattern exists to remove.
- Mark fixtures that consume shared resources with `[Category("Slow")]`.

## References

| Document                                                                                              | Purpose                                                                                                                       |
| ----------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| [shared-test-fixtures-generic-base.md](./references/shared-test-fixtures-generic-base.md)             | The generic `SharedFixtures<T>` base that centralizes lock, refcount, and `Instance` handling, with a scene-fixture subclass. |
| [shared-test-fixtures-reference-counting.md](./references/shared-test-fixtures-reference-counting.md) | Full reference-counted static fixture implementation, including defensive property accessors and resource disposal.           |
| [shared-test-fixtures.md](./references/shared-test-fixtures.md)                                       | Why shared fixtures exist, the acquire/release contract, and the do/don't list.                                               |
| [test-base-class-cleanup-part-1.md](./references/test-base-class-cleanup-part-1.md)                   | The complete `CommonTestBase` implementation: tracking lists, helpers, and each cleanup hook.                                 |
| [test-base-class-cleanup-usage.md](./references/test-base-class-cleanup-usage.md)                     | Applying the base class: fluent tracking, disposables, deferred cleanup, scene tests, and performance notes.                  |
| [test-base-class-cleanup.md](./references/test-base-class-cleanup.md)                                 | The manual-cleanup anti-patterns the base class replaces, and what it provides.                                               |
