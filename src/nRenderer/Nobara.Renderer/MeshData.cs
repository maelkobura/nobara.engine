// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.InteropServices;
using SharpGLTF.Schema2;

namespace Nobara.Renderer;

/// <summary>A vertex of a mesh, as <see cref="RenderMesh.VertexLayout"/> describes it.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct MeshVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoord;
    public Vector4 Tangent;
}

/// <summary>A part of a decoded mesh.</summary>
internal sealed record MeshPart(string Slot, int FirstIndex, int IndexCount);

/// <summary>
/// The triangles of a glTF binary file, with the transforms of the nodes of
/// its scene applied, grouped by material slot.
/// </summary>
internal sealed class MeshData
{
    /// <summary>The slot of the primitives that have no material.</summary>
    public const string DefaultSlot = "default";

    private MeshData(MeshVertex[] vertices, uint[] indices, IReadOnlyList<MeshPart> parts, Vector3 min, Vector3 max)
    {
        Vertices = vertices;
        Indices = indices;
        Parts = parts;
        BoundsMin = min;
        BoundsMax = max;
    }

    public MeshVertex[] Vertices { get; }

    public uint[] Indices { get; }

    public IReadOnlyList<MeshPart> Parts { get; }

    public Vector3 BoundsMin { get; }

    public Vector3 BoundsMax { get; }

    /// <summary>Gets the slot name of a glTF material.</summary>
    public static string SlotOf(SharpGLTF.Schema2.Material? material) =>
        material is null ? DefaultSlot : string.IsNullOrEmpty(material.Name) ? "#" + material.LogicalIndex : material.Name;

    /// <summary>Reads a glTF binary file.</summary>
    /// <param name="file">The bytes of the <c>.glb</c> file.</param>
    /// <returns>The mesh.</returns>
    /// <exception cref="InvalidDataException">The file is not a glTF binary file, has no triangles, or has primitives that are not triangles or lack positions.</exception>
    public static MeshData Read(byte[] file)
    {
        ModelRoot model;
        try
        {
            using var stream = new MemoryStream(file, writable: false);
            model = ModelRoot.ReadGLB(stream);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException($"the file is not a glTF binary file that can be read: {exception.Message}", exception);
        }

        Scene scene = model.DefaultScene ?? model.LogicalScenes.FirstOrDefault()
            ?? throw new InvalidDataException("the file has no scene");

        // Primitives of the scene, grouped by slot in the order the slots first appear.
        var groups = new List<(string Slot, List<(MeshVertex[] Vertices, uint[] Indices)> Pieces)>();
        foreach (Node node in Flatten(scene.VisualChildren))
        {
            if (node.Mesh is null)
            {
                continue;
            }

            Matrix4x4 world = node.WorldMatrix;
            foreach (MeshPrimitive primitive in node.Mesh.Primitives)
            {
                string slot = SlotOf(primitive.Material);
                if (primitive.DrawPrimitiveType != PrimitiveType.TRIANGLES)
                {
                    throw new InvalidDataException($"the mesh '{node.Mesh.Name}' has {primitive.DrawPrimitiveType} primitives; only triangles are read");
                }

                (MeshVertex[] Vertices, uint[] Indices) piece = ReadPrimitive(primitive, world, node.Mesh.Name);
                int group = groups.FindIndex(g => g.Slot == slot);
                if (group < 0)
                {
                    groups.Add((slot, []));
                    group = groups.Count - 1;
                }

                groups[group].Pieces.Add(piece);
            }
        }

        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        var parts = new List<MeshPart>();
        foreach ((string slot, List<(MeshVertex[] Vertices, uint[] Indices)> pieces) in groups)
        {
            int first = indices.Count;
            foreach ((MeshVertex[] pieceVertices, uint[] pieceIndices) in pieces)
            {
                uint baseVertex = (uint)vertices.Count;
                vertices.AddRange(pieceVertices);
                indices.AddRange(pieceIndices.Select(i => i + baseVertex));
            }

            parts.Add(new MeshPart(slot, first, indices.Count - first));
        }

        if (indices.Count == 0)
        {
            throw new InvalidDataException("the scene of the file has no triangles");
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (MeshVertex vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        return new MeshData([.. vertices], [.. indices], parts, min, max);
    }

    /// <summary>Gets the bytes of the vertices.</summary>
    public ReadOnlySpan<byte> VertexBytes => MemoryMarshal.AsBytes(Vertices.AsSpan());

    /// <summary>Gets the bytes of the indices.</summary>
    public ReadOnlySpan<byte> IndexBytes => MemoryMarshal.AsBytes(Indices.AsSpan());

    private static IEnumerable<Node> Flatten(IEnumerable<Node> nodes)
    {
        foreach (Node node in nodes)
        {
            yield return node;
            foreach (Node child in Flatten(node.VisualChildren))
            {
                yield return child;
            }
        }
    }

    private static (MeshVertex[] Vertices, uint[] Indices) ReadPrimitive(MeshPrimitive primitive, Matrix4x4 world, string meshName)
    {
        Accessor positions = primitive.GetVertexAccessor("POSITION")
            ?? throw new InvalidDataException($"a primitive of the mesh '{meshName}' has no positions");
        IList<Vector3> position = positions.AsVector3Array();
        IList<Vector3>? normal = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        IList<Vector2>? texCoord = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        IList<Vector4>? tangent = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
        uint[] indices = primitive.IndexAccessor is null
            ? Enumerable.Range(0, position.Count).Select(i => (uint)i).ToArray()
            : [.. primitive.GetIndices()];
        if (indices.Length % 3 != 0 || indices.Any(i => i >= position.Count))
        {
            throw new InvalidDataException($"a primitive of the mesh '{meshName}' has indices that do not form triangles of its vertices");
        }

        Matrix4x4.Invert(world, out Matrix4x4 inverse);
        Matrix4x4 normalMatrix = Matrix4x4.Transpose(inverse);
        var vertices = new MeshVertex[position.Count];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Position = Vector3.Transform(position[i], world);
            vertices[i].TexCoord = texCoord is null ? Vector2.Zero : texCoord[i];
            if (normal is not null)
            {
                vertices[i].Normal = SafeNormalize(Vector3.TransformNormal(normal[i], normalMatrix));
            }

            if (tangent is not null)
            {
                Vector3 direction = SafeNormalize(Vector3.TransformNormal(new Vector3(tangent[i].X, tangent[i].Y, tangent[i].Z), world));
                vertices[i].Tangent = new Vector4(direction, tangent[i].W < 0 ? -1 : 1);
            }
        }

        // A transform that mirrors the mesh turns its triangles inside out.
        if (world.GetDeterminant() < 0)
        {
            for (int i = 0; i < indices.Length; i += 3)
            {
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            }
        }

        if (normal is null)
        {
            ComputeNormals(vertices, indices);
        }

        if (tangent is null)
        {
            ComputeTangents(vertices, indices, texCoord is not null);
        }

        return (vertices, indices);
    }

    private static void ComputeNormals(MeshVertex[] vertices, uint[] indices)
    {
        for (int i = 0; i < indices.Length; i += 3)
        {
            ref MeshVertex a = ref vertices[indices[i]];
            ref MeshVertex b = ref vertices[indices[i + 1]];
            ref MeshVertex c = ref vertices[indices[i + 2]];
            Vector3 face = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            a.Normal += face;
            b.Normal += face;
            c.Normal += face;
        }

        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Normal = vertices[i].Normal == Vector3.Zero ? Vector3.UnitY : Vector3.Normalize(vertices[i].Normal);
        }
    }

