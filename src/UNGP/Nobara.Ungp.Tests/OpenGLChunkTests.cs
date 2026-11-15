// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Graphics;
using Nobara.Renderer;
using Nobara.Ungp.Tests.Support;
using Xunit.Abstractions;

namespace Nobara.Ungp.Tests;

/// <summary>
/// Draws chunk geometry and objects with OpenGL and reads the pixels. The
/// camera stands at the origin and looks along -Z; the faces of the scene face
/// it. A red wall of chunk geometry stands at depth 6; a glass face of chunk
/// geometry in front of it, on the left, partly covered by a nearer glass
/// square object; another glass square object in front of the wall, on the
/// right; and below the wall, a cut-out face whose left half is opaque.
/// </summary>
[Trait("Category", "GPU")]
[Collection(nameof(GpuCollection))]
public sealed class OpenGLChunkTests(ITestOutputHelper output) : IDisposable
{
    private const int Size = 64;

    // The shade of a face toward +Z, as the screen shows it, and in linear values.
    private const float FrontShade = 0.8f;
    private static readonly float s_frontLinear = MathF.Pow(FrontShade, 2.2f);

    private static readonly (byte R, byte G, byte B) SkyColor = (0x40, 0x80, 0xC0);
    private static readonly (byte R, byte G, byte B, byte A) Glass = (100, 100, 255, 128);

    private readonly UngpHarness _harness = new(gpu: true);
    private Engine? _engine;
    private Texture? _color;
    private Texture? _depth;
    private ChunkGroupId _wall;

