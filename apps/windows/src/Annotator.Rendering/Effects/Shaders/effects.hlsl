// Annotator pixel effects (WIN-REND-02), compiled at build time with fxc (shader model 5.0) and embedded.
// Every pass draws a full-screen triangle into a viewport; the pixel shader maps its render-target pixel
// (SV_Position) linearly to a source texel coordinate. Display coordinates are device pixels with a top-left
// origin. EffectPipeline.cs sets the constants.
//
// The formulas reproduce the Core Image output in the Mac conformance corpus (reference/conformance/effects, S-13):
// - CIGaussianBlur is an exact Gaussian with a standard deviation of its radius, in linear light, over the scene
//   clamped to its extent. Large blurs here average 2 x 2 blocks into smaller levels first.
// - CIPixellate shows, for each cell, a bilinear sample at the cell's center; the grid passes through the region
//   center, and cells are counted in Core Image's upward y axis.
// - CITwirlDistortion turns each sample point around the center by angle x (1 - smoothstep(0, 1, r / radius)),
//   counterclockwise in Core Image's upward y axis (clockwise on screen).
// - Core Image interpolates the 8-bit capture's encoded values when Pixelate and Distort read it directly, and linear
//   values when they read a composited scene (covers, image layers or a privacy crop): sampleEncoded selects this.
// - Image layers are sampled bilinearly from their premultiplied encoded pixels (transparent outside the image),
//   then composited source-over in linear light.
// Intermediate levels are linear 16-bit floats; the final pass encodes sRGB again.

Texture2D source : register(t0);
Texture2D layer : register(t1);
SamplerState linearClamp : register(s0);

cbuffer EffectPass : register(b0)
{
    float2 sourceScale;    // source texels per target pixel
    float2 sourceOffset;   // source texel coordinate of the target's origin
    float2 sourceSize;     // source texture size in texels
    float2 direction;      // blur step in source texels: (1, 0) or (0, 1)
    float4 effect;         // pixelate: center.xy, cell size; twirl: center.xy, radius, angle (display pixels)
    float2 displayOffset;  // display coordinate of the target's origin (effect and layer passes)
    float2 effectOrigin;   // display coordinate of the source texture's origin (effect passes)
    float4 sourceClamp;    // first and last texel centers of the source's valid area: samples beyond repeat its edge
    int taps;              // blur taps on each side of the center
    int sourceSrgb;        // 1: the source holds sRGB-encoded 8-bit values
    int targetSrgb;        // 1: write sRGB-encoded values (the final pass)
    int sampleEncoded;     // 1: interpolate encoded values, as Core Image does when it reads the capture directly
    float4 layerPlacement; // image layer: center of its placement, cosine and sine of its rotation (display pixels)
    float4 layerMapping;   // image layer: top-left of the unrotated placement, image texels per display pixel
    float4 layerImage;     // image layer: width and height in texels, opacity, unused
    float4 weights[7];     // blur weights for offsets 0..taps (at most 24), four per vector
};

struct VSOut
{
    float4 position : SV_Position;
};

VSOut vs_main(uint id : SV_VertexID)
{
    VSOut output;
    float2 uv = float2((id << 1) & 2, id & 2);
    output.position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return output;
}

float3 Decode(float3 c)
{
    return c <= 0.04045 ? c / 12.92 : pow(abs((c + 0.055) / 1.055), 2.4);
}

float3 Encode(float3 c)
{
    c = saturate(c);
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(abs(c), 1.0 / 2.4) - 0.055;
}

// Premultiplied encoded to premultiplied linear: the transfer curve applies to the unpremultiplied color.
float4 ToLinear(float4 value)
{
    return value.a > 0.0 ? float4(Decode(value.rgb / value.a) * value.a, value.a) : float4(0.0, 0.0, 0.0, 0.0);
}

float4 ToEncoded(float4 value)
{
    return value.a > 0.0 ? float4(Encode(value.rgb / value.a) * value.a, value.a) : float4(0.0, 0.0, 0.0, 0.0);
}

float4 Output(float4 color)
{
    // The desktop is opaque; an alpha below one would let a cover underneath show through.
    return float4(targetSrgb != 0 ? Encode(color.rgb) : color.rgb, 1.0);
}

// One stored texel. Indices beyond the valid area repeat its edge, like Core Image's clamped extent.
float4 Raw(int2 index)
{
    index = clamp(index, int2(sourceClamp.xy), int2(sourceClamp.zw));
    return source.Load(int3(index, 0));
}

// One texel in linear light.
float4 Texel(int2 index)
{
    float4 value = Raw(index);
    return sourceSrgb != 0 ? ToLinear(value) : value;
}

// A bilinear sample in linear light at a source texel coordinate (texel centers are at half pixels).
float4 Fetch(float2 texel)
{
    texel = clamp(texel, sourceClamp.xy, sourceClamp.zw);
    float4 result;
    if (sourceSrgb == 0)
    {
        result = source.SampleLevel(linearClamp, texel / sourceSize, 0);
    }
    else
    {
        float2 position = texel - 0.5;
        int2 index = int2(floor(position));
        float2 fraction = position - index;
        if (sampleEncoded != 0)
        {
            float4 top = lerp(Raw(index), Raw(index + int2(1, 0)), fraction.x);
            float4 bottom = lerp(Raw(index + int2(0, 1)), Raw(index + int2(1, 1)), fraction.x);
            result = ToLinear(lerp(top, bottom, fraction.y));
        }
        else
        {
            float4 upper = lerp(Texel(index), Texel(index + int2(1, 0)), fraction.x);
            float4 lower = lerp(Texel(index + int2(0, 1)), Texel(index + int2(1, 1)), fraction.x);
            result = lerp(upper, lower, fraction.y);
        }
    }
    return result;
}

