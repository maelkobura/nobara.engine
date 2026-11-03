// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Voxel;

namespace Nobara.Voxel.Tests;

public sealed class PaletteTests
{
    [Fact]
    public void PaletteGrowsToSixteenBitsAndCompactsToUniform()
    {
        using var palette = new Palette();
        for (int cell = 0; cell < 257; cell++) palette.Set(cell, (uint)cell);

        Assert.False(palette.IsUniform);
        Assert.Equal(16, palette.BitsPerIndex);
        Assert.Equal(256u, palette.Get(256));

        for (int cell = 0; cell < VoxelCoordinates.ChunkVolume; cell++) palette.Set(cell, 42);
        palette.Compact(VoxelCoordinates.ChunkVolume);

        Assert.True(palette.IsUniform);
        Assert.Equal(0, palette.BitsPerIndex);
        Assert.Equal(42u, palette.Get(17));
    }
}
