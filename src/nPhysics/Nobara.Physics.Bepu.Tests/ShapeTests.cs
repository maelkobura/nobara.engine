// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Physics.Bepu.Tests.Support;

namespace Nobara.Physics.Bepu.Tests;

/// <summary>The shapes of the neutral API in BEPUphysics: their inertia, their collisions, and their life in a world.</summary>
public sealed class ShapeTests
{
    [Fact]
    public void ComputesTheInertiaOfABoxAndASphere()
    {
        using var scene = new BepuScene();
        BepuBackend backend = scene.Harness.Backend;

        BodyInertia box = backend.ComputeInertia(scene.Physics.GetShape(scene.Physics.CreateBox(new Vector3(1, 2, 3))), 12);
        BodyInertia sphere = backend.ComputeInertia(scene.Physics.GetShape(scene.Physics.CreateSphere(1)), 5);

        // m (b² + c²) / 12 for a box; 2/5 m r² for a sphere.
        Assert.Equal(13f, box.XX, 1e-3f);
        Assert.Equal(10f, box.YY, 1e-3f);
        Assert.Equal(5f, box.ZZ, 1e-3f);
        Assert.Equal(0f, box.YX, 1e-4f);
        Assert.Equal(2f, sphere.XX, 1e-3f);
        Assert.Equal(2f, sphere.ZZ, 1e-3f);
    }

    [Fact]
    public void ComputesTheInertiaOfAHullAboutTheOriginOfItsPoints()
    {
        using var scene = new BepuScene();
        BepuBackend backend = scene.Harness.Backend;
        Vector3[] centered = Cube(Vector3.Zero);
        Vector3[] moved = Cube(new Vector3(0, 2, 0));

        BodyInertia atCenter = backend.ComputeInertia(scene.Physics.GetShape(scene.Physics.CreateConvexHull(centered)), 6);
        BodyInertia atOffset = backend.ComputeInertia(scene.Physics.GetShape(scene.Physics.CreateConvexHull(moved)), 6);

        Assert.Equal(1f, atCenter.XX, 1e-3f);                // a cube of side 1: m / 6
        Assert.Equal(1f + (6 * 4), atOffset.XX, 1e-2f);     // plus m d² about X
        Assert.Equal(1f, atOffset.YY, 1e-3f);                // unchanged about Y
    }

    [Fact]
    public void ComputesTheInertiaOfACompoundFromItsChildren()
    {
        using var scene = new BepuScene();
        PhysicsShape cube = scene.Physics.CreateBox(Vector3.One);
        PhysicsShape pair = scene.Physics.CreateCompound(
        [
            new CompoundChild(cube, new Vector3(-1, 0, 0), Quaternion.Identity),
            new CompoundChild(cube, new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f)),
        ]);

        BodyInertia inertia = scene.Harness.Backend.ComputeInertia(scene.Physics.GetShape(pair), 2);

        // Two cubes of 1 kilogram, one block from the origin along X.
        Assert.Equal(2f / 6, inertia.XX, 1e-3f);
        Assert.Equal((2f / 6) + 2, inertia.YY, 1e-3f);
        Assert.Equal((2f / 6) + 2, inertia.ZZ, 1e-3f);
        Assert.Equal(0f, inertia.ZX, 1e-4f);
    }

    [Fact]
    public void EveryShapeFallsOnTheVoxelsAndRests()
    {
        using var scene = new BepuScene();
        IPhysicsSystem physics = scene.Physics;
        PhysicsShape cube = physics.CreateBox(Vector3.One);
        (PhysicsShape Shape, float Bottom)[] shapes =
        [
            (physics.CreateSphere(0.5f), 0.5f),
            (physics.CreateCapsule(0.4f, 1), 0.9f),
            (physics.CreateBox(new Vector3(1, 0.6f, 1)), 0.3f),
            (physics.CreateCylinder(0.5f, 0.8f), 0.4f),
            (physics.CreateConvexHull(Cube(new Vector3(0, 0.5f, 0))), 0f),
            (physics.CreateCompound([new CompoundChild(cube, new Vector3(-0.5f, 0, 0), Quaternion.Identity), new CompoundChild(cube, new Vector3(0.5f, 0, 0), Quaternion.Identity)]), 0.5f),
        ];
        var bodies = new BodyHandle[shapes.Length];
        for (int i = 0; i < shapes.Length; i++)
        {
            bodies[i] = scene.Add(WorldPosition.FromBlocks(3 + (4 * i), 4, 16), shapes[i].Shape);
        }

        scene.Ground();
        scene.Tick(150);

        for (int i = 0; i < shapes.Length; i++)
        {
            Assert.Equal(1 + shapes[i].Bottom, scene.Local(bodies[i]).Y, 0.03f);
        }
    }

    [Fact]
    public void AStaticMeshHoldsABodyOnTheSideWhereItsTrianglesTurnCounterclockwise()
    {
        using var scene = new BepuScene();
        Vector3[] vertices = [new(-4, 0, -4), new(4, 0, -4), new(4, 0, 4), new(-4, 0, 4)];
        int[] up = [0, 2, 1, 0, 3, 2];   // counterclockwise seen from above
        scene.Add(WorldPosition.FromBlocks(8, 4, 16), scene.Physics.CreateMesh(vertices, up), BodyKind.Static);
        BodyHandle held = scene.Add(WorldPosition.FromBlocks(8, 6, 16), scene.Physics.CreateBox(Vector3.One));

        scene.Tick(60);

        Assert.Equal(4.5f, scene.Local(held).Y, 0.03f);
    }

    [Fact]
    public void AWorldFreesTheShapesNoBodyUsesAnyMore()
    {
        using var scene = new BepuScene();
        PhysicsShape cube = scene.Physics.CreateBox(Vector3.One);
        PhysicsShape pair = scene.Physics.CreateCompound([new CompoundChild(cube, Vector3.Zero, Quaternion.Identity), new CompoundChild(cube, Vector3.UnitX, Quaternion.Identity)]);
        PhysicsShape hull = scene.Physics.CreateConvexHull(Cube(new Vector3(0, 1, 0)));
        BodyHandle anchor = scene.Add(WorldPosition.FromBlocks(1, 1, 1), scene.Physics.CreateSphere(1), BodyKind.Static);
        BodyHandle a = scene.Add(WorldPosition.FromBlocks(4, 4, 4), pair);
        BodyHandle b = scene.Add(WorldPosition.FromBlocks(8, 4, 4), cube);
        BodyHandle c = scene.Add(WorldPosition.FromBlocks(12, 4, 4), hull);
        BepuWorld world = scene.World;
        Assert.Equal(4, world.ShapeCount);   // sphere, compound, cube, hull

        scene.Physics.RemoveBody(a);
        Assert.Equal(3, world.ShapeCount);   // the compound goes; the cube is still used by a body
        scene.Physics.RemoveBody(b);
        scene.Physics.RemoveBody(c);

        Assert.Equal(1, world.ShapeCount);
        Assert.True(scene.Physics.IsAlive(anchor));
    }

    private static Vector3[] Cube(Vector3 center)
    {
        var points = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            points[i] = center + new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f);
        }

        return points;
    }
}
