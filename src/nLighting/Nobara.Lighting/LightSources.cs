// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using System.Globalization;
using Nobara.Blocks;
using Nobara.Core;
using Nobara.Core.Resources;
using Nobara.Voxel;

namespace Nobara.Lighting;

/// <summary>
/// The sources of light that nLighting knows, and what each block state does
/// to the light that crosses it. Resolve it with
/// <c>context.Resolve&lt;ILightSources&gt;()</c>.
/// </summary>
/// <remarks>
/// The sources are the block states whose <c>&lt;LightEmission&gt;</c> is not
/// zero, variants applied. The propagation reads nothing else. The tables
/// are built when nLighting starts; read them on any thread afterward.
/// </remarks>
public interface ILightSources
{
    /// <summary>Gets the states that emit light, in increasing order.</summary>
    /// <exception cref="InvalidOperationException">nLighting did not start.</exception>
    IReadOnlyList<uint> StaticSources { get; }

    /// <summary>Gets the light that a state emits.</summary>
    /// <param name="state">The nBlocks state index.</param>
    /// <returns>The color; without color, its three components are equal.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The state is unknown.</exception>
    /// <exception cref="InvalidOperationException">nLighting did not start.</exception>
    LightColor GetEmission(uint state);

    /// <summary>
    /// Gets the levels that a state takes from the light that enters it, on
    /// top of the level that the light loses at each block.
    /// </summary>
    /// <param name="state">The nBlocks state index.</param>
    /// <returns>The color; without color, its three components are equal.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The state is unknown.</exception>
    /// <exception cref="InvalidOperationException">nLighting did not start.</exception>
    LightColor GetFilter(uint state);

    /// <summary>Tells whether light crosses a face of a state, in either direction.</summary>
    /// <param name="state">The nBlocks state index.</param>
    /// <param name="face">The face of the cell, after the orientation of the state.</param>
    /// <returns>
    /// <see langword="false"/> when the state is <c>opaque</c> and its shape
    /// covers that face. The light of a block closed on its six faces still
    /// leaves it, when it emits. A shape that depends on block data reports
    /// its faces open here: its faces are read from the block data of each
    /// cell.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The state is unknown.</exception>
    /// <exception cref="InvalidOperationException">nLighting did not start.</exception>
    bool IsOpen(uint state, BlockFace face);
}

/// <summary>What a state does to light: its emission, its filter, and its closed faces, packed for the propagation.</summary>
internal readonly struct StateLight
{
    public const byte Solid = 1;
    public const byte Instance = 2;

    [HotPath]
    public StateLight(ushort emission, ushort filter, byte faces, byte flags) => (Emission, Filter, Faces, Flags) = (emission, filter, faces, flags);

    /// <summary>Gets the emitted light, packed as a block channel.</summary>
    public ushort Emission { get; }

    /// <summary>Gets the filter, packed as a block channel.</summary>
    public ushort Filter { get; }

    /// <summary>Gets the faces that light does not cross, one bit per <see cref="BlockFace"/>.</summary>
    public byte Faces { get; }

    /// <summary>Gets <see cref="Solid"/> and <see cref="Instance"/>.</summary>
    public byte Flags { get; }

    [HotPath]
    public bool Equals(in StateLight other) => Emission == other.Emission && Filter == other.Filter && Faces == other.Faces && Flags == other.Flags;
}

/// <summary>
/// The registry of the light sources and filters of one context, built from
/// the free properties of the blocks when nLighting starts.
/// </summary>
internal sealed class LightSourceRegistry : ILightSources
{
    private const string ModelShape = "shape:nobara.model";

    private readonly FreeProperty _emission;
    private readonly FreeProperty _filter;
    private StateLight[] _states = [];
    private ReadOnlyCollection<uint> _sources = ReadOnlyCollection<uint>.Empty;
    private IBlockRegistry? _blocks;

    public LightSourceRegistry(ModuleContext context)
    {
        _emission = context.Resources.DeclareProperty("LightEmission", PropertyType.String);
        _filter = context.Resources.DeclareProperty("LightFilter", PropertyType.String);
    }

    /// <summary>Gets the number of components of the block channel: 3 with color, 1 without.</summary>
    public int Components { get; private set; } = 1;

    public bool IsColored => Components == 3;

    public IReadOnlyList<uint> StaticSources => _blocks is null ? throw NotStarted() : _sources;

    /// <summary>Gets the light of every state, by state index.</summary>
    internal StateLight[] States
    {
        [HotPath]
        get => _states;
    }

