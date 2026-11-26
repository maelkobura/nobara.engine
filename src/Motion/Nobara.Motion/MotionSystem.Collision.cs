// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using Nobara.Core;
using Nobara.Physics;
using Nobara.Voxel;

namespace Nobara.Motion;

/// <summary>
/// The collision of the grids with the terrain and with other grids: the
/// surface cells of each grid, kept from the notifications of nVoxel; the
/// mode in which they are tested, blocks or corners, switched by the mass of
/// the grid and by the load of the tick, each with a hysteresis; the budget
/// of tests of a pair; and the impacts of the grids.
/// </summary>
internal sealed partial class MotionSystem : IPhysicsStepObserver
{
    /// <summary>The share of the corner mass under which a heavy grid tests its blocks again.</summary>
    public const float MassHysteresis = 0.8f;

    /// <summary>The ticks in a row over the load share after which every grid tests corners.</summary>
    public const int OverloadTicks = 3;

    /// <summary>The ticks in a row under <see cref="RecoverShare"/> of the load share after which the grids test their blocks again.</summary>
    public const int RecoverTicks = 30;

    /// <summary>The share of the load share under which the load counts as recovered.</summary>
    public const float RecoverShare = 0.7f;

    private SurfaceStates? _surfaces;
    private PhysicsStepSubscription? _stepEnd;
    private long _tickStart;
    private double _tickSeconds = 1.0 / 30;
    private int _overTicks;
    private int _underTicks;

    /// <summary>Gets the mass from which a grid tests corners, in kilograms; 0 for never.</summary>
    public float CornerMass { get; private set; }

    /// <summary>Gets the share of the tick that Motion and the step may take before every grid tests corners; 0 for never.</summary>
    public float LoadShare { get; private set; }

    /// <summary>Gets the budget of tests of a pair in a step; 0 for no limit.</summary>
    public int PairBudget { get; private set; }

    /// <summary>Gets the slowest approach that a grid reports as an impact, in blocks per second; 0 for none.</summary>
    public float ImpactSpeed { get; private set; }

    /// <summary>Gets whether the tick is over its load, so that every grid tests corners.</summary>
    public bool Overloaded { get; private set; }

    /// <summary>Gets the share of the last tick that Motion and the step took.</summary>
    public double Load { get; private set; }

    /// <summary>Gets the switches of a grid from one mode to the other since Motion started.</summary>
    public long Switches { get; private set; }

    /// <summary>Gets what the collision of a grid with other voxels did at the last step.</summary>
    /// <param name="grid">A grid that Motion moves.</param>
    /// <returns>The mode of its surface cells, the work of its pairs, and its surface cells.</returns>
    internal (VoxelContactMode Mode, VoxelContactCounts Counts, int Surface) CollisionOf(Grid grid)
    {
        GridRecord record = Require(grid);
        return (record.Collision!.Mode, record.Counts, record.Collision.SurfaceCount);
    }

    /// <summary>Gets the surface of a grid, for the tests.</summary>
    internal GridVoxelCollision SurfaceOf(Grid grid) => Require(grid).Collision!;

    /// <summary>Notes the end of the step: the time that Motion and the step took, and the load it makes.</summary>
    [HotPath]
    public void OnStepEnd(in PhysicsStepEnd step)
    {
        if (_tickStart == 0)
        {
            return;
        }

        Load = Stopwatch.GetElapsedTime(_tickStart).TotalSeconds / _tickSeconds;
        _tickStart = 0;
        if (LoadShare <= 0)
        {
            return;
        }

        if (!Overloaded)
        {
            _overTicks = Load > LoadShare ? _overTicks + 1 : 0;
            if (_overTicks >= OverloadTicks)
            {
                Overloaded = true;
                _underTicks = 0;
            }
        }
        else
        {
            _underTicks = Load < LoadShare * RecoverShare ? _underTicks + 1 : 0;
            if (_underTicks >= RecoverTicks)
            {
                Overloaded = false;
                _overTicks = 0;
            }
        }
    }

    /// <summary>Sets the load of the tick, as a test would measure it.</summary>
    internal void SimulateLoad(double load)
    {
        _tickStart = Stopwatch.GetTimestamp() - (long)(load * _tickSeconds * Stopwatch.Frequency);
        OnStepEnd(default);
    }

