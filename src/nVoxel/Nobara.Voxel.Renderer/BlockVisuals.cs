// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Nobara.Blocks;
using Nobara.Core;
using Nobara.Renderer;

namespace Nobara.Voxel.Renderer;

/// <summary>A vertex of a model, placed and turned in the cell of its block.</summary>
internal readonly record struct ModelVertex(Vector3 Position, Vector2 TextureCoordinates, ChunkNormal Normal, TextureLocation Texture);

/// <summary>The triangles of a model for one state, in the cell of the block.</summary>
internal sealed class ModelGeometry(ModelVertex[] vertices, uint[] indices)
{
    public ModelVertex[] Vertices { get; } = vertices;

    public uint[] Indices { get; } = indices;
}

/// <summary>The free properties of blocks that the meshing reads, as declared by nVoxel.Renderer.</summary>
internal sealed class VisualProperties
{
    /// <summary>The names of the properties of textures, from the most general to the most specific.</summary>
    public static ImmutableArray<string> TextureNames { get; } =
    [
        "RenderTexture",
        "RenderTextureSide", "RenderTextureTop", "RenderTextureBottom",
        "RenderTextureNorth", "RenderTextureEast", "RenderTextureSouth", "RenderTextureWest",
    ];

    public VisualProperties(ModuleContext context)
    {
        Textures = [.. TextureNames.Select(name => context.Resources.DeclareProperty(name, Core.Resources.PropertyType.ResourceId))];
        Model = context.Resources.DeclareProperty("RenderModel", Core.Resources.PropertyType.ResourceId);
        Tint = context.Resources.DeclareProperty("RenderTint", Core.Resources.PropertyType.String);
    }

    public Core.Resources.FreeProperty[] Textures { get; }

    public Core.Resources.FreeProperty Model { get; }

    public Core.Resources.FreeProperty Tint { get; }
}

/// <summary>A problem of the visual of blocks, found when nVoxel.Renderer starts.</summary>
public sealed class BlockVisualException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="problems">The problems, one per line, each with its file and line.</param>
    public BlockVisualException(IReadOnlyList<string> problems)
        : base("The visual of blocks cannot be used:" + Environment.NewLine + string.Join(Environment.NewLine, problems))
    {
        Problems = problems;
    }

    /// <summary>Gets the problems.</summary>
    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// The tables of the meshing, by block state, built once when nVoxel.Renderer
/// starts: what meshes a state, which faces of its neighbors it hides, its
/// category, its textures after its orientation, its tint, and its model.
/// Background jobs read them; nothing changes them.
/// </summary>
internal sealed class BlockVisuals
{
    public const byte Air = 0;
    public const byte Cube = 1;
    public const byte Model = 2;
    public const byte Custom = 3;
    public const byte Invisible = 4;

    /// <summary>The bit of <see cref="Occludes"/> for a state whose shape depends on its block data.</summary>
    public const byte InstanceBit = 0x80;

    /// <summary>The six faces, in the order of <see cref="ChunkNormal"/>.</summary>
    public const byte AllFaces = 0x3F;

    // The tables, by state; FaceTextures and FaceTurns have six entries per state, in the order of ChunkNormal.
    internal readonly byte[] Kinds;
    internal readonly byte[] OccludeMasks;
    internal readonly byte[] Categories;
    internal readonly byte[] TintIndices;
    internal readonly uint[] FaceTextures;

    // The quarter turns, 0 to 3, of the texture of each face in the face, after the orientation of the state.
    internal readonly byte[] FaceTurns;
    internal readonly int[] TypeIndices;
    internal readonly int[] ModelIndices;
    internal readonly ushort[] MesherIndices;
    internal readonly Matrix4x4[] Orientations;

    internal BlockVisuals(int states)
    {
        Kinds = new byte[states];
        OccludeMasks = new byte[states];
        Categories = new byte[states];
        TintIndices = new byte[states];
        FaceTextures = new uint[states * 6];
        FaceTurns = new byte[states * 6];
        TypeIndices = new int[states];
        ModelIndices = new int[states];
        MesherIndices = new ushort[states];
        Orientations = new Matrix4x4[states];
    }

    /// <summary>Gets the number of states.</summary>
    [HotPath]
    public int StateCount => Kinds.Length;

    /// <summary>Gets the models, by the index of <see cref="ModelOf"/>.</summary>
    [HotPath]
    public ModelGeometry[] Models { get; internal set; } = [];

    /// <summary>Gets the meshers of shapes added by modules, by the index of <see cref="MesherOf"/>.</summary>
    [HotPath]
    public IBlockMesher[] Meshers { get; internal set; } = [];

