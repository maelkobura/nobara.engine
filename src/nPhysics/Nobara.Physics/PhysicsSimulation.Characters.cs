// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// The character controller: upright boxes that follow their movement intent,
/// resolved axis by axis against the voxels of every grid and the static
/// bodies, in the frame of their support grid when they stand on one. It runs
/// after the worlds step and the grids are moved, with no backend: the same
/// code on both sides.
/// </summary>
internal sealed partial class PhysicsSimulation
{
    /// <summary>The gap below which two faces touch without overlapping, in blocks.</summary>
    private const float Skin = 1e-4f;

    /// <summary>The part of the cosine of <see cref="CharacterController.MaxSupportTilt"/> that a grid's vertical keeps along the vertical of the world.</summary>
    private static readonly float SupportCosine = MathF.Cos(CharacterController.MaxSupportTilt * MathF.PI / 180f);

    private CharacterRecord[] _characters = new CharacterRecord[16];
    private int _characterHigh;
    private int _characterFree = -1;
    private int _characterCount;
    private CharacterBox[] _boxes = new CharacterBox[64];
    private int _boxCount;
    private CharacterStep[] _characterSteps = new CharacterStep[16];
    private int _characterStepCount;
    private CharacterLanding[] _characterLandings = new CharacterLanding[16];
    private int _characterLandingCount;
    private readonly List<Grid> _characterGrids = [];
    private ComponentType<CharacterBody> _characterComponent;
    private ComponentType<MovementIntent> _intentComponent;
    private ComponentType<CharacterState> _characterStateComponent;
    private ComponentType<LocalTransform> _localTransform;

    /// <summary>Gets the number of characters.</summary>
    internal int CharacterCount => _characterCount;

    public void AddCharacter(DimensionId dimension, EntityId entity, in CharacterBody character)
    {
        RequireStarted();
        RequireCharacter(character);
        if (!_entities!.TryGet(dimension, out EntityWorld? world))
        {
            throw new ArgumentException($"The {dimension} has no entity world.", nameof(dimension));
        }

        if (!world.IsAlive(entity))
        {
            throw new ArgumentException($"The {entity} is not alive in {dimension}.", nameof(entity));
        }

        if (world.IsLocked)
        {
            throw new InvalidOperationException("A character is added outside entity systems: the structure of the world is locked while one runs.");
        }

        if (!world.Has(entity, _transform))
        {
            throw new ArgumentException($"The {entity} has no world transform: a character starts where its entity is.", nameof(entity));
        }

        if (!world.GetParent(entity).IsNull)
        {
            throw new ArgumentException($"The {entity} has a parent: a character is a root entity, whose parent only nPhysics sets, to its support grid.", nameof(entity));
        }

        if (world.Has(entity, _bodyComponent))
        {
            throw new ArgumentException($"The {entity} is a body: remove its body before making it a character.", nameof(entity));
        }

        if (world.Has(entity, _characterComponent))
        {
            throw new ArgumentException($"The {entity} is a character already.", nameof(entity));
        }

        int slot = AllocateCharacter();
        ref CharacterRecord record = ref _characters[slot];
        record.World = world;
        record.Dimension = dimension;
        record.Entity = entity;
        record.Support = null;
        record.Velocity = world.Has(entity, _velocityComponent) ? world.Read(entity, _velocityComponent).Linear : Vector3.Zero;   // a character back from a ragdoll goes on with its velocity
        world.Add(entity, _characterComponent, character);
        world.Add(entity, _characterStateComponent, new CharacterState { Slot = slot });
        if (!world.Has(entity, _intentComponent))
        {
            world.Add(entity, _intentComponent, default);
        }

        if (!world.Has(entity, _velocityComponent))
        {
            world.Add(entity, _velocityComponent, default);
        }
    }

    public void RemoveCharacter(DimensionId dimension, EntityId entity)
    {
        if (!_entities!.TryGet(dimension, out EntityWorld? world) || !world.IsAlive(entity) || !world.Has(entity, _characterStateComponent))
        {
            throw new ArgumentException($"The {entity} is not a character of {dimension}.", nameof(entity));
        }

        if (world.IsLocked)
        {
            throw new InvalidOperationException("A character is removed outside entity systems: the structure of the world is locked while one runs.");
        }

        int slot = world.Read(entity, _characterStateComponent).Slot;
        if ((uint)slot >= (uint)_characterHigh || !_characters[slot].Alive || _characters[slot].World != world || _characters[slot].Entity != entity)
        {
            throw new ArgumentException($"The {entity} is not a character of {dimension}: its state names another one.", nameof(entity));
        }

        RemoveCharacterCore(slot);
    }

    [HotPath]
    public bool IsCharacter(DimensionId dimension, EntityId entity) =>
        _entities!.TryGet(dimension, out EntityWorld? world) && world.IsAlive(entity) && world.Has(entity, _characterStateComponent);

    /// <summary>Registers the components of the characters.</summary>
    private void RegisterCharacters(IEntityWorlds entities)
    {
        _localTransform = entities.LocalTransform;
        _characterComponent = entities.RegisterComponent<CharacterBody>(_context, "nobara.physics.character");
        _intentComponent = entities.RegisterComponent<MovementIntent>(_context, "nobara.physics.movement_intent");
        _characterStateComponent = entities.RegisterComponent<CharacterState>(_context, "nobara.physics.character_state");
    }