    // Tangents from the texture coordinates, averaged per vertex and made orthogonal to the normal.
    private static void ComputeTangents(MeshVertex[] vertices, uint[] indices, bool hasTexCoords)
    {
        var tangents = new Vector3[vertices.Length];
        var bitangents = new Vector3[vertices.Length];
        if (hasTexCoords)
        {
            for (int i = 0; i < indices.Length; i += 3)
            {
                uint ia = indices[i], ib = indices[i + 1], ic = indices[i + 2];
                Vector3 e1 = vertices[ib].Position - vertices[ia].Position;
                Vector3 e2 = vertices[ic].Position - vertices[ia].Position;
                Vector2 d1 = vertices[ib].TexCoord - vertices[ia].TexCoord;
                Vector2 d2 = vertices[ic].TexCoord - vertices[ia].TexCoord;
                float determinant = (d1.X * d2.Y) - (d2.X * d1.Y);
                if (MathF.Abs(determinant) < 1e-12f)
                {
                    continue;
                }

                float r = 1 / determinant;
                Vector3 t = ((e1 * d2.Y) - (e2 * d1.Y)) * r;
                Vector3 b = ((e2 * d1.X) - (e1 * d2.X)) * r;
                foreach (uint v in new[] { ia, ib, ic })
                {
                    tangents[v] += t;
                    bitangents[v] += b;
                }
            }
        }

        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 n = vertices[i].Normal;
            Vector3 t = tangents[i] - (n * Vector3.Dot(n, tangents[i]));
            if (t.LengthSquared() < 1e-12f)
            {
                // Any direction orthogonal to the normal.
                t = Vector3.Cross(MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, n);
            }

            t = Vector3.Normalize(t);
            float w = Vector3.Dot(Vector3.Cross(n, t), bitangents[i]) < 0 ? -1 : 1;
            vertices[i].Tangent = new Vector4(t, w);
        }
    }

    private static Vector3 SafeNormalize(Vector3 value) => value == Vector3.Zero ? value : Vector3.Normalize(value);
}