    /// <summary>Gets the palette of tints, in linear colors; index 0 is white.</summary>
    [HotPath]
    public Vector3[] Tints { get; internal set; } = [Vector3.One];

    /// <summary>Gets whether some state has an instance-dependent shape.</summary>
    [HotPath]
    public bool HasInstanceStates { get; internal set; }

    /// <summary>Gets the direction of a face, in the order of <see cref="ChunkNormal"/>.</summary>
    [HotPath]
    public static int Direction(BlockFace face) => face switch
    {
        BlockFace.West => 0,
        BlockFace.East => 1,
        BlockFace.Down => 2,
        BlockFace.Up => 3,
        BlockFace.North => 4,
        _ => 5,
    };

    /// <summary>Gets the face of a direction.</summary>
    [HotPath]
    public static BlockFace FaceOf(int direction) => direction switch
    {
        0 => BlockFace.West,
        1 => BlockFace.East,
        2 => BlockFace.Down,
        3 => BlockFace.Up,
        4 => BlockFace.North,
        _ => BlockFace.South,
    };

    /// <summary>Gets the opposite direction.</summary>
    [HotPath]
    public static int Opposite(int direction) => direction ^ 1;

    /// <summary>Packs a texture for <see cref="FaceTexture"/>: 0 is no texture.</summary>
    [HotPath]
    public static uint Pack(TextureLocation texture) => (((uint)texture.Array << 11) | (uint)texture.Layer) + 1;

    /// <summary>Unpacks a texture of <see cref="FaceTexture"/>.</summary>
    [HotPath]
    public static bool TryUnpack(uint packed, out TextureLocation texture)
    {
        if (packed == 0)
        {
            texture = default;
            return false;
        }

        uint value = packed - 1;
        texture = new TextureLocation((int)(value >> 11), (int)(value & 0x7FF));
        return true;
    }

    [HotPath]
    public byte KindOf(uint state) => state < (uint)Kinds.Length ? Kinds[state] : Air;

    [HotPath]
    public byte Occludes(uint state) => state < (uint)OccludeMasks.Length ? OccludeMasks[state] : (byte)0;

    [HotPath]
    public ChunkCategory CategoryOf(uint state) => state < (uint)Categories.Length ? (ChunkCategory)Categories[state] : ChunkCategory.Opaque;

    [HotPath]
    public int TintOf(uint state) => state < (uint)TintIndices.Length ? TintIndices[state] : 0;

    [HotPath]
    public int TypeOf(uint state) => state < (uint)TypeIndices.Length ? TypeIndices[state] : -1;

    [HotPath]
    public int ModelOf(uint state) => state < (uint)ModelIndices.Length ? ModelIndices[state] : -1;

    [HotPath]
    public int MesherOf(uint state) => MesherIndices[state];

    [HotPath]
    public Matrix4x4 OrientationOf(uint state) => state < (uint)Orientations.Length ? Orientations[state] : Matrix4x4.Identity;

    [HotPath]
    public uint FaceTexture(uint state, int direction) => state < (uint)Kinds.Length ? FaceTextures[(state * 6) + direction] : 0;

    /// <summary>Gets how many quarter turns the texture of a face is turned in the face, from 0 to 3.</summary>
    [HotPath]
    public int FaceTurn(uint state, int direction) => state < (uint)Kinds.Length ? FaceTurns[(state * 6) + direction] : 0;

    [HotPath]
    public BlockVisual Get(uint state) => state < (uint)Kinds.Length ? new BlockVisual(this, state) : throw new ArgumentOutOfRangeException(nameof(state));

    /// <summary>Gets the unit vector of a direction.</summary>
    [HotPath]
    public static Vector3 Normal(int direction) => direction switch
    {
        0 => -Vector3.UnitX,
        1 => Vector3.UnitX,
        2 => -Vector3.UnitY,
        3 => Vector3.UnitY,
        4 => -Vector3.UnitZ,
        _ => Vector3.UnitZ,
    };
}