    /// <summary>Reads the settings of the collision and the collision of every state, and follows the end of the steps.</summary>
    private void StartCollision()
    {
        CornerMass = (float)_context.Settings.GetFloat(MotionSettings.CornerMass);
        LoadShare = (float)_context.Settings.GetFloat(MotionSettings.LoadShare);
        PairBudget = _context.Settings.GetInt(MotionSettings.PairBudget);
        ImpactSpeed = (float)_context.Settings.GetFloat(MotionSettings.ImpactSpeed);
        _surfaces = SurfaceStates.Build(_blocks!, _shapes!);
        _stepEnd = _physics!.SubscribeStepEnd(_context, this);
    }

    /// <summary>Gives a grid that becomes a body its collision with other voxels.</summary>
    private void AddCollision(GridRecord record)
    {
        var collision = new GridVoxelCollision { PairBudget = PairBudget };
        record.Collision = collision;
        RefreshSurface(record);
        record.Heavy = CornerMass > 0 && record.AppliedMass >= CornerMass;
        collision.Mode = ModeOf(record);
        _physics!.SetVoxelCollision(record.Grid, collision);
    }

    /// <summary>Takes the collision away from a grid that stops being a body.</summary>
    private void RemoveCollision(GridRecord record)
    {
        if (!record.Grid.IsDestroyed)
        {
            _physics!.SetVoxelCollision(record.Grid, null);
        }
    }

    /// <summary>Marks every surface cell of a grid in memory again; the first surface cell of a chunk may allocate its words.</summary>
    [HotPath]
    private void RefreshSurface(GridRecord record)
    {
        GridVoxelCollision collision = record.Collision!;
        collision.ClearSurface();
        _chunks.Clear();
        record.Grid.Storage.GetResidentChunks(_chunks);
        foreach (Chunk chunk in _chunks)
        {
            Palette palette = chunk.Palette;
            if (palette.IsUniform && palette.GetEntry(0) == 0)
            {
                continue;
            }

            for (int index = 0; index < Chunk.Volume; index++)
            {
                if (!_surfaces!.CollidesAt(palette.Get(index)))
                {
                    continue;
                }

                BlockCoordinate cell = CellOf(chunk.Coordinate, index);
                if (_surfaces.IsSurface(record.Grid, cell, chunk))
                {
                    collision.SetSurface(cell, true);
                }
            }
        }

        _chunks.Clear();
    }

    /// <summary>Marks a changed cell and its six neighbors as surface cells or not.</summary>
    [HotPath]
    private void UpdateSurface(GridRecord record, BlockCoordinate cell)
    {
        UpdateCell(record, cell);
        UpdateCell(record, new BlockCoordinate(cell.X + 1, cell.Y, cell.Z));
        UpdateCell(record, new BlockCoordinate(cell.X - 1, cell.Y, cell.Z));
        UpdateCell(record, new BlockCoordinate(cell.X, cell.Y + 1, cell.Z));
        UpdateCell(record, new BlockCoordinate(cell.X, cell.Y - 1, cell.Z));
        UpdateCell(record, new BlockCoordinate(cell.X, cell.Y, cell.Z + 1));
        UpdateCell(record, new BlockCoordinate(cell.X, cell.Y, cell.Z - 1));
    }

    /// <summary>Marks one cell as a surface cell or not.</summary>
    [HotPath]
    private void UpdateCell(GridRecord record, BlockCoordinate cell)
    {
        record.Grid.Storage.TryGetChunk(VoxelCoordinates.ToChunk(cell), out Chunk? chunk);
        record.Collision!.SetSurface(cell, _surfaces!.IsSurface(record.Grid, cell, chunk));
    }

    /// <summary>Notes the start of the tick, and sets the mode of every grid: corners for a heavy grid, or for all while the tick is over its load.</summary>
    [HotPath]
    private void StartTick(in Nobara.Game.TickTime time)
    {
        _tickStart = Stopwatch.GetTimestamp();
        _tickSeconds = Math.Max(time.Delta.TotalSeconds, 1e-6);
    }

    /// <summary>Sets the mode of a grid after its mass and the load, and reads the work of its pairs at the last step.</summary>
    [HotPath]
    private void UpdateCollision(GridRecord record)
    {
        GridVoxelCollision collision = record.Collision!;
        record.Counts = collision.TakeCounts();
        if (CornerMass > 0)
        {
            record.Heavy = record.Heavy ? record.AppliedMass >= CornerMass * MassHysteresis : record.AppliedMass >= CornerMass;
        }

        VoxelContactMode mode = ModeOf(record);
        if (mode != collision.Mode)
        {
            collision.Mode = mode;
            Switches++;
        }
    }

    [HotPath]
    private VoxelContactMode ModeOf(GridRecord record) => record.Heavy || Overloaded ? VoxelContactMode.Corners : VoxelContactMode.Blocks;
}
