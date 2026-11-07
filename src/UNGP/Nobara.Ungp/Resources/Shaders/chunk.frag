// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#version 460

// The texture arrays of nRenderer, by index, which the pipeline binds; an
// index without an array reads white.
layout(binding = 0) uniform sampler2DArray BlockTextures[16];

layout(location = 0) in vec2 vTexCoord;
layout(location = 1) flat in uint vArray;
layout(location = 2) flat in uint vLayer;
layout(location = 3) in vec3 vShade;
layout(location = 0) out vec4 outColor;

// An array of samplers is indexed by constants only, since the index changes
// from one face to the next; the derivatives are taken before the branch.
vec4 blockTexel(uint array, vec3 coordinate, vec2 dx, vec2 dy)
{
    switch (array)
    {
        case 0u: return textureGrad(BlockTextures[0], coordinate, dx, dy);
        case 1u: return textureGrad(BlockTextures[1], coordinate, dx, dy);
        case 2u: return textureGrad(BlockTextures[2], coordinate, dx, dy);
        case 3u: return textureGrad(BlockTextures[3], coordinate, dx, dy);
        case 4u: return textureGrad(BlockTextures[4], coordinate, dx, dy);
        case 5u: return textureGrad(BlockTextures[5], coordinate, dx, dy);
        case 6u: return textureGrad(BlockTextures[6], coordinate, dx, dy);
        case 7u: return textureGrad(BlockTextures[7], coordinate, dx, dy);
        case 8u: return textureGrad(BlockTextures[8], coordinate, dx, dy);
        case 9u: return textureGrad(BlockTextures[9], coordinate, dx, dy);
        case 10u: return textureGrad(BlockTextures[10], coordinate, dx, dy);
        case 11u: return textureGrad(BlockTextures[11], coordinate, dx, dy);
        case 12u: return textureGrad(BlockTextures[12], coordinate, dx, dy);
        case 13u: return textureGrad(BlockTextures[13], coordinate, dx, dy);
        case 14u: return textureGrad(BlockTextures[14], coordinate, dx, dy);
        case 15u: return textureGrad(BlockTextures[15], coordinate, dx, dy);
        default: return vec4(1.0);
    }
}

void main()
{
    vec2 dx = dFdx(vTexCoord);
    vec2 dy = dFdy(vTexCoord);
    vec4 texel = blockTexel(vArray, vec3(vTexCoord, float(vLayer)), dx, dy);
#ifdef UNGP_CUTOUT
    if (texel.a < 0.5)
    {
        discard;
    }
#endif
    outColor = vec4(texel.rgb * vShade, texel.a);
}
