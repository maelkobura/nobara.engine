// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Nobara.Core.Profiling;

/// <summary>What a measure covers.</summary>
public enum ProfileKind
{
    /// <summary>A whole phase of the loop of a context, every module included. The module of the entry is empty.</summary>
    Phase,

    /// <summary>The handlers of one module in one phase, or at the start of the frame (<c>FrameStart</c>).</summary>
    Handler,

    /// <summary>The callbacks of one module that the synchronization point runs.</summary>
    Synchronization,

    /// <summary>The executions of one job type of one module, on any thread.</summary>
    Job,

    /// <summary>A scope that a module measures itself, with a marker it creates.</summary>
    Scope,
}

/// <summary>What the profiler measured for one marker since it was last reset.</summary>
/// <param name="Context">The context: <c>client</c> or <c>server</c>.</param>
/// <param name="Module">The module, or an empty string for a measure of the whole context.</param>
/// <param name="Kind">What the measure covers.</param>
/// <param name="Name">The phase, the job type, or the name of the scope.</param>
/// <param name="Calls">The number of measured runs.</param>
/// <param name="Time">The total time of the runs, measured on the thread that ran them.</param>
/// <param name="MaxTime">The longest run.</param>
/// <param name="AllocatedBytes">The managed memory that the runs allocated on their thread.</param>
/// <param name="GcPause">
/// The time the garbage collector paused the process during the runs. A pause
/// counts for every scope it falls in, whatever thread caused it.
/// </param>
public sealed record ProfileEntry(
    string Context,
    string Module,
    ProfileKind Kind,
    string Name,
    long Calls,
    TimeSpan Time,
    TimeSpan MaxTime,
    long AllocatedBytes,
    TimeSpan GcPause);

/// <summary>
/// Measures, for each context, module, phase, and job, the time spent, the
/// managed memory allocated, and the pauses of the garbage collector. There
/// is one profiler per process.
/// </summary>
/// <remarks>
/// <para>
/// The core measures phases, phase handlers, callbacks of the synchronization
/// point, and jobs. A module measures its own scopes with
/// <see cref="ModuleProfiler.CreateMarker"/>.
/// </para>
/// <para>
/// A disabled profiler costs one test per measured scope. An enabled one
/// reads a timestamp, the bytes allocated by the thread, and the total pause
/// time of the garbage collector at the start and the end of each scope, and
/// allocates nothing. Measures include the scopes nested in them.
/// </para>
/// </remarks>
public sealed class Profiler
{
    private readonly Dictionary<(string Context, string Module, ProfileKind Kind, string Name), ProfilerMarker> _markers = [];
    private readonly Lock _gate = new();
    private volatile bool _enabled;

    internal Profiler(bool enabled)
    {
        _enabled = enabled;
    }

    /// <summary>Gets or sets whether the profiler measures. It can be changed at any time, from any thread.</summary>
    public bool Enabled
    {
        [HotPath]
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Gets what was measured since the last reset, for each marker that ran at least once.</summary>
    /// <returns>The entries, ordered by context, kind, module, and name.</returns>
    public IReadOnlyList<ProfileEntry> Snapshot()
    {
        ProfilerMarker[] markers;
        lock (_gate)
        {
            markers = [.. _markers.Values];
        }

        return markers
            .Select(m => m.ToEntry())
            .Where(e => e.Calls > 0)
            .OrderBy(e => e.Context, StringComparer.Ordinal)
            .ThenBy(e => e.Kind)
            .ThenBy(e => e.Module, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Sets every measure back to zero.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            foreach (ProfilerMarker marker in _markers.Values)
            {
                marker.Reset();
            }
        }
    }

    /// <summary>Formats what was measured since the last reset as a text table, one line per entry.</summary>
    /// <returns>The table.</returns>
    public string FormatReport()
    {
        var text = new StringBuilder();
        text.AppendLine("Context  Kind             Module                    Name                      Calls      Total ms   Max ms     Allocated  GC ms");
        foreach (ProfileEntry entry in Snapshot())
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{entry.Context,-8} {entry.Kind,-16} {entry.Module,-25} {entry.Name,-25} {entry.Calls,-10} {entry.Time.TotalMilliseconds,-10:F3} {entry.MaxTime.TotalMilliseconds,-10:F3} {entry.AllocatedBytes,-10} {entry.GcPause.TotalMilliseconds:F3}");
        }

        return text.ToString();
    }

    // One marker per context, module, kind, and name: two subscriptions of a
    // module to one phase add up.
    internal ProfilerMarker CreateMarker(string context, string module, ProfileKind kind, string name)
    {
        lock (_gate)
        {
            if (!_markers.TryGetValue((context, module, kind, name), out ProfilerMarker? marker))
            {
                marker = new ProfilerMarker(this, context, module, kind, name);
                _markers.Add((context, module, kind, name), marker);
            }

            return marker;
        }
    }
}

/// <summary>
/// One thing that the profiler measures. Create it once, outside the hot
/// path, then measure each run with <see cref="Begin"/>.
/// </summary>
/// <example>
/// <code>
/// _growth = context.Profiler.CreateMarker("Crop growth");
/// // In a phase handler:
/// using (_growth.Begin())
/// {
///     GrowCrops();
/// }
/// </code>
/// </example>
public sealed class ProfilerMarker
{
    private readonly Profiler _profiler;
    private long _calls;
    private long _time;
    private long _maxTime;
    private long _allocated;
    private long _gcPause;