// Halves the resolution: the average of each 2 x 2 block.
float4 ps_down(VSOut input) : SV_Target
{
    int2 corner = int2(floor(input.position.xy * sourceScale + sourceOffset - 1.0));
    float4 sum = Texel(corner) + Texel(corner + int2(1, 0)) + Texel(corner + int2(0, 1)) + Texel(corner + int2(1, 1));
    return float4(sum.rgb * 0.25, 1.0);
}

float Weight(int index)
{
    float4 group = weights[index >> 2];
    int lane = index & 3;
    return lane == 0 ? group.x : lane == 1 ? group.y : lane == 2 ? group.z : group.w;
}

// One direction of a separable Gaussian over texel centers.
float4 ps_blur(VSOut input) : SV_Target
{
    int2 center = int2(floor(input.position.xy * sourceScale + sourceOffset));
    int2 step = int2(direction);
    float4 sum = Texel(center) * Weight(0);
    for (int index = 1; index <= taps; index++)
    {
        sum += (Texel(center + step * index) + Texel(center - step * index)) * Weight(index);
    }
    return Output(sum);
}

// Scales a reduced level back up with bilinear filtering.
float4 ps_up(VSOut input) : SV_Target
{
    return Output(Fetch(input.position.xy * sourceScale + sourceOffset));
}

// CIPixellate: square cells of effect.z pixels on a grid through the region center (effect.xy). Rows are counted in
// Core Image's upward y axis, so a pixel center on a horizontal grid line belongs to the cell above it on screen.
float4 ps_pixelate(VSOut input) : SV_Target
{
    float2 display = input.position.xy + displayOffset;
    float size = effect.z;
    float column = floor((display.x - effect.x) / size);
    float row = floor((effect.y - display.y) / size);
    float2 center = float2(effect.x + (column + 0.5) * size, effect.y - (row + 0.5) * size);
    return Output(Fetch(center - effectOrigin));
}

// CITwirlDistortion: inside the radius, the sample point turns around the center by the angle times
// 1 - smoothstep(0, 1, distance / radius). On screen (y down) a positive angle turns it clockwise.
float4 ps_twirl(VSOut input) : SV_Target
{
    float2 display = input.position.xy + displayOffset;
    float2 offset = display - effect.xy;
    float distance = length(offset);
    float radius = effect.z;
    if (distance < radius)
    {
        float t = distance / radius;
        float turn = effect.w * (1.0 - t * t * (3.0 - 2.0 * t));
        float s, c;
        sincos(turn, s, c);
        offset = float2(c * offset.x - s * offset.y, s * offset.x + c * offset.y);
    }
    return Output(Fetch(effect.xy + offset - effectOrigin));
}

// One texel of the image layer; transparent outside the image.
float4 LayerTexel(int2 index)
{
    bool inside = index.x >= 0 && index.y >= 0 && index.x < int(layerImage.x) && index.y < int(layerImage.y);
    return inside ? layer.Load(int3(index, 0)) : float4(0.0, 0.0, 0.0, 0.0);
}

// Composites one image layer over the scene (source) in linear light: Core Image's transformed CIImage with its
// default bilinear sampling, alpha times the opacity (CIColorMatrix), source-over. Writes premultiplied sRGB.
float4 ps_image(VSOut input) : SV_Target
{
    float4 below = ToLinear(source.Load(int3(int2(input.position.xy), 0)));
    float2 display = input.position.xy + displayOffset;
    float2 offset = display - layerPlacement.xy;
    // Undo the layer's rotation around its center, then map the placement onto the image's texels.
    float2 unrotated = layerPlacement.xy
        + float2(layerPlacement.z * offset.x + layerPlacement.w * offset.y, -layerPlacement.w * offset.x + layerPlacement.z * offset.y);
    float2 position = (unrotated - layerMapping.xy) * layerMapping.zw - 0.5;
    int2 index = int2(floor(position));
    float2 fraction = position - index;
    float4 top = lerp(LayerTexel(index), LayerTexel(index + int2(1, 0)), fraction.x);
    float4 bottom = lerp(LayerTexel(index + int2(0, 1)), LayerTexel(index + int2(1, 1)), fraction.x);
    float4 image = ToLinear(lerp(top, bottom, fraction.y)) * layerImage.z;
    return ToEncoded(image + below * (1.0 - image.a));
}

// Screen zoom and the pointer lens (Mac ViewportProjection.imageForMetal): Core Image's samplingNearest() through the
// viewport. Each target pixel shows the source texel under its center: sourceScale is source texels per target pixel and
// sourceOffset the source position of the target's origin. The desktop is opaque.
float4 ps_zoom(VSOut input) : SV_Target
{
    float2 texel = input.position.xy * sourceScale + sourceOffset;
    return float4(Raw(int2(floor(texel))).rgb, 1.0);
}

// The effect layer through the zoom viewport: the same nearest sampling, keeping the layer's premultiplied transparency.
float4 ps_project(VSOut input) : SV_Target
{
    float2 texel = input.position.xy * sourceScale + sourceOffset;
    return Raw(int2(floor(texel)));
}
