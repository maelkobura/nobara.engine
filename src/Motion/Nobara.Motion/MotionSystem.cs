// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Blocks;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Core.Resources;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Physics;
using Nobara.Voxel;

namespace Nobara.Motion;

/// <summary>
/// The grids that Motion moves: each is a body of nPhysics whose mass, center
/// of mass, and inertia follow its blocks. The notifications of nVoxel adjust
/// the sums of each grid as blocks change; before each step, the tick system
/// gives nPhysics the new mass and shape of the grids that changed, and the
/// forces of their gravity scale.
/// </summary>
/// <remarks>
/// <para>
/// The body is centered on the center of mass; the transform of the grid is
/// the origin of its blocks. When the center of mass moves, Motion gives the
/// body a shape centered on the new one with <c>SetShape</c>, which keeps the
/// transform of the grid where it is, and the velocity that the new center had.
/// </para>
/// <para>
/// A grid of <see cref="SmallGridBlocks"/> blocks or fewer is a compound of the
/// collision boxes of its blocks, built again at each change; it becomes the
/// voxel shape of its grid again past twice that number.
/// </para>
/// </remarks>
internal sealed partial class MotionSystem : IMotionSystem, ITickSystem, IDisposable
{
    /// <summary>The name of the tick system, in the stage <c>Physics</c>, before the step of nPhysics.</summary>
    public const string TickName = "nobara.motion.grids";

    /// <summary>The mass of a grid whose blocks weigh nothing, in kilograms.</summary>
    public const float MinimumMass = 1f;

    /// <summary>How far the center of mass moves before the body gets a shape centered on the new one, in blocks.</summary>
    private const float CenterTolerance = 1e-5f;

    private const int ChunkSize = VoxelCoordinates.ChunkSize;

    private readonly ModuleContext _context;
    private readonly FreeProperty _weight;
    private readonly Dictionary<Grid, GridRecord> _records = [];
    private readonly List<GridRecord> _list = [];
    private readonly List<Chunk> _chunks = [];
    private readonly Dictionary<Vector3, PhysicsShape> _boxes = [];
    private readonly List<CompoundChild> _children = [];
    private List<PhysicsShape> _retired = [];
    private List<PhysicsShape> _retiring = [];
    private IPhysicsSystem? _physics;
    private IVoxelWorlds? _voxels;
    private IBlockRegistry? _blocks;
    private IShapeRegistry? _shapes;
    private IMultiBlockRegistry? _multiBlocks;
    private BlockMasses? _masses;
    private TickSystemHandle? _tick;
    private VoxelNotificationHandle? _notifications;
    private ChunkResidencySubscription? _residency;
    private SessionSubscription? _sessionEnded;

    public MotionSystem(ModuleContext context)
    {
        _context = context;
        _weight = context.Resources.DeclareProperty("Weight", PropertyType.Float);
    }

    public IReadOnlyCollection<Grid> Grids => _records.Keys;

    public int SmallGridBlocks { get; private set; } = 8;

    /// <summary>Resolves the services, registers the tick system, and follows the blocks and the chunks of the grids.</summary>
    public void Initialize(IPhysicsSystem physics, IVoxelWorlds voxels, IVoxelBatches batches, IChunkLoading loading, IBlockRegistry blocks, IShapeRegistry shapes, IMultiBlockRegistry multiBlocks, IEntityWorlds entities, ITickSchedule schedule, IWorldSessions sessions)
    {
        _physics = physics;
        _voxels = voxels;
        _blocks = blocks;
        _shapes = shapes;
        _multiBlocks = multiBlocks;
        _tick = schedule.AddSystem(_context, TickName, TickStages.Physics, this, new TickSystemOptions
        {
            Before = [PhysicsModule.StepSystem],
            Reads = [voxels.ChunkAccess, entities.GetComponent(entities.WorldTransform).Data, VelocityData(entities)],
        });
        _notifications = batches.Subscribe(_context, OnBlocksChanged);
        _residency = loading.SubscribeResidency(_context, OnResidency);
        _sessionEnded = sessions.SubscribeEnded(_context, _ => Reset());
    }

