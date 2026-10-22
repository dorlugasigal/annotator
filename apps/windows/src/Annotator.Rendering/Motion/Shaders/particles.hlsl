// Dissolve: the Mac's ParticleBurstView shader (apps/macos/Sources/PresentationRendering/ParticleBurstView.swift) for Direct3D 11.
// One draw of the image, which fades from the edge facing the toolbar, then one instanced draw of round particles that
// take their colors from the image and drift out. Positions are in the layer's pixels with y down; the burst's own
// layer motion (Mac: the snapshot view's transform and opacity) is applied here, about the image's center.

cbuffer Burst : register(b0)
{
    float4 viewport;   // xy: layer size in pixels
    float4 image;      // xy: image origin in layer pixels, zw: image size in points
    float4 grid;       // xy: columns and rows, z: pixels per point, w: how far the fastest particle travels in points
    float4 motion;     // x: burst progress (0 whole, 1 gone), y: sweep angle (y up), z: layer opacity
    float4 transform;  // xy: the layer's scale and rotation (a, b) with y down, zw: its translation in pixels
    float4 center;     // xy: the image's center in layer pixels
};

Texture2D<float4> source : register(t0);
SamplerState pixels : register(s0);

float randomValue(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    value ^= value >> 16;
    return float(value & 0xffffffu) / 16777216.0;
}

// 0 on the edge facing the sweep direction, 1 on the far edge. uv.y grows downward.
float sweepAlong(float2 uv)
{
    float angle = motion.y;
    return saturate(0.5 - dot(uv - 0.5, float2(cos(angle), -sin(angle))));
}

float localProgress(float p, float along)
{
    return saturate((p - 0.25 * along) / 0.75);
}

float2 place(float2 position)
{
    float2 offset = position - center.xy;
    return center.xy + float2(transform.x * offset.x - transform.y * offset.y, transform.y * offset.x + transform.x * offset.y) + transform.zw;
}

float4 project(float2 position)
{
    return float4(position.x / viewport.x * 2 - 1, 1 - position.y / viewport.y * 2, 0, 1);
}

struct ImageVertex
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

ImageVertex image_vs(uint id : SV_VertexID)
{
    float2 corner = float2(id & 1, id >> 1);
    ImageVertex output;
    output.uv = corner;
    output.position = project(place(image.xy + corner * image.zw * grid.z));
    return output;
}

float4 image_ps(ImageVertex input) : SV_Target
{
    float p = localProgress(motion.x, sweepAlong(input.uv));
    return source.Sample(pixels, input.uv) * (1 - smoothstep(0.0, 0.18, p)) * motion.z;
}

struct ParticleVertex
{
    float4 position : SV_Position;
    float2 corner : TEXCOORD0;
    float4 color : COLOR0;
};

ParticleVertex particle_vs(uint vertexId : SV_VertexID, uint id : SV_InstanceID)
{
    uint columns = (uint)grid.x;
    float2 cell = float2(id % columns, id / columns);
    float2 uv = (cell + 0.5) / grid.xy;
    float p = localProgress(motion.x, sweepAlong(uv));
    float angle = randomValue(id + 1) * 6.2831853;
    float2 direction = float2(cos(angle), sin(angle));
    float speed = 0.25 + 0.75 * randomValue(id + 9209);
    // Drag: a quick burst that slows down, like a puff.
    float drag = (1 - exp(-3.5 * p)) / (1 - exp(-3.5));
    float2 displacement = direction * grid.w * speed * drag;
    displacement += float2(-direction.y, direction.x) * sin(p * 3.1415927) * 10;
    displacement.y += p * p * 12;
    // Metal's point size: a diameter in drawable pixels, which the layer's own scale then scales.
    float size = (1.5 + 2.0 * randomValue(id + 7919)) * (1 - 0.65 * p) * grid.z * length(transform.xy);
    float opacity = smoothstep(0.0, 0.18, p) * (1 - smoothstep(0.35, 1.0, p));
    float2 corner = float2(vertexId & 1, vertexId >> 1) * 2 - 1;
    ParticleVertex output;
    output.position = project(place(image.xy + (uv * image.zw + displacement) * grid.z) + corner * size * 0.5);
    output.corner = corner;
    output.color = source.SampleLevel(pixels, uv, 0) * opacity * motion.z;
    return output;
}

float4 particle_ps(ParticleVertex input) : SV_Target
{
    float radius = length(input.corner);
    return input.color * (1 - smoothstep(0.6, 1.0, radius));
}
