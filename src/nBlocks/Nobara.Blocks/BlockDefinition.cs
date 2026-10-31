// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Blocks.Schemas;
using Nobara.Core;
using Nobara.Core.Resources;

namespace Nobara.Blocks;

/// <summary>
/// The content of a block resource, decoded once per process and shared by
/// every context. Each context builds its own <see cref="BlockType"/> from it.
/// </summary>
internal sealed class BlockDefinition
{
    private BlockDefinition(ResourceData data, Block content)
    {
        Data = data;
        Content = content;
    }

    /// <summary>Gets the resource, overrides applied.</summary>
    public ResourceData Data { get; }

    /// <summary>Gets the content. Never change it: it is shared.</summary>
    public Block Content { get; }

    /// <summary>Decodes a resource of type <c>block</c>.</summary>
    /// <param name="data">The resource.</param>
    /// <returns>The definition.</returns>
    public static BlockDefinition Decode(ResourceData data) => new(data, Block.Parser.ParseFrom(data.Content.Span));
}

/// <summary>One reason why the blocks cannot be loaded.</summary>
/// <param name="ObjectId">The type of block at fault.</param>
/// <param name="Message">What is wrong, with where it is written in the content.</param>
/// <param name="Source">The position in the <c>.nof</c> file, as precise as the compiled data allows.</param>
public sealed record BlockProblem(string ObjectId, string Message, SourcePosition Source)
{
    /// <summary>Returns the problem as compilers write it.</summary>
    /// <returns>For example <c>Resources/blocks.nof(3,5): block:example.blocks.lamp: …</c>.</returns>
    public override string ToString() => string.IsNullOrEmpty(Source.File) ? $"{ObjectId}: {Message}" : $"{Source}: {ObjectId}: {Message}";
}

/// <summary>
/// Thrown when the blocks of the loaded modules cannot be used: a content
/// that an override broke, a type with more states than the distribution
/// allows, or free properties of a variant of the wrong type.
/// </summary>
public sealed class BlockLoadException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="problems">Every problem found, at least one.</param>
    public BlockLoadException(IReadOnlyList<BlockProblem> problems)
        : base("The blocks cannot be loaded:" + string.Concat(problems.Select(p => Environment.NewLine + "- " + p)))
    {
        Problems = problems;
    }

    /// <summary>Gets every problem found.</summary>
    public IReadOnlyList<BlockProblem> Problems { get; }
}
