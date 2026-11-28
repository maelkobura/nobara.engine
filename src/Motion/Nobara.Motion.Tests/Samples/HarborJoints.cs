// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Physics;
using Nobara.Voxel;

namespace Nobara.Motion.Tests.Samples;

/// <summary>
/// The sample of the guide "Link two grids": the harbor of a fictional sky
/// game, with a windmill whose sails turn on the terrain, a ferry that
/// carries a crane whose boom turns, a gate that slides open and shut, and a
/// rule that puts a character caught by the ferry on its deck.
/// </summary>
public sealed class HarborJoints : ICrushHandler, IDisposable
{
    /// <summary>How fast the sails turn, in radians per second.</summary>
    public const float SailSpeed = 0.5f;

    /// <summary>How fast the boom of the crane turns, in radians per second.</summary>
    public const float BoomSpeed = 0.3f;

    /// <summary>How fast the gate slides, in blocks per second.</summary>
    public const float GateSpeed = 1f;

    /// <summary>Where a character caught by the ferry goes, in the frame of the ferry: the middle of its deck.</summary>
    public static readonly Vector3 Deck = new(3, 1, 1.5f);

    private readonly IMotionSystem _motion;
    private readonly IVoxelWorlds _voxels;
    private readonly ModuleContext _context;
    private readonly CrushHandlerHandle _crushes;
    private Grid? _ferry;
    private GridJointHandle _gate;

    public HarborJoints(ModuleContext context)
    {
        _context = context;
        _motion = context.Resolve<IMotionSystem>();
        _voxels = context.Resolve<IVoxelWorlds>();
        _crushes = _motion.AddCrushHandler(context, this);
    }

    /// <summary>Builds the sails of a windmill, a cross of planks, and makes them turn on the terrain about the hub.</summary>
    public Grid BuildSails(in WorldPosition hub, uint plank)
    {
        Grid sails = Build(hub, plank, [.. Enumerable.Range(-3, 7).SelectMany(i => new[] { new BlockCoordinate(i, 0, 0), new BlockCoordinate(0, i, 0) }).Distinct()]);
        _motion.Add(sails);
        _motion.Link(sails, Terrain(), new GridJointDescription(
            JointKind.Hinge,
            Anchor: new Vector3(0.5f, 0.5f, 0.5f),   // the middle of the hub block, in the frame of the sails
            Axis: Vector3.UnitZ,
            Motor: new JointMotor(TargetVelocity: SailSpeed, MaxForce: 5e6f)));
        return sails;
    }

    /// <summary>Builds a ferry, a kinematic deck that moves on its own, and a crane boom that turns on it.</summary>
    public (Grid Ferry, Grid Boom) BuildFerry(in WorldPosition position, uint plank)
    {
        _ferry = Build(position, plank, Cells(0, 0, 0, 5, 0, 2));
        _motion.Add(_ferry, GridMotionMode.Kinematic);
        Grid boom = Build(position.Offset(new Vector3(1, 1.1f, 1)), plank, Cells(0, 0, 0, 3, 0, 0));
        _motion.Add(boom);
        _motion.Link(boom, _ferry, new GridJointDescription(
            JointKind.Hinge,
            Anchor: new Vector3(0.5f, 0, 0.5f),   // the foot of the boom, just above the deck
            Axis: Vector3.UnitY,
            Motor: new JointMotor(BoomSpeed, 1e6f)));
        return (_ferry, boom);
    }

    /// <summary>Builds a gate that slides along Z on the terrain, shut.</summary>
    public Grid BuildGate(in WorldPosition position, uint plank)
    {
        Grid gate = Build(position, plank, Cells(0, 0, 0, 0, 2, 3));
        _motion.Add(gate);
        _gate = _motion.Link(gate, Terrain(), new GridJointDescription(JointKind.Slider, new Vector3(0.5f, 1.5f, 2), Vector3.UnitZ));
        return gate;
    }

    /// <summary>Slides the gate open.</summary>
    public void OpenGate() => _motion.SetMotor(_gate, new JointMotor(GateSpeed, 1e6f));

    /// <summary>Slides the gate shut.</summary>
    public void CloseGate() => _motion.SetMotor(_gate, new JointMotor(-GateSpeed, 1e6f));

    /// <summary>Puts a character caught by the ferry on its deck; leaves the other crushes to the next handler.</summary>
    public bool TryResolve(in CharacterCrush crush, out CrushResponse response)
    {
        if (crush.Grid != _ferry)
        {
            response = default;
            return false;   // not ours: the next handler, or the default, decides
        }

        response = CrushResponse.MoveTo(_ferry.Transform.TransformPoint(Deck));
        return true;
    }

    /// <summary>Removes the crush handler.</summary>
    public void Dispose() => _crushes.Dispose();

    private Grid Terrain() => _voxels.TryGetTerrain(DimensionId.Default, out Grid? terrain) ? terrain! : throw new InvalidOperationException("No terrain.");

    private Grid Build(in WorldPosition position, uint plank, BlockCoordinate[] cells)
    {
        Grid grid = _voxels.CreateGrid(_context, DimensionId.Default, new GridOptions { Transform = WorldTransform.At(position) });
        foreach (BlockCoordinate cell in cells)
        {
            grid.Storage.GetOrCreateChunk(VoxelCoordinates.ToChunk(cell)).Palette.Set(VoxelCoordinates.ToIndex(VoxelCoordinates.ToLocal(cell)), plank);
        }

        return grid;
    }

    private static BlockCoordinate[] Cells(int x0, int y0, int z0, int x1, int y1, int z1) =>
        [.. Enumerable.Range(y0, y1 - y0 + 1).SelectMany(y => Enumerable.Range(z0, z1 - z0 + 1).SelectMany(z => Enumerable.Range(x0, x1 - x0 + 1).Select(x => new BlockCoordinate(x, y, z))))];
}
