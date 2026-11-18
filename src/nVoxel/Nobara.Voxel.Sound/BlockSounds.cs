// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Audio;
using Nobara.Blocks;
using Nobara.Core;
using Nobara.Core.Resources;

namespace Nobara.Voxel.Sound;

/// <summary>The sound of each state of the blocks when it is placed and when it is broken, resolved when the module initializes.</summary>
internal sealed class BlockSounds
{
    /// <summary>The name of the free property of the sound of a block that is placed.</summary>
    public const string PlaceProperty = "PlaceSound";

    /// <summary>The name of the free property of the sound of a block that breaks.</summary>
    public const string BreakProperty = "BreakSound";

    private BlockSounds(SoundEvent?[] place, SoundEvent?[] @break, BlockType[] types)
    {
        Place = place;
        Break = @break;
        Types = types;
    }

    /// <summary>Gets the sound of each state when a block of it is placed, or <see langword="null"/> for silence.</summary>
    public SoundEvent?[] Place { get; }

    /// <summary>Gets the sound of each state when a block of it is broken, or <see langword="null"/> for silence.</summary>
    public SoundEvent?[] Break { get; }

    /// <summary>Gets the types of blocks with a sound in at least one state: the only ones the module subscribes to.</summary>
    public BlockType[] Types { get; }

    /// <summary>Builds the tables from the registries of the context.</summary>
    /// <param name="context">The module, for its qualified properties.</param>
    /// <param name="blocks">The blocks, frozen.</param>
    /// <param name="sounds">The sound events.</param>
    /// <param name="place">The property of the sound of placement.</param>
    /// <param name="break">The property of the sound of breaking.</param>
    /// <returns>The tables.</returns>
    /// <exception cref="BlockSoundException">A property names a sound event that no module provides.</exception>
    public static BlockSounds Build(ModuleContext context, IBlockRegistry blocks, ISoundRegistry sounds, FreeProperty place, FreeProperty @break)
    {
        var problems = new List<string>();
        SoundEvent?[] placed = Resolve(blocks.ResolveProperty(context, place), sounds, problems);
        SoundEvent?[] broken = Resolve(blocks.ResolveProperty(context, @break), sounds, problems);
        if (problems.Count > 0)
        {
            throw new BlockSoundException(problems);
        }

        var withSound = new bool[blocks.Types.Count];
        for (uint state = 0; state < (uint)placed.Length; state++)
        {
            if (placed[state] is not null || broken[state] is not null)
            {
                withSound[blocks.TypeOf(state).Index] = true;
            }
        }

        BlockType[] types = [.. blocks.Types.Where(t => withSound[t.Index])];
        return new BlockSounds(placed, broken, types);
    }

    private static SoundEvent?[] Resolve(GenericElement?[] values, ISoundRegistry sounds, List<string> problems)
    {
        var table = new SoundEvent?[values.Length];
        var found = new Dictionary<string, SoundEvent?>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        for (int state = 0; state < values.Length; state++)
        {
            GenericElement? value = values[state];
            string id = value?.Value?.Text ?? string.Empty;
            if (value is null || id.Length == 0)
            {
                continue;
            }

            if (!found.TryGetValue(id, out SoundEvent? sound))
            {
                sounds.TryGet(id, out sound);
                found.Add(id, sound);
            }

            if (sound is null)
            {
                // A property and its variants share one source: report each source once.
                if (reported.Add($"{value.Source}|{id}"))
                {
                    problems.Add($"{value.Source}: <{value.Name}> names the sound '{id}', which no loaded module provides.");
                }

                continue;
            }

            table[state] = sound;
        }

        return table;
    }
}