    /// <summary>Gets the storage of the velocities of the bodies, which the state of a grid reads.</summary>
    private static Nobara.Core.Jobs.JobData VelocityData(IEntityWorlds entities) =>
        entities.TryGetComponent(out ComponentType<BodyVelocity> velocity) ? entities.GetComponent(velocity).Data : throw new InvalidOperationException("nPhysics registers the velocities of the bodies before Motion initializes.");

    /// <summary>Reads the threshold of the small grids and the mass of every block state.</summary>
    public void Start()
    {
        SmallGridBlocks = _context.Settings.GetInt(MotionSettings.SmallGridBlocks);
        _masses = BlockMasses.Build(_blocks!, _shapes!, _multiBlocks!, _blocks!.ResolveProperty(_context, _weight), message => _context.Logger.Warning(message));
    }

    public BodyHandle Add(Grid grid, GridMotionMode mode = GridMotionMode.Dynamic)
    {
        ArgumentNullException.ThrowIfNull(grid);
        RequireStarted();
        if (grid.IsDestroyed)
        {
            throw new ArgumentException($"The grid {grid.Identifier} is destroyed.", nameof(grid));
        }

        if (grid.IsTerrain)
        {
            throw new ArgumentException("The terrain never moves.", nameof(grid));
        }

        if (grid.Transform.Scale != Vector3.One)
        {
            throw new ArgumentException($"The grid {grid.Identifier} is scaled: Motion moves grids at scale 1.", nameof(grid));
        }

        if (_records.ContainsKey(grid) || _physics!.TryGetBody(grid.Dimension, grid.Entity, out _))
        {
            throw new ArgumentException($"The grid {grid.Identifier} is a body already.", nameof(grid));
        }

        var record = new GridRecord(grid) { Mode = mode };
        Count(record);
        record.Mass.Compute(MinimumMass, out float mass, out Vector3 center, out BodyInertia inertia);
        record.Small = IsSmallAtStart(record.Mass.Blocks);
        PhysicsShape shape = BuildShape(record, center);
        record.Body = _physics.AddBody(grid.Dimension, grid.Entity, new BodyDescription
        {
            Kind = KindOf(mode),
            Shape = shape,
            Center = center,
            Mass = mass,
            Inertia = inertia,
        });
        record.Shape = shape;
        record.Center = center;
        record.AppliedMass = mass;
        record.AppliedInertia = inertia;
        _records.Add(grid, record);
        _list.Add(record);
        return record.Body;
    }

    public void Remove(Grid grid)
    {
        GridRecord record = Require(grid);
        if (_physics!.IsAlive(record.Body))
        {
            _physics.RemoveBody(record.Body);
        }

        Forget(record);
    }

    public bool Contains(Grid grid) => grid is not null && _records.ContainsKey(grid);

    public BodyHandle GetBody(Grid grid) => Require(grid).Body;

    public GridMotionState GetState(Grid grid)
    {
        GridRecord record = Require(grid);
        BodyState state = _physics!.GetState(record.Body);
        return new GridMotionState(record.Mode, state.Transform, state.LinearVelocity, state.AngularVelocity, record.AppliedMass, record.Center, record.Mass.Blocks, state.IsAsleep, record.Frozen, record.Small);
    }

    public void SetMode(Grid grid, GridMotionMode mode)
    {
        GridRecord record = Require(grid);
        if (record.Mode == mode)
        {
            return;
        }

        record.Mode = mode;
        if (!record.Frozen)
        {
            _physics!.SetKind(record.Body, KindOf(mode));
        }
    }

    [HotPath]
    public void ApplyForce(Grid grid, Vector3 force)
    {
        if (Moving(grid) is { } record)
        {
            _physics!.ApplyForce(record.Body, force);
        }
    }

    [HotPath]
    public void ApplyForceAt(Grid grid, Vector3 force, Vector3 point)
    {
        if (Moving(grid) is { } record)
        {
            _physics!.ApplyForceAt(record.Body, force, grid.Transform.TransformPoint(point));
        }
    }

