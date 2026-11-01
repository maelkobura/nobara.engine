// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Configuration;
using Nobara.Core.Profiling;
using Nobara.Debug.Tests.Support;

namespace Nobara.Debug.Tests;

/// <summary>The windows that nDebug provides: the profiler and the settings.</summary>
public sealed class ToolWindowTests : IDisposable
{
    private const string Sensitivity = "input.mouseSensitivity";
    private const string InvertY = "input.invertY";

    private readonly DebugHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private T Tool<T>(string title)
        where T : class, IDebugWindow
    {
        DebugWindow window = _harness.Debug.Windows.Single(w => w.Title == title);
        window.IsOpen = true;
        return Assert.IsType<T>(window.Content);
    }

    [Fact]
    public void The_profiler_window_shows_the_phases_and_the_modules_and_resets()
    {
        using Engine engine = _harness.Start();
        _harness.Debug.IsOpen = true;
        ProfilerWindow profiler = Tool<ProfilerWindow>("Profiler");
        _harness.Frame(engine);
        Assert.Empty(profiler.Entries);

        _harness.GameModule.Context.Profiler.Enabled = true;
        _harness.Frame(engine, 40);

        Assert.Contains(profiler.Entries, e => e is { Kind: ProfileKind.Phase, Name: "Render", Module: "" });
        Assert.Contains(profiler.Entries, e => e is { Kind: ProfileKind.Handler, Name: "Render", Module: "nobara.renderer" });
        Assert.Contains(profiler.Entries, e => e is { Kind: ProfileKind.Handler, Name: "FrameStart", Module: "nobara.input" });
        Assert.Contains(profiler.Entries, e => e is { Kind: ProfileKind.Scope, Name: "Debug window Profiler", Module: DebugModule.ModuleId });
        Assert.Contains(profiler.Entries, e => e is { Kind: ProfileKind.Scope, Name: "Debug frame", Module: DebugModule.ModuleId });

        profiler.SortBy(ProfilerWindow.Column.Calls, descending: true);
        Assert.True(profiler.Entries.Zip(profiler.Entries.Skip(1)).All(p => p.First.Calls >= p.Second.Calls));
        profiler.SortBy(ProfilerWindow.Column.Module, descending: false);
        Assert.True(profiler.Entries.Zip(profiler.Entries.Skip(1)).All(p => string.CompareOrdinal(p.First.Module, p.Second.Module) <= 0));

        profiler.ResetMeasures();
        Assert.Empty(profiler.Entries);
        Assert.Empty(_harness.GameModule.Context.Profiler.Snapshot());
    }

    [Fact]
    public void nDebug_leaves_a_disabled_profiler_off_and_measures_nothing()
    {
        using Engine engine = _harness.Start();
        _harness.Frame(engine, 5);
        _harness.Debug.IsOpen = true;
        Tool<ProfilerWindow>("Profiler");
        _harness.Frame(engine, 40);

        Assert.False(_harness.GameModule.Context.Profiler.Enabled);
        Assert.Empty(_harness.GameModule.Context.Profiler.Snapshot());
    }

    [Fact]
    public void A_frame_without_anything_to_show_allocates_nothing_in_nDebug()
    {
        _harness.Profiling = true;
        using Engine engine = _harness.Start();
        _harness.Device.Recording = false;
        _harness.Frame(engine, 20);
        _harness.GameModule.Context.Profiler.Reset();

        _harness.Frame(engine, 20);

        ProfileEntry handler = Assert.Single(_harness.GameModule.Context.Profiler.Snapshot(), e => e is { Kind: ProfileKind.Handler, Module: DebugModule.ModuleId });
        Assert.Equal(0, handler.AllocatedBytes);
        Assert.DoesNotContain(_harness.GameModule.Context.Profiler.Snapshot(), e => e is { Kind: ProfileKind.Scope, Module: DebugModule.ModuleId });
    }

    [Fact]
    public void The_settings_window_lists_the_keys_of_nDebug_and_of_its_dependencies()
    {
        using Engine engine = _harness.Start();
        _harness.Debug.IsOpen = true;
        SettingsWindow settings = Tool<SettingsWindow>("Settings");
        _harness.Frame(engine, 2);

        string[] keys = [.. settings.Keys.Select(k => k.Key)];
        Assert.Contains(DebugSettings.Tools, keys);
        Assert.Contains(Sensitivity, keys);
        Assert.Contains("graphics.vsync", keys);
        Assert.Contains("frames.mode", keys);
        Assert.Equal(keys.Order(StringComparer.Ordinal).Count(), keys.Distinct().Count());
    }

    [Fact]
    public void A_user_key_changed_in_the_window_applies_at_once_and_is_read_at_the_next_start()
    {
        var changed = new List<string>();
        using (Engine engine = _harness.Start())
        {
            using SettingSubscription subscription = _harness.GameModule.Context.Settings.Subscribe(changed.Add);
            _harness.Debug.IsOpen = true;
            SettingsWindow settings = Tool<SettingsWindow>("Settings");
            _harness.Frame(engine);

            Assert.True(settings.Edit(Sensitivity, "2.5"), settings.Error);
            _harness.Frame(engine);

            Assert.Equal(2.5, _harness.GameModule.Context.Settings.GetFloat(Sensitivity));
            Assert.Contains(Sensitivity, changed);
            Assert.Contains(Sensitivity, File.ReadAllText(_harness.GameModule.Context.Settings.FilePath), StringComparison.Ordinal);
        }

        using (Engine engine = _harness.Start())
        {
            Assert.Equal(2.5, _harness.GameModule.Context.Settings.GetFloat(Sensitivity));
        }
    }

    [Fact]
    public void A_locked_key_and_a_key_of_the_distribution_cannot_be_changed()
    {
        _harness.Locks[InvertY] = "true";
        using Engine engine = _harness.Start();
        _harness.Debug.IsOpen = true;
        SettingsWindow settings = Tool<SettingsWindow>("Settings");
        _harness.Frame(engine);

        Assert.False(SettingsWindow.IsEditable(settings.Keys.Single(k => k.Key == InvertY)));
        Assert.False(settings.Edit(InvertY, "false"));
        Assert.Contains("locks", settings.Error, StringComparison.Ordinal);
        Assert.True(_harness.GameModule.Context.Settings.GetBool(InvertY));

        Assert.False(settings.Edit(DebugSettings.Tools, DebugSettings.ToolsNever));
        Assert.False(File.Exists(_harness.GameModule.Context.Settings.FilePath));
    }

    [Fact]
    public void A_value_out_of_bounds_is_refused_with_a_message()
    {
        using Engine engine = _harness.Start();
        _harness.Debug.IsOpen = true;
        SettingsWindow settings = Tool<SettingsWindow>("Settings");
        _harness.Frame(engine);

        Assert.False(settings.Edit(Sensitivity, "1000"));
        Assert.NotNull(settings.Error);
        Assert.Equal(1, _harness.GameModule.Context.Settings.GetFloat(Sensitivity));
    }
}
