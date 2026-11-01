// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#version 460

// The vertices of Dear ImGui, in logical units from the top-left corner of
// the display. Colors are sRGB; they are decoded to linear values when the
// target encodes them itself.
layout(std140, binding = 0) uniform Frame
{
    mat4 Projection;
    int DecodeSrgb;
};

layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec2 aTexCoord;
layout(location = 2) in vec4 aColor;

layout(location = 0) out vec2 vTexCoord;
layout(location = 1) out vec4 vColor;

vec3 DecodeSrgbColor(vec3 color)
{
    return mix(color / 12.92, pow((color + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), color));
}

void main()
{
    vTexCoord = aTexCoord;
    vColor = DecodeSrgb != 0 ? vec4(DecodeSrgbColor(aColor.rgb), aColor.a) : aColor;
    gl_Position = Projection * vec4(aPosition, 0.0, 1.0);
}
