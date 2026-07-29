# Array Pooling Usage Examples

> **One-line summary**: Real-world array pooling examples for network buffers, serialization, and large data processing.

## Overview

This skill focuses on practical array pooling scenarios in real workflows.

## Solution

Use these examples as templates for safe leasing and returning.

## Usage

### Network Buffer Processing

```csharp
public void ProcessPacket(NetworkPacket packet)
{
    using PooledArray<byte> lease = WallstopFastArrayPool<byte>.Get(4096, out byte[] buffer);

    int bytesRead = packet.ReadInto(buffer);
    ProcessBytes(buffer, 0, bytesRead);

} // buffer returned to pool
```

### Serialization

```csharp
public byte[] Serialize<T>(T obj)
{
    // Use SystemArrayPool when size varies
    using PooledArray<byte> lease = SystemArrayPool<byte>.Get(EstimateSize(obj), out byte[] buffer);

    int actualSize = serializer.Serialize(obj, buffer);

    // Copy exact size for return (buffer may be larger)
    byte[] result = new byte[actualSize];
    Buffer.BlockCopy(buffer, 0, result, 0, actualSize);
    return result;
}
```

### Secure Data Handling

```csharp
public void ProcessCredentials(byte[] encryptedData)
{
    // Use WallstopArrayPool - clears on return
    using PooledArray<byte> lease = WallstopArrayPool<byte>.Get(256, out byte[] decrypted);

    Decrypt(encryptedData, decrypted);
    ValidateCredentials(decrypted);

} // decrypted array is CLEARED before returning to pool
```

### Image Processing

```csharp
public Texture2D ApplyFilter(Texture2D source)
{
    int pixelCount = source.width * source.height;

    using PooledArray<Color32> srcLease = WallstopFastArrayPool<Color32>.Get(pixelCount, out Color32[] srcPixels);
    using PooledArray<Color32> dstLease = WallstopFastArrayPool<Color32>.Get(pixelCount, out Color32[] dstPixels);

    source.GetPixels32(srcPixels);

    for (int i = 0; i < pixelCount; i++)
    {
        dstPixels[i] = ApplyFilterToPixel(srcPixels[i]);
    }

    Texture2D result = new Texture2D(source.width, source.height);
    result.SetPixels32(dstPixels);
    result.Apply();
    return result;
}
```
