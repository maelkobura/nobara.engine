// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Blocks.Tests.Support;
using Nobara.Core;
using Nobara.Core.Resources;
using Nobara.Items;
using static Nobara.Blocks.Tests.Support.BlocksHarness;

namespace Nobara.Blocks.Tests;

public sealed class RegistryTests : IDisposable
{
    private const string Module = "example.blocks";

    private static readonly string Blocks = Nof("Example/Blocks", """
        <Block>
          <Name>Lamp</Name>
          <Content>
            <Hardness>0.5</Hardness>
            <States><Bool name="lit"/></States>
            <Variants><Variant when="lit=true" opacity="translucent" hardness="5"/></Variants>
            <CreateItem maxStack="16" state="lit=true"/>
          </Content>
          <Tags><Tag>#example.lights</Tag></Tags>
        </Block>
        <Block>
          <Name>Stairs</Name>
          <Content>
            <Orientations>horizontal</Orientations>
            <States>
              <Enum name="half" values="bottom,top"/>
              <Bool name="waterlogged"/>
            </States>
          </Content>
        </Block>
        <Block><Name>Log</Name><Content><Orientations>facing</Orientations></Content></Block>
        <Block><Name>Statue</Name><Content><Orientations>full</Orientations></Content></Block>
        <Block>
          <Name>Fence</Name>
          <Content>
            <States>
              <Bool name="north" rotatesTo="east"/>
              <Bool name="east" rotatesTo="south"/>
              <Bool name="south" rotatesTo="west"/>
              <Bool name="west" rotatesTo="north"/>
              <Enum name="axis" values="x,z" rotation="x:z,z:x"/>
            </States>
          </Content>
        </Block>
        <Block>
          <Name>Door</Name>
          <Content>
            <Orientations>horizontal</Orientations>
            <Footprint><Cell y="1"/></Footprint>
            <States><Bool name="open"/></States>
          </Content>
        </Block>
        <Tag registry="block"><Name>Wood</Name><Content><Member>block:example.blocks.fence</Member><Member>block:example.blocks.door</Member></Content></Tag>
        """);

    private readonly BlocksHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private (Engine Engine, Probe Probe) Start(string nof)
    {
        Probe? probe = null;
        _harness.Module(Module, nof, p => p.OnInitialize = found => probe = found);
        Engine engine = _harness.Start();
        return (engine, probe!);
    }

    [Fact]
    public void TheAirIsTypeZeroAndStateZero()
    {
        (Engine engine, Probe probe) = Start(Blocks);
        using (engine)
        {
            BlockType air = probe.Blocks.Air;
            Assert.Same(air, probe.Blocks.Get("block:nobara.air"));
            Assert.True(air.IsAir);
            Assert.Equal(0, air.Index);
            Assert.Equal(0u, air.FirstState);
            Assert.Equal(1, air.StateCount);
            Assert.Same(air, probe.Blocks.TypeOf(0));
            Assert.Equal(BlockOpacity.Transparent, probe.Blocks.GetOpacity(0));
            Assert.Equal(BlockCollision.None, probe.Blocks.GetCollision(0));
            Assert.Equal(string.Empty, air.Shape);
            Assert.True(float.IsPositiveInfinity(air.Hardness));
        }
    }

    [Fact]
    public void TypesAreSortedByIdentifierAndTheirStatesAreContiguous()
    {
        (Engine engine, Probe probe) = Start(Blocks);
        using (engine)
        {
            Assert.Equal(
                ["block:nobara.air", "block:example.blocks.door", "block:example.blocks.fence", "block:example.blocks.lamp", "block:example.blocks.log", "block:example.blocks.stairs", "block:example.blocks.statue"],
                probe.Blocks.Types.Select(t => t.ObjectId));
            uint next = 0;
            foreach (BlockType type in probe.Blocks.Types)
            {
                Assert.Equal(next, type.FirstState);
                next += (uint)type.StateCount;
            }

            Assert.Equal((int)next, probe.Blocks.StateCount);
            Assert.Equal([1, 16, 32, 2, 6, 16, 24], probe.Blocks.Types.Select(t => t.StateCount));
        }
    }

