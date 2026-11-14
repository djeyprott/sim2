using System.Text;

namespace Sim.Core.Engine;

/// <summary>
/// The simulation's own random number generator (xoshiro256**), so results never
/// depend on the .NET version. Each system gets its own stream from <see cref="ForStream"/>.
/// </summary>
public sealed class Rng
{
    public const int StateSize = 4;

    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    public Rng(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix64(ref x);
        _s1 = SplitMix64(ref x);
        _s2 = SplitMix64(ref x);
        _s3 = SplitMix64(ref x);
    }

    /// <summary>
    /// A generator for a named stream of a world. Streams with different names are independent,
    /// so adding a new stream never changes the numbers an existing one produces.
    /// </summary>
    public static Rng ForStream(ulong worldSeed, string streamName)
    {
        ulong x = worldSeed;
        ulong mixed = SplitMix64(ref x);
        return new Rng(mixed ^ Fnv1a64(streamName));
    }

    public ulong NextULong()
    {
        ulong result = RotateLeft(_s1 * 5UL, 7) * 9UL;
        ulong t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);

        return result;
    }

    /// <summary>A number from 0 (inclusive) to 1 (exclusive).</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>A whole number from 0 (inclusive) to <paramref name="maxExclusive"/> (exclusive), without bias.</summary>
    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "Must be positive.");
        }

        ulong range = (ulong)maxExclusive;
        ulong high = Math.BigMul(NextULong(), range, out ulong low);
        if (low < range)
        {
            ulong threshold = (0UL - range) % range;
            while (low < threshold)
            {
                high = Math.BigMul(NextULong(), range, out low);
            }
        }

        return (int)high;
    }

    /// <summary>A whole number from <paramref name="minInclusive"/> to <paramref name="maxExclusive"/>.</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "Must be greater than the minimum.");
        }

        long span = (long)maxExclusive - minInclusive;
        if (span > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "Range is too wide.");
        }

        return minInclusive + NextInt((int)span);
    }

    /// <summary>True with the given probability.</summary>
    public bool Chance(double probability) => NextDouble() < probability;

    /// <summary>Copies the generator's state, for saving.</summary>
    public ulong[] GetState() => new[] { _s0, _s1, _s2, _s3 };

    /// <summary>Restores a state produced by <see cref="GetState"/>.</summary>
    public void SetState(ReadOnlySpan<ulong> state)
    {
        if (state.Length != StateSize)
        {
            throw new ArgumentException("State must have four values.", nameof(state));
        }

        _s0 = state[0];
        _s1 = state[1];
        _s2 = state[2];
        _s3 = state[3];
    }

    private static ulong RotateLeft(ulong value, int count) => (value << count) | (value >> (64 - count));

    private static ulong SplitMix64(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        ulong z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    // A fixed text hash. string.GetHashCode differs between runs, so it cannot be used here.
    private static ulong Fnv1a64(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }
}
