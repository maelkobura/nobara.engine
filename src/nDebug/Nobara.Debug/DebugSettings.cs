// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Debug;

/// <summary>The names and the values of the configuration keys of nDebug.</summary>
public static class DebugSettings
{
    /// <summary>
    /// When the debug tools are available: <see cref="ToolsDevelopment"/>,
    /// <see cref="ToolsAlways"/>, or <see cref="ToolsNever"/>. Scope
    /// <c>distribution</c>; read when nDebug starts. Screens show whatever its
    /// value.
    /// </summary>
    public const string Tools = "debug.tools";

    /// <summary>The value of <see cref="Tools"/> that makes the tools available in development mode only. The default.</summary>
    public const string ToolsDevelopment = "development";

    /// <summary>The value of <see cref="Tools"/> that makes the tools available in every build.</summary>
    public const string ToolsAlways = "always";

    /// <summary>The value of <see cref="Tools"/> that makes the tools never available.</summary>
    public const string ToolsNever = "never";
}
