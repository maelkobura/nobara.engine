// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Immutable;

namespace Nobara.Blocks;

/// <summary>The set of orientations that a type of block declares in <c>&lt;Orientations&gt;</c>.</summary>
public enum BlockOrientations
{
    /// <summary><c>none</c>: the block is never turned.</summary>
    None,

    /// <summary><c>horizontal</c>: 4 orientations, <c>north</c>, <c>east</c>, <c>south</c>, <c>west</c>, such as a furnace.</summary>
    Horizontal,

    /// <summary><c>facing</c>: 6 orientations, the four horizontal ones then <c>up</c> and <c>down</c>, such as a log.</summary>
    Facing,

    /// <summary><c>full</c>: the 24 rotations of the cube, such as stairs laid in any direction.</summary>
    Full,
}

/// <summary>
/// The orientations of each set, their names, and the quarter turn that
/// rotates them.
/// </summary>
/// <remarks>
/// <para>
/// Axes: X toward the east, Y up, Z toward the south. The reference
/// orientation, value 0, faces north (negative Z). A quarter turn is a
/// rotation of 90 degrees around the vertical axis, clockwise seen from
/// above: north becomes east.
/// </para>
/// <para>
/// A <see cref="BlockOrientations.Full"/> orientation <c>F_R</c> faces
/// <c>F</c> after a roll of <c>R</c> degrees around the facing axis: the
/// rotation that takes the block from its reference is "roll by <c>R</c>
/// around the north axis, then turn north toward <c>F</c>". <c>F</c> alone
/// means a roll of 0.
/// </para>
/// </remarks>
internal static class Orientations
{
    private static readonly ImmutableArray<string> Directions = ["north", "east", "south", "west", "up", "down"];

    // A quarter turn clockwise seen from above: (x, y, z) becomes (-z, y, x).
    private static int[] Turn => [0, 0, -1, 0, 1, 0, 1, 0, 0];

    private static int[][] Vectors => [[0, 0, -1], [1, 0, 0], [0, 0, 1], [-1, 0, 0], [0, 1, 0], [0, -1, 0]];

    /// <summary>Gets the number of orientations of a set.</summary>
    public static int Count(BlockOrientations set) => set switch
    {
        BlockOrientations.Horizontal => 4,
        BlockOrientations.Facing => 6,
        BlockOrientations.Full => 24,
        _ => 1,
    };

    /// <summary>Reads the value of <c>&lt;Orientations&gt;</c>; empty is <c>none</c>.</summary>
    public static bool TryParse(string text, out BlockOrientations set)
    {
        set = text switch
        {
            "" or "none" => BlockOrientations.None,
            "horizontal" => BlockOrientations.Horizontal,
            "facing" => BlockOrientations.Facing,
            "full" => BlockOrientations.Full,
            _ => (BlockOrientations)(-1),
        };
        return set >= BlockOrientations.None;
    }

    /// <summary>Gets the names of the orientations of a set, reference first.</summary>
    public static string[] Names(BlockOrientations set)
    {
        switch (set)
        {
            case BlockOrientations.Horizontal:
                return [.. Directions[..4]];
            case BlockOrientations.Facing:
                return [.. Directions];
            case BlockOrientations.Full:
                var names = new string[24];
                for (int facing = 0; facing < 6; facing++)
                {
                    for (int roll = 0; roll < 4; roll++)
                    {
                        names[(facing * 4) + roll] = roll == 0 ? Directions[facing] : $"{Directions[facing]}_{roll * 90}";
                    }
                }

                return names;
            default:
                return [];
        }
    }

    /// <summary>Gets, for each orientation of a set, the orientation after a quarter turn.</summary>
    public static int[] QuarterTurn(BlockOrientations set)
    {
        int count = Count(set);
        var result = new int[count];
        if (set == BlockOrientations.Full)
        {
            int[][] matrices = FullMatrices();
            for (int i = 0; i < count; i++)
            {
                int[] turned = Multiply(Turn, matrices[i]);
                result[i] = Array.FindIndex(matrices, m => m.AsSpan().SequenceEqual(turned));
            }

            return result;
        }

        if (set == BlockOrientations.None)
        {
            return [0];
        }

        int[][] vectors = Vectors;
        for (int i = 0; i < count; i++)
        {
            int[] turned = Apply(Turn, vectors[i]);
            result[i] = Array.FindIndex(vectors, v => v.AsSpan().SequenceEqual(turned));
        }

        return result;
    }

    /// <summary>The 24 rotations of <see cref="BlockOrientations.Full"/>, by orientation, as row-major 3×3 matrices.</summary>
    private static int[][] FullMatrices()
    {
        int[] identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        int[] up = [1, 0, 0, 0, 0, -1, 0, 1, 0];
        int[] down = [1, 0, 0, 0, 0, 1, 0, -1, 0];
        int[] roll = [0, -1, 0, 1, 0, 0, 0, 0, 1];
        var facings = new int[6][];
        facings[0] = identity;
        for (int i = 1; i < 4; i++)
        {
            facings[i] = Multiply(Turn, facings[i - 1]);
        }

        facings[4] = up;
        facings[5] = down;
        var result = new int[24][];
        for (int facing = 0; facing < 6; facing++)
        {
            int[] rolled = identity;
            for (int r = 0; r < 4; r++)
            {
                result[(facing * 4) + r] = Multiply(facings[facing], rolled);
                rolled = Multiply(roll, rolled);
            }
        }

        return result;
    }

    private static int[] Multiply(int[] a, int[] b)
    {
        var result = new int[9];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                int sum = 0;
                for (int k = 0; k < 3; k++)
                {
                    sum += a[(row * 3) + k] * b[(k * 3) + column];
                }

                result[(row * 3) + column] = sum;
            }
        }

        return result;
    }

    private static int[] Apply(int[] matrix, int[] vector)
    {
        var result = new int[3];
        for (int row = 0; row < 3; row++)
        {
            result[row] = (matrix[row * 3] * vector[0]) + (matrix[(row * 3) + 1] * vector[1]) + (matrix[(row * 3) + 2] * vector[2]);
        }

        return result;
    }
}
