// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Entity;
using Nobara.Physics;
using Nobara.Voxel;

namespace Nobara.Motion;

/// <summary>
/// The crush handlers: Motion is the resolver of crushes of nPhysics, and asks
/// its handlers from the highest priority down; without an answer, the grid
/// is blocked, the default of nPhysics.
/// </summary>
internal sealed partial class MotionSystem : ICrushResolver
{
    /// <summary>The most crushes of a step that Motion keeps for the window of nDebug.</summary>
    public const int KeptCrushes = 32;

    private readonly List<CrushHandlerHandle> _handlers = [];
    private readonly CrushRecord[] _crushes = new CrushRecord[KeptCrushes];
    private CrushResolverHandle? _crushResolver;
    private long _handlerOrder;
    private int _resolving = int.MaxValue;
    private int _crushCount;

    /// <summary>Gets the number of crushes since Motion started.</summary>
    public long Crushes { get; private set; }

    /// <summary>Gets the crushes of the last step, up to <see cref="KeptCrushes"/>.</summary>
    internal ReadOnlySpan<CrushRecord> LastCrushes => new(_crushes, 0, Math.Min(_crushCount, KeptCrushes));

    /// <summary>Gets the number of crushes of the last step.</summary>
    internal int LastCrushCount => _crushCount;

    public CrushHandlerHandle AddCrushHandler(ModuleContext module, ICrushHandler handler, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(handler);
        var handle = new CrushHandlerHandle(module, handler, priority, _handlerOrder++, RemoveHandler);
        int index = 0;
        while (index < _handlers.Count && _handlers[index].Priority >= priority)
        {
            index++;   // after the handlers of a higher or equal priority: those added before it
        }

        _handlers.Insert(index, handle);
        if (index <= _resolving && _resolving != int.MaxValue)
        {
            _resolving++;   // added by a handler while Motion asks them: the handler that runs is not asked again
        }

        return handle;
    }

    /// <summary>Asks the handlers what a crush does: the first that decides wins; without one, the grid is blocked.</summary>
    [HotPath]
    public CrushResponse Resolve(in CharacterCrush crush)
    {
        CrushResponse response = CrushResponse.Block;
        for (_resolving = 0; _resolving < _handlers.Count; _resolving++)
        {
            CrushHandlerHandle handle = _handlers[_resolving];
            try
            {
                if (handle.Handler.TryResolve(crush, out CrushResponse answer))
                {
                    response = answer;
                    break;
                }
            }
            catch (Exception exception)
            {
                handle.Module.ReportFault(exception);
            }
        }

        _resolving = int.MaxValue;
        Crushes++;
        if (_crushCount < KeptCrushes)
        {
            _crushes[_crushCount] = new CrushRecord(crush.Grid, crush.Character, crush.Direction, response.Kind);
        }

        _crushCount++;
        return response;
    }

    /// <summary>Becomes the resolver of crushes of nPhysics.</summary>
    private void StartCrushes() => _crushResolver = _physics!.SetCrushResolver(_context, this);

    /// <summary>Forgets the crushes of the last step, before the next one.</summary>
    [HotPath]
    private void ClearCrushes() => _crushCount = 0;

    private void RemoveHandler(CrushHandlerHandle handle)
    {
        int index = _handlers.IndexOf(handle);
        if (index < 0)
        {
            return;
        }

        _handlers.RemoveAt(index);
        if (index <= _resolving && _resolving != int.MaxValue)
        {
            _resolving--;   // the handler after it is not skipped
        }
    }

    /// <summary>A crush of the last step, for the window of nDebug.</summary>
    /// <param name="Grid">The grid that crushed.</param>
    /// <param name="Character">The character it crushed.</param>
    /// <param name="Direction">The way the grid pushed, in the world.</param>
    /// <param name="Response">What happened.</param>
    [HotPath]
    internal readonly record struct CrushRecord(Grid Grid, EntityId Character, Vector3 Direction, CrushResponseKind Response);
}
