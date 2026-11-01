// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#version 460

// One triangle that covers the viewport, at depth 0: the far plane of a
// reversed depth.
layout(location = 0) out vec2 vTexCoord;

void main()
{
    vec2 position = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0;
    vTexCoord = position * 0.5 + 0.5;
    gl_Position = vec4(position, 0.0, 1.0);
}
