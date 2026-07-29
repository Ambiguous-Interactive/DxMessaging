# Test Base Class Cleanup Usage

> **One-line summary**: Usage patterns, performance notes, and best practices for automatic test cleanup.

## Overview

This skill covers how to apply cleanup tracking in test fixtures.

## Solution

Use the usage patterns below to keep tests isolated.

## Usage

### Basic Test Class

```csharp
[TestFixture]
public sealed class PlayerTests : CommonTestBase
{
    [Test]
    public void PlayerTakesDamage()
    {
        GameObject go = CreateGameObject("Player");
        Player player = go.AddComponent<Player>();

        player.TakeDamage(10);

        Assert.AreEqual(90, player.Health);
    } // go automatically destroyed after test
}
```

### Fluent Tracking

```csharp
[Test]
public void EnemySpawnsCorrectly()
{
    // Track returns the object for chaining
    Enemy enemy = Track(Object.Instantiate(enemyPrefab)).GetComponent<Enemy>();

    Assert.IsNotNull(enemy);
    Assert.AreEqual(100, enemy.Health);
}
```

### Tracking Multiple Objects

```csharp
[Test]
public void BulletHitsEnemy()
{
    GameObject playerGo = CreateGameObject("Player", typeof(Rigidbody));
    GameObject enemyGo = CreateGameObject("Enemy", typeof(Rigidbody), typeof(Collider));
    GameObject bulletGo = CreateGameObject("Bullet", typeof(Rigidbody));

    // All three destroyed after test
    // ...
}
```

### Tracking Disposables

```csharp
[Test]
public void CacheEvictsOldEntries()
{
    var cache = TrackDisposable(new CacheBuilder<string, int>()
        .WithMaximumSize(10)
        .Build());

    for (int i = 0; i < 20; i++)
    {
        cache.Put($"key{i}", i);
    }

    Assert.AreEqual(10, cache.Count);
} // cache.Dispose() called automatically
```

### Shared Fixtures (Deferred Cleanup)

```csharp
[TestFixture]
public sealed class ExpensiveAssetTests : CommonTestBase
{
    protected override bool DeferAssetCleanupToOneTimeTearDown => true;

    private Texture2D sharedTexture;

    [OneTimeSetUp]
    public override void CommonOneTimeSetUp()
    {
        base.CommonOneTimeSetUp();
        sharedTexture = Track(new Texture2D(1024, 1024));
        // Expensive setup done once
    }

    [Test]
    public void Test1()
    {
        // Uses sharedTexture
    }

    [Test]
    public void Test2()
    {
        // Also uses sharedTexture
    }

    // sharedTexture destroyed in OneTimeTearDown
}
```

### Scene Tests

```csharp
[TestFixture]
public sealed class SceneLoadingTests : CommonTestBase
{
    [UnityTest]
    public IEnumerator SceneLoadsCorrectly()
    {
        Scene testScene = CreateTestScene("TestScene");
        SceneManager.SetActiveScene(testScene);

        GameObject go = CreateGameObject("TestObject");
        SceneManager.MoveGameObjectToScene(go, testScene);

        Assert.IsTrue(testScene.isLoaded);
        yield return null;
    } // testScene unloaded after test
}
```

## Performance Notes

- **Overhead**: ~1us per tracked object
- **Memory**: List allocation in OneTimeSetUp only
- **Destruction Order**: Reverse order prevents orphan issues

## Best Practices

### Do

- Always use `CreateGameObject` or `Track` for test objects
- Use `TrackDisposable` for caches, streams, etc.
- Set `DeferAssetCleanupToOneTimeTearDown = true` for expensive shared fixtures
- Destroy in reverse order (handled automatically)

### Don't

- Don't manually destroy tracked objects (double-destroy error)
- Don't forget to call base methods when overriding SetUp/TearDown
- Don't create objects in [OneTimeSetUp] without tracking
- Don't skip tracking "temporary" objects (they leak)
