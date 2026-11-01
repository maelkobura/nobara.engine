// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers.Binary;
using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Graphics;
using Nobara.Renderer.Tests.Support;
using Nobara.ResourceCompiler;

namespace Nobara.Renderer.Tests;

/// <summary>The GPU objects that nRenderer makes from the resources, on a fake device.</summary>
public sealed class LoadTests : IDisposable
{
    private readonly RendererHarness _harness = new();
    private readonly string _resources;

    public LoadTests()
    {
        _resources = _harness.CopyResources();
    }

    public void Dispose() => _harness.Dispose();

    private void Add(string resources)
    {
        string path = Path.Combine(_resources, "game.nof");
        File.WriteAllText(path, File.ReadAllText(path).Replace("</Nobara>", resources + "\n</Nobara>", StringComparison.Ordinal));
    }

    private Engine Start() => _harness.WithGame(RendererHarness.Compiled(_resources)).Start();

    [Fact]
    public void LoadsTheShadersMaterialsMeshAndTexturesOfAModule()
    {
        using Engine engine = Start();
        IRenderResources resources = _harness.Resources;

        Assert.True(resources.IsCreated);
        Assert.Equal(["shader:example.render.lit", "shader:example.render.unlit"], resources.Shaders.Select(s => s.ObjectId));
        Assert.Equal(["material:example.render.dirt", "material:example.render.leaves", "material:example.render.stone"], resources.Materials.Select(m => m.ObjectId));
        Assert.Equal(["mesh:example.render.crate"], resources.Meshes.Select(m => m.ObjectId));
        Assert.Equal(4, resources.Textures.Count);
        Assert.Contains(_harness.Log.Entries, e => e.Message.Contains("Created 4 textures in 3 texture arrays, 2 shaders, 3 materials, and 1 meshes", StringComparison.Ordinal));
    }

    [Fact]
    public void PlacesTheTexturesInOneArrayPerSizeAndSettings()
    {
        using Engine engine = Start();
        IRenderResources resources = _harness.Resources;

        Assert.Equal(
            ["array 0: 16x16 Srgb Nearest, 5 mip levels, 2 layers", "array 1: 32x32 Srgb Linear, 6 mip levels, 1 layers", "array 2: 32x32 Linear Linear, 6 mip levels, 1 layers"],
            resources.TextureArrays.Select(a => a.ToString()));
        Assert.Equal(new TextureLocation(0, 0), resources.GetTexture("texture:example.render.dirt").Location);
        Assert.Equal(new TextureLocation(0, 1), resources.GetTexture("texture:example.render.leaves").Location);
        Assert.Equal(new TextureLocation(1, 0), resources.GetTexture("texture:example.render.stone").Location);
        Assert.Equal(new TextureLocation(2, 0), resources.GetTexture("texture:example.render.stonenormal").Location);

        TextureArray small = resources.TextureArrays[0];
        Assert.Equal(TextureDimension.Texture2DArray, small.Texture.Dimension);
        Assert.Equal(PixelFormat.Rgba8UnormSrgb, small.Texture.Format);
        Assert.Equal(2, small.Texture.DepthOrLayers);
        Assert.Equal(PixelFormat.Rgba8Unorm, resources.TextureArrays[2].Texture.Format);
        Assert.Equal(FilterMode.Nearest, small.Sampler.Description.MagFilter);
        Assert.Equal(FilterMode.Linear, resources.TextureArrays[1].Sampler.Description.MagFilter);
    }

    [Fact]
    public void UploadsEachMipLevelOfEachLayer()
    {
        using Engine engine = Start();
        TextureArray small = _harness.Resources.TextureArrays[0];

        for (int layer = 0; layer < 2; layer++)
        {
            for (int level = 0; level < 5; level++)
            {
                int side = 16 >> level;
                Assert.Equal(side * side * 4, _harness.Fake.Texels[(small.Texture, level, layer)].Length);
            }
        }

        // The dirt texture is a checker of two browns: its last level is their average.
        byte[] top = _harness.Fake.Texels[(small.Texture, 0, 0)];
        Assert.Equal([100, 60, 30, 255], top[..4]);
        byte[] last = _harness.Fake.Texels[(small.Texture, 4, 0)];
        Assert.InRange(last[0], 100, 120);
        Assert.Equal(255, last[3]);
    }

