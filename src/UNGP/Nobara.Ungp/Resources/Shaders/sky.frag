// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#version 460

// A plain sky, in linear HDR color.
layout(std140, binding = 0) uniform Sky
{
    vec4 Color;
};

layout(location = 0) out vec4 outColor;

void main()
{
    outColor = vec4(Color.rgb, 1.0);
}
