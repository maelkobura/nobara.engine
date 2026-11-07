// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// The vertices of chunk geometry, for the vertex shaders that draw them with
// ChunkGeometry.Draw. Include it with #include <nobara/renderer/chunk.glsl>.
//
// It declares the inputs at locations 0 (the vertex) and 1 (its section, per
// instance), the storage buffer of the groups at binding 7, and the storage
// buffer of the tints at binding 8, that the resource set of
// ChunkGeometry.DrawLayout binds. Call
// nobara_chunkPosition(nobara_ChunkSection, vertex.position) for the position
// relative to the camera, then multiply it by the view and projection.

layout(location = 0) in uvec4 nobara_ChunkVertexData;
layout(location = 1) in uvec4 nobara_ChunkSection;

// A group of sections, as nRenderer writes it every frame: the position of the
// camera in the group (whole chunk and position in the chunk), and the scale
// and rotation of the group.
struct NobaraChunkGroup
{
    ivec4 cameraChunk;
    vec4 cameraLocal;
    mat4 basis;
    ivec4 previousChunk;
    vec4 previousLocal;
    mat4 previousBasis;
};

layout(std430, binding = 7) readonly buffer NobaraChunkGroups
{
    NobaraChunkGroup nobara_chunkGroups[];
};

// The palette of tints of ChunkGeometry.SetTints, in linear colors: index 0 is
// white.
layout(std430, binding = 8) readonly buffer NobaraChunkTints
{
    vec4 nobara_chunkTints[];
};

// A decoded chunk vertex.
struct NobaraChunkVertex
{
    // In blocks from the corner of the chunk.
    vec3 position;
    // One of the six directions of the axes; +Y for faces without one.
    vec3 normal;
    // 0 to 5 for -X, +X, -Y, +Y, -Z, +Z; 6 for no direction.
    uint face;
    vec2 uv;
    uint textureArray;
    uint textureLayer;
    // From 0 to 15.5.
    float skyLight;
    vec3 blockLight;
    // From 0 (none) to 3 (a corner surrounded by blocks).
    uint occlusion;
    // The index of the tint in the palette, 0 (white) to 127.
    uint tint;
};

const float NOBARA_CHUNK_SIZE = 32.0;

NobaraChunkVertex nobara_decodeChunkVertex(uvec4 data)
{
    const float step = 1.0 / 1024.0;
    NobaraChunkVertex v;
    v.position = vec3(float(data.x & 0xFFFFu), float(data.x >> 16), float(data.y & 0xFFFFu)) * step - 16.0;
    v.uv = vec2(float(data.y >> 16), float(data.z & 0xFFFFu)) * step;
    v.textureLayer = (data.z >> 16) & 0x7FFu;
    v.textureArray = data.z >> 27;
    v.face = min(data.w & 7u, 6u);
    const vec3 normals[7] = vec3[7](
        vec3(-1.0, 0.0, 0.0), vec3(1.0, 0.0, 0.0),
        vec3(0.0, -1.0, 0.0), vec3(0.0, 1.0, 0.0),
        vec3(0.0, 0.0, -1.0), vec3(0.0, 0.0, 1.0),
        vec3(0.0, 1.0, 0.0));
    v.normal = normals[v.face];
    v.occlusion = (data.w >> 3) & 3u;
    v.skyLight = float((data.w >> 5) & 31u) * 0.5;
    v.blockLight = vec3(float((data.w >> 10) & 31u), float((data.w >> 15) & 31u), float((data.w >> 20) & 31u)) * 0.5;
    v.tint = data.w >> 25;
    return v;
}

NobaraChunkVertex nobara_chunkVertex()
{
    return nobara_decodeChunkVertex(nobara_ChunkVertexData);
}

// The position relative to the camera, in blocks, of a point of a section:
// the chunk of the section minus the chunk of the camera, in whole numbers,
// then the position in the chunk, so no large coordinate reaches a float.
vec3 nobara_chunkPosition(uvec4 section, vec3 local)
{
    NobaraChunkGroup g = nobara_chunkGroups[section.w];
    vec3 offset = vec3(ivec3(section.xyz) - g.cameraChunk.xyz) * NOBARA_CHUNK_SIZE + (local - g.cameraLocal.xyz);
    return (g.basis * vec4(offset, 0.0)).xyz;
}

// The normal in the frame of the camera, for a group that may be rotated.
vec3 nobara_chunkNormal(uvec4 section, vec3 normal)
{
    return normalize((nobara_chunkGroups[section.w].basis * vec4(normal, 0.0)).xyz);
}

// The tint of a vertex, a linear color that multiplies its texture.
vec3 nobara_chunkTint(NobaraChunkVertex v)
{
    return nobara_chunkTints[v.tint].rgb;
}

// The light of a vertex, from 0 to 1 per channel: the sky light scaled by the
// day and night multiplier, then the brightest of the sky and the blocks.
vec3 nobara_chunkLight(NobaraChunkVertex v, float daylight)
{
    return max(vec3(v.skyLight * daylight), v.blockLight) / 15.0;
}
