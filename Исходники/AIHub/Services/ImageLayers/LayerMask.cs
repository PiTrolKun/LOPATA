using System.Collections;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;

namespace AIHub.Services.ImageLayers;

/// <summary>Binary coverage on the original, orientation-normalized pixel grid.</summary>
internal sealed class LayerMask
{
    private readonly BitArray _bits;
    private string? _key;
    private int? _count;

    internal LayerMask(int length) => _bits = new(length);
    private LayerMask(BitArray bits) => _bits = bits;
    internal int Length => _bits.Length;
    internal bool this[int index] => _bits[index];
    internal bool Any => _bits.HasAnySet();
    internal int Count => _count ??= Pack().Sum(value => System.Numerics.BitOperations.PopCount((uint)value));
    internal string Key => _key ??= Convert.ToHexStringLower(SHA256.HashData(Pack()));

    internal byte[] Pack()
    {
        var bytes = new byte[(Length + 7) / 8];
        _bits.CopyTo(bytes, 0);
        if (Length % 8 != 0) bytes[^1] &= (byte)((1 << (Length % 8)) - 1);
        return bytes;
    }

    internal static LayerMask Unpack(byte[] bytes, int length)
    {
        if (bytes.Length != (length + 7) / 8 || (length % 8 != 0 && (bytes[^1] >> (length % 8)) != 0))
            throw new InvalidDataException("ImageLayers.InvalidProject");
        return new(new BitArray(bytes) { Length = length });
    }

    internal static LayerMask Build(int length, Action<BitArray> fill)
    {
        var bits = new BitArray(length);
        fill(bits);
        return new(bits);
    }

    internal static LayerMask Area(int width, int height, Rectangle area) => Build(width * height, bits =>
    {
        for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++) bits[y * width + x] = true;
    });

    internal Rectangle Bounds(int width)
    {
        var left = width; var top = Length / width; var right = 0; var bottom = 0;
        for (var i = 0; i < Length; i++)
        {
            if (!_bits[i]) continue;
            var x = i % width; var y = i / width;
            left = Math.Min(left, x); top = Math.Min(top, y);
            right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
        }
        return left < right && top < bottom ? Rectangle.FromLTRB(left, top, right, bottom) : Rectangle.Empty;
    }

    internal LayerMask Union(LayerMask other) => new(new BitArray(_bits).Or(other._bits));
    internal LayerMask Intersect(LayerMask other) => new(new BitArray(_bits).And(other._bits));
    internal LayerMask Difference(LayerMask other) => new(new BitArray(_bits).And(new BitArray(other._bits).Not()));
    internal LayerMask Changes(LayerMask other) => new(new BitArray(_bits).Xor(other._bits));
    internal LayerMask Invert() => new(new BitArray(_bits).Not());
    internal bool Meets(LayerMask other) => Intersect(other).Any;
}
