// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Nobara.Core;
using Nobara.Core.Configuration;

namespace Nobara.Debug;

/// <summary>
/// The window of the configuration keys that nDebug reads: its own and those
/// of its dependencies. It shows the value, the scope, and the default of each
/// key, and edits the keys of scope <c>user</c> that the distribution does not
/// lock: a change applies at once, through the notifications of the core,
/// and is saved to the settings file of the player.
/// </summary>
internal sealed class SettingsWindow(ModuleContext context) : IDebugWindow
{
    private const ImGuiTableFlags TableFlags = ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg
        | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

    private static readonly Vector4 ErrorColor = new(1, 0.4f, 0.4f, 1);

    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private SettingInfo[]? _keys;
    private string? _error;

    /// <summary>Gets the last error of a change or of a save, for tests.</summary>
    internal string? Error => _error;

    /// <summary>Gets the keys the window lists, by module then key.</summary>
    internal IReadOnlyList<SettingInfo> Keys => _keys ??= [.. context.Settings.Keys.OrderBy(k => k.ModuleId, StringComparer.Ordinal).ThenBy(k => k.Key, StringComparer.Ordinal)];

    public void Draw(DebugWindow window)
    {
        ImGui.TextWrapped("Keys of scope user apply at once and are saved to " + context.Settings.FilePath + ".");
        if (_error is not null)
        {
            ImGui.TextColored(ErrorColor, _error);
        }

        if (!ImGui.BeginTable("keys", 5, TableFlags, Vector2.Zero))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.None, 3);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.None, 3);
        ImGui.TableSetupColumn("Scope", ImGuiTableColumnFlags.None, 1.5f);
        ImGui.TableSetupColumn("Default", ImGuiTableColumnFlags.None, 1.5f);
        ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.None, 1);
        ImGui.TableHeadersRow();
        foreach (SettingInfo info in Keys)
        {
            ImGui.PushID(info.Key);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(info.Key);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Declared by " + info.ModuleId);
            }

            bool editable = IsEditable(info);
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            ImGui.BeginDisabled(!editable);
            Editor(info);
            ImGui.EndDisabled();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(info.Scope == SettingScope.User ? info.IsLocked ? "user, locked" : "user" : "distribution");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(info.Default);
            ImGui.TableNextColumn();
            if (editable && Current(info) != info.Default && ImGui.SmallButton("Reset"))
            {
                Apply(() => context.Settings.Reset(info.Key), save: true);
                _texts.Remove(info.Key);
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    /// <summary>Whether the window edits a key: scope <c>user</c>, not locked by the distribution.</summary>
    internal static bool IsEditable(SettingInfo info) => info.Scope == SettingScope.User && !info.IsLocked;

    /// <summary>Changes the value of a key from its text, as the editor of the window does, and saves the settings file.</summary>
    /// <returns><see langword="true"/> when the value changed.</returns>
    internal bool Edit(string key, string text)
    {
        SettingInfo info = context.Settings.GetInfo(key);
        if (!IsEditable(info))
        {
            _error = $"'{key}' cannot be changed: " + (info.Scope == SettingScope.Distribution ? "only the distribution sets it." : "the distribution locks it.");
            return false;
        }

        return Apply(
            () =>
            {
                switch (info.Type)
                {
                    case SettingType.Bool:
                        context.Settings.Set(key, bool.Parse(text));
                        break;
                    case SettingType.Int:
                        context.Settings.Set(key, int.Parse(text, CultureInfo.InvariantCulture));
                        break;
                    case SettingType.Float:
                        context.Settings.Set(key, double.Parse(text, CultureInfo.InvariantCulture));
                        break;
                    default:
                        context.Settings.Set(key, text);
                        break;
                }
            },
            save: true);
    }

    private string Current(SettingInfo info) => info.Type switch
    {
        SettingType.Bool => context.Settings.GetBool(info.Key) ? "true" : "false",
        SettingType.Int => context.Settings.GetInt(info.Key).ToString(CultureInfo.InvariantCulture),
        SettingType.Float => context.Settings.GetFloat(info.Key).ToString("R", CultureInfo.InvariantCulture),
        SettingType.Enum => context.Settings.GetEnum(info.Key),
        _ => context.Settings.GetString(info.Key),
    };

    private void Editor(SettingInfo info)
    {
        string key = info.Key;
        switch (info.Type)
        {
            case SettingType.Bool:
            {
                bool value = context.Settings.GetBool(key);
                if (ImGui.Checkbox("##value", ref value))
                {
                    Apply(() => context.Settings.Set(key, value), save: true);
                }

                break;
            }

            case SettingType.Int:
            {
                int value = context.Settings.GetInt(key);
                bool changed = info.Min is double min && info.Max is double max
                    ? ImGui.SliderInt("##value", ref value, (int)min, (int)max)
                    : ImGui.InputInt("##value", ref value);
                if (changed)
                {
                    Apply(() => context.Settings.Set(key, Clamp(info, value)), save: false);
                }

                SaveAfterEdit();
                break;
            }

            case SettingType.Float:
            {
                double value = context.Settings.GetFloat(key);
                bool changed;
                if (info.Min is double min && info.Max is double max)
                {
                    float single = (float)value;
                    changed = ImGui.SliderFloat("##value", ref single, (float)min, (float)max, "%.4g");
                    value = single;
                }
                else
                {
                    changed = ImGui.InputDouble("##value", ref value, 0, 0, "%.6g");
                }

                if (changed)
                {
                    Apply(() => context.Settings.Set(key, Clamp(info, value)), save: false);
                }

                SaveAfterEdit();
                break;
            }

            case SettingType.Enum:
            {
                string current = context.Settings.GetEnum(key);
                if (ImGui.BeginCombo("##value", current))
                {
                    foreach (string value in info.Values)
                    {
                        if (ImGui.Selectable(value, value == current) && value != current)
                        {
                            Apply(() => context.Settings.Set(key, value), save: true);
                        }
                    }

                    ImGui.EndCombo();
                }

                break;
            }

            default:
            {
                if (!_texts.TryGetValue(key, out string? text))
                {
                    text = context.Settings.GetString(key);
                }

                if (ImGui.InputText("##value", ref text, 256))
                {
                    _texts[key] = text;
                }

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    Apply(() => context.Settings.Set(key, text), save: true);
                    _texts.Remove(key);
                }

                break;
            }
        }
    }

    private static int Clamp(SettingInfo info, int value) =>
        (int)Math.Clamp(value, info.Min ?? int.MinValue, info.Max ?? int.MaxValue);

    private static double Clamp(SettingInfo info, double value) =>
        Math.Clamp(value, info.Min ?? double.MinValue, info.Max ?? double.MaxValue);

    // A slider or a number field changes the value while it is edited, and saves once the edit ends.
    private void SaveAfterEdit()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Apply(() => { }, save: true);
        }
    }

    private bool Apply(Action change, bool save)
    {
        try
        {
            change();
            if (save)
            {
                context.Settings.Save();
            }

            _error = null;
            return true;
        }
        catch (Exception exception) when (exception is SettingException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
        {
            _error = exception.Message;
            return false;
        }
    }
}
