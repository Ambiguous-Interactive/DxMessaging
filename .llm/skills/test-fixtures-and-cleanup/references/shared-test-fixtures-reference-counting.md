# Shared Fixtures: Reference Counting

> **One-line summary**: Use a static fixture with a reference count to manage shared resources across tests.

## Overview

Reference-counted fixtures ensure that expensive resources are created once and destroyed only after the last consumer releases them.

## Problem Statement

Without reference counting, shared fixtures are either leaked or destroyed while still in use.

## Solution

### Implementation

```csharp
namespace WallstopStudios.UnityHelpers.Tests
{
    using System.IO;
    using UnityEngine;

    public static class SharedTextureTestFixtures
    {
        private static readonly object syncLock = new object();
        private static int refCount;

        private static Texture2D solid300x100;
        private static Texture2D solid1024x1024;
        private static Texture2D transparent256x256;
        private static string solid300x100Path;

        public static string Solid300x100Path
        {
            get
            {
                EnsureAcquired();
                return solid300x100Path;
            }
        }

        public static Texture2D Solid300x100
        {
            get
            {
                EnsureAcquired();
                return solid300x100;
            }
        }

        public static Texture2D Solid1024x1024
        {
            get
            {
                EnsureAcquired();
                return solid1024x1024;
            }
        }

        public static Texture2D Transparent256x256
        {
            get
            {
                EnsureAcquired();
                return transparent256x256;
            }
        }

        public static void AcquireFixtures()
        {
            lock (syncLock)
            {
                if (refCount == 0)
                {
                    CreateFixtures();
                }
                refCount++;
            }
        }

        public static void ReleaseFixtures()
        {
            lock (syncLock)
            {
                refCount--;
                if (refCount == 0)
                {
                    DestroyFixtures();
                }
                else if (refCount < 0)
                {
                    Debug.LogError("[SharedTextureTestFixtures] ReleaseFixtures called more than AcquireFixtures!");
                    refCount = 0;
                }
            }
        }

        private static void EnsureAcquired()
        {
            lock (syncLock)
            {
                if (refCount == 0)
                {
                    Debug.LogWarning("[SharedTextureTestFixtures] Accessing fixtures without AcquireFixtures()");
                }
            }
        }

        private static void CreateFixtures()
        {
            solid300x100 = CreateSolidTexture(300, 100, Color.white);
            solid1024x1024 = CreateSolidTexture(1024, 1024, Color.white);
            transparent256x256 = CreateSolidTexture(256, 256, new Color(1, 1, 1, 0));

            string tempDir = Path.Combine(Application.temporaryCachePath, "TestFixtures");
            Directory.CreateDirectory(tempDir);
            solid300x100Path = Path.Combine(tempDir, "solid300x100.png");
            File.WriteAllBytes(solid300x100Path, solid300x100.EncodeToPNG());
        }

        private static void DestroyFixtures()
        {
            if (solid300x100 != null)
            {
                Object.DestroyImmediate(solid300x100);
                solid300x100 = null;
            }
            if (solid1024x1024 != null)
            {
                Object.DestroyImmediate(solid1024x1024);
                solid1024x1024 = null;
            }
            if (transparent256x256 != null)
            {
                Object.DestroyImmediate(transparent256x256);
                transparent256x256 = null;
            }

            if (!string.IsNullOrEmpty(solid300x100Path) && File.Exists(solid300x100Path))
            {
                File.Delete(solid300x100Path);
                solid300x100Path = null;
            }
        }

        private static Texture2D CreateSolidTexture(int width, int height, Color color)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }
    }
}
```

### Defensive Access Pattern

```csharp
public static Texture2D Solid300x100
{
    get
    {
        lock (syncLock)
        {
            if (refCount == 0)
            {
                throw new InvalidOperationException(
                    "SharedTextureTestFixtures.AcquireFixtures() not called");
            }
            return solid300x100;
        }
    }
}
```

## See Also

- [Shared Test Fixtures](./shared-test-fixtures.md)
- [Shared Fixtures: Generic Base](./shared-test-fixtures-generic-base.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-21 | Initial version |
