// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers.Binary;
using System.Text;

namespace Nobara.Ungp.Tests.Support;

/// <summary>Writes the GLB files of the test game.</summary>
internal static class Glb
{
    /// <summary>
    /// A square of one block in the XY plane, centered on the origin, facing
    /// +Z (counterclockwise seen from +Z), without material: its one slot is
    /// <c>default</c>. The texture covers it once, its first row at the top.
    /// </summary>
    public static byte[] Quad()
    {
        float[] positions = [-0.5f, -0.5f, 0, 0.5f, -0.5f, 0, 0.5f, 0.5f, 0, -0.5f, 0.5f, 0];
        float[] normals = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1];
        float[] texCoords = [0, 1, 1, 1, 1, 0, 0, 0];
        ushort[] indices = [0, 1, 2, 0, 2, 3];

        using var binary = new MemoryStream();
        foreach (float value in positions.Concat(normals).Concat(texCoords))
        {
            binary.Write(BitConverter.GetBytes(value));
        }

        foreach (ushort index in indices)
        {
            binary.Write(BitConverter.GetBytes(index));
        }

        const string Json = """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
            "meshes":[{"primitives":[{"attributes":{"POSITION":0,"NORMAL":1,"TEXCOORD_0":2},"indices":3}]}],
            "buffers":[{"byteLength":140}],
            "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":48},{"buffer":0,"byteOffset":48,"byteLength":48},
            {"buffer":0,"byteOffset":96,"byteLength":32},{"buffer":0,"byteOffset":128,"byteLength":12}],
            "accessors":[{"bufferView":0,"componentType":5126,"count":4,"type":"VEC3","min":[-0.5,-0.5,0],"max":[0.5,0.5,0]},
            {"bufferView":1,"componentType":5126,"count":4,"type":"VEC3"},
            {"bufferView":2,"componentType":5126,"count":4,"type":"VEC2"},
            {"bufferView":3,"componentType":5123,"count":6,"type":"SCALAR"}]}
            """;
        return Pack(Encoding.UTF8.GetBytes(Json), binary.ToArray());
    }

    private static byte[] Pack(byte[] json, byte[] binary)
    {
        byte[] jsonChunk = Padded(json, (byte)' ');
        byte[] binaryChunk = Padded(binary, 0);
        using var glb = new MemoryStream();
        Write(glb, 0x46546C67u);
        Write(glb, 2u);
        Write(glb, (uint)(12 + 8 + jsonChunk.Length + 8 + binaryChunk.Length));
        Write(glb, (uint)jsonChunk.Length);
        Write(glb, 0x4E4F534Au);
        glb.Write(jsonChunk);
        Write(glb, (uint)binaryChunk.Length);
        Write(glb, 0x004E4942u);
        glb.Write(binaryChunk);
        return glb.ToArray();
    }

    private static byte[] Padded(byte[] bytes, byte padding)
    {
        var padded = new byte[(bytes.Length + 3) & ~3];
        Array.Fill(padded, padding);
        bytes.CopyTo(padded, 0);
        return padded;
    }

    private static void Write(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