    /// <summary>
    /// Moves every character once, after the worlds stepped and the grids
    /// moved: each one follows its intent from its transform, against the
    /// voxels and the static bodies, in the frame of its support grid.
    /// </summary>
    [HotPath]
    private void MoveCharacters(float delta)
    {
        _characterStepCount = 0;
        _characterLandingCount = 0;
        for (int i = 0; i < _characterHigh; i++)
        {
            ref CharacterRecord record = ref _characters[i];
            if (!record.Alive)
            {
                continue;
            }

            EntityWorld world = record.World!;
            if (world.IsDestroyed || !world.IsAlive(record.Entity))
            {
                FreeCharacter(i);
                continue;
            }

            if (!world.Has(record.Entity, _characterComponent))
            {
                RemoveCharacterCore(i);   // its module removed the component itself
                continue;
            }

            MoveCharacter(ref record, delta);
        }
    }

    [HotPath]
    private void MoveCharacter(ref CharacterRecord record, float delta)
    {
        EntityWorld world = record.World!;
        EntityId entity = record.Entity;
        CharacterBody body = Sanitize(world.Read(entity, _characterComponent));
        MovementIntent intent = world.Read(entity, _intentComponent);
        CharacterState state = world.Read(entity, _characterStateComponent);
        Grid? support = record.Support;
        if (support is not null && (support.IsDestroyed || world.GetParent(entity) != support.Entity))
        {
            support = null;   // its grid is gone, which detached it where it was
            record.Support = null;
            record.Velocity = world.Read(entity, _velocityComponent).Linear;
        }

        WorldTransform transform = world.Read(entity, _transform);
        Frame frame;
        Vector3 feet;
        if (support is not null)
        {
            WorldTransform grid = support.Transform;
            frame = new Frame(grid.Position, Quaternion.Normalize(grid.Rotation));
            feet = world.Read(entity, _localTransform).Position;
        }
        else
        {
            WorldPosition position = transform.Position;
            frame = new Frame(new WorldPosition(position.ChunkX, position.ChunkY, position.ChunkZ, Vector3.Zero), Quaternion.Identity);
            feet = position.Local;
        }

        var half = new Vector3(body.Size.X / 2, 0, body.Size.Z / 2);
        Vector3 v = record.Velocity;
        float reachHorizontal = MathF.Sqrt((v.X * v.X) + (v.Z * v.Z)) * delta;
        if (TouchesInactive(record.Dimension, frame.ToWorld(feet + new Vector3(0, body.Size.Y / 2, 0)), MathF.Max(half.X, MathF.Max(half.Z, body.Size.Y / 2)) + reachHorizontal + MathF.Abs(v.Y * delta)))
        {
            state.IsFrozen = true;
            world.Set(entity, _characterStateComponent, state);
            return;   // it keeps its place and its velocity until the chunks are active
        }

        state.IsFrozen = false;
        bool wasGrounded = state.IsGrounded;
        Quaternion inverse = Quaternion.Conjugate(frame.Rotation);
        Vector3 gravity = Vector3.Transform(_gravity * body.GravityScale, inverse);
        Vector3 wanted = Wanted(intent.Velocity, inverse);
        bool jumped = false;
        if (wasGrounded)
        {
            v.X = wanted.X;
            v.Z = wanted.Z;
            v.Y = MathF.Max(v.Y, 0f);
            if (intent.JumpSpeed > 0 && float.IsFinite(intent.JumpSpeed))
            {
                v.Y = intent.JumpSpeed;
                jumped = true;
            }
        }
        else if (wanted.X != 0 || wanted.Z != 0)
        {
            float dx = wanted.X - v.X;
            float dz = wanted.Z - v.Z;
            float change = MathF.Sqrt((dx * dx) + (dz * dz));
            float most = body.AirAcceleration * delta;
            if (change > most)
            {
                dx *= most / change;
                dz *= most / change;
            }

            v.X += dx;
            v.Z += dz;
        }

        // On the ground, only the part of the gravity across the floor pulls: a character does not slide down a tilted support grid.
        v += wasGrounded && !jumped ? new Vector3(0, gravity.Y * delta, 0) : gravity * delta;
        Vector3 move = v * delta;
        Vector3 minimum = feet - half;
        Vector3 maximum = feet + half + new Vector3(0, body.Size.Y, 0);
        GatherBoxes(record.Dimension, frame, Vector3.Min(minimum, minimum + move) - new Vector3(Skin), Vector3.Max(maximum, maximum + move) + new Vector3(Skin, body.StepHeight + Skin, Skin), body.Filter);
        Vector3 moved = Collide(minimum, maximum, move, out bool hitDown);
        if (body.StepHeight > 0 && (wasGrounded || hitDown) && !jumped && (moved.X != move.X || moved.Z != move.Z))
        {
            Vector3 stepped = StepUp(minimum, maximum, move, body.StepHeight, out bool landed);
            if (landed && (stepped.X * stepped.X) + (stepped.Z * stepped.Z) > (moved.X * moved.X) + (moved.Z * moved.Z) + (Skin * Skin))
            {
                moved = stepped;
                hitDown = true;
            }
        }

        float impact = -v.Y;
        if (moved.Y != move.Y)
        {
            v.Y = 0;
        }

        if (moved.X != move.X)
        {
            v.X = 0;
        }

        if (moved.Z != move.Z)
        {
            v.Z = 0;
        }

        feet += moved;
        bool grounded = hitDown;
        CharacterGround ground = grounded ? GroundUnder(feet, half) : default;
        grounded &= ground.Kind != CharacterGroundKind.None;
        int airSteps = state.AirSteps;
        WorldPosition feetInWorld = frame.ToWorld(feet);
        Vector3 carried = support is null ? Vector3.Zero : SupportVelocity(support, feetInWorld);
        Vector3 velocity = Vector3.Transform(v, frame.Rotation) + carried;   // in the world
        Grid? next = support;
        if (grounded)
        {
            Grid? grid = ground.Kind == CharacterGroundKind.Block && _voxels!.TryGet(record.Dimension, ground.Grid, out Grid? found) ? found : null;
            next = grid is not null && CanSupport(grid) ? grid : null;
            state.AirSteps = 0;
        }
        else
        {
            state.AirSteps = airSteps + 1;
            if (support is not null && state.AirSteps > CharacterController.SupportGraceSteps)
            {
                next = null;
            }
        }

        if (next != support)
        {
            Vector3 frameVelocity = next is null ? velocity : velocity - SupportVelocity(next, feetInWorld);
            v = Vector3.Transform(frameVelocity, next is null ? Quaternion.Identity : Quaternion.Conjugate(Quaternion.Normalize(next.Transform.Rotation)));
            ChangeSupport(ref record, feetInWorld, next);
            state.Support = next?.Entity ?? EntityId.Null;
        }
        else if (support is not null)
        {
            LocalTransform local = world.Read(entity, _localTransform);
            local = local with { Position = feet };
            world.Set(entity, _localTransform, local);
            world.Set(entity, _transform, support.Transform.Combine(local.Position, local.Rotation, local.Scale));
        }
        else
        {
            world.Set(entity, _transform, transform with { Position = feetInWorld });
        }

        record.Velocity = v;
        Vector3 angular = record.Support is { } holder && TryGetGridBody(holder, out int slot) ? _bodies[slot].Angular : Vector3.Zero;
        world.Set(entity, _velocityComponent, new BodyVelocity { Linear = velocity, Angular = angular });
        state.IsGrounded = grounded;
        state.Ground = ground;
        world.Set(entity, _characterStateComponent, state);
        if (grounded)
        {
            float walked = MathF.Sqrt((moved.X * moved.X) + (moved.Z * moved.Z));
            if (wasGrounded && walked > Skin)
            {
                AddStep(new CharacterStep(record.Dimension, entity, feetInWorld, walked, ground));
            }
            else if (!wasGrounded && impact >= CharacterController.LandingSpeed)
            {
                AddLanding(new CharacterLanding(record.Dimension, entity, feetInWorld, impact, airSteps, ground));
            }
        }

        Push(record.Dimension, frame, feet, half, body, velocity);
    }

