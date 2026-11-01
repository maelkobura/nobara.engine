// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#version 460

// The lines of debug drawing: positions relative to the camera, linear colors.
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inColor;

layout(std140, binding = 0) uniform DebugView
{
    mat4 ViewProjection;
};

layout(location = 0) out vec4 vColor;

void main()
{
    vColor = inColor;
    gl_Position = ViewProjection * vec4(inPosition, 1.0);
}