    [HotPath]
    public void ApplyImpulse(Grid grid, Vector3 impulse)
    {
        if (Moving(grid) is { } record)
        {
            _physics!.ApplyImpulse(record.Body, impulse);
        }
    }

    [HotPath]
    public void ApplyImpulseAt(Grid grid, Vector3 impulse, Vector3 point)
    {
        if (Moving(grid) is { } record)
        {
            _physics!.ApplyImpulseAt(record.Body, impulse, grid.Transform.TransformPoint(point));
        }
    }

    [HotPath]
    public void ApplyTorque(Grid grid, Vector3 torque)
    {
        if (Moving(grid) is { } record)
        {
            _physics!.ApplyTorque(record.Body, torque);
        }
    }

    [HotPath]
    public void ApplyAngularImpulse(Grid grid, Vector3 impulse)
    {
        if (Moving(grid) is { } record)
        {
            _physics!.ApplyAngularImpulse(record.Body, impulse);
        }
    }

    [HotPath]
    public void SetGravityScale(Grid grid, float scale)
    {
        GridRecord record = Require(grid);
        if (!float.IsFinite(scale))
        {
            throw new ArgumentException($"The gravity scale {scale} is not finite.", nameof(scale));
        }

        record.GravityScale = scale;
    }

    [HotPath]
    public void SetVelocity(Grid grid, Vector3 linear, Vector3 angular)
    {
        GridRecord record = Require(grid);
        if (!record.Frozen)
        {
            _physics!.SetVelocity(record.Body, linear, angular);
        }
    }

    [HotPath]
    public void SetTarget(Grid grid, in WorldPosition position, Quaternion rotation)
    {
        GridRecord record = Require(grid);
        if (record.Mode != GridMotionMode.Kinematic)
        {
            throw new InvalidOperationException($"The grid {grid.Identifier} is dynamic: only a kinematic grid follows a target.");
        }

        if (!record.Frozen)
        {
            _physics!.SetKinematicTarget(record.Body, position, rotation);
        }
    }

    public void Freeze(Grid grid)
    {
        GridRecord record = Require(grid);
        if (record.Frozen)
        {
            return;
        }

        record.Frozen = true;
        _physics!.SetKind(record.Body, BodyKind.Kinematic);
        _physics.SetVelocity(record.Body, Vector3.Zero, Vector3.Zero);
    }

    public void Release(Grid grid)
    {
        GridRecord record = Require(grid);
        if (!record.Frozen)
        {
            return;
        }

        record.Frozen = false;
        _physics!.SetKind(record.Body, KindOf(record.Mode));
        _physics.SetVelocity(record.Body, Vector3.Zero, Vector3.Zero);
    }

    public Grid Split(ModuleContext owner, Grid source, IReadOnlyCollection<BlockCoordinate> selection)
    {
        GridRecord record = Require(source);
        BodyState state = _physics!.GetState(record.Body);
        WorldPosition center = source.Transform.TransformPoint(record.Center);
        Grid split = _voxels!.Split(owner, source, selection);
        Add(split, record.Mode);
        GridRecord added = _records[split];
        added.Inherit = !record.Frozen;
        added.InheritLinear = state.LinearVelocity;
        added.InheritAngular = state.AngularVelocity;
        added.InheritCenter = center;
        return split;
    }

    /// <summary>Gives nPhysics the mass and the shape of the grids whose blocks changed, and the forces of their gravity scale.</summary>
    [HotPath]
    public void Run(in TickTime time)
    {
        DestroyRetired();
        Vector3 gravity = _physics!.Gravity;
        for (int i = _list.Count - 1; i >= 0; i--)
        {
            GridRecord record = _list[i];
            if (record.Grid.IsDestroyed || !_physics.IsAlive(record.Body))
            {
                Forget(record);   // a grid absorbed by a merge, or destroyed: nPhysics removes its body
                continue;
            }

            if (record.Recount)
            {
                record.Recount = false;
                record.Mass.Clear();
                record.Instances.Clear();
                Count(record);
                record.Dirty = true;
            }

            if (record.Dirty)
            {
                Apply(record);
            }

            if (record.Mode == GridMotionMode.Dynamic && !record.Frozen && record.GravityScale != 1f)
            {
                _physics.ApplyForce(record.Body, (record.GravityScale - 1f) * record.AppliedMass * gravity);
            }
        }
    }

