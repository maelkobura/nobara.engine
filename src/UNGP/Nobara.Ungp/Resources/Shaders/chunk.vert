// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#version 460

// The chunk shader of UNGP: it decodes the chunk vertex of nRenderer, and
// gives the fragment stage its texture and its shade, which is the tint times
// the brightness of the vertex: its light, its ambient occlusion, and the
// shade of its face.
#include <nobara/renderer/chunk.glsl>

layout(std140, binding = 0) uniform View
{
    mat4 ViewProjection;
};

layout(location = 0) out vec2 vTexCoord;
layout(location = 1) flat out uint vArray;
layout(location = 2) flat out uint vLayer;
layout(location = 3) out vec3 vShade;

// The depth prepass and the opaque pass must compute the same depth.
invariant gl_Position;

// By face: -X, +X, -Y, +Y, -Z, +Z, then faces without a direction.
const float FACE_SHADES[7] = float[7](0.6, 0.6, 0.5, 1.0, 0.8, 0.8, 1.0);

// By ambient occlusion, from none to a corner surrounded by blocks.
const float OCCLUSION[4] = float[4](1.0, 0.8, 0.6, 0.4);

void main()
{
    NobaraChunkVertex v = nobara_chunkVertex();
    vTexCoord = v.uv;
    vArray = v.textureArray;
    vLayer = v.textureLayer;

    // The brightness is a value as the screen shows it; the scene is linear.
    vec3 brightness = min(nobara_chunkLight(v, 1.0), vec3(1.0)) * FACE_SHADES[v.face] * OCCLUSION[v.occlusion];
    vShade = nobara_chunkTint(v) * pow(brightness, vec3(2.2));
    gl_Position = ViewProjection * vec4(nobara_chunkPosition(nobara_ChunkSection, v.position), 1.0);
}
