// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Audio;
using Nobara.Blocks;
using Nobara.Core.Logging;
using Nobara.Voxel.Sound.Tests.Support;

namespace Nobara.Voxel.Sound.Tests;

/// <summary>The sounds of blocks that are placed and broken, from the hooks of nBlocks to the voices of the device.</summary>
public sealed class BlockSoundTests : IDisposable
{
    private readonly SoundHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void PlacingABlockThatHasASoundPlaysItAtTheBlockOnItsBus()
    {
        _harness.Start();

        List<TestVoice> started = _harness.Started(() => _harness.Place(_harness.Probe.Plain, (new BlockCoordinate(3, 0, 0), _harness.State("stone"))));

        TestVoice voice = Assert.Single(started);
        Assert.True(voice.Parameters.Spatial);
        Assert.True(voice.Parameters.Gain > 0);
        Assert.Equal(new Vector3(3.5f, 0.5f, 0.5f), voice.Parameters.Position);
        SoundEvent placed = _harness.Probe.Context.Resolve<ISoundRegistry>().Get("sound:example.world.stoneplace");
        Assert.Equal("bus:nobara.audio.effects.blocks", placed.Bus.ObjectId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000_000)]
    [InlineData(-1_000_000_000)]
    public void ABlockOfTheTerrainSoundsWhereItIsEvenFarFromTheOrigin(int origin)
    {
        _harness.Start();
        _harness.Probe.Listener!.Teleport(Nobara.Core.Mathematics.WorldPosition.FromBlocks(origin, 0, 0), Quaternion.Identity);
        _harness.Frames(1);

        List<TestVoice> started = _harness.Started(() => _harness.Place(_harness.Terrain, (new BlockCoordinate(origin + 3, 0, 0), _harness.State("stone"))));

        TestVoice voice = Assert.Single(started);
        AssertNear(new Vector3(3.5f, 0.5f, 0.5f), voice.Parameters.Position);
    }

    [Fact]
    public void BreakingABlockPlaysItsBreakSoundAndNotItsPlaceSound()
    {
        _harness.Start();
        _harness.Started(() => _harness.Place(_harness.Probe.Plain, (new BlockCoordinate(3, 0, 0), _harness.State("stone"))));
        _harness.Frames(10);
        int plays = _harness.Plays;

        List<TestVoice> started = _harness.Started(() => _harness.Break(_harness.Probe.Plain, new BlockCoordinate(3, 0, 0)));

        Assert.Equal(plays + 1, _harness.Plays);
        Assert.Equal(new Vector3(3.5f, 0.5f, 0.5f), Assert.Single(started).Parameters.Position);
    }

    [Fact]
    public void ABlockWithoutTheSoundStaysSilent()
    {
        _harness.Start();

        // Dust has no sound, and Glass has none when it is placed.
        _harness.Place(_harness.Probe.Plain, (new BlockCoordinate(0, 0, 0), _harness.State("dust")), (new BlockCoordinate(5, 0, 0), _harness.State("glass")));
        _harness.Frames(3);
        _harness.Break(_harness.Probe.Plain, new BlockCoordinate(0, 0, 0));
        _harness.Frames(3);
        Assert.Equal(0, _harness.Plays);

        List<TestVoice> started = _harness.Started(() => _harness.Break(_harness.Probe.Plain, new BlockCoordinate(5, 0, 0)));

        Assert.Single(started);
    }

    [Fact]
    public void TheStateOfABlockChangesItsSound()
    {
        _harness.Start();
        Grid grid = _harness.Probe.Plain;
        _harness.Place(grid, (new BlockCoordinate(0, 0, 0), _harness.Lamp(lit: false)), (new BlockCoordinate(20, 0, 0), _harness.Lamp(lit: true)), (new BlockCoordinate(40, 0, 0), _harness.State("stone")));
        _harness.Frames(3);

        // A voice forgets its buffer when its sound ends: read it at once.
        AudioBuffer off = Assert.Single(_harness.Started(() => _harness.Break(grid, new BlockCoordinate(0, 0, 0)))).Buffer;
        _harness.Frames(40);
        AudioBuffer on = Assert.Single(_harness.Started(() => _harness.Break(grid, new BlockCoordinate(20, 0, 0)))).Buffer;
        _harness.Frames(40);
        AudioBuffer stone = Assert.Single(_harness.Started(() => _harness.Break(grid, new BlockCoordinate(40, 0, 0)))).Buffer;

        // A lamp that is off breaks like stone, which its own property says; a lit one has the sound of its variant.
        Assert.Equal(stone, off);
        Assert.NotEqual(stone, on);
    }