/// <summary>Builds <see cref="BlockVisuals"/> when nVoxel.Renderer starts.</summary>
internal static class BlockVisualBuilder
{
    /// <summary>Builds the tables from the registries of the context and the resolved properties.</summary>
    /// <param name="context">The module, for its logger.</param>
    /// <param name="blocks">The blocks.</param>
    /// <param name="shapes">The shapes, frozen.</param>
    /// <param name="multiBlocks">The multi-blocks, frozen.</param>
    /// <param name="resources">The render resources.</param>
    /// <param name="properties">The declared properties.</param>
    /// <param name="meshers">The meshers of shapes added by modules, by shape.</param>
    /// <returns>The tables.</returns>
    /// <exception cref="BlockVisualException">A property names a resource that does not exist, or has a value that cannot be read.</exception>
    public static BlockVisuals Build(ModuleContext context, IBlockRegistry blocks, IShapeRegistry shapes, IMultiBlockRegistry multiBlocks, IRenderResources resources, VisualProperties properties, IReadOnlyDictionary<string, IBlockMesher> meshers)
    {
        var visuals = new BlockVisuals(blocks.StateCount);
        var problems = new List<string>();
        var warned = new HashSet<string>(StringComparer.Ordinal);
        GenericElement?[][] textures = [.. properties.Textures.Select(p => blocks.ResolveProperty(context, p))];
        GenericElement?[] models = blocks.ResolveProperty(context, properties.Model);
        GenericElement?[] tints = blocks.ResolveProperty(context, properties.Tint);

        var mesherIds = meshers.Keys.Order(StringComparer.Ordinal).ToArray();
        visuals.Meshers = [.. mesherIds.Select(id => meshers[id])];
        var tintIndex = new Dictionary<Vector3, int> { [Vector3.One] = 0 };
        var tintColors = new List<Vector3> { Vector3.One };
        var modelIndex = new Dictionary<(string Mesh, Matrix4x4 Orientation, uint Fallback), int>();
        var modelList = new List<ModelGeometry>();

        foreach (string shape in blocks.Types.Select(t => t.Shape).Where(s => s.Length > 0 && !IsNative(s) && !meshers.ContainsKey(s)).Distinct().Order(StringComparer.Ordinal))
        {
            context.Logger.Warning($"No mesher is registered for the shape '{shape}': its blocks are not drawn. A module that adds a shape registers its mesher with IVoxelMeshing.RegisterMesher.");
        }

        Span<uint> reference = stackalloc uint[6];
        Span<int> referenceTurns = stackalloc int[6];
        for (uint state = 0; state < (uint)blocks.StateCount; state++)
        {
            BlockType type = blocks.TypeOf(state);
            visuals.TypeIndices[state] = type.Index;
            visuals.ModelIndices[state] = -1;
            if (type.IsAir)
            {
                continue;
            }

            byte kind = type.Shape switch
            {
                ShapeIds.Cube => BlockVisuals.Cube,
                ShapeIds.Model => BlockVisuals.Model,
                _ when meshers.ContainsKey(type.Shape) => BlockVisuals.Custom,
                _ => BlockVisuals.Invisible,
            };
            visuals.Kinds[state] = kind;
            if (kind == BlockVisuals.Custom)
            {
                visuals.MesherIndices[state] = (ushort)Array.IndexOf(mesherIds, type.Shape);
            }

            BlockOpacity opacity = blocks.GetOpacity(state);
            visuals.Categories[state] = (byte)(opacity switch
            {
                BlockOpacity.Cutout => ChunkCategory.Cutout,
                BlockOpacity.Translucent => ChunkCategory.Transparent,
                _ => ChunkCategory.Opaque,
            });

            Matrix4x4 orientation = shapes.GetOrientation(state);
            visuals.Orientations[state] = orientation;
            if (shapes.TryGet(type.Shape, out IBlockShape? shapeImplementation) && shapeImplementation!.IsInstanceDependent)
            {
                visuals.OccludeMasks[state] = BlockVisuals.InstanceBit;
                visuals.HasInstanceStates = true;
            }
            else if (opacity == BlockOpacity.Opaque && kind != BlockVisuals.Model)
            {
                visuals.OccludeMasks[state] = MaskOf(shapes.Get(state, BlockDataView.Empty).Opacity);
            }

            // Textures by face of the reference orientation, then turned with the block.
            for (int face = 0; face < 6; face++)
            {
                reference[face] = ResolveTexture(textures, state, face, resources, problems, out referenceTurns[face]);
            }

            for (int face = 0; face < 6; face++)
            {
                int turned = DirectionOf(Vector3.TransformNormal(BlockVisuals.Normal(face), orientation));
                visuals.FaceTextures[(state * 6) + turned] = reference[face];
                visuals.FaceTurns[(state * 6) + turned] = (byte)((referenceTurns[face] + TurnOf(face, turned, orientation)) & 3);
            }

            if (kind == BlockVisuals.Cube && reference.IndexOfAnyExcept(0u) < 0 && warned.Add(type.ObjectId))
            {
                context.Logger.Warning($"Block '{type.ObjectId}' ({type.Source}) has a cube shape and no RenderTexture: its faces are not drawn.");
            }

            visuals.TintIndices[state] = (byte)ResolveTint(tints[state], tintIndex, tintColors, problems);

            if (kind == BlockVisuals.Model && (!multiBlocks.IsMultiBlock(state) || multiBlocks.IsOrigin(state)))
            {
                visuals.ModelIndices[state] = ResolveModel(context, type, models[state], orientation, reference[0], resources, modelIndex, modelList, problems, warned);
            }
        }

        if (problems.Count > 0)
        {
            throw new BlockVisualException([.. problems.Distinct()]);
        }

        visuals.Models = [.. modelList];
        visuals.Tints = [.. tintColors];
        return visuals;
    }