    internal ProfilerMarker(Profiler profiler, string context, string module, ProfileKind kind, string name)
    {
        _profiler = profiler;
        Context = context;
        Module = module;
        Kind = kind;
        Name = name;
    }

    /// <summary>Gets the context: <c>client</c> or <c>server</c>.</summary>
    public string Context { get; }

    /// <summary>Gets the module, or an empty string for a measure of the whole context.</summary>
    public string Module { get; }

    /// <summary>Gets what the marker measures.</summary>
    public ProfileKind Kind { get; }

    /// <summary>Gets the phase, the job type, or the name of the scope.</summary>
    public string Name { get; }

    /// <summary>Starts measuring one run. Dispose the scope at the end of the run, on the same thread.</summary>
    /// <returns>The scope; it measures nothing when the profiler is disabled.</returns>
    [HotPath]
    public ProfilerScope Begin() => _profiler.Enabled
        ? new ProfilerScope(this, Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread(), GC.GetTotalPauseDuration().Ticks)
        : default;

    [HotPath]
    internal void Record(long elapsed, long allocated, long gcPause)
    {
        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _time, elapsed);
        Interlocked.Add(ref _allocated, allocated);
        Interlocked.Add(ref _gcPause, gcPause);
        long max = Volatile.Read(ref _maxTime);
        while (elapsed > max)
        {
            long seen = Interlocked.CompareExchange(ref _maxTime, elapsed, max);
            if (seen == max)
            {
                break;
            }

            max = seen;
        }
    }

    internal ProfileEntry ToEntry() => new(
        Context,
        Module,
        Kind,
        Name,
        Interlocked.Read(ref _calls),
        Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _time)),
        Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _maxTime)),
        Interlocked.Read(ref _allocated),
        TimeSpan.FromTicks(Interlocked.Read(ref _gcPause)));

    internal void Reset()
    {
        Interlocked.Exchange(ref _calls, 0);
        Interlocked.Exchange(ref _time, 0);
        Interlocked.Exchange(ref _maxTime, 0);
        Interlocked.Exchange(ref _allocated, 0);
        Interlocked.Exchange(ref _gcPause, 0);
    }
}

/// <summary>
/// One run being measured, from <see cref="ProfilerMarker.Begin"/> until it
/// is disposed. It is a value: measuring allocates nothing.
/// </summary>
public readonly struct ProfilerScope : IDisposable
{
    private readonly ProfilerMarker? _marker;
    private readonly long _start;
    private readonly long _allocated;
    private readonly long _gcPause;

    [HotPath]
    internal ProfilerScope(ProfilerMarker marker, long start, long allocated, long gcPause)
    {
        _marker = marker;
        _start = start;
        _allocated = allocated;
        _gcPause = gcPause;
    }

    /// <summary>Ends the run and adds it to the measures of its marker.</summary>
    [HotPath]
    public void Dispose() => _marker?.Record(
        Stopwatch.GetTimestamp() - _start,
        GC.GetAllocatedBytesForCurrentThread() - _allocated,
        GC.GetTotalPauseDuration().Ticks - _gcPause);
}

/// <summary>
/// The profiler as one module sees it: it creates the markers of the module
/// in its context, and reads the profiler of the process, for a tool that
/// shows the measures.
/// </summary>
/// <remarks>
/// Every module may read the profiler, turn it on or off, and reset it: the
/// measures change nothing in the game. Turning it on or resetting it applies
/// to the whole process, whoever reads it.
/// </remarks>
public sealed class ModuleProfiler
{
    private readonly Profiler _profiler;
    private readonly string _context;
    private readonly string _module;

    internal ModuleProfiler(Profiler profiler, string context, string module)
    {
        _profiler = profiler;
        _context = context;
        _module = module;
    }

    /// <summary>
    /// Gets or sets whether the profiler of the process measures. It can be
    /// changed at any time, from any thread, and applies to every context.
    /// </summary>
    public bool Enabled
    {
        [HotPath]
        get => _profiler.Enabled;
        set => _profiler.Enabled = value;
    }

    /// <summary>Gets what the profiler of the process measured since the last reset, in every context.</summary>
    /// <returns>The entries of the markers that ran at least once, ordered by context, kind, module, and name.</returns>
    /// <remarks>It allocates the list and the entries: call it for a tool, not at every frame of a game.</remarks>
    public IReadOnlyList<ProfileEntry> Snapshot() => _profiler.Snapshot();

    /// <summary>Sets every measure of the process back to zero, in every context.</summary>
    public void Reset() => _profiler.Reset();

    /// <summary>Formats what the profiler of the process measured since the last reset as a text table.</summary>
    /// <returns>The table, one line per entry.</returns>
    public string FormatReport() => _profiler.FormatReport();

    /// <summary>Creates a marker for a scope of the module. Create it once, during registration or initialization.</summary>
    /// <param name="name">The name of the scope in reports, such as the name of a system.</param>
    /// <returns>The marker. The same name gives the same marker.</returns>
    public ProfilerMarker CreateMarker(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _profiler.CreateMarker(_context, _module, ProfileKind.Scope, name);
    }
}
