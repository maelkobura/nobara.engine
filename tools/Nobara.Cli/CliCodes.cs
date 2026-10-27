// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Cli;

/// <summary>The codes of the problems that the tool finds itself; the others come from the resource compiler.</summary>
internal static class CliCodes
{
    /// <summary>A target is for a platform that this version does not build.</summary>
    public const string Platform = "NCLI001";

    /// <summary>Two modules need versions of a library that no single version satisfies.</summary>
    public const string LibraryVersion = "NCLI002";

    /// <summary>A module folder has no project file named after the module.</summary>
    public const string ProjectFile = "NCLI003";

    /// <summary>The module packages cannot be restored.</summary>
    public const string Restore = "NCLI004";
}