    [Fact]
    public void IndicesAreTheSameInTwoLoads()
    {
        (Engine first, Probe a) = Start(Blocks);
        string[] before;
        using (first)
        {
            before = Enumerable.Range(0, a.Blocks.StateCount).Select(s => a.Blocks.Describe((uint)s)).ToArray();
        }

        using var other = new BlocksHarness();
        Probe? b = null;
        other.Module(Module, Blocks, p => p.OnInitialize = found => b = found);
        using Engine second = other.Start();

        Assert.Equal(before, Enumerable.Range(0, b!.Blocks.StateCount).Select(s => b.Blocks.Describe((uint)s)));
    }

    [Fact]
    public void PropertiesAreOrderedEngineFirstThenByKindAndTheLastVariesFastest()
    {
        (Engine engine, Probe probe) = Start(Blocks);
        using (engine)
        {
            BlockType stairs = probe.Blocks.Get("block:example.blocks.stairs");
            Assert.Equal(["orientation", "waterlogged", "half"], stairs.Properties.Select(p => p.Name));
            Assert.Equal(StatePropertyKind.Orientation, stairs.Properties[0].Kind);
            Assert.Equal(["north", "east", "south", "west"], stairs.Properties[0].Values);
            Assert.Equal("block:example.blocks.stairs[orientation=north,waterlogged=false,half=bottom]", probe.Blocks.Describe(stairs.FirstState));
            Assert.Equal("block:example.blocks.stairs[orientation=north,waterlogged=false,half=top]", probe.Blocks.Describe(stairs.FirstState + 1));

            BlockType door = probe.Blocks.Get("block:example.blocks.door");
            Assert.Equal(["orientation", "part", "open"], door.Properties.Select(p => p.Name));
            Assert.Equal([new BlockCell(0, 0, 0), new BlockCell(0, 1, 0)], door.Footprint);
        }
    }

