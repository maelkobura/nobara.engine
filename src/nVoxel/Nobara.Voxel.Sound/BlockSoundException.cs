// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Voxel.Sound;

/// <summary>A sound property of a block that cannot be used, such as a sound event that no module provides.</summary>
public sealed class BlockSoundException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="problems">The problems, one per line, each with the file and line of the property.</param>
    public BlockSoundException(IReadOnlyList<string> problems)
        : base("The sounds of blocks cannot be used:" + Environment.NewLine + string.Join(Environment.NewLine, problems))
    {
        Problems = problems;
    }

    /// <summary>Gets the problems, one per property.</summary>
    public IReadOnlyList<string> Problems { get; }
}
