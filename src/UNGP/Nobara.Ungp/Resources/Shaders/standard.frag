// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#version 460

// Full brightness: the albedo times the color, without lighting.
layout(binding = 0) uniform sampler2DArray Albedo;

layout(std140, binding = 1) uniform Material
{
    vec4 Color;
    float AlphaCutoff;
    int AlbedoLayer;
};

layout(location = 0) in vec2 vTexCoord;
layout(location = 0) out vec4 outColor;

void main()
{
    vec4 color = texture(Albedo, vec3(vTexCoord, max(AlbedoLayer, 0))) * Color;
#ifdef UNGP_CUTOUT
    if (color.a < AlphaCutoff)
    {
        discard;
    }
#endif
    outColor = vec4(color.rgb, 1.0);
}