    /// <summary>Moves a box axis by axis, vertical first, so that it slides along what it meets.</summary>
    /// <returns>The motion it made.</returns>
    [HotPath]
    private Vector3 Collide(Vector3 minimum, Vector3 maximum, Vector3 move, out bool hitDown)
    {
        float dy = ClipY(minimum, maximum, move.Y);
        hitDown = move.Y < 0 && dy != move.Y;
        minimum.Y += dy;
        maximum.Y += dy;
        float dx = ClipX(minimum, maximum, move.X);
        minimum.X += dx;
        maximum.X += dx;
        float dz = ClipZ(minimum, maximum, move.Z);
        return new Vector3(dx, dy, dz);
    }

    /// <summary>Tries the horizontal motion from a step higher, then comes down onto what is there; <c>landed</c> tells whether the box came down onto something.</summary>
    /// <returns>The motion of the attempt.</returns>
    [HotPath]
    private Vector3 StepUp(Vector3 minimum, Vector3 maximum, Vector3 move, float height, out bool landed)
    {
        float up = ClipY(minimum, maximum, height);
        minimum.Y += up;
        maximum.Y += up;
        float dx = ClipX(minimum, maximum, move.X);
        minimum.X += dx;
        maximum.X += dx;
        float dz = ClipZ(minimum, maximum, move.Z);
        minimum.Z += dz;
        maximum.Z += dz;
        float down = ClipY(minimum, maximum, -up - Skin);
        landed = down != -up - Skin;
        return new Vector3(dx, up + down, dz);
    }

    [HotPath]
    private float ClipY(Vector3 minimum, Vector3 maximum, float dy)
    {
        for (int i = 0; i < _boxCount; i++)
        {
            ref CharacterBox box = ref _boxes[i];
            if (box.Max.X <= minimum.X + Skin || box.Min.X >= maximum.X - Skin || box.Max.Z <= minimum.Z + Skin || box.Min.Z >= maximum.Z - Skin)
            {
                continue;
            }

            if (dy > 0 && box.Min.Y >= maximum.Y - Skin)
            {
                dy = MathF.Min(dy, box.Min.Y - maximum.Y);
            }
            else if (dy < 0 && box.Max.Y <= minimum.Y + Skin)
            {
                dy = MathF.Max(dy, box.Max.Y - minimum.Y);
            }
        }

        return dy;
    }