    [Fact]
    public void TheLocationOfATextureIsTheSameFromOneStartToTheNext()
    {
        CompilationResult compiled = RendererHarness.Compiled(_resources);
        Dictionary<string, TextureLocation> first;
        using (Engine engine = _harness.WithGame(compiled).Start())
        {
            first = _harness.Resources.Textures.ToDictionary(t => t.ObjectId, t => t.Location);
        }

        using var again = new RendererHarness();
        using Engine second = again.WithGame(RendererHarness.Compiled(_resources)).Start();

        Assert.Equal(first, again.Resources.Textures.ToDictionary(t => t.ObjectId, t => t.Location));
    }

    [Fact]
    public void FindsAResourceByItsResourceIdentifier()
    {
        using Engine engine = Start();
        IRenderResources resources = _harness.Resources;

        Assert.Same(resources.GetTexture("texture:example.render.dirt"), resources.GetTexture("texture://example/render/DIRT"));
        Assert.Same(resources.GetMesh("mesh:example.render.crate"), resources.GetMesh("mesh://Example/Render/Crate"));
        Assert.False(resources.TryGetMaterial("material:example.render.missing", out _));
        Assert.Throws<KeyNotFoundException>(() => resources.GetShader("shader:example.render.missing"));
    }

    [Fact]
    public void BuildsTheProgramsAndTheMaterialLayoutOfAShader()
    {
        using Engine engine = Start();
        RenderShader lit = _harness.Resources.GetShader("shader:example.render.lit");

        Assert.False(lit.IsCompute);
        Assert.Equal(["", "Cutout"], lit.Programs.Select(p => p.Variant));
        Assert.All(lit.Programs, p => Assert.NotNull(p.Vertex));
        Assert.All(lit.Programs, p => Assert.NotNull(p.Fragment));
        Assert.NotSame(lit.Programs[0].Fragment, lit.Programs[1].Fragment);
        Assert.Equal([new ShaderTextureBinding("Albedo", 0, false, TextureFallback.White), new ShaderTextureBinding("Normal", 1, true, TextureFallback.Normal)], lit.Textures);
        Assert.Equal([new ShaderBufferBinding("Camera", 0)], lit.Uniforms);

        // std140: vec4 at 0, float at 16, then two ints; the block is rounded to 16 bytes.
        ShaderParameterBlock block = lit.Parameters!;
        Assert.Equal(1, block.Binding);
        Assert.Equal(32, block.Size);
        Assert.Equal([0, 16, 20, 24], block.Parameters.Select(p => p.Offset));
        Assert.Equal("Albedo", block.Parameters[2].Texture);

        Assert.Equal(
            [new ResourceLayoutEntry(0, ResourceKind.SampledTexture, ShaderStages.Vertex | ShaderStages.Fragment), new ResourceLayoutEntry(1, ResourceKind.SampledTexture, ShaderStages.Vertex | ShaderStages.Fragment), new ResourceLayoutEntry(1, ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment)],
            lit.MaterialLayout!.Entries);
    }

