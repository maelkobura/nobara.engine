// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#version 460

// The standard shader of UNGP: the mesh vertices, the transform of the
// object seen from the camera as instance data, and the view of the frame.
layout(location = 0) in vec3 inPosition;
layout(location = 2) in vec2 inTexCoord;
layout(location = 4) in mat4 inModel;

layout(std140, binding = 0) uniform View
{
    mat4 ViewProjection;
};

layout(location = 0) out vec2 vTexCoord;

// The depth prepass and the opaque pass must compute the same depth.
invariant gl_Position;

void main()
{
    vTexCoord = inTexCoord;
    gl_Position = ViewProjection * (inModel * vec4(inPosition, 1.0));
}