    private static bool IsNative(string shape) => shape is ShapeIds.Cube or ShapeIds.Model;

    private static byte MaskOf(FaceMask mask)
    {
        byte bits = 0;
        for (int direction = 0; direction < 6; direction++)
        {
            if (mask[BlockVisuals.FaceOf(direction)])
            {
                bits |= (byte)(1 << direction);
            }
        }

        return bits;
    }

    // The directions of U and V of the texture of each face, as the mesher lays them out (ChunkMesher.TextureCoordinates).
    private static (Vector3 U, Vector3 V) TextureAxes(int direction) => direction switch
    {
        0 => (Vector3.UnitZ, -Vector3.UnitY),
        1 => (-Vector3.UnitZ, -Vector3.UnitY),
        2 => (Vector3.UnitX, -Vector3.UnitZ),
        3 => (Vector3.UnitX, Vector3.UnitZ),
        4 => (-Vector3.UnitX, -Vector3.UnitY),
        _ => (Vector3.UnitX, -Vector3.UnitY),
    };

    // The quarter turns that take the U axis of the texture of a face of the reference orientation,
    // turned with the state, to the axes of the face it turns to: 0 along U, 1 along V, 2 against U, 3 against V.
    private static int TurnOf(int face, int turned, Matrix4x4 orientation)
    {
        Vector3 u = Vector3.TransformNormal(TextureAxes(face).U, orientation);
        (Vector3 targetU, Vector3 targetV) = TextureAxes(turned);
        float alongU = Vector3.Dot(u, targetU);
        float alongV = Vector3.Dot(u, targetV);
        if (alongU > 0.5f) return 0;
        if (alongV > 0.5f) return 1;
        return alongU < -0.5f ? 2 : 3;
    }

    private static int DirectionOf(Vector3 normal)
    {
        Vector3 a = Vector3.Abs(normal);
        if (a.X >= a.Y && a.X >= a.Z)
        {
            return normal.X < 0 ? 0 : 1;
        }

        if (a.Y >= a.Z)
        {
            return normal.Y < 0 ? 2 : 3;
        }

        return normal.Z < 0 ? 4 : 5;
    }

    // The most specific property wins: the face itself, then Side, Top or Bottom, then RenderTexture.
    // The attribute turn gives the quarter turns of the texture in the face, clockwise seen from the front.
    private static uint ResolveTexture(GenericElement?[][] textures, uint state, int direction, IRenderResources resources, List<string> problems, out int turn)
    {
        turn = 0;
        int specific = direction switch
        {
            0 => 7, // West
            1 => 5, // East
            2 => 3, // Bottom
            3 => 2, // Top
            4 => 4, // North
            _ => 6, // South
        };
        int middle = direction is 2 or 3 ? -1 : 1;
        GenericElement? value = textures[specific][state] ?? (middle >= 0 ? textures[middle][state] : null) ?? textures[0][state];
        if (value is null)
        {
            return 0;
        }

        if (value.Attribute("turn") is { } attribute)
        {
            turn = attribute.Value.Text.Trim() switch
            {
                "0" => 0,
                "90" => 1,
                "180" => 2,
                "270" => 3,
                _ => -1,
            };
            if (turn < 0)
            {
                problems.Add($"{attribute.Source}: the attribute turn of <{value.Name}> is '{attribute.Value.Text}'; it is 0, 90, 180, or 270.");
                turn = 0;
            }
        }

        string id = value.Value?.Text ?? string.Empty;
        if (!resources.TryGetTexture(id, out RenderTexture? texture))
        {
            problems.Add($"{value.Source}: <{value.Name}> names the texture '{id}', which no loaded module provides.");
            return 0;
        }

        if (texture.Location.Array > ChunkVertex.MaxTextureArray || texture.Location.Layer > ChunkVertex.MaxTextureLayer)
        {
            problems.Add($"{value.Source}: the texture '{id}' is in texture array {texture.Location.Array}, layer {texture.Location.Layer}; a chunk vertex holds arrays 0 to {ChunkVertex.MaxTextureArray} and layers 0 to {ChunkVertex.MaxTextureLayer}.");
            return 0;
        }

        return BlockVisuals.Pack(texture.Location);
    }

