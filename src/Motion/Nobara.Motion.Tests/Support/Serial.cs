// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Motion.Tests.Support;

/// <summary>
/// The tests that count the managed memory of the whole process, or the
/// threads it runs on: they run alone, while no other test of the assembly
/// allocates or uses the workers.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Serial
{
    /// <summary>The name of the collection.</summary>
    public const string Name = "Serial";
}
