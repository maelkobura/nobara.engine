// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#version 460

// From the HDR scene to the target of the view. The scene is bound by the
// pipeline at sampled texture binding 0.
layout(binding = 0) uniform sampler2D Scene;

layout(std140, binding = 0) uniform Output
{
    float Exposure;
    int EncodeSrgb;
};

layout(location = 0) in vec2 vTexCoord;
layout(location = 0) out vec4 outColor;

// Keeps colors below 0.76 as they are, and compresses brighter ones toward
// white without clipping: the highlight compression of the Khronos PBR
// Neutral tone mapper, without its toe.
vec3 Tonemap(vec3 color)
{
    const float start = 0.76;
    const float desaturation = 0.15;
    float peak = max(color.r, max(color.g, color.b));
    if (peak < start)
    {
        return color;
    }

    const float d = 1.0 - start;
    float newPeak = 1.0 - d * d / (peak + d - start);
    color *= newPeak / peak;
    float g = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);
    return mix(color, vec3(newPeak), g);
}

vec3 EncodeSrgbColor(vec3 color)
{
    return mix(color * 12.92, 1.055 * pow(color, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), color));
}

void main()
{
    vec3 color = max(texture(Scene, vTexCoord).rgb * Exposure, vec3(0.0));
#ifdef UNGP_CLAMP
    color = min(color, vec3(1.0));
#else
    color = Tonemap(color);
#endif
    if (EncodeSrgb != 0)
    {
        color = EncodeSrgbColor(color);
    }

    outColor = vec4(color, 1.0);
}
