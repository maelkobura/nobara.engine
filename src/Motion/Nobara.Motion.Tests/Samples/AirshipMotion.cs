// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Motion.Tests.Samples;

/// <summary>
/// The sample of the guide "Move a grid": the airships of a fictional sky
/// game, grids that float and that their engines push, and its lifts,
/// platforms that rise along a path.
/// </summary>
public sealed class AirshipMotion : ITickSystem
{
    /// <summary>The thrust of the engine of an airship, in newtons.</summary>
    public const float Thrust = 40_000f;

    /// <summary>How fast a lift rises, in blocks per second.</summary>
    public const float LiftSpeed = 2f;

    private readonly IMotionSystem _motion;
    private readonly IVoxelWorlds _voxels;
    private readonly ModuleContext _context;
    private readonly List<Grid> _airships = [];
    private Grid? _lift;
    private WorldPosition _liftStart;
    private float _liftHeight;

    public AirshipMotion(ModuleContext context)
    {
        _context = context;
        _motion = context.Resolve<IMotionSystem>();
        _voxels = context.Resolve<IVoxelWorlds>();
        IEntityWorlds entities = context.Resolve<IEntityWorlds>();
        context.Resolve<ITickSchedule>().AddSystem(context, "example.game.airships", TickStages.Physics, this, new TickSystemOptions
        {
            Before = [MotionModule.TickSystem],
            Reads = [entities.GetComponent(entities.WorldTransform).Data],   // the transforms of the grids, for the points in their frame
        });
    }

    /// <summary>Builds an airship of planks and makes it a body that floats.</summary>
    public Grid Launch(in WorldPosition position, uint plank)
    {
        Grid airship = _voxels.CreateGrid(_context, DimensionId.Default, new GridOptions { Transform = WorldTransform.At(position) });
        for (int x = 0; x < 6; x++)
        {
            for (int z = 0; z < 3; z++)
            {
                var cell = new BlockCoordinate(x, 0, z);
                airship.Storage.GetOrCreateChunk(VoxelCoordinates.ToChunk(cell)).Palette.Set(VoxelCoordinates.ToIndex(cell), plank);
            }
        }

        _motion.Add(airship);                    // dynamic: its mass comes from its planks
        _motion.SetGravityScale(airship, 0);     // its balloon holds it up
        _airships.Add(airship);
        return airship;
    }

    /// <summary>Builds a lift and makes it a kinematic body that rises.</summary>
    public Grid BuildLift(in WorldPosition position, uint plank)
    {
        _lift = _voxels.CreateGrid(_context, DimensionId.Default, new GridOptions { Transform = WorldTransform.At(position) });
        var cell = new BlockCoordinate(0, 0, 0);
        _lift.Storage.GetOrCreateChunk(VoxelCoordinates.ToChunk(cell)).Palette.Set(VoxelCoordinates.ToIndex(cell), plank);
        _motion.Add(_lift, GridMotionMode.Kinematic);
        _liftStart = position;
        return _lift;
    }

    /// <summary>Pushes each airship from its stern, along its own axis, and moves the lift one step up.</summary>
    public void Run(in TickTime time)
    {
        foreach (Grid airship in _airships)
        {
            Vector3 forward = Vector3.Transform(Vector3.UnitX, airship.Transform.Rotation);
            _motion.ApplyForceAt(airship, forward * Thrust, new Vector3(0, 0.5f, 0));   // the engine, at the stern on one side, in the frame of the grid: it also turns the airship
        }

        if (_lift is not null)
        {
            _liftHeight += LiftSpeed * (float)time.Delta.TotalSeconds;
            _motion.SetTarget(_lift, _liftStart.Offset(new Vector3(0, _liftHeight, 0)), Quaternion.Identity);
        }
    }
}
