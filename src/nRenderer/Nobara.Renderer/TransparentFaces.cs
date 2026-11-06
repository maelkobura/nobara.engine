// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers;
using System.Numerics;
using Nobara.Core;
using Nobara.Core.Jobs;

namespace Nobara.Renderer;

/// <summary>
/// The transparent triangles of a section, kept on the CPU so they can be
/// sorted from back to front when the camera moves: their indices as the
/// mesher gave them, their centers, and the order of the last sort.
/// </summary>
internal sealed class TransparentFaces
{
    private TransparentFaces(int triangles)
    {
        TriangleCount = triangles;
        Triangles = ArrayPool<uint>.Shared.Rent(triangles * 3);
        Sorted = ArrayPool<uint>.Shared.Rent(triangles * 3);
        Centers = ArrayPool<Vector3>.Shared.Rent(triangles);
        Keys = ArrayPool<float>.Shared.Rent(triangles);
        Order = ArrayPool<int>.Shared.Rent(triangles);
    }

    public int TriangleCount { get; }

    public uint[] Triangles { get; }

    public uint[] Sorted { get; }

    public Vector3[] Centers { get; }

    public float[] Keys { get; }

    public int[] Order { get; }

    /// <summary>Gets or sets whether the faces were sorted once.</summary>
    public bool HasOrder { get; set; }

    /// <summary>Gets or sets the camera of the last sort, in blocks from the corner of the section.</summary>
    public Vector3 SortedFrom { get; set; }

    public static TransparentFaces Create(ReadOnlySpan<ChunkVertex> vertices, ReadOnlySpan<uint> indices)
    {
        var faces = new TransparentFaces(indices.Length / 3);
        indices.CopyTo(faces.Triangles);
        for (int t = 0; t < faces.TriangleCount; t++)
        {
            faces.Centers[t] = (vertices[(int)indices[3 * t]].Position + vertices[(int)indices[(3 * t) + 1]].Position + vertices[(int)indices[(3 * t) + 2]].Position) / 3;
        }

        return faces;
    }

    /// <summary>Gets the triangles of the last sort.</summary>
    public ReadOnlySpan<uint> SortedIndices
    {
        [HotPath]
        get => Sorted.AsSpan(0, TriangleCount * 3);
    }

    /// <summary>
    /// Gets whether the camera moved enough since the last sort: farther than
    /// <paramref name="distance"/>, and farther than the distance to the
    /// center of the section times the sine of <paramref name="angle"/>, so
    /// the direction from the section to the camera turned by about that
    /// angle.
    /// </summary>
    [HotPath]
    public bool NeedsSort(Vector3 camera, Vector3 center, float distance, float angle)
    {
        if (!HasOrder)
        {
            return true;
        }

        float moved = Vector3.Distance(camera, SortedFrom);
        float turn = Vector3.Distance(SortedFrom, center) * MathF.Sin(angle * (MathF.PI / 180));
        return moved > Math.Max(distance, turn);
    }

    /// <summary>Sorts the triangles from the farthest from the camera to the nearest.</summary>
    /// <param name="camera">The camera, in blocks from the corner of the section.</param>
    [HotPath]
    public void Sort(Vector3 camera)
    {
        int count = TriangleCount;
        Span<float> keys = Keys.AsSpan(0, count);
        Span<int> order = Order.AsSpan(0, count);
        for (int t = 0; t < count; t++)
        {
            keys[t] = -Vector3.DistanceSquared(Centers[t], camera);
            order[t] = t;
        }

        keys.Sort(order);
        for (int t = 0; t < count; t++)
        {
            int from = order[t] * 3;
            Sorted[3 * t] = Triangles[from];
            Sorted[(3 * t) + 1] = Triangles[from + 1];
            Sorted[(3 * t) + 2] = Triangles[from + 2];
        }

        SortedFrom = camera;
        HasOrder = true;
    }

    public void Return()
    {
        ArrayPool<uint>.Shared.Return(Triangles);
        ArrayPool<uint>.Shared.Return(Sorted);
        ArrayPool<Vector3>.Shared.Return(Centers);
        ArrayPool<float>.Shared.Return(Keys);
        ArrayPool<int>.Shared.Return(Order);
    }

    /// <summary>Sorts the transparent faces of several sections, one section per index.</summary>
    internal readonly struct SortJob(TransparentFaces?[] faces, Vector3[] cameras) : IJobFor
    {
        [HotPath]
        public void Execute(int index) => faces[index]!.Sort(cameras[index]);
    }
}