    [Fact]
    public void AQualifiedPropertyOverridesTheSoundForThisModuleOnly()
    {
        _harness.Blocks = """
            <Block><Name>Plain</Name><Content><Shape>shape:nobara.cube</Shape></Content><Properties><BreakSound>sound:example.world.stonebreak</BreakSound></Properties></Block>
            <Block><Name>Quiet</Name><Content><Shape>shape:nobara.cube</Shape></Content><Properties><BreakSound>sound:example.world.stonebreak</BreakSound><VoxelSound.BreakSound>sound:example.world.glassbreak</VoxelSound.BreakSound></Properties></Block>
            <Block><Name>Glass</Name><Content><Shape>shape:nobara.cube</Shape></Content><Properties><BreakSound>sound:example.world.glassbreak</BreakSound></Properties></Block>
            """;
        _harness.Start();
        Grid grid = _harness.Probe.Plain;
        _harness.Place(grid, (new BlockCoordinate(0, 0, 0), _harness.State("plain")), (new BlockCoordinate(20, 0, 0), _harness.State("quiet")), (new BlockCoordinate(40, 0, 0), _harness.State("glass")));
        _harness.Frames(3);

        AudioBuffer plain = Assert.Single(_harness.Started(() => _harness.Break(grid, new BlockCoordinate(0, 0, 0)))).Buffer;
        _harness.Frames(40);
        AudioBuffer quiet = Assert.Single(_harness.Started(() => _harness.Break(grid, new BlockCoordinate(20, 0, 0)))).Buffer;
        _harness.Frames(40);
        AudioBuffer glass = Assert.Single(_harness.Started(() => _harness.Break(grid, new BlockCoordinate(40, 0, 0)))).Buffer;

        Assert.Equal(glass, quiet);
        Assert.NotEqual(glass, plain);
    }

    [Fact]
    public void ABlockOfSeveralCellsMakesOneSoundAtItsOrigin()
    {
        _harness.Start();
        _harness.Place(_harness.Probe.Plain, (new BlockCoordinate(10, 0, 0), _harness.State("bench")));
        _harness.Frames(3);

        TestVoice voice = Assert.Single(_harness.Started(() => _harness.Break(_harness.Probe.Plain, new BlockCoordinate(10, 0, 0))));

        Assert.Equal(new Vector3(10.5f, 0.5f, 0.5f), voice.Parameters.Position);
    }

    [Fact]
    public void ABlockOnATurnedGridSoundsWhereItsGridPutsIt()
    {
        _harness.Start();
        Grid moved = _harness.Probe.Moved;
        _harness.Place(moved, (new BlockCoordinate(2, 0, 0), _harness.State("stone")));

        TestVoice voice = Assert.Single(_harness.Started(() => { }, frames: 2));

        // The cell (2, 0, 0) has its middle at (2.5, 0.5, 0.5) in the grid; a quarter turn around Y maps it to (0.5, 0.5, -2.5), then the grid moves it by (50, 10, 0).
        AssertNear(new Vector3(50.5f, 10.5f, -2.5f), voice.Parameters.Position);
    }

    [Fact]
    public void ASoundOnAGridFollowsItWhenItMoves()
    {
        _harness.Start();
        Grid moved = _harness.Probe.Moved;
        _harness.Place(moved, (new BlockCoordinate(0, 0, 0), _harness.State("bench")));
        _harness.Frames(3);
        TestVoice voice = Assert.Single(_harness.Started(() => _harness.Break(moved, new BlockCoordinate(0, 0, 0))));
        Vector3 before = voice.Parameters.Position;

        _harness.Probe.Worlds.ClaimGridTransforms(_harness.Probe.Context).Set(moved, WorldProbe.MovedTransform with { Position = Nobara.Core.Mathematics.WorldPosition.FromBlocks(70, 10, 0) });
        _harness.Frames(4);

        Assert.True(voice.Playing);
        AssertNear(before + new Vector3(20, 0, 0), voice.Parameters.Position);
    }

    [Fact]
    public void TheBlocksOfAZoneThatShareASoundMakeOneSoundAtTheirMiddle()
    {
        _harness.Start();
        BlockCoordinate[] cells = [.. from x in Enumerable.Range(0, 3) from y in Enumerable.Range(0, 3) from z in Enumerable.Range(0, 3) select new BlockCoordinate(x, y, z)];

        List<TestVoice> started = _harness.Started(() => _harness.Place(_harness.Probe.Plain, [.. cells.Select(c => (c, _harness.State("stone")))]));

        TestVoice voice = Assert.Single(started);
        Assert.Equal(new Vector3(1.5f, 1.5f, 1.5f), voice.Parameters.Position);
    }