    [HotPath]
    private float ClipX(Vector3 minimum, Vector3 maximum, float dx)
    {
        for (int i = 0; i < _boxCount; i++)
        {
            ref CharacterBox box = ref _boxes[i];
            if (box.Max.Y <= minimum.Y + Skin || box.Min.Y >= maximum.Y - Skin || box.Max.Z <= minimum.Z + Skin || box.Min.Z >= maximum.Z - Skin)
            {
                continue;
            }

            if (dx > 0 && box.Min.X >= maximum.X - Skin)
            {
                dx = MathF.Min(dx, box.Min.X - maximum.X);
            }
            else if (dx < 0 && box.Max.X <= minimum.X + Skin)
            {
                dx = MathF.Max(dx, box.Max.X - minimum.X);
            }
        }

        return dx;
    }

    [HotPath]
    private float ClipZ(Vector3 minimum, Vector3 maximum, float dz)
    {
        for (int i = 0; i < _boxCount; i++)
        {
            ref CharacterBox box = ref _boxes[i];
            if (box.Max.Y <= minimum.Y + Skin || box.Min.Y >= maximum.Y - Skin || box.Max.X <= minimum.X + Skin || box.Min.X >= maximum.X - Skin)
            {
                continue;
            }

            if (dz > 0 && box.Min.Z >= maximum.Z - Skin)
            {
                dz = MathF.Min(dz, box.Min.Z - maximum.Z);
            }
            else if (dz < 0 && box.Max.Z <= minimum.Z + Skin)
            {
                dz = MathF.Max(dz, box.Max.Z - minimum.Z);
            }
        }

        return dz;
    }

    /// <summary>Finds what the feet stand on: of the boxes whose top is at their height, the one under most of the bottom of the character.</summary>
    [HotPath]
    private CharacterGround GroundUnder(Vector3 feet, Vector3 half)
    {
        float best = 0;
        CharacterGround ground = default;
        for (int i = 0; i < _boxCount; i++)
        {
            ref CharacterBox box = ref _boxes[i];
            if (MathF.Abs(box.Max.Y - feet.Y) > 2 * Skin)
            {
                continue;
            }

            float x = MathF.Min(box.Max.X, feet.X + half.X) - MathF.Max(box.Min.X, feet.X - half.X);
            float z = MathF.Min(box.Max.Z, feet.Z + half.Z) - MathF.Max(box.Min.Z, feet.Z - half.Z);
            if (x > Skin && z > Skin && x * z > best)
            {
                best = x * z;
                ground = box.Ground;
            }
        }

        return ground;
    }

    /// <summary>
    /// Collects, in the frame of the character, the collision boxes of the
    /// voxels of every grid and of the static bodies that a box reaches.
    /// </summary>
    [HotPath]
    private void GatherBoxes(DimensionId dimension, in Frame frame, Vector3 minimum, Vector3 maximum, in CollisionFilter filter)
    {
        _boxCount = 0;
        Vector3 center = (minimum + maximum) / 2;
        Vector3 extent = (maximum - minimum) / 2;
        WorldPosition centerInWorld = frame.ToWorld(center);
        float radius = extent.Length();
        if ((filter.Mask & CollisionFilter.VoxelLayer) != 0)
        {
            _characterGrids.Clear();
            _voxels!.SpatialIndex.Query(WorldBox.Around(centerInWorld, new Vector3(radius)), _characterGrids);
            for (int g = 0; g < _characterGrids.Count; g++)
            {
                Grid grid = _characterGrids[g];
                if (!grid.IsDestroyed && grid.Dimension == dimension && grid.Transform.Scale == Vector3.One)
                {
                    GatherCells(grid, frame, center, extent, centerInWorld);
                }
            }
        }

        for (int i = 0; i < _bodyHigh; i++)
        {
            ref BodyRecord body = ref _bodies[i];
            if (!body.Alive || body.Kind != BodyKind.Static || body.Dimension != dimension || body.Grid is not null || !filter.CollidesWith(body.Filter))
            {
                continue;
            }

            ref ShapeSlot shape = ref _shapes[body.Shape.Index];
            Vector3 offset = body.Position.RelativeTo(centerInWorld);
            if (offset.Length() > shape.Radius + radius || shape.Description!.Kind == ShapeKind.Mesh)
            {
                continue;
            }

            Quaternion inverse = Quaternion.Conjugate(frame.Rotation);
            Vector3 position = center + Vector3.Transform(offset, inverse);
            Quaternion rotation = Quaternion.Normalize(inverse * body.Rotation);
            var ground = new CharacterGround(CharacterGroundKind.Body, default, default, 0, new BodyHandle(i, body.Generation));
            if (shape.Description.Kind == ShapeKind.Compound)
            {
                ReadOnlySpan<CompoundChild> children = shape.Description.Children.Span;
                for (int c = 0; c < children.Length; c++)
                {
                    ShapeDescription child = _shapes[children[c].Shape.Index].Description!;
                    Bounds(child, out Vector3 low, out Vector3 high);
                    AddOrientedBox(position + Vector3.Transform(children[c].Position, rotation), Quaternion.Normalize(rotation * children[c].Rotation), low, high, ground);
                }
            }
            else
            {
                Bounds(shape.Description, out Vector3 low, out Vector3 high);
                AddOrientedBox(position, rotation, low, high, ground);
            }
        }
    }

