// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#version 460

// The font atlas, bound by the pass at sampled texture binding 0.
layout(binding = 0) uniform sampler2D Atlas;

layout(location = 0) in vec2 vTexCoord;
layout(location = 1) in vec4 vColor;
layout(location = 0) out vec4 outColor;

void main()
{
    outColor = vColor * texture(Atlas, vTexCoord);
}
