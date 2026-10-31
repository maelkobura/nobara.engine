// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Blocks.Tests.Support;
using Nobara.Core;
using Nobara.Items;
using static Nobara.Blocks.Tests.Support.BlocksHarness;
using F = Nobara.Formats;

namespace Nobara.Blocks.Tests;

public sealed class HookTests : IDisposable
{
    private const string Module = "example.blocks";

    private static readonly string Blocks = Nof("Example/Blocks", """
        <Block><Name>Lamp</Name><Content><States><Bool name="lit"/></States></Content><Tags><Tag>#example.lights</Tag></Tags></Block>
        <Block><Name>Torch</Name><Content/><Tags><Tag>#example.lights</Tag></Tags></Block>
        <Block><Name>Stone</Name><Content/></Block>
        <Block><Name>Door</Name><Content><Footprint><Cell y="1"/></Footprint></Content></Block>
        """);

    private readonly BlocksHarness _harness = new();
    private readonly List<string> _calls = [];
    private Probe _probe = null!;

    public void Dispose() => _harness.Dispose();

    private Engine Start(Action<Probe> initialize, params (string Id, F.Policy Policy, Action<Probe> Initialize)[] others)
    {
        _harness.Module(Module, Blocks, p => p.OnInitialize = probe =>
        {
            _probe = probe;
            initialize(probe);
        });
        foreach ((string id, F.Policy policy, Action<Probe> other) in others)
        {
            _harness.Module(id, behavior: p => p.OnInitialize = other, policy: policy);
        }

        return _harness.Start();
    }

    private BlockType Type(string name) => _probe.Blocks.Get("block:example.blocks." + name);

    private static BlockChange Break(int x, BlockType type, ulong multiBlock = 0) =>
        new(new BlockPosition(1, x, 64, 0), type.DefaultState, 0, multiBlock);

    private BlockBatch Batch(params BlockChange[] changes)
    {
        var batch = new BlockBatch { Actor = BlockActor.OfEntity(42), Cause = BlockCause.Hand };
        foreach (BlockChange change in changes)
        {
            batch.Add(change);
        }

        return batch;
    }

    [Fact]
    public void ABlockRemovedByASubscriberIsNotSeenByTheNextOnes()
    {
        using Engine engine = Start(p =>
        {
            BlockType lamp = p.Blocks.Get("block:example.blocks.lamp");
            p.Hooks.Subscribe(p.Context, p.Hooks.Breaking, lamp, new BatchRecorder(_calls, "first", view => view.Remove(0)), priority: 10);
            p.Hooks.Subscribe(p.Context, p.Hooks.Breaking, lamp, new BatchRecorder(_calls, "second"));
            p.Hooks.SubscribeAll(p.Context, p.Hooks.Breaking, new BatchRecorder(_calls, "all"), priority: -1);
        });
        BlockBatch batch = Batch(Break(1, Type("lamp")), Break(2, Type("stone")), Break(3, Type("lamp")));

        Assert.True(_probe.Hooks.Fire(_probe.Hooks.Breaking, batch));

        Assert.Equal(["first:1,3", "second:3", "all:2,3"], _calls);
        Assert.Equal(2, batch.RemainingCount);
        Assert.True(batch.IsRemoved(0));
    }

    [Fact]
    public void ACancelledBatchStopsTheNextSubscribers()
    {
        using Engine engine = Start(p =>
        {
            BlockType lamp = p.Blocks.Get("block:example.blocks.lamp");
            p.Hooks.Subscribe(p.Context, p.Hooks.Placing, lamp, new BatchRecorder(_calls, "guard", view => view.Cancel()), priority: 5);
            p.Hooks.Subscribe(p.Context, p.Hooks.Placing, lamp, new BatchRecorder(_calls, "other"));
        });
        BlockType lamp = Type("lamp");
        BlockBatch batch = Batch(new BlockChange(new BlockPosition(1, 1, 0, 0), 0, lamp.DefaultState));

        Assert.False(_probe.Hooks.Fire(_probe.Hooks.Placing, batch));

        Assert.Equal(["guard:1"], _calls);
        Assert.True(batch.IsCancelled);
        batch.Clear();
        Assert.False(batch.IsCancelled);
    }

    [Fact]
    public void SubscribersAreOrderedByPriorityThenLoadOrderThenSubscription()
    {
        using Engine engine = Start(
            p =>
            {
                BlockType stone = p.Blocks.Get("block:example.blocks.stone");
                p.Hooks.Subscribe(p.Context, p.Hooks.Broken, stone, new BatchRecorder(_calls, "a"));
                p.Hooks.Subscribe(p.Context, p.Hooks.Broken, stone, new BatchRecorder(_calls, "b"));
                p.Hooks.Subscribe(p.Context, p.Hooks.Broken, stone, new BatchRecorder(_calls, "low"), priority: -5);
            },
            ("example.late", F.Policy.Stop, p =>
            {
                BlockType stone = p.Blocks.Get("block:example.blocks.stone");
                p.Hooks.Subscribe(p.Context, p.Hooks.Broken, stone, new BatchRecorder(_calls, "late"));
                p.Hooks.Subscribe(p.Context, p.Hooks.Broken, stone, new BatchRecorder(_calls, "high"), priority: 5);
            }));

        _probe.Hooks.Fire(_probe.Hooks.Broken, Batch(Break(7, Type("stone"))));

        Assert.Equal(["high:7", "a:7", "b:7", "late:7", "low:7"], _calls);
    }

