// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Motion;

/// <summary>The names of the configuration keys of Motion.</summary>
public static class MotionSettings
{
    /// <summary>The number of blocks under which a grid is a small grid, simulated as a compound of boxes; 0 for none.</summary>
    public const string SmallGridBlocks = "motion.smallGridBlocks";

    /// <summary>The mass, in kilograms, from which a grid tests the corners of its surface cells instead of their boxes; 0 for never.</summary>
    public const string CornerMass = "motion.cornerMass";

    /// <summary>The share of the tick that Motion and the step of nPhysics may take before every grid tests the corners of its surface cells; 0 for never.</summary>
    public const string LoadShare = "motion.loadShare";

    /// <summary>The most tests that a pair of a grid and other voxels makes in a step; 0 for no limit.</summary>
    public const string PairBudget = "motion.pairBudget";

    /// <summary>The slowest approach, in blocks per second, that a grid reports as an impact; 0 for none.</summary>
    public const string ImpactSpeed = "motion.impactSpeed";
}
