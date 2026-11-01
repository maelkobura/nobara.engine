// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Nobara.Core.Profiling;

namespace Nobara.Debug;

/// <summary>
/// The window of the profiler of the process: one row per marker, by context,
/// module, kind, and name, sorted by any column. It reads the measures twice a
/// second while it shows, and turns the profiler on and off, and resets it.
/// </summary>
internal sealed class ProfilerWindow(ModuleProfiler profiler) : IDebugWindow
{
    /// <summary>The time between two readings of the profiler, in seconds.</summary>
    public const double RefreshInterval = 0.5;

    private const ImGuiTableFlags TableFlags = ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg
        | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;

    private static readonly ImmutableArray<string> Headers = ["Context", "Module", "Kind", "Name", "Calls", "Total (ms)", "Mean (ms)", "Max (ms)", "Allocated (KB)", "GC (ms)"];

    private List<ProfileEntry> _entries = [];
    private string _filter = string.Empty;
    private bool _paused;
    private double _nextRefresh;
    private Column _sort = Column.Total;
    private bool _descending = true;

    /// <summary>The columns of the table, in order.</summary>
    internal enum Column
    {
        Context,
        Module,
        Kind,
        Name,
        Calls,
        Total,
        Mean,
        Max,
        Allocated,
        GcPause,
    }

    /// <summary>Gets the rows of the last reading, in the order of the table, for tests.</summary>
    internal IReadOnlyList<ProfileEntry> Entries => _entries;

    public void Draw(DebugWindow window)
    {
        bool enabled = profiler.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            profiler.Enabled = enabled;
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset"))
        {
            ResetMeasures();
        }

        ImGui.SameLine();
        ImGui.Checkbox("Paused", ref _paused);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##filter", "Filter", ref _filter, 128);
        if (!enabled)
        {
            ImGui.TextDisabled("The profiler is off: turn it on to measure.");
        }

        double now = ImGui.GetTime();
        if (!_paused && now >= _nextRefresh)
        {
            Refresh();
            _nextRefresh = now + RefreshInterval;
        }

        if (!ImGui.BeginTable("entries", Headers.Length, TableFlags, Vector2.Zero))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        for (int i = 0; i < Headers.Length; i++)
        {
            ImGuiTableColumnFlags flags = (Column)i == Column.Total
                ? ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending
                : i >= (int)Column.Calls ? ImGuiTableColumnFlags.PreferSortDescending : ImGuiTableColumnFlags.None;
            ImGui.TableSetupColumn(Headers[i], flags);
        }

        ImGui.TableHeadersRow();
        ReadSortSpecs();
        foreach (ProfileEntry entry in _entries)
        {
            if (_filter.Length > 0 && !Matches(entry, _filter))
            {
                continue;
            }

            ImGui.TableNextRow();
            Cell(entry.Context);
            Cell(entry.Module.Length == 0 ? "(whole context)" : entry.Module);
            Cell(entry.Kind.ToString());
            Cell(entry.Name);
            Cell(entry.Calls.ToString(CultureInfo.InvariantCulture));
            Cell(Milliseconds(entry.Time));
            Cell(Milliseconds(entry.Calls == 0 ? TimeSpan.Zero : entry.Time / entry.Calls));
            Cell(Milliseconds(entry.MaxTime));
            Cell((entry.AllocatedBytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture));
            Cell(Milliseconds(entry.GcPause));
        }

        ImGui.EndTable();
    }

    /// <summary>Sets the measures of the profiler back to zero, and reads them again, as the button of the window does.</summary>
    internal void ResetMeasures()
    {
        profiler.Reset();
        Refresh();
    }

    /// <summary>Reads the profiler again, and sorts the rows.</summary>
    internal void Refresh()
    {
        _entries = [.. profiler.Snapshot()];
        Sort();
    }

    /// <summary>Sorts the rows by a column, for the table and for tests.</summary>
    internal void SortBy(Column column, bool descending)
    {
        _sort = column;
        _descending = descending;
        Sort();
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }

    private static string Milliseconds(TimeSpan time) => time.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture);

    private static bool Matches(ProfileEntry entry, string filter) =>
        entry.Module.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || entry.Context.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || entry.Kind.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static int Compare(ProfileEntry a, ProfileEntry b, Column column) => column switch
    {
        Column.Context => string.CompareOrdinal(a.Context, b.Context),
        Column.Module => string.CompareOrdinal(a.Module, b.Module),
        Column.Kind => a.Kind.CompareTo(b.Kind),
        Column.Name => string.CompareOrdinal(a.Name, b.Name),
        Column.Calls => a.Calls.CompareTo(b.Calls),
        Column.Total => a.Time.CompareTo(b.Time),
        Column.Mean => (a.Calls == 0 ? TimeSpan.Zero : a.Time / a.Calls).CompareTo(b.Calls == 0 ? TimeSpan.Zero : b.Time / b.Calls),
        Column.Max => a.MaxTime.CompareTo(b.MaxTime),
        Column.Allocated => a.AllocatedBytes.CompareTo(b.AllocatedBytes),
        _ => a.GcPause.CompareTo(b.GcPause),
    };

    private unsafe void ReadSortSpecs()
    {
        ImGuiTableSortSpecsPtr specs = ImGui.TableGetSortSpecs();
        if (specs.NativePtr == null || !specs.SpecsDirty || specs.SpecsCount == 0)
        {
            return;
        }

        SortBy((Column)specs.Specs.ColumnIndex, specs.Specs.SortDirection == ImGuiSortDirection.Descending);
        specs.SpecsDirty = false;
    }

    // The sort is stable: ties keep the order of the profiler, by context, kind, module, and name.
    private void Sort()
    {
        Column column = _sort;
        int sign = _descending ? -1 : 1;
        _entries = [.. _entries.OrderBy(e => e, Comparer<ProfileEntry>.Create((a, b) => sign * Compare(a, b, column)))];
    }
}