    public void Dispose()
    {
        _tick?.Dispose();
        _notifications?.Dispose();
        _residency?.Dispose();
        _sessionEnded?.Dispose();
    }

    /// <summary>Gets the sums of a grid, as the notifications left them.</summary>
    internal GridMass SumsOf(Grid grid) => Require(grid).Mass;

    /// <summary>Counts every block of a grid again, without changing its record: what the sums should be.</summary>
    internal GridMass Recount(Grid grid)
    {
        var record = new GridRecord(grid);
        Count(record);
        return record.Mass;
    }

    /// <summary>Gives nPhysics the mass, the inertia, and the shape of a grid whose blocks changed.</summary>
    [HotPath]
    private void Apply(GridRecord record)
    {
        record.Dirty = false;
        record.Mass.Compute(MinimumMass, out float mass, out Vector3 center, out BodyInertia inertia);
        bool small = IsSmallNow(record.Small, record.Mass.Blocks);
        bool moved = Vector3.DistanceSquared(center, record.Center) > CenterTolerance * CenterTolerance;
        if (small || small != record.Small || moved)
        {
            Vector3 previous = record.Center;
            record.Small = small;
            PhysicsShape shape = BuildShape(record, center);
            _physics!.SetShape(record.Body, shape, center);
            _retiring.Add(record.Shape);
            record.Shape = shape;
            record.Center = center;
            if (moved && record.Mode == GridMotionMode.Dynamic && !record.Frozen && !record.Inherit)
            {
                // The velocity that the new center had, so that the grid goes on as it went.
                BodyState state = _physics.GetState(record.Body);
                Vector3 arm = Vector3.Transform(center - previous, Quaternion.Normalize(state.Transform.Rotation));
                _physics.SetVelocity(record.Body, state.LinearVelocity + Vector3.Cross(state.AngularVelocity, arm), state.AngularVelocity);
            }
        }

        if (mass != record.AppliedMass || inertia != record.AppliedInertia)
        {
            _physics!.SetMass(record.Body, mass, inertia);
            record.AppliedMass = mass;
            record.AppliedInertia = inertia;
        }

        if (record.Inherit && record.Mass.Blocks > 0)
        {
            record.Inherit = false;
            Vector3 arm = record.Grid.Transform.TransformPoint(center).RelativeTo(record.InheritCenter);
            _physics!.SetVelocity(record.Body, record.InheritLinear + Vector3.Cross(record.InheritAngular, arm), record.InheritAngular);
        }
    }

    /// <summary>Follows a batch of changed blocks: the sums of the grid take away the old blocks and add the new ones.</summary>
    private void OnBlocksChanged(VoxelBatchNotification notification)
    {
        if (!_records.TryGetValue(notification.Grid, out GridRecord? record) || _masses is null)
        {
            return;
        }

        if (notification.Cause == BlockCause.Generation)
        {
            record.Recount = true;   // a chunk filled as it loads: counted again with the chunks in memory
            return;
        }

        Grid grid = notification.Grid;
        grid.Storage.TryGetChunk(notification.Chunk, out Chunk? chunk);
        IReadOnlyList<VoxelBlockChange> changes = notification.Changes;
        for (int i = 0; i < changes.Count; i++)
        {
            VoxelBlockChange change = changes[i];
            if (change.OldState == change.NewState && !(change.DataChanged && _masses.IsInstance(change.NewState)))
            {
                continue;
            }

            Vector3 corner = Corner(notification.Chunk, change.LocalIndex);
            record.Mass.Add(MassOf(record, notification.Chunk, change.LocalIndex, change.OldState, null, remove: true), corner, -1, change.OldState != 0);
            record.Mass.Add(MassOf(record, notification.Chunk, change.LocalIndex, change.NewState, chunk, remove: false), corner, 1, change.NewState != 0);
        }

        record.Dirty = true;
    }

