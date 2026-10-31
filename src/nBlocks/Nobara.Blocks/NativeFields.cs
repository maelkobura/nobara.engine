// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Blocks;

/// <summary>What a block collides with: the value of <c>&lt;Collision&gt;</c>.</summary>
public enum BlockCollision : byte
{
    /// <summary><c>shape</c>: the collision of the shape of the block.</summary>
    Shape,

    /// <summary><c>none</c>: nothing collides with the block, such as tall grass or a flower.</summary>
    None,
}

/// <summary>How a block hides what is behind it: the value of <c>&lt;Opacity&gt;</c>.</summary>
public enum BlockOpacity : byte
{
    /// <summary><c>opaque</c>: the block hides the faces of its neighbors.</summary>
    Opaque,

    /// <summary><c>cutout</c>: holes without blending, such as leaves; the faces of the neighbors stay.</summary>
    Cutout,

    /// <summary><c>translucent</c>: drawn with blending, such as tinted glass.</summary>
    Translucent,

    /// <summary>Nothing to draw: the opacity of <c>block:nobara.air</c> only, which a <c>.nof</c> file cannot write.</summary>
    Transparent,
}

/// <summary>The kind of a state property.</summary>
public enum StatePropertyKind
{
    /// <summary>The orientation, managed by the engine: <c>orientation</c>, from <c>&lt;Orientations&gt;</c>.</summary>
    Orientation,

    /// <summary>The index of the cell in a block of several cells, managed by the engine: <c>part</c>, from <c>&lt;Footprint&gt;</c>; 0 is the origin.</summary>
    Part,

    /// <summary>A boolean: <c>&lt;Bool&gt;</c>, values <c>false</c> then <c>true</c>.</summary>
    Bool,

    /// <summary>A bounded whole number: <c>&lt;Int&gt;</c>, values from its minimum to its maximum.</summary>
    Int,

    /// <summary>One value of a list: <c>&lt;Enum&gt;</c>, values in the order written.</summary>
    Enum,
}
