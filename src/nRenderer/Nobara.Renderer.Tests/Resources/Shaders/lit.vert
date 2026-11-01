// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#version 460

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inTexCoord;
layout(location = 3) in vec4 inTangent;

layout(std140, binding = 0) uniform Camera
{
    mat4 ViewProjection;
};

layout(location = 0) out vec2 vTexCoord;
layout(location = 1) out vec3 vNormal;

void main()
{
    vTexCoord = inTexCoord;
    vNormal = inNormal;
    gl_Position = ViewProjection * vec4(inPosition, 1.0);
}