    private static int ResolveTint(GenericElement? value, Dictionary<Vector3, int> index, List<Vector3> colors, List<string> problems)
    {
        if (value is null)
        {
            return 0;
        }

        string text = value.Value?.Text.Trim() ?? string.Empty;
        if (text.Length != 7 || text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
        {
            problems.Add($"{value.Source}: <{value.Name}> is '{text}'; a tint is a color #RRGGBB, such as #7CBD6B.");
            return 0;
        }

        var color = new Vector3(Linear((rgb >> 16) & 0xFF), Linear((rgb >> 8) & 0xFF), Linear(rgb & 0xFF));
        if (index.TryGetValue(color, out int existing))
        {
            return existing;
        }

        if (colors.Count > ChunkVertex.MaxTint)
        {
            problems.Add($"{value.Source}: the blocks have more than {ChunkVertex.MaxTint} tints, which is what a chunk vertex holds.");
            return 0;
        }

        index[color] = colors.Count;
        colors.Add(color);
        return colors.Count - 1;
    }

    // An 8-bit sRGB channel as a linear value.
    private static float Linear(uint channel)
    {
        float c = channel / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private static int ResolveModel(
        ModuleContext context,
        BlockType type,
        GenericElement? value,
        Matrix4x4 orientation,
        uint fallback,
        IRenderResources resources,
        Dictionary<(string Mesh, Matrix4x4 Orientation, uint Fallback), int> index,
        List<ModelGeometry> models,
        List<string> problems,
        HashSet<string> warned)
    {
        if (value is null)
        {
            if (warned.Add(type.ObjectId))
            {
                context.Logger.Warning($"Block '{type.ObjectId}' ({type.Source}) has a model shape and no RenderModel: it is not drawn.");
            }

            return -1;
        }

        string id = value.Value?.Text ?? string.Empty;
        if (!resources.TryGetMesh(id, out RenderMesh? mesh))
        {
            problems.Add($"{value.Source}: <{value.Name}> names the mesh '{id}', which no loaded module provides.");
            return -1;
        }

        if (index.TryGetValue((mesh.ObjectId, orientation, fallback), out int existing))
        {
            return existing;
        }

        MeshGeometry geometry = mesh.Geometry;
        var vertices = new List<ModelVertex>();
        var indices = new List<uint>();
        var place = Matrix4x4.CreateTranslation(0.5f, 0, 0.5f) * orientation;
        foreach (Submesh part in mesh.Submeshes)
        {
            uint texture = part.Material is { } material && material.Textures.TryGetValue("Albedo", out RenderTexture? albedo) ? BlockVisuals.Pack(albedo.Location) : fallback;
            if (!BlockVisuals.TryUnpack(texture, out TextureLocation location))
            {
                if (warned.Add(type.ObjectId + "#" + part.Slot))
                {
                    context.Logger.Warning($"Block '{type.ObjectId}' ({type.Source}): the slot '{part.Slot}' of the mesh '{id}' has no material with an Albedo texture, and the block no RenderTexture: the slot is not drawn.");
                }

                continue;
            }

            // The vertices of each slot are its own, with the texture of the slot.
            var placed = new Dictionary<uint, uint>();
            for (int i = part.FirstIndex; i < part.FirstIndex + part.IndexCount; i++)
            {
                uint source = geometry.Indices[i];
                if (!placed.TryGetValue(source, out uint vertex))
                {
                    Vector3 normal = Vector3.TransformNormal(geometry.Normals[(int)source], orientation);
                    vertex = (uint)vertices.Count;
                    placed[source] = vertex;
                    vertices.Add(new ModelVertex(Vector3.Transform(geometry.Positions[(int)source], place), geometry.TextureCoordinates[(int)source], NormalOf(normal), location));
                }

                indices.Add(vertex);
            }
        }

        models.Add(new ModelGeometry([.. vertices], [.. indices]));
        index[(mesh.ObjectId, orientation, fallback)] = models.Count - 1;
        return models.Count - 1;
    }

    // The axis of a normal that points along one, or none.
    private static ChunkNormal NormalOf(Vector3 normal)
    {
        int direction = DirectionOf(normal);
        return MathF.Abs(Vector3.Dot(normal, BlockVisuals.Normal(direction))) > 0.999f ? (ChunkNormal)direction : ChunkNormal.None;
    }
}

/// <summary>The identifiers of the shapes of nVoxel.</summary>
internal static class ShapeIds
{
    public const string Cube = "shape:nobara.cube";
    public const string Model = "shape:nobara.model";
}
