// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Mathematics;

namespace Nobara.Entity;

/// <summary>
/// Computes the world transform of every child from its parent and its local
/// transform: system <c>nobara.entity.transforms</c>, in stage <c>Late</c>.
/// It goes depth by depth, so the parent of each child is up to date when the
/// child is computed; within a depth, blocks run in parallel.
/// </summary>
internal sealed class TransformSystem(EntityWorlds worlds) : IEntitySystem
{
    /// <summary>The name of the system.</summary>
    public const string Name = "nobara.entity.transforms";

    public void Run(in EntitySystemRun run)
    {
        int maxDepth = run.World.MaxDepth;
        for (int depth = 1; depth <= maxDepth; depth++)
        {
            run.RunChunks(new PropagateJob
            {
                World = run.World,
                Depth = depth,
                Transform = worlds.WorldTransform,
                Local = worlds.LocalTransform,
                Link = worlds.ParentLink,
            });
        }
    }

    /// <summary>Computes the world transforms of the children of one depth in one block.</summary>
    private struct PropagateJob : IChunkJob
    {
        public EntityWorld World;
        public int Depth;
        public ComponentType<WorldTransform> Transform;
        public ComponentType<LocalTransform> Local;
        public ComponentType<ParentLink> Link;

        public readonly void Execute(in ChunkView chunk)
        {
            ReadOnlySpan<ParentLink> links = chunk.Read(Link);
            ReadOnlySpan<LocalTransform> locals = chunk.Read(Local);
            Span<WorldTransform> transforms = chunk.WriteUntracked(Transform);
            for (int i = 0; i < chunk.Count; i++)
            {
                ParentLink link = links[i];
                if (link.Depth != Depth || link.Parent.IsNull)
                {
                    continue;
                }

                // The parent has a smaller depth: no job of this depth writes it.
                ref readonly WorldTransform parent = ref World.Read(link.Parent, Transform);
                LocalTransform local = locals[i];
                WorldTransform result = parent.Combine(local.Position, local.Rotation, local.Scale);
                if (!result.Equals(transforms[i]))
                {
                    transforms[i] = result;
                    chunk.MarkChanged(Transform, i);
                }
            }
        }
    }
}
