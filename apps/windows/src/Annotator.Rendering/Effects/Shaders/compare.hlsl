// Capture frame comparison (Effects/CaptureFrameComparer.cs). One thread group per 32 x 32 tile of the frame: when any
// pixel of the tile differs between the two frames, the bits of the leases that look at the tile go into the result.
// Tiles that no lease looks at are not read. Both frames are B8G8R8A8_UNORM of the same size; equal bytes load as equal
// values, so the test is exact.

Texture2D<float4> previous_frame : register(t0);
Texture2D<float4> current_frame : register(t1);
// One uint of lease bits per tile, row by row over the whole frame.
ByteAddressBuffer tile_leases : register(t2);
RWByteAddressBuffer changed_leases : register(u0);

cbuffer Tiles : register(b0)
{
    // The dispatch covers the tiles from (first_column, first_row) on.
    uint first_column;
    uint first_row;
    // Tiles in a row of the whole frame.
    uint columns;
    uint frame_width;
    uint frame_height;
    uint3 unused;
};

groupshared uint tile_differs;

[numthreads(16, 16, 1)]
void cs_compare(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID, uint index : SV_GroupIndex)
{
    uint column = first_column + group.x;
    uint row = first_row + group.y;
    uint leases = tile_leases.Load((row * columns + column) * 4);
    if (index == 0)
    {
        tile_differs = 0;
    }
    GroupMemoryBarrierWithGroupSync();
    if (leases != 0)
    {
        // Each thread compares a 2 x 2 block of the tile.
        uint2 origin = uint2(column, row) * 32 + thread.xy * 2;
        bool differs = false;
        [unroll]
        for (uint y = 0; y < 2; y++)
        {
            [unroll]
            for (uint x = 0; x < 2; x++)
            {
                uint2 pixel = origin + uint2(x, y);
                if (pixel.x < frame_width && pixel.y < frame_height
                    && any(previous_frame.Load(int3(pixel, 0)) != current_frame.Load(int3(pixel, 0))))
                {
                    differs = true;
                }
            }
        }
        if (differs)
        {
            InterlockedOr(tile_differs, 1u);
        }
    }
    GroupMemoryBarrierWithGroupSync();
    if (index == 0 && tile_differs != 0)
    {
        changed_leases.InterlockedOr(0, leases);
    }
}
