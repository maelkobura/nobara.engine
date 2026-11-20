// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Physics;

/// <summary>The names of the configuration keys of nPhysics.</summary>
public static class PhysicsSettings
{
    /// <summary>The name of the physics backend, or <see cref="AutomaticBackend"/>. Scope <c>distribution</c>; read at start.</summary>
    public const string Backend = "physics.backend";

    /// <summary>The number of substeps of each step, from 1 to 32; 8 by default. Scope <c>distribution</c>; read at start.</summary>
    public const string Substeps = "physics.substeps";

    /// <summary>The number of velocity iterations of the solver in each substep, from 1 to 16; 1 by default. Scope <c>distribution</c>; read at start.</summary>
    public const string VelocityIterations = "physics.velocityIterations";

    /// <summary>The value of <see cref="Backend"/> that takes the first backend registered.</summary>
    public const string AutomaticBackend = "auto";
}