    /// <summary>Collects the boxes of the cells of a grid that a box of the frame of the character reaches.</summary>
    [HotPath]
    private void GatherCells(Grid grid, in Frame frame, Vector3 center, Vector3 extent, in WorldPosition centerInWorld)
    {
        WorldTransform transform = grid.Transform;
        Quaternion rotation = Quaternion.Normalize(transform.Rotation);
        Quaternion toGrid = Quaternion.Conjugate(rotation) * frame.Rotation;   // from the frame of the character to the frame of the grid
        bool aligned = 1f - MathF.Abs(toGrid.W) < 1e-6f;
        Difference(centerInWorld, transform.Position, out double rx, out double ry, out double rz);
        Rotate(Quaternion.Conjugate(rotation), rx, ry, rz, out double gx, out double gy, out double gz);   // the center in the frame of the grid, in double precision a billion blocks away
        Vector3 reach = aligned ? extent : Spread(toGrid, extent);
        double minX = Math.Floor(gx - reach.X);
        double minY = Math.Floor(gy - reach.Y);
        double minZ = Math.Floor(gz - reach.Z);
        double maxX = Math.Floor(gx + reach.X);
        double maxY = Math.Floor(gy + reach.Y);
        double maxZ = Math.Floor(gz + reach.Z);
        if (!Fits(minX) || !Fits(minY) || !Fits(minZ) || !Fits(maxX) || !Fits(maxY) || !Fits(maxZ))
        {
            return;
        }

        Quaternion toFrame = Quaternion.Conjugate(toGrid);
        Chunk? chunk = null;
        ChunkCoordinate held = default;
        GridHandle handle = grid.Handle;
        for (int y = (int)minY; y <= (int)maxY; y++)
        {
            for (int z = (int)minZ; z <= (int)maxZ; z++)
            {
                for (int x = (int)minX; x <= (int)maxX; x++)
                {
                    var block = new BlockCoordinate(x, y, z);
                    ChunkCoordinate coordinate = VoxelCoordinates.ToChunk(block);
                    if (chunk is null || coordinate != held)
                    {
                        held = coordinate;
                        if (!grid.Storage.TryGetChunk(coordinate, out chunk))
                        {
                            chunk = null;
                        }
                    }

                    if (chunk is null)
                    {
                        continue;
                    }

                    int cell = VoxelCoordinates.ToIndex(VoxelCoordinates.ToLocal(block));
                    uint state = chunk.Palette.Get(cell);
                    if (state == 0)
                    {
                        continue;
                    }

                    var corner = new Vector3((float)(x - gx), (float)(y - gy), (float)(z - gz));   // the corner of the cell from the center, small
                    var ground = new CharacterGround(CharacterGroundKind.Block, handle, block, state, default);
                    ReadOnlySpan<CollisionBox> boxes = BoxesOf(grid, chunk, coordinate, cell, state, out CollisionShape? evaluated);
                    int count = evaluated?.Count ?? boxes.Length;
                    for (int b = 0; b < count; b++)
                    {
                        CollisionBox box = evaluated is null ? boxes[b] : evaluated[b];
                        if (aligned)
                        {
                            AddBox(center + corner + box.Minimum, center + corner + box.Maximum, ground);
                        }
                        else
                        {
                            AddOrientedBox(center + Vector3.Transform(corner, toFrame), toFrame, box.Minimum, box.Maximum, ground);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Gets the collision boxes of a cell: from the table of states, or for a shape that depends on the instance, from the cells a world holds or the registry of shapes.</summary>
    [HotPath]
    private ReadOnlySpan<CollisionBox> BoxesOf(Grid grid, Chunk chunk, ChunkCoordinate coordinate, int cell, uint state, out CollisionShape? evaluated)
    {
        evaluated = null;
        ref readonly StateShape shape = ref _voxelShapes!.Get(state);
        if (!shape.Instance)
        {
            return _voxelShapes.Boxes(shape);
        }

        if (_cells.TryGetValue(new VoxelChunkKey(grid, coordinate), out VoxelChunkCells? cells))
        {
            return cells.GetBoxes(cell);
        }

        evaluated = _shapeRegistry!.Get(state, chunk.GetBlockDataView(cell)).Collision;   // no world holds the chunk: evaluated now
        return default;
    }

    /// <summary>Pushes the dynamic bodies the character walks into: an impulse along the way it goes, which a light body feels, and a heavy one hardly; they do not push it back.</summary>
    [HotPath]
    private void Push(DimensionId dimension, in Frame frame, Vector3 feet, Vector3 half, in CharacterBody character, Vector3 velocity)
    {
        var extent = new Vector3(half.X, character.Size.Y / 2, half.Z);
        WorldPosition center = frame.ToWorld(feet + new Vector3(0, extent.Y, 0));
        Quaternion inverse = Quaternion.Conjugate(frame.Rotation);
        for (int i = 0; i < _bodyHigh; i++)
        {
            ref BodyRecord body = ref _bodies[i];
            if (!body.Alive || body.Kind != BodyKind.Dynamic || body.World is null || body.Grid is not null || body.Dimension != dimension || !character.Filter.CollidesWith(body.Filter))
            {
                continue;
            }

            float radius = _shapes[body.Shape.Index].Radius;
            Vector3 offset = Vector3.Transform(body.Position.RelativeTo(center), inverse);
            Vector3 nearest = Vector3.Clamp(offset, -extent, extent);
            if (Vector3.DistanceSquared(nearest, offset) >= radius * radius)
            {
                continue;
            }

            var away = new Vector3(offset.X, 0, offset.Z);
            float length = away.Length();
            if (length < Skin)
            {
                continue;
            }

            Vector3 normal = Vector3.Transform(away / length, frame.Rotation);
            float speed = Vector3.Dot(velocity - body.Linear, normal);
            if (speed <= 0)
            {
                continue;
            }

            float share = character.Mass * body.Mass / (character.Mass + body.Mass);   // the impulse of a collision that ends with both at one speed
            PhysicsBackendWorld backend = body.World.Backend;
            backend.Wake(body.BackendId);
            backend.ApplyImpulse(body.BackendId, normal * (share * speed), Vector3.Zero);
            body.Linear += normal * (share * speed / body.Mass);
        }
    }

    /// <summary>Puts a character on a support grid, moves it to another, or takes it off, keeping where it is in the world.</summary>
    [HotPath]
    private void ChangeSupport(ref CharacterRecord record, in WorldPosition feet, Grid? next)
    {
        EntityWorld world = record.World!;
        EntityId entity = record.Entity;
        WorldTransform now = world.Read(entity, _transform);
        world.Set(entity, _transform, now with { Position = feet });
        if (next is null)
        {
            world.ClearParent(entity);
            world.Set(entity, _transform, world.Read(entity, _transform) with { Position = feet });
        }
        else
        {
            world.SetParent(entity, next.Entity, OrphanPolicy.Detach, keepWorldTransform: true);
            WorldTransform grid = next.Transform;
            Difference(feet, grid.Position, out double x, out double y, out double z);
            Rotate(Quaternion.Conjugate(Quaternion.Normalize(grid.Rotation)), x, y, z, out double lx, out double ly, out double lz);
            LocalTransform local = world.Read(entity, _localTransform) with { Position = new Vector3((float)lx, (float)ly, (float)lz) };
            world.Set(entity, _localTransform, local);
            world.Set(entity, _transform, grid.Combine(local.Position, local.Rotation, local.Scale));
        }

        record.Support = next;
    }

    /// <summary>Gets the velocity of a grid at a point of the world: the velocities of its body, or none for a grid that is no body and does not move.</summary>
    [HotPath]
    private Vector3 SupportVelocity(Grid grid, in WorldPosition point)
    {
        if (!TryGetGridBody(grid, out int slot))
        {
            return Vector3.Zero;
        }

        ref BodyRecord body = ref _bodies[slot];
        return body.Linear + Vector3.Cross(body.Angular, point.RelativeTo(body.Position));
    }

    [HotPath]
    private bool TryGetGridBody(Grid grid, out int slot)
    {
        EntityWorld? world = null;
        if (!grid.IsDestroyed && _entities!.TryGet(grid.Dimension, out world) && world.IsAlive(grid.Entity) && world.Has(grid.Entity, _bodyComponent))
        {
            BodyHandle handle = world.Read(grid.Entity, _bodyComponent).Handle;
            if (IsAlive(handle))
            {
                slot = handle.Index;
                return true;
            }
        }

        slot = -1;
        return false;
    }

    /// <summary>Tells whether a grid can carry characters: not the terrain, not scaled, and not turned past <see cref="CharacterController.MaxSupportTilt"/>.</summary>
    [HotPath]
    private static bool CanSupport(Grid grid)
    {
        WorldTransform transform = grid.Transform;
        return !grid.IsTerrain && !grid.IsDestroyed && transform.Scale == Vector3.One
            && Vector3.Transform(Vector3.UnitY, Quaternion.Normalize(transform.Rotation)).Y >= SupportCosine;
    }

    /// <summary>Tells whether a cube around a point reaches a chunk of the terrain of a dimension that is not loaded or not active.</summary>
    [HotPath]
    private bool TouchesInactive(DimensionId dimension, in WorldPosition center, float half)
    {
        if (_loading is null)
        {
            return false;
        }

        Grid? terrain = TerrainOf(dimension);
        if (terrain is null)
        {
            FindTerrain(dimension);
            terrain = TerrainOf(dimension);
        }

        if (terrain is null || terrain.Residency != GridResidency.Tickets || !ChunkRange(terrain, center, half, out ChunkCoordinate minimum, out ChunkCoordinate maximum))
        {
            return false;
        }

        for (int y = minimum.Y; y <= maximum.Y; y++)
        {
            for (int z = minimum.Z; z <= maximum.Z; z++)
            {
                for (int x = minimum.X; x <= maximum.X; x++)
                {
                    var coordinate = new ChunkCoordinate(x, y, z);
                    if (terrain.Storage.Overlaps(coordinate) && !_loading.GetStatus(terrain, coordinate).IsActive)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Gets the wanted horizontal velocity in the frame of the character, of the length it has in the world.</summary>
    [HotPath]
    private static Vector3 Wanted(Vector3 velocity, Quaternion inverse)
    {
        var horizontal = new Vector3(velocity.X, 0, velocity.Z);
        float speed = horizontal.Length();
        if (!(speed > 0) || !float.IsFinite(speed))
        {
            return Vector3.Zero;
        }

        Vector3 local = Vector3.Transform(horizontal, inverse);
        local.Y = 0;
        float length = local.Length();
        return length < 1e-6f ? Vector3.Zero : local * (speed / length);
    }

    [HotPath]
    private void AddBox(Vector3 minimum, Vector3 maximum, in CharacterGround ground)
    {
        if (_boxCount == _boxes.Length)
        {
            Array.Resize(ref _boxes, _boxCount * 2);
        }

        _boxes[_boxCount++] = new CharacterBox { Min = minimum, Max = maximum, Ground = ground };
    }

    /// <summary>Adds the box that holds a turned box of the frame of the character.</summary>
    [HotPath]
    private void AddOrientedBox(Vector3 position, Quaternion rotation, Vector3 minimum, Vector3 maximum, in CharacterGround ground)
    {
        Vector3 middle = position + Vector3.Transform((minimum + maximum) / 2, rotation);
        Vector3 extent = Spread(rotation, (maximum - minimum) / 2);
        AddBox(middle - extent, middle + extent, ground);
    }

    /// <summary>Gets the half size, along the axes, of a box of a half size turned by a rotation.</summary>
    [HotPath]
    private static Vector3 Spread(Quaternion rotation, Vector3 extent)
    {
        var matrix = Matrix4x4.CreateFromQuaternion(rotation);
        return new Vector3(
            (MathF.Abs(matrix.M11) * extent.X) + (MathF.Abs(matrix.M21) * extent.Y) + (MathF.Abs(matrix.M31) * extent.Z),
            (MathF.Abs(matrix.M12) * extent.X) + (MathF.Abs(matrix.M22) * extent.Y) + (MathF.Abs(matrix.M32) * extent.Z),
            (MathF.Abs(matrix.M13) * extent.X) + (MathF.Abs(matrix.M23) * extent.Y) + (MathF.Abs(matrix.M33) * extent.Z));
    }

    /// <summary>Gets the box that holds a convex shape, in its own frame.</summary>
    [HotPath]
    private static void Bounds(ShapeDescription shape, out Vector3 minimum, out Vector3 maximum)
    {
        switch (shape.Kind)
        {
            case ShapeKind.Sphere:
                maximum = new Vector3(shape.Radius);
                break;
            case ShapeKind.Capsule:
                maximum = new Vector3(shape.Radius, shape.Radius + (shape.Length / 2), shape.Radius);
                break;
            case ShapeKind.Cylinder:
                maximum = new Vector3(shape.Radius, shape.Length / 2, shape.Radius);
                break;
            case ShapeKind.Box:
                maximum = shape.Size / 2;
                break;
            case ShapeKind.ConvexHull:
                ReadOnlySpan<Vector3> points = shape.Points.Span;
                minimum = maximum = points[0];
                for (int i = 1; i < points.Length; i++)
                {
                    minimum = Vector3.Min(minimum, points[i]);
                    maximum = Vector3.Max(maximum, points[i]);
                }

                return;
            default:
                maximum = Vector3.Zero;
                break;
        }

        minimum = -maximum;
    }

    /// <summary>Gets the difference of two positions, in blocks, in double precision.</summary>
    [HotPath]
    private static void Difference(in WorldPosition position, in WorldPosition origin, out double x, out double y, out double z)
    {
        const double size = WorldPosition.ChunkSize;
        x = (((long)position.ChunkX - origin.ChunkX) * size) + ((double)position.Local.X - origin.Local.X);
        y = (((long)position.ChunkY - origin.ChunkY) * size) + ((double)position.Local.Y - origin.Local.Y);
        z = (((long)position.ChunkZ - origin.ChunkZ) * size) + ((double)position.Local.Z - origin.Local.Z);
    }

    /// <summary>Turns a vector of double precision by a rotation.</summary>
    [HotPath]
    private static void Rotate(Quaternion rotation, double x, double y, double z, out double rx, out double ry, out double rz)
    {
        double qx = rotation.X;
        double qy = rotation.Y;
        double qz = rotation.Z;
        double qw = rotation.W;
        double tx = 2 * ((qy * z) - (qz * y));
        double ty = 2 * ((qz * x) - (qx * z));
        double tz = 2 * ((qx * y) - (qy * x));
        rx = x + (qw * tx) + ((qy * tz) - (qz * ty));
        ry = y + (qw * ty) + ((qz * tx) - (qx * tz));
        rz = z + (qw * tz) + ((qx * ty) - (qy * tx));
    }

    [HotPath]
    private void AddStep(in CharacterStep step)
    {
        if (_characterStepCount == _characterSteps.Length)
        {
            Array.Resize(ref _characterSteps, _characterStepCount * 2);
        }

        _characterSteps[_characterStepCount++] = step;
    }

    [HotPath]
    private void AddLanding(in CharacterLanding landing)
    {
        if (_characterLandingCount == _characterLandings.Length)
        {
            Array.Resize(ref _characterLandings, _characterLandingCount * 2);
        }

        _characterLandings[_characterLandingCount++] = landing;
    }

    /// <summary>Replaces the values of a character that cannot move it, such as a size of 0, by usable ones: its module writes the component at any time.</summary>
    [HotPath]
    private static CharacterBody Sanitize(CharacterBody body)
    {
        CharacterBody fallback = CharacterBody.Default;
        if (!(body.Size.X > 0) || !(body.Size.Y > 0) || !(body.Size.Z > 0) || !IsFinite(body.Size))
        {
            body.Size = fallback.Size;
        }

        body.StepHeight = body.StepHeight >= 0 && float.IsFinite(body.StepHeight) ? body.StepHeight : fallback.StepHeight;
        body.GravityScale = body.GravityScale >= 0 && float.IsFinite(body.GravityScale) ? body.GravityScale : fallback.GravityScale;
        body.AirAcceleration = body.AirAcceleration >= 0 && float.IsFinite(body.AirAcceleration) ? body.AirAcceleration : fallback.AirAcceleration;
        body.Mass = body.Mass > 0 && float.IsFinite(body.Mass) ? body.Mass : fallback.Mass;
        return body;
    }

    private static void RequireCharacter(in CharacterBody body)
    {
        if (!(body.Size.X > 0) || !(body.Size.Y > 0) || !(body.Size.Z > 0) || !IsFinite(body.Size))
        {
            throw new ArgumentException("The size of a character is finite and above 0 on each axis.", nameof(body));
        }

        if (!(body.StepHeight >= 0) || !float.IsFinite(body.StepHeight) || !(body.GravityScale >= 0) || !float.IsFinite(body.GravityScale)
            || !(body.AirAcceleration >= 0) || !float.IsFinite(body.AirAcceleration))
        {
            throw new ArgumentException("The step height, the gravity scale, and the air acceleration of a character are finite and from 0.", nameof(body));
        }

        if (!(body.Mass > 0) || !float.IsFinite(body.Mass))
        {
            throw new ArgumentException("The mass of a character is finite and above 0.", nameof(body));
        }
    }

    /// <summary>Stops moving a character: it leaves its support grid where it is in the world and keeps its velocity component; its module moves it from then on.</summary>
    [HotPath]
    private void RemoveCharacterCore(int slot)
    {
        ref CharacterRecord record = ref _characters[slot];
        EntityWorld world = record.World!;
        EntityId entity = record.Entity;
        if (!world.IsDestroyed && world.IsAlive(entity))
        {
            if (record.Support is not null && !world.GetParent(entity).IsNull)
            {
                WorldTransform placed = world.Read(entity, _transform);
                world.ClearParent(entity);
                world.Set(entity, _transform, world.Read(entity, _transform) with { Position = placed.Position });
            }

            if (world.Has(entity, _characterComponent))
            {
                world.Remove(entity, _characterComponent);
            }

            if (world.Has(entity, _characterStateComponent))
            {
                world.Remove(entity, _characterStateComponent);
            }
        }

        FreeCharacter(slot);
    }

    private int AllocateCharacter()
    {
        int slot;
        if (_characterFree >= 0)
        {
            slot = _characterFree;
            _characterFree = _characters[slot].NextFree;
        }
        else
        {
            if (_characterHigh == _characters.Length)
            {
                Array.Resize(ref _characters, _characterHigh * 2);
            }

            slot = _characterHigh++;
        }

        ref CharacterRecord record = ref _characters[slot];
        record.Alive = true;
        record.Generation++;
        _characterCount++;
        return slot;
    }

    [HotPath]
    private void FreeCharacter(int slot)
    {
        ref CharacterRecord record = ref _characters[slot];
        record.Alive = false;
        record.World = null;
        record.Support = null;
        record.NextFree = _characterFree;
        _characterFree = slot;
        _characterCount--;
    }

    /// <summary>Forgets every character, at the end of a world session.</summary>
    private void ResetCharacters()
    {
        for (int i = 0; i < _characterHigh; i++)
        {
            if (_characters[i].Alive)
            {
                FreeCharacter(i);
            }
        }

        _boxCount = 0;
        _characterStepCount = 0;
        _characterLandingCount = 0;
        _characterGrids.Clear();
    }

    /// <summary>A frame in which a character is resolved: the frame of its support grid, or the corner of its chunk without one.</summary>
    private readonly struct Frame
    {
        [HotPath]
        public Frame(WorldPosition origin, Quaternion rotation) => (Origin, Rotation) = (origin, rotation);

        public WorldPosition Origin { get; }

        public Quaternion Rotation { get; }

        [HotPath]
        public WorldPosition ToWorld(Vector3 local) => Origin.Offset(Vector3.Transform(local, Rotation));
    }

    /// <summary>A collision box in the frame of a character, and what it belongs to.</summary>
    private struct CharacterBox
    {
        public Vector3 Min;
        public Vector3 Max;
        public CharacterGround Ground;
    }

    /// <summary>A character that nPhysics moves.</summary>
    private struct CharacterRecord
    {
        public int Generation;
        public bool Alive;
        public int NextFree;
        public EntityWorld? World;
        public DimensionId Dimension;
        public EntityId Entity;

        /// <summary>The support grid, the parent of the entity.</summary>
        public Grid? Support;

        /// <summary>The velocity relative to the frame of the character, along its axes.</summary>
        public Vector3 Velocity;
    }
}
