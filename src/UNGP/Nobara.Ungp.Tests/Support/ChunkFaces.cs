// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Renderer;

namespace Nobara.Ungp.Tests.Support;

/// <summary>Builds the geometry of a test section, face by face, in one category per face.</summary>
internal sealed class ChunkFaces
{
    private readonly List<ChunkVertex> _vertices = [];
    private readonly List<uint> _opaque = [];
    private readonly List<uint> _cutout = [];
    private readonly List<uint> _transparent = [];

    /// <summary>Adds a square facing +Z at depth <paramref name="z"/>, from (x0, y0) to (x1, y1), in blocks of the chunk; its texture goes from u = 0 at x0 to u = 1 at x1.</summary>
    public ChunkFaces Front(ChunkCategory category, TextureLocation texture, float x0, float y0, float x1, float y1, float z)
    {
        Face(category, texture, ChunkNormal.PositiveZ, new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z));
        return this;
    }

    /// <summary>Adds the six faces of a box, from <paramref name="low"/> to <paramref name="high"/>, in blocks of the chunk.</summary>
    public ChunkFaces Box(ChunkCategory category, TextureLocation texture, Vector3 low, Vector3 high)
    {
        (float x0, float y0, float z0) = (low.X, low.Y, low.Z);
        (float x1, float y1, float z1) = (high.X, high.Y, high.Z);
        Face(category, texture, ChunkNormal.PositiveZ, new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1));
        Face(category, texture, ChunkNormal.NegativeZ, new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0));
        Face(category, texture, ChunkNormal.PositiveX, new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1));
        Face(category, texture, ChunkNormal.NegativeX, new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0));
        Face(category, texture, ChunkNormal.PositiveY, new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(x0, y1, z0));
        Face(category, texture, ChunkNormal.NegativeY, new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1));
        return this;
    }

    /// <summary>Gets the geometry, which <see cref="ChunkGeometry"/> copies.</summary>
    public ChunkSectionGeometry Geometry => new(_vertices.ToArray(), _opaque.ToArray(), _cutout.ToArray(), _transparent.ToArray());

    // Counter-clockwise seen from the front, the corners from the bottom left.
    private void Face(ChunkCategory category, TextureLocation texture, ChunkNormal normal, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        uint first = (uint)_vertices.Count;
        _vertices.Add(new ChunkVertex(a, new Vector2(0, 0), texture, normal, ChunkLight.Full));
        _vertices.Add(new ChunkVertex(b, new Vector2(1, 0), texture, normal, ChunkLight.Full));
        _vertices.Add(new ChunkVertex(c, new Vector2(1, 1), texture, normal, ChunkLight.Full));
        _vertices.Add(new ChunkVertex(d, new Vector2(0, 1), texture, normal, ChunkLight.Full));
        List<uint> list = category switch
        {
            ChunkCategory.Opaque => _opaque,
            ChunkCategory.Cutout => _cutout,
            _ => _transparent,
        };
        list.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
    }
}
