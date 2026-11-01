// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#version 460

layout(binding = 0) uniform sampler2DArray Albedo;
layout(binding = 1) uniform sampler2DArray Normal;

layout(std140, binding = 1) uniform Material
{
    vec4 Tint;
    float AlphaCutoff;
    int AlbedoLayer;
    int NormalLayer;
};

layout(location = 0) in vec2 vTexCoord;
layout(location = 1) in vec3 vNormal;
layout(location = 0) out vec4 outColor;

void main()
{
    vec4 color = texture(Albedo, vec3(vTexCoord, AlbedoLayer)) * Tint;
#ifdef ALPHA_CUTOUT
    if (color.a < AlphaCutoff)
    {
        discard;
    }
#endif
    vec3 normal = NormalLayer >= 0 ? texture(Normal, vec3(vTexCoord, NormalLayer)).xyz * 2.0 - 1.0 : vec3(0.0, 0.0, 1.0);
    outColor = vec4(color.rgb * (0.5 + 0.5 * normal.z), color.a);
}