    [Fact]
    public void ValuesAreReadAndChangedInConstantTime()
    {
        (Engine engine, Probe probe) = Start(Blocks);
        using (engine)
        {
            BlockType stairs = probe.Blocks.Get("block:example.blocks.stairs");
            StateProperty half = stairs.GetProperty("half");
            StateProperty orientation = stairs.GetProperty("orientation");
            uint state = probe.Blocks.With(stairs.DefaultState, half, half.IndexOf("top"));
            state = probe.Blocks.With(state, orientation, orientation.IndexOf("west"));

            Assert.Equal(1, probe.Blocks.GetValue(state, half));
            Assert.Equal(3, probe.Blocks.GetValue(state, orientation));
            Assert.Equal(0, probe.Blocks.GetValue(state, stairs.GetProperty("waterlogged")));
            Assert.Same(stairs, probe.Blocks.TypeOf(state));
            Assert.Throws<ArgumentException>(() => probe.Blocks.GetValue(probe.Blocks.Air.FirstState, half));
            Assert.Throws<ArgumentOutOfRangeException>(() => probe.Blocks.With(state, half, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => probe.Blocks.TypeOf((uint)probe.Blocks.StateCount));
        }
    }

    [Fact]
    public void StateIdentifiersRoundTrip()
    {
        (Engine engine, Probe probe) = Start(Blocks);
        using (engine)
        {
            for (uint state = 0; state < probe.Blocks.StateCount; state++)
            {
                Assert.True(probe.Blocks.TryParseState(probe.Blocks.Describe(state), out uint parsed));
                Assert.Equal(state, parsed);
            }

            BlockType lamp = probe.Blocks.Get("block:example.blocks.lamp");
            Assert.True(probe.Blocks.TryParseState("block:example.blocks.lamp", out uint plain));
            Assert.Equal(lamp.DefaultState, plain);
            Assert.False(probe.Blocks.TryParseState("block:example.blocks.lamp[lit=maybe]", out _));
            Assert.False(probe.Blocks.TryParseState("block:example.blocks.missing", out _));
        }
    }

    [Fact]
    public void VariantsChangeTheNativeFieldsOfTheStatesTheyMatch()
    {
        (Engine engine, Probe probe) = Start(Blocks);
        using (engine)
        {
            BlockType lamp = probe.Blocks.Get("block:example.blocks.lamp");
            uint off = lamp.DefaultState;
            uint on = probe.Blocks.With(off, lamp.GetProperty("lit"), 1);

            Assert.Equal(BlockOpacity.Opaque, probe.Blocks.GetOpacity(off));
            Assert.Equal(0.5f, probe.Blocks.GetHardness(off));
            Assert.Equal(BlockOpacity.Translucent, probe.Blocks.GetOpacity(on));
            Assert.Equal(5f, probe.Blocks.GetHardness(on));
            Assert.Equal(BlockOpacity.Opaque, lamp.Opacity);
        }
    }

    [Fact]
    public void FourQuarterTurnsGiveTheSameState()
    {
        (Engine engine, Probe probe) = Start(Blocks);
        using (engine)
        {
            for (uint state = 0; state < probe.Blocks.StateCount; state++)
            {
                uint turned = probe.Blocks.Rotate(probe.Blocks.Rotate(probe.Blocks.Rotate(probe.Blocks.Rotate(state))));
                Assert.Equal(state, turned);
                Assert.Same(probe.Blocks.TypeOf(state), probe.Blocks.TypeOf(probe.Blocks.Rotate(state)));
            }
        }
    }

    [Theory]
    [InlineData("block:example.blocks.stairs[orientation=north]", "block:example.blocks.stairs[orientation=east,waterlogged=false,half=bottom]")]
    [InlineData("block:example.blocks.stairs[orientation=west,half=top]", "block:example.blocks.stairs[orientation=north,waterlogged=false,half=top]")]
    [InlineData("block:example.blocks.log[orientation=up]", "block:example.blocks.log[orientation=up]")]
    [InlineData("block:example.blocks.log[orientation=south]", "block:example.blocks.log[orientation=west]")]
    [InlineData("block:example.blocks.statue[orientation=north]", "block:example.blocks.statue[orientation=east]")]
    [InlineData("block:example.blocks.fence[north=true,axis=x]", "block:example.blocks.fence[north=false,east=true,south=false,west=false,axis=z]")]
    [InlineData("block:example.blocks.fence[west=true,south=true]", "block:example.blocks.fence[north=true,east=false,south=false,west=true,axis=z]")]
    public void AQuarterTurnTurnsOrientationsAndProperties(string from, string to)
    {
        (Engine engine, Probe probe) = Start(Blocks);
        using (engine)
        {
            Assert.True(probe.Blocks.TryParseState(from, out uint state));
            Assert.True(probe.Blocks.TryParseState(to, out uint expected));
            Assert.Equal(probe.Blocks.Describe(expected), probe.Blocks.Describe(probe.Blocks.Rotate(state)));
        }
    }

    [Fact]
    public void TagsComeFromTheShortcutAndFromTagResources()
    {
        (Engine engine, Probe probe) = Start(Blocks);
        using (engine)
        {
            Assert.True(probe.Blocks.TryGetTag("#example.lights", out BlockTag? lights));
            Assert.True(probe.Blocks.TryGetTag("#block:example.blocks.wood", out BlockTag? wood));
            BlockType lamp = probe.Blocks.Get("block:example.blocks.lamp");
            BlockType fence = probe.Blocks.Get("block:example.blocks.fence");

            Assert.Equal([lamp], lights.Members);
            Assert.True(lamp.HasTag(lights));
            Assert.True(probe.Blocks.HasTag(fence.FirstState + 3, wood));
            Assert.False(probe.Blocks.HasTag(lamp.DefaultState, wood));
            Assert.Equal(2, wood.Members.Count);
            Assert.Throws<ArgumentException>(() => probe.Blocks.TryGetTag("#item:example.lights", out _));
        }
    }

    [Fact]
    public void TheItemOfABlockPlacesItsState()
    {
        (Engine engine, Probe probe) = Start(Blocks);
        using (engine)
        {
            BlockType lamp = probe.Blocks.Get("block:example.blocks.lamp");
            ItemType item = probe.Items.Get("item:example.blocks.lamp");

            Assert.Equal("item:example.blocks.lamp", lamp.ItemId);
            Assert.Equal(lamp.ObjectId, item.DerivedFrom);
            Assert.Equal(16, item.MaxStack);
            Assert.True(item.Components.TryGet(probe.Blocks.Placeable, out PlaceableBlock? placeable));
            Assert.Same(lamp, placeable.Block);
            Assert.Equal("block:example.blocks.lamp[lit=true]", probe.Blocks.Describe(placeable.State));
            Assert.Equal(string.Empty, probe.Blocks.Get("block:example.blocks.log").ItemId);
        }
    }

    [Fact]
    public void ATypeAboveTheCapStopsTheLoad()
    {
        _harness.Module(Module, Lamp("""<States><Int name="level" max="1999"/></States>"""));

        EngineStopException stop = Assert.Throws<EngineStopException>(() => _harness.Start());

        Assert.Equal(BlocksModule.ModuleId, stop.ModuleId);
        BlockLoadException error = Assert.IsType<BlockLoadException>(stop.InnerException);
        BlockProblem problem = Assert.Single(error.Problems);
        Assert.Equal("block:example.blocks.lamp", problem.ObjectId);
        Assert.Contains("2000 states", problem.Message, StringComparison.Ordinal);
        Assert.Contains("blocks.maxStatesPerType, 1024", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDistributionRaisesOrLowersTheCap()
    {
        _harness.MaxStatesPerType = 2048;
        IBlockRegistry? registry = null;
        _harness.Module(Module, Lamp("""<States><Int name="level" max="1999"/></States>"""), p => p.OnInitialize = probe => registry = probe.Blocks);
        using (Engine engine = _harness.Start())
        {
            Assert.Equal(2048, registry!.MaxStatesPerType);
            Assert.Equal(2000, registry.Get("block:example.blocks.lamp").StateCount);
        }

        using var lowered = new BlocksHarness { MaxStatesPerType = 16 };
        lowered.Module(Module, Blocks);
        EngineStopException stop = Assert.Throws<EngineStopException>(() => lowered.Start());
        BlockLoadException error = Assert.IsType<BlockLoadException>(stop.InnerException);
        Assert.Equal(["block:example.blocks.fence", "block:example.blocks.statue"], error.Problems.Select(p => p.ObjectId));
    }

    [Fact]
    public void AFreePropertyIsResolvedPerStateWithItsVariants()
    {
        GenericElement?[]? values = null;
        FreeProperty? light = null;
        IBlockRegistry? blocks = null;
        _harness.Module(
            Module,
            Lamp(
                """
                <States><Bool name="lit"/><Int name="power" max="1"/></States>
                <Variants>
                  <Variant when="lit=true"><Properties><Light>10</Light><Probe.Light>12</Probe.Light></Properties></Variant>
                  <Variant when="lit=true,power=1"><Properties><Light>15</Light></Properties></Variant>
                </Variants>
                """).Replace("</Content>", "</Content><Properties><Light>3</Light></Properties>", StringComparison.Ordinal),
            p =>
            {
                p.OnRegister = probe => light = probe.Context.Resources.DeclareProperty("Light", PropertyType.Int);
                p.OnInitialize = probe =>
                {
                    blocks = probe.Blocks;
                    values = probe.Blocks.ResolveProperty(probe.Context, light!);
                };
            },
            shortName: "Probe");
        using Engine engine = _harness.Start();
        BlockType lamp = blocks!.Get("block:example.blocks.lamp");

        string? Light(string state)
        {
            Assert.True(blocks.TryParseState(state, out uint index));
            return values![index]?.Value?.Text;
        }

        Assert.Equal(blocks.StateCount, values!.Length);
        Assert.Null(values[0]);
        Assert.Equal("3", Light("block:example.blocks.lamp"));
        Assert.Equal("3", Light("block:example.blocks.lamp[power=1]"));
        Assert.Equal("12", Light("block:example.blocks.lamp[lit=true]"));
        Assert.Equal("15", Light("block:example.blocks.lamp[lit=true,power=1]"));
        Assert.Equal(4, lamp.StateCount);
    }

    [Fact]
    public void AFreePropertyOfTheWrongTypeInAVariantIsReported()
    {
        Exception? error = null;
        _harness.Module(
            Module,
            Lamp("""
                <States><Bool name="lit"/></States>
                <Variants><Variant when="lit=true"><Properties><Light>bright</Light></Properties></Variant></Variants>
                """),
            p =>
            {
                FreeProperty? light = null;
                p.OnRegister = probe => light = probe.Context.Resources.DeclareProperty("Light", PropertyType.Int);
                p.OnInitialize = probe => error = Record.Exception(() => probe.Blocks.ResolveProperty(probe.Context, light!));
            });
        using Engine engine = _harness.Start();

        BlockLoadException exception = Assert.IsType<BlockLoadException>(error);
        Assert.Contains("'bright'", Assert.Single(exception.Problems).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRegistryIsReadOnceNBlocksHasInitialized()
    {
        Exception? early = null;
        _harness.Module(Module, behavior: p => p.OnRegister = probe => early = Record.Exception(() => probe.Context.Resolve<IBlockRegistry>().Types));
        using Engine engine = _harness.Start();

        Assert.IsType<InvalidOperationException>(early);
    }
}