    [Fact]
    public void ABlockWithoutSubscribersCallsNobody()
    {
        using Engine engine = Start(p => p.Hooks.Subscribe(p.Context, p.Hooks.Breaking, p.Blocks.Get("block:example.blocks.lamp"), new BatchRecorder(_calls, "lamp")));
        BlockType stone = Type("stone");

        Assert.False(_probe.Hooks.HasSubscribers(_probe.Hooks.Breaking, stone.DefaultState));
        Assert.True(_probe.Hooks.HasSubscribers(_probe.Hooks.Breaking, Type("lamp").DefaultState));
        Assert.False(_probe.Hooks.HasSubscribers(_probe.Hooks.Interact, Type("lamp").DefaultState));
        Assert.True(_probe.Hooks.Fire(_probe.Hooks.Breaking, Batch(Break(1, stone))));
        Assert.Empty(_calls);
    }

    [Fact]
    public void RemovingOneCellOfABlockOfSeveralCellsRemovesTheOthers()
    {
        using Engine engine = Start(p => p.Hooks.Subscribe(p.Context, p.Hooks.Breaking, p.Blocks.Get("block:example.blocks.door"), new BatchRecorder(_calls, "door", view => view.Remove(1))));
        BlockType door = Type("door");
        BlockBatch batch = Batch(Break(1, door, multiBlock: 9), Break(2, Type("stone")), Break(3, door, multiBlock: 9), Break(4, door, multiBlock: 10), Break(5, door, multiBlock: 10));

        _probe.Hooks.Fire(_probe.Hooks.Breaking, batch);

        Assert.True(batch.IsRemoved(0));
        Assert.True(batch.IsRemoved(2));
        Assert.False(batch.IsRemoved(1));
        Assert.False(batch.IsRemoved(3));
        Assert.Equal(3, batch.RemainingCount);
    }

    [Fact]
    public void AHookAfterTheBatchCannotChangeIt()
    {
        Exception? error = null;
        using Engine engine = Start(p => p.Hooks.Subscribe(p.Context, p.Hooks.Broken, p.Blocks.Get("block:example.blocks.stone"), new BatchRecorder(_calls, "broken", view =>
        {
            try
            {
                view.Remove(0);
            }
            catch (InvalidOperationException e)
            {
                error = e;
            }
        })));

        Assert.True(_probe.Hooks.Fire(_probe.Hooks.Broken, Batch(Break(1, Type("stone")))));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Throws<ArgumentException>(() => _probe.Hooks.SubscribeAll(_probe.Context, _probe.Hooks.Broken, new BatchRecorder(_calls, "x")));
    }

    [Fact]
    public void ASubscriberOfATagIsCalledForItsTypes()
    {
        using Engine engine = Start(p =>
        {
            Assert.True(p.Blocks.TryGetTag("#example.lights", out BlockTag? lights));
            p.Hooks.Subscribe(p.Context, p.Hooks.RandomTick, lights, new UnitRecorder<BlockTick>(_calls, "glow"));
        });

        _probe.Hooks.Fire(_probe.Hooks.RandomTick, Type("torch").DefaultState, new BlockTick(default, Type("torch").DefaultState));
        _probe.Hooks.Fire(_probe.Hooks.RandomTick, Type("lamp").DefaultState, new BlockTick(default, Type("lamp").DefaultState));
        _probe.Hooks.Fire(_probe.Hooks.RandomTick, Type("stone").DefaultState, new BlockTick(default, Type("stone").DefaultState));

        Assert.Equal(["glow", "glow"], _calls);
    }

    [Fact]
    public void InteractIsConsumedAndOtherUnitHooksAreNot()
    {
        using Engine engine = Start(p =>
        {
            BlockType lamp = p.Blocks.Get("block:example.blocks.lamp");
            p.Hooks.Subscribe(p.Context, p.Hooks.Interact, lamp, new UnitRecorder<BlockInteraction>(_calls, "switch", answer: true), priority: 1);
            p.Hooks.Subscribe(p.Context, p.Hooks.Interact, lamp, new UnitRecorder<BlockInteraction>(_calls, "never"));
            p.Hooks.Subscribe(p.Context, p.Hooks.NeighborChanged, lamp, new UnitRecorder<NeighborChange>(_calls, "n1", answer: true), priority: 1);
            p.Hooks.Subscribe(p.Context, p.Hooks.NeighborChanged, lamp, new UnitRecorder<NeighborChange>(_calls, "n2"));
        });
        uint lamp = Type("lamp").DefaultState;

        Assert.True(_probe.Hooks.Fire(_probe.Hooks.Interact, lamp, new BlockInteraction(BlockActor.OfEntity(1), default, lamp, BlockFace.Up)));
        Assert.False(_probe.Hooks.Fire(_probe.Hooks.NeighborChanged, lamp, new NeighborChange(default, lamp, default, 0, 0)));

        Assert.Equal(["switch", "n1", "n2"], _calls);
        Assert.True(_probe.Hooks.Interact.IsConsumable);
    }