    /// <summary>Counts a grid again once one of its chunks enters or leaves memory.</summary>
    private void OnResidency(ChunkResidencyChange change)
    {
        if (_records.TryGetValue(change.Grid, out GridRecord? record))
        {
            record.Recount = true;
        }
    }

    /// <summary>Gets the mass of a cell, from the table of states, or for a state whose mass depends on its instance, from its shape; the cache of the instances follows.</summary>
    [HotPath]
    private BlockMass MassOf(GridRecord record, ChunkCoordinate chunk, int cell, uint state, Chunk? source, bool remove)
    {
        if (!_masses!.IsInstance(state))
        {
            return _masses.Of(state);
        }

        var key = new InstanceKey(chunk, cell);
        if (remove)
        {
            return record.Instances.Remove(key, out BlockMass held) ? held : _masses.Of(state);
        }

        BlockMass mass = source is not null ? _masses.OfInstance(state, source.GetBlockDataView(cell)) : _masses.Of(state);
        record.Instances[key] = mass;
        return mass;
    }

    /// <summary>Adds every block of the grid in memory to its sums.</summary>
    [HotPath]
    private void Count(GridRecord record)
    {
        _chunks.Clear();
        record.Grid.Storage.GetResidentChunks(_chunks);
        foreach (Chunk chunk in _chunks)
        {
            Palette palette = chunk.Palette;
            if (palette.IsUniform && palette.GetEntry(0) == 0)
            {
                continue;
            }

            for (int cell = 0; cell < Chunk.Volume; cell++)
            {
                uint state = palette.Get(cell);
                if (state != 0)
                {
                    record.Mass.Add(MassOf(record, chunk.Coordinate, cell, state, chunk, remove: false), Corner(chunk.Coordinate, cell), 1, true);
                }
            }
        }

        _chunks.Clear();
    }

    /// <summary>Builds the shape of a grid around its center of mass: the compound of its boxes for a small grid, its voxel shape otherwise.</summary>
    [HotPath]
    private PhysicsShape BuildShape(GridRecord record, Vector3 center)
    {
        if (record.Small && BuildCompound(record, center) is { } compound)
        {
            return compound;
        }

        record.Small = false;
        return _physics!.CreateVoxels(record.Grid, center);
    }