    [Fact]
    public void BlocksInDifferentZonesMakeTheirOwnSounds()
    {
        _harness.Start();

        List<TestVoice> started = _harness.Started(() => _harness.Place(_harness.Probe.Plain, (new BlockCoordinate(0, 0, 0), _harness.State("stone")), (new BlockCoordinate(4, 0, 0), _harness.State("stone")), (new BlockCoordinate(0, 0, 8), _harness.State("stone"))));

        Assert.Equal(3, started.Count);
    }

    [Fact]
    public void AMassPlacingAndBreakingAsksForABoundedNumberOfSounds()
    {
        _harness.Start();
        BlockCoordinate[] cells = [.. from x in Enumerable.Range(0, 25) from y in Enumerable.Range(0, 20) from z in Enumerable.Range(0, 20) select new BlockCoordinate(x, y, z)];
        Assert.Equal(10_000, cells.Length);
        Grid grid = _harness.Probe.Plain;

        int placed = _harness.Started(() => _harness.Place(grid, [.. cells.Select(c => (c, _harness.State("stone")))])).Count;
        _harness.Frames(10);
        int broken = _harness.Started(() => _harness.Break(grid, cells)).Count;

        // 175 zones of 4 blocks hold the blocks; the rule gives one sound for each of the first 16.
        Assert.Equal(BlockSoundPlayer.MaxSoundsPerTick, placed);
        Assert.Equal(BlockSoundPlayer.MaxSoundsPerTick, broken);
    }

    [Fact]
    public void SeveralBatchesOfATickShareTheBudgetOfTheTick()
    {
        _harness.Start();
        Grid grid = _harness.Probe.Plain;

        List<TestVoice> started = _harness.Started(() =>
        {
            for (int i = 0; i < 4; i++)
            {
                _harness.Place(grid, [.. Enumerable.Range(0, 10).Select(j => (new BlockCoordinate(((i * 10) + j) * 4, 0, 0), _harness.State("stone")))]);
            }
        });

        Assert.Equal(BlockSoundPlayer.MaxSoundsPerTick, started.Count);
    }

    [Fact]
    public void AHandlerOfAMassBatchAllocatesNothing()
    {
        _harness.Start();
        Grid grid = _harness.Probe.Moved;
        uint stone = _harness.State("stone");
        var batch = new BlockBatch(10_000);
        for (int x = 0; x < 25; x++)
        {
            for (int y = 0; y < 20; y++)
            {
                for (int z = 0; z < 20; z++)
                {
                    batch.Add(new BlockChange(new BlockPosition(grid.Identifier, x, y, z), 0, stone));
                }
            }
        }

        IBlockHooks hooks = _harness.Probe.Hooks;
        hooks.Fire(hooks.Placed, batch);
        _harness.Frames(2);
        hooks.Fire(hooks.Placed, batch);
        _harness.Frames(2);

        long before = GC.GetAllocatedBytesForCurrentThread();
        hooks.Fire(hooks.Placed, batch);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void AnUnknownSoundStopsTheStartWithTheBlockThatNamesIt()
    {
        _harness.Blocks = "<Block><Name>Odd</Name><Content><Shape>shape:nobara.cube</Shape></Content><Properties><BreakSound>sound:example.world.missing</BreakSound></Properties></Block>";

        Exception error = Assert.ThrowsAny<Exception>(() => _harness.Start());

        BlockSoundException sound = Assert.IsType<BlockSoundException>(Unwrap(error));
        string problem = Assert.Single(sound.Problems);
        Assert.Contains("sound:example.world.missing", problem, StringComparison.Ordinal);
        Assert.Contains("blocks.nof", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ADistributionWithoutTheModuleHasNoBlockSoundAndNoError()
    {
        _harness.WithSound = false;
        _harness.Start();

        _harness.Place(_harness.Probe.Plain, (new BlockCoordinate(3, 0, 0), _harness.State("stone")));
        _harness.Frames(3);
        _harness.Break(_harness.Probe.Plain, new BlockCoordinate(3, 0, 0));
        _harness.Frames(3);

        Assert.Equal(0, _harness.Plays);
        Assert.DoesNotContain(_harness.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void ADistributionWithoutAudioPlaysNothingAndFailsNowhere()
    {
        _harness.WithSound = false;
        _harness.WithAudio = false;
        _harness.Start();

        _harness.Place(_harness.Probe.Plain, (new BlockCoordinate(3, 0, 0), _harness.State("stone")));
        _harness.Frames(3);

        Assert.DoesNotContain(_harness.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 0.05f, $"Expected {expected}, got {actual}.");

    private static Exception Unwrap(Exception error)
    {
        while (error is not BlockSoundException && error.InnerException is { } inner)
        {
            error = inner;
        }

        return error;
    }
}
