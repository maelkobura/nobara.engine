// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Ungp.Tests.Support;

/// <summary>The tests that open a window and an OpenGL context run one at a time, alone.</summary>
[CollectionDefinition(nameof(GpuCollection), DisableParallelization = true)]
public sealed class GpuCollection
{
}