    /// <summary>Builds the tables of every state.</summary>
    /// <exception cref="BlockLoadException">A <c>LightEmission</c> or <c>LightFilter</c> cannot be read.</exception>
    public void Start(ModuleContext context, IBlockRegistry blocks, IShapeRegistry shapes, bool colored)
    {
        Components = colored ? 3 : 1;
        GenericElement?[] emissions = blocks.ResolveProperty(context, _emission);
        GenericElement?[] filters = blocks.ResolveProperty(context, _filter);
        var problems = new List<BlockProblem>();
        var states = new StateLight[blocks.StateCount];
        var sources = new List<uint>();
        for (uint state = 0; state < (uint)states.Length; state++)
        {
            BlockType type = blocks.TypeOf(state);
            if (type.IsAir) continue;
            ushort emission = Read(emissions[state], "LightEmission", type, problems);
            ushort filter = Read(filters[state], "LightFilter", type, problems);
            if (!colored)
            {
                // One level: a source emits its brightest component, and a filter lets the most permissive one through.
                emission = (ushort)Math.Max(emission >> 8, Math.Max((emission >> 4) & 15, emission & 15));
                filter = (ushort)Math.Min(filter >> 8, Math.Min((filter >> 4) & 15, filter & 15));
            }

            byte faces = 0;
            byte flags = 0;
            if (blocks.GetOpacity(state) == BlockOpacity.Opaque && !string.Equals(type.Shape, ModelShape, StringComparison.Ordinal))
            {
                if (shapes.TryGet(type.Shape, out IBlockShape? shape) && shape!.IsInstanceDependent)
                {
                    flags |= StateLight.Instance;
                }
                else
                {
                    faces = shapes.Get(state, BlockDataView.Empty).Opacity.Bits;
                    if (faces == FaceMask.All.Bits) flags |= StateLight.Solid;
                }
            }

            states[state] = new StateLight(emission, filter, faces, flags);
            if (emission != 0) sources.Add(state);
        }

        if (problems.Count > 0) throw new BlockLoadException(problems);
        _states = states;
        _sources = Array.AsReadOnly(sources.ToArray());
        _blocks = blocks;
    }

    public LightColor GetEmission(uint state) => ToColor(Get(state).Emission);

    public LightColor GetFilter(uint state) => ToColor(Get(state).Filter);

    public bool IsOpen(uint state, BlockFace face) => (Get(state).Faces & (1 << (int)face)) == 0;

    /// <summary>Tells whether a change of state can change the light: emission, filter, or closed faces differ.</summary>
    [HotPath]
    internal bool Differs(uint oldState, uint newState)
    {
        if (oldState >= (uint)_states.Length || newState >= (uint)_states.Length) return true;
        ref readonly StateLight before = ref _states[oldState];
        ref readonly StateLight after = ref _states[newState];
        return !before.Equals(after) || (after.Flags & StateLight.Instance) != 0;
    }

    private StateLight Get(uint state)
    {
        if (_blocks is null) throw NotStarted();
        if (state >= (uint)_states.Length) throw new ArgumentOutOfRangeException(nameof(state));
        return _states[state];
    }

    private LightColor ToColor(ushort packed) =>
        IsColored ? new LightColor(packed >> 8, (packed >> 4) & 15, packed & 15) : new LightColor(packed & 15, packed & 15, packed & 15);

    private static InvalidOperationException NotStarted() => new("The light sources are read after nLighting starts.");

    // Reads "14" (the three components at once) or "15 8 2" (red, green, blue), each from 0 to 15.
    private static ushort Read(GenericElement? element, string name, BlockType type, List<BlockProblem> problems)
    {
        if (element?.Value is null) return 0;
        string[] parts = element.Value.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Span<int> levels = stackalloc int[3];
        if (parts.Length is 1 or 3)
        {
            bool valid = true;
            for (int i = 0; i < parts.Length && valid; i++)
            {
                valid = int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out levels[i]) && levels[i] <= 15;
            }

            if (valid)
            {
                if (parts.Length == 1) levels[1] = levels[2] = levels[0];
                return (ushort)((levels[0] << 8) | (levels[1] << 4) | levels[2]);
            }
        }

        problems.Add(new BlockProblem(type.ObjectId, $"<{name}> '{element.Value.Text}' is not a level from 0 to 15, or three levels (red, green, blue).", element.Source));
        return 0;
    }
}