    public void Dispose()
    {
        _color?.Dispose();
        _depth?.Dispose();
        _engine?.Dispose();
        _harness.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChunksAndObjectsAreDrawnInTheOrderOfThePasses(bool prepass)
    {
        Start();
        _harness.Ungp.DepthPrepass = prepass;
        AddScene();

        byte[] pixels = Draw(frames: 3);

        Vector3 wall = Shaded(UngpHarness.Red);
        AssertColor(Encode(wall), pixels, 32, 42, "the opaque wall of chunk geometry");
        Vector3 chunkGlass = Over(Shaded(Glass), Glass.A / 255f, wall);
        AssertColor(Encode(chunkGlass), pixels, 22, 36, "the glass face of chunk geometry over the wall");
        AssertColor(Encode(Over(Linear(Glass), Glass.A / 255f, chunkGlass)), pixels, 22, 30, "a glass object over the glass face of chunk geometry");
        AssertColor(Encode(Over(Linear(Glass), Glass.A / 255f, wall)), pixels, 43, 32, "the glass object over the wall");
        AssertColor(Encode(Shaded(UngpHarness.Leaf)), pixels, 29, 13, "the opaque half of the cut-out face");
        AssertColor(SkyColor, pixels, 35, 13, "the sky through the transparent half of the cut-out face");
        AssertColor(SkyColor, pixels, 2, 2, "the sky in a corner");
        Assert.Empty(_harness.Errors);
    }

    [Fact]
    public void OcclusionCullingOffKeepsTheImageOfASceneWithoutOcclusion()
    {
        Start();
        AddScene();
        byte[] on = Draw(frames: 4);

        _harness.Ungp.OcclusionCulling = false;
        byte[] off = Draw(frames: 4);

        Assert.Equal(on, off);
        Assert.DoesNotContain(UngpPasses.DepthPyramid, _harness.Render.LastGraph!.Passes);
    }

    [Fact]
    public void OcclusionCullingReducesTheSectionsDrawnBehindAWall()
    {
        Start();
        TextureLocation red = Texture("texture:example.scene.red");
        _harness.Render.AddExtractor(_harness.Context, new TestExtractor
        {
            Next = e =>
            {
                ChunkGroupId group = e.Chunks.CreateGroup(WorldTransform.Identity);

                // A wall that fills the view, then 5 x 5 x 4 boxes behind it.
                e.Chunks.AddSection(group, new SectionOrigin(-1, -1, -1), new ChunkFaces().Box(ChunkCategory.Opaque, red, new Vector3(-16, -16, 26), new Vector3(48, 48, 28)).Geometry);
                for (int x = -2; x <= 2; x++)
                {
                    for (int y = -2; y <= 2; y++)
                    {
                        for (int z = -5; z <= -2; z++)
                        {
                            e.Chunks.AddSection(group, new SectionOrigin(x, y, z), new ChunkFaces().Box(ChunkCategory.Opaque, red, new Vector3(8, 8, 8), new Vector3(24, 24, 24)).Geometry);
                        }
                    }
                }
            },
        });

        _harness.Ungp.OcclusionCulling = false;
        Draw(frames: 6);
        ChunkGeometryStatistics off = _harness.Render.Chunks.Statistics;

        _harness.Ungp.OcclusionCulling = true;
        Draw(frames: 6);
        ChunkGeometryStatistics on = _harness.Render.Chunks.Statistics;

        output.WriteLine($"Occlusion off: {off.Drawn} drawn, {off.FrustumCulled} outside the view; on: {on.Drawn} drawn, {on.OcclusionCulled} hidden.");
        Assert.True(off.Drawn > 20, $"Without occlusion culling, {off.Drawn} sections are drawn.");
        Assert.True(on.Drawn < off.Drawn / 4, $"With occlusion culling, {on.Drawn} sections are drawn; {off.Drawn} without it.");
        Assert.Empty(_harness.Errors);
    }

    [Fact]
    public void TheSkyLightMultiplierOfTheViewScalesTheSkyLightOnly()
    {
        Start();
        AddLitWall(WorldTransform.Identity, null);
        byte[] day = Draw(frames: 3);
        AssertColor(Encode(Lit(UngpHarness.Red, 1)), day, 24, 32, "the face lit by the sky, by day");
        AssertColor(Encode(Lit(UngpHarness.Red, 1)), day, 40, 32, "the face lit by blocks, by day");

        // By night, without a remesh: the sky face takes the multiplier, the face lit by blocks keeps its light.
        _harness.Render.MainView.SkyLight = new ColorF(0.25f, 0.25f, 0.5f, 1);
        byte[] night = Draw(frames: 2);

        Vector3 red = Linear((UngpHarness.Red.R, UngpHarness.Red.G, UngpHarness.Red.B, (byte)255));
        Vector3 sky = red * new Vector3(MathF.Pow(0.25f * FrontShade, 2.2f), MathF.Pow(0.25f * FrontShade, 2.2f), MathF.Pow(0.5f * FrontShade, 2.2f));
        AssertColor(Encode(sky), night, 24, 32, "the face lit by the sky, by night");
        AssertColor(Encode(Lit(UngpHarness.Red, 1)), night, 40, 32, "the face lit by blocks, by night");
        Assert.Empty(_harness.Errors);
    }

    [Fact]
    public void TheLightAroundAGroupScalesItsSkyLightAndGivesTheLightOfBlocksWhereTheSkyReaches()
    {
        Start();
        AddLitWall(WorldTransform.Identity, new ChunkLight(5, 6, 6, 6));
        byte[] pixels = Draw(frames: 3);

        // The sky face is lit by the brightest of 5 from the sky and 6 from the blocks around; the face that the sky does not reach keeps its own light.
        AssertColor(Encode(Lit(UngpHarness.Red, 6f / 15)), pixels, 24, 32, "the face lit by the sky, in a dark place");
        AssertColor(Encode(Lit(UngpHarness.Red, 1)), pixels, 40, 32, "the face lit by blocks, in a dark place");
        Assert.Equal(new ChunkLight(5, 6, 6, 6), _harness.Render.Chunks.GetGroupLight(_wall));
        Assert.Empty(_harness.Errors);
    }

    // A wall in two halves facing the camera: on the left, lit by the sky only; on the right, by blocks only.
    private void AddLitWall(WorldTransform transform, ChunkLight? around)
    {
        TextureLocation red = Texture("texture:example.scene.red");
        var faces = new ChunkFaces()
            .Front(ChunkCategory.Opaque, red, 28, 28, 32, 36, 26, new ChunkLight(15, 0, 0, 0))
            .Front(ChunkCategory.Opaque, red, 32, 28, 36, 36, 26, new ChunkLight(0, 15, 15, 15));
        _harness.Render.AddExtractor(_harness.Context, new TestExtractor
        {
            Next = e =>
            {
                _wall = e.Chunks.CreateGroup(transform);
                if (around is { } light)
                {
                    e.Chunks.SetGroupLight(_wall, light);
                }

                e.Chunks.AddSection(_wall, new SectionOrigin(-1, -1, -1), faces.Geometry);
            },
        });
    }

    // A color of a face toward +Z at a light from 0 to 1, as the chunk shader shades it.
    private static Vector3 Lit((byte R, byte G, byte B) color, float light) =>
        new Vector3(Linear(color.R), Linear(color.G), Linear(color.B)) * MathF.Pow(light * FrontShade, 2.2f);

    private void Start()
    {
        _harness.Settings[UngpSettings.SkyColor] = "#4080C0";
        _engine = _harness.Start();
        GraphicsDevice device = _harness.Device;
        _color = device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Rgba8Unorm, Size, Size, TextureUsage.RenderTarget | TextureUsage.Sampled));
        _depth = device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Depth32Float, Size, Size, TextureUsage.DepthStencil));
        _harness.Render.MainView.SetTarget(_color, _depth);
    }

    // The section at (-1, -1, -1) holds the blocks from -32 to 0 on each axis: a point of the world is 32 more in the section.
    private void AddScene()
    {
        TextureLocation red = Texture("texture:example.scene.red");
        TextureLocation glass = Texture("texture:example.scene.glass");
        TextureLocation leaf = Texture("texture:example.scene.leaf");
        var faces = new ChunkFaces()
            .Front(ChunkCategory.Opaque, red, 30, 30, 34, 34, 26)
            .Front(ChunkCategory.Transparent, glass, 30.5f, 31.5f, 31.5f, 32.5f, 27.5f)
            .Front(ChunkCategory.Cutout, leaf, 31.5f, 30, 32.5f, 31, 28);
        _harness.Render.AddExtractor(_harness.Context, new TestExtractor
        {
            Next = e =>
            {
                ChunkGroupId group = e.Chunks.CreateGroup(WorldTransform.Identity);
                e.Chunks.AddSection(group, new SectionOrigin(-1, -1, -1), faces.Geometry);
                e.World.Create(_harness.Square("material:example.scene.glass", 1, 0, -3));
                e.World.Create(_harness.Square("material:example.scene.glass", -0.67, -0.2, -3, scale: 0.5f));
            },
        });
    }

    private TextureLocation Texture(string id) => _harness.Resources.GetTexture(id).Location;

    private byte[] Draw(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            _harness.Clock.Advance(TimeSpan.FromSeconds(1.0 / 60));
            _engine!.MainContext.RunFrame();
        }

        byte[] pixels = new byte[Size * Size * 4];
        _harness.Device.ReadTexture(_color!, TextureRegion.Whole(_color!), pixels);
        return pixels;
    }

    private static float Linear(byte value)
    {
        float c = value / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private static Vector3 Linear((byte R, byte G, byte B, byte A) color) => new(Linear(color.R), Linear(color.G), Linear(color.B));

    private static Vector3 Shaded((byte R, byte G, byte B) color) => new Vector3(Linear(color.R), Linear(color.G), Linear(color.B)) * s_frontLinear;

    private static Vector3 Shaded((byte R, byte G, byte B, byte A) color) => Linear(color) * s_frontLinear;

    private static Vector3 Over(Vector3 source, float alpha, Vector3 destination) => (source * alpha) + (destination * (1 - alpha));

    // Every value of the scene is under the start of the highlight compression, so tonemapping keeps it.
    private static (byte R, byte G, byte B) Encode(Vector3 linear)
    {
        static byte Channel(float c) => (byte)MathF.Round(255 * (c <= 0.0031308f ? c * 12.92f : (1.055f * MathF.Pow(c, 1 / 2.4f)) - 0.055f));
        return (Channel(linear.X), Channel(linear.Y), Channel(linear.Z));
    }

    // Rows are counted from the bottom of the target.
    private static void AssertColor((byte R, byte G, byte B) expected, byte[] pixels, int x, int y, string what)
    {
        int i = ((y * Size) + x) * 4;
        (byte R, byte G, byte B) actual = (pixels[i], pixels[i + 1], pixels[i + 2]);
        Assert.True(
            Math.Abs(actual.R - expected.R) <= 3 && Math.Abs(actual.G - expected.G) <= 3 && Math.Abs(actual.B - expected.B) <= 3,
            $"Pixel ({x}, {y}), {what}, is {actual}; expected {expected}.");
    }
}