    [Fact]
    public void AModuleDeclaresAHookOfItsOwn()
    {
        BlockHook<BlockTick>? contact = null;
        Exception? wrongName = null;
        using Engine engine = Start(p =>
        {
            contact = p.Hooks.DeclareHook<BlockTick>(p.Context, "example.blocks.contact");
            wrongName = Record.Exception(() => p.Hooks.DeclareHook<BlockTick>(p.Context, "other.contact"));
            p.Hooks.Subscribe(p.Context, contact, p.Blocks.Get("block:example.blocks.stone"), new UnitRecorder<BlockTick>(_calls, "touched"));
        });
        uint stone = Type("stone").DefaultState;

        _probe.Hooks.Fire(contact!, stone, new BlockTick(default, stone));

        Assert.Equal(["touched"], _calls);
        Assert.Equal("example.blocks.contact", contact!.Name);
        Assert.Equal(Module, contact.ModuleId);
        Assert.Contains(contact, _probe.Hooks.Kinds);
        Assert.IsType<ArgumentException>(wrongName);
        Assert.Throws<InvalidOperationException>(() => _probe.Hooks.DeclareHook<BlockTick>(_probe.Context, "example.blocks.late"));
    }

    [Fact]
    public void ARevokedSubscriberStopsAtOnceAndTheTablesFollowAtTheNextFrame()
    {
        BlockHookHandle? handle = null;
        using Engine engine = Start(p => handle = p.Hooks.Subscribe(p.Context, p.Hooks.Breaking, p.Blocks.Get("block:example.blocks.stone"), new BatchRecorder(_calls, "stone")));
        BlockType stone = Type("stone");

        handle!.Dispose();
        _probe.Hooks.Fire(_probe.Hooks.Breaking, Batch(Break(1, stone)));

        Assert.True(handle.IsRevoked);
        Assert.Empty(_calls);
        Assert.True(_probe.Hooks.HasSubscribers(_probe.Hooks.Breaking, stone.DefaultState));
        _harness.Tick(engine);
        Assert.False(_probe.Hooks.HasSubscribers(_probe.Hooks.Breaking, stone.DefaultState));
    }

    [Fact]
    public void ASubscriptionAddedOnceStartedAppliesAtTheNextFrame()
    {
        using Engine engine = Start(_ => { });
        BlockType stone = Type("stone");

        _probe.Hooks.Subscribe(_probe.Context, _probe.Hooks.Breaking, stone, new BatchRecorder(_calls, "late"));
        _probe.Hooks.Fire(_probe.Hooks.Breaking, Batch(Break(1, stone)));
        Assert.Empty(_calls);

        _harness.Tick(engine);
        _probe.Hooks.Fire(_probe.Hooks.Breaking, Batch(Break(2, stone)));
        Assert.Equal(["late:2"], _calls);
    }

    [Fact]
    public void ASubscriberThatThrowsIsReportedAndTheNextOneRuns()
    {
        using Engine engine = Start(
            p => p.Hooks.Subscribe(p.Context, p.Hooks.Breaking, p.Blocks.Get("block:example.blocks.stone"), new BatchRecorder(_calls, "after")),
            ("example.buggy", F.Policy.Log, p => p.Hooks.Subscribe(p.Context, p.Hooks.Breaking, p.Blocks.Get("block:example.blocks.stone"), new BatchRecorder(_calls, "buggy", _ => throw new InvalidOperationException("boom")), priority: 5)));

        Assert.True(_probe.Hooks.Fire(_probe.Hooks.Breaking, Batch(Break(1, Type("stone")))));

        Assert.Equal(["buggy:1", "after:1"], _calls);
        Assert.Contains(_harness.Log.Entries, e => e.Message.StartsWith("Module 'example.buggy'", StringComparison.Ordinal) && e.Message.Contains("its policy is Log", StringComparison.Ordinal));
    }

    [Fact]
    public void HooksAreFiredOnceNBlocksHasStarted()
    {
        Exception? early = null;
        using Engine engine = Start(p => early = Record.Exception(() => p.Hooks.Fire(p.Hooks.Breaking, new BlockBatch())));

        Assert.IsType<InvalidOperationException>(early);
    }
}