    [Fact]
    public void FillsTheParametersOfAMaterialWithItsValuesTheDefaultsAndTheLayersOfItsTextures()
    {
        using Engine engine = Start();
        RenderMaterial leaves = _harness.Resources.GetMaterial("material:example.render.leaves");
        RenderMaterial stone = _harness.Resources.GetMaterial("material:example.render.stone");

        Assert.Equal(MaterialMode.Cutout, leaves.Mode);
        Assert.Equal("Cutout", leaves.Program.Variant);
        Assert.Equal(MaterialMode.Opaque, stone.Mode);
        Assert.Equal("", stone.Program.Variant);

        ReadOnlySpan<byte> data = leaves.ParameterData.Span;
        Assert.Equal([1f, 1f, 1f, 1f], Floats(data, 0, 4));
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(data[16..]));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(data[20..]));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(data[24..]));

        data = stone.ParameterData.Span;
        Assert.Equal([0.9f, 0.9f, 1f, 1f], Floats(data, 0, 4));
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(data[16..]));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(data[24..]));
        Assert.Same(_harness.Resources.GetTexture("texture:example.render.stonenormal"), stone.Textures["Normal"]);
        Assert.False(leaves.Textures.ContainsKey("Normal"));

        // The parameters are on the GPU, in the uniform buffer the resource set binds.
        byte[] read = new byte[leaves.ParameterData.Length];
        _harness.Device.ReadBuffer(leaves.ParameterBuffer!, 0, read);
        Assert.Equal(leaves.ParameterData.ToArray(), read);
        Assert.Same(leaves.Shader.MaterialLayout, leaves.ResourceSet!.Layout);
    }

    [Fact]
    public void UploadsAMeshWithOnePartPerMaterialSlotAndTheTransformsOfItsNodes()
    {
        using Engine engine = Start();
        RenderMesh crate = _harness.Resources.GetMesh("mesh:example.render.crate");

        Assert.Equal(8, crate.VertexCount);
        Assert.Equal(12, crate.IndexCount);
        Assert.Equal(8L * RenderMesh.VertexSize, crate.VertexBuffer.Size);
        Assert.Equal(12L * 4, crate.IndexBuffer.Size);
        Assert.Equal(
            [("Wood", 0, 6, "material:example.render.dirt"), ("Leaves", 6, 6, "material:example.render.leaves")],
            crate.Submeshes.Select(s => (s.Slot, s.FirstIndex, s.IndexCount, s.Material!.ObjectId)));

        // The node of the mesh is moved up by 1.
        Assert.Equal(new System.Numerics.Vector3(-0.5f, 0.5f, 0), crate.BoundsMin);
        Assert.Equal(new System.Numerics.Vector3(0.5f, 1.5f, 0.25f), crate.BoundsMax);
    }

    [Fact]
    public void AModuleThatDependsOnTheRendererGivesItsOwnResources()
    {
        // The game is any module that depends on nobara.renderer.
        using Engine engine = Start();

        Assert.All(_harness.Resources.Shaders, s => Assert.StartsWith("shader:example.", s.ObjectId, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<Shader>shader:example.render.lit</Shader><Texture name=\"Detail\" texture=\"texture:example.render.dirt\" />", "has no texture 'Detail'; it has Albedo, Normal")]
    [InlineData("<Shader>shader:example.render.lit</Shader>", "needs the texture 'Albedo'")]
    [InlineData("<Shader>shader:example.render.lit</Shader><Variant>Fog</Variant><Texture name=\"Albedo\" texture=\"texture:example.render.dirt\" />", "has no variant 'Fog'; it has Cutout")]
    [InlineData("<Shader>shader:example.render.lit</Shader><Texture name=\"Albedo\" texture=\"texture:example.render.dirt\" /><Parameter name=\"Tint\" value=\"1 1\" />", "a vec4 takes 4 numbers; got 2")]
    [InlineData("<Shader>shader:example.render.lit</Shader><Texture name=\"Albedo\" texture=\"texture:example.render.dirt\" /><Parameter name=\"AlbedoLayer\" value=\"3\" />", "a layer is set by the renderer")]
    [InlineData("<Shader>shader:example.render.lit</Shader><Texture name=\"Albedo\" texture=\"texture:example.render.dirt\" /><Parameter name=\"Gloss\" value=\"1\" />", "has no parameter 'Gloss'; it has Tint, AlphaCutoff")]
    public void AMaterialThatDoesNotFitItsShaderStopsTheStart(string content, string message)
    {
        Add($"<Material><Name>Broken</Name><Content>{content}</Content></Material>");

        Exception exception = Assert.ThrowsAny<Exception>(() => Start());

        RenderResourceException? cause = null;
        for (Exception? inner = exception; inner is not null && cause is null; inner = inner.InnerException)
        {
            cause = inner as RenderResourceException;
        }

        Assert.True(cause is not null, exception.ToString());
        string problem = Assert.Single(cause.Problems, p => p.Contains(message, StringComparison.Ordinal));
        Assert.Contains("material:example.render.broken", problem, StringComparison.Ordinal);
        Assert.Contains("game.nof(", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasesItsGpuObjectsBeforeTheDevice()
    {
        Engine engine = Start();
        FakeDevice device = _harness.Fake;
        Assert.True(device.NativeObjects > 0);

        engine.Dispose();

        Assert.DoesNotContain(_harness.Log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Equal(0, device.LiveObjectCount);
    }

    private static float[] Floats(ReadOnlySpan<byte> data, int offset, int count)
    {
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + (i * 4))..]);
        }

        return values;
    }
}