    /// <summary>Builds the compound of the collision boxes of the blocks of a small grid, or none when its blocks have no collision.</summary>
    [HotPath]
    private PhysicsShape? BuildCompound(GridRecord record, Vector3 center)
    {
        _children.Clear();
        _chunks.Clear();
        record.Grid.Storage.GetResidentChunks(_chunks);
        foreach (Chunk chunk in _chunks)
        {
            Palette palette = chunk.Palette;
            if (palette.IsUniform && palette.GetEntry(0) == 0)
            {
                continue;
            }

            for (int cell = 0; cell < Chunk.Volume; cell++)
            {
                uint state = palette.Get(cell);
                if (state == 0)
                {
                    continue;
                }

                CollisionShape collision = _masses!.IsInstance(state) ? _shapes!.Get(state, chunk.GetBlockDataView(cell)).Collision : _shapes!.Get(state, BlockDataView.Empty).Collision;
                Vector3 corner = Corner(chunk.Coordinate, cell);
                for (int b = 0; b < collision.Count; b++)
                {
                    CollisionBox box = collision[b];
                    Vector3 size = box.Maximum - box.Minimum;
                    if (size.X <= 0 || size.Y <= 0 || size.Z <= 0)
                    {
                        continue;
                    }

                    _children.Add(new CompoundChild(Box(size), corner + ((box.Minimum + box.Maximum) / 2) - center, Quaternion.Identity));
                }
            }
        }

        _chunks.Clear();
        return _children.Count == 0 ? null : _physics!.CreateCompound(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_children));
    }

    /// <summary>Gets the box of a size, created once and kept for the compounds of the small grids.</summary>
    [HotPath]
    private PhysicsShape Box(Vector3 size)
    {
        if (!_boxes.TryGetValue(size, out PhysicsShape box))
        {
            box = _physics!.CreateBox(size);
            _boxes.Add(size, box);
        }

        return box;
    }

    /// <summary>Destroys the shapes that bodies stopped using at the last step.</summary>
    [HotPath]
    private void DestroyRetired()
    {
        foreach (PhysicsShape shape in _retired)
        {
            _physics!.DestroyShape(shape);
        }

        _retired.Clear();
        (_retired, _retiring) = (_retiring, _retired);
    }

    /// <summary>Forgets a grid that Motion no longer moves; its shape is destroyed once nPhysics stops using it.</summary>
    [HotPath]
    private void Forget(GridRecord record)
    {
        _records.Remove(record.Grid);
        _list.Remove(record);
        _retiring.Add(record.Shape);
    }

    /// <summary>Forgets every grid at the end of the world session: nPhysics forgets their bodies.</summary>
    private void Reset()
    {
        foreach (GridRecord record in _list)
        {
            _retiring.Add(record.Shape);
        }

        _records.Clear();
        _list.Clear();
    }

    /// <summary>Tells whether a grid that Motion takes starts as a small grid.</summary>
    [HotPath]
    private bool IsSmallAtStart(int blocks) => blocks > 0 && blocks <= SmallGridBlocks;

    /// <summary>Tells whether a grid is a small grid after a change: it becomes one at <see cref="SmallGridBlocks"/> blocks or fewer, and stops past twice that number.</summary>
    [HotPath]
    private bool IsSmallNow(bool small, int blocks) => blocks > 0 && blocks <= (small ? 2 * SmallGridBlocks : SmallGridBlocks);

    [HotPath]
    private static BodyKind KindOf(GridMotionMode mode) => mode == GridMotionMode.Dynamic ? BodyKind.Dynamic : BodyKind.Kinematic;

    /// <summary>Gets the corner of a cell in the frame of its grid.</summary>
    [HotPath]
    private static Vector3 Corner(ChunkCoordinate chunk, int cell) => new(
        (chunk.X * (float)ChunkSize) + (cell % ChunkSize),
        (chunk.Y * (float)ChunkSize) + (cell / (ChunkSize * ChunkSize)),
        (chunk.Z * (float)ChunkSize) + (cell / ChunkSize % ChunkSize));

    /// <summary>Gets the record of a grid that is not frozen and moves dynamically, or none: forces and impulses wait for neither.</summary>
    [HotPath]
    private GridRecord? Moving(Grid grid)
    {
        GridRecord record = Require(grid);
        return record.Frozen || record.Mode != GridMotionMode.Dynamic ? null : record;
    }

    [HotPath]
    private GridRecord Require(Grid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return _records.TryGetValue(grid, out GridRecord? record) ? record : throw new ArgumentException($"Motion does not move the grid {grid.Identifier}.", nameof(grid));
    }

    private void RequireStarted()
    {
        if (_masses is null)
        {
            throw new InvalidOperationException("Motion takes grids once it has started.");
        }
    }

    /// <summary>A cell of a grid whose mass depends on its instance.</summary>
    private readonly record struct InstanceKey
    {
        [HotPath]
        public InstanceKey(ChunkCoordinate chunk, int cell) => (Chunk, Cell) = (chunk, cell);

        public ChunkCoordinate Chunk { get; }

        public int Cell { get; }
    }

    /// <summary>A grid that Motion moves.</summary>
    private sealed class GridRecord(Grid grid)
    {
        public Grid Grid { get; } = grid;

        public BodyHandle Body;
        public GridMotionMode Mode;
        public bool Frozen;
        public float GravityScale = 1f;
        public GridMass Mass;
        public Dictionary<InstanceKey, BlockMass> Instances { get; } = [];
        public bool Dirty;
        public bool Recount;
        public bool Small;
        public PhysicsShape Shape;
        public Vector3 Center;
        public float AppliedMass;
        public BodyInertia AppliedInertia;
        public bool Inherit;
        public Vector3 InheritLinear;
        public Vector3 InheritAngular;
        public WorldPosition InheritCenter;
    }
}
