// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Core.Mathematics;

/// <summary>
/// Integer arithmetic that .NET does not provide: division rounded down or
/// up, and a remainder that is never negative. Grid and chunk coordinates use
/// them, since <c>-1 / 16</c> is 0 in C# but the cell belongs to chunk -1.
/// </summary>
[HotPath]
public static class IntMath
{
    /// <summary>Divides and rounds toward negative infinity: <c>DivFloor(-1, 16)</c> is -1.</summary>
    /// <param name="value">The dividend.</param>
    /// <param name="divisor">The divisor, greater than 0.</param>
    /// <returns>The largest integer that is at most <paramref name="value"/> / <paramref name="divisor"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="divisor"/> is not positive.</exception>
    public static int DivFloor(int value, int divisor)
    {
        if (divisor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(divisor), divisor, "The divisor is greater than 0.");
        }

        int quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    /// <summary>Gets the remainder of <see cref="DivFloor"/>, from 0 to <paramref name="divisor"/> minus 1: <c>ModFloor(-1, 16)</c> is 15.</summary>
    /// <param name="value">The dividend.</param>
    /// <param name="divisor">The divisor, greater than 0.</param>
    /// <returns>The remainder, never negative.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="divisor"/> is not positive.</exception>
    public static int ModFloor(int value, int divisor)
    {
        if (divisor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(divisor), divisor, "The divisor is greater than 0.");
        }

        int remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    /// <summary>Divides a count and rounds up: the number of batches of <paramref name="divisor"/> items that hold <paramref name="value"/> items.</summary>
    /// <param name="value">The dividend, at least 0.</param>
    /// <param name="divisor">The divisor, greater than 0.</param>
    /// <returns>The smallest integer that is at least <paramref name="value"/> / <paramref name="divisor"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative, or <paramref name="divisor"/> is not positive.</exception>
    public static int DivCeil(int value, int divisor)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "The dividend is at least 0.");
        }

        if (divisor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(divisor), divisor, "The divisor is greater than 0.");
        }

        return (int)(((long)value + divisor - 1) / divisor);
    }
}
