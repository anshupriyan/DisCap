using System;
using Vortice.DXGI;

namespace Discap.Host.Capture;

/// <summary>
/// Extracts DXGI Desktop Duplication pointer shapes into a clean BGRA byte array for GPU compositing.
/// Handles Color, MaskedColor, and Monochrome pointer formats.
/// Uses a special alpha-channel trick (A=0, R=255) to instruct the GPU Compute Shader to perform XOR inversion.
/// </summary>
public static class CursorCompositor
{
    public static byte[]? ExtractCursorBitmap(OutduplPointerShapeInfo shapeInfo, ReadOnlySpan<byte> shapeBuffer)
    {
        if (shapeBuffer.IsEmpty) return null;

        int width = (int)shapeInfo.Width;
        // Monochrome cursor height is officially doubled (contains AND mask then XOR mask)
        int height = shapeInfo.Type == (uint)PointerShapeType.Monochrome ? (int)(shapeInfo.Height / 2) : (int)shapeInfo.Height;

        byte[] output = new byte[width * height * 4];

        switch ((PointerShapeType)shapeInfo.Type)
        {
            case PointerShapeType.Color:
                ExtractColor(shapeInfo, shapeBuffer, output, width, height);
                break;
            case PointerShapeType.MaskedColor:
                ExtractMaskedColor(shapeInfo, shapeBuffer, output, width, height);
                break;
            case PointerShapeType.Monochrome:
                ExtractMonochrome(shapeInfo, shapeBuffer, output, width, height);
                break;
            default:
                return null;
        }

        return output;
    }

    private static void ExtractColor(OutduplPointerShapeInfo shapeInfo, ReadOnlySpan<byte> shape, byte[] output, int width, int height)
    {
        int pitch = (int)shapeInfo.Pitch;
        int outStride = width * 4;

        for (int y = 0; y < height; y++)
        {
            int srcRow = y * pitch;
            int dstRow = y * outStride;

            for (int x = 0; x < width; x++)
            {
                int src = srcRow + x * 4;
                if (src + 3 >= shape.Length) return;

                int dst = dstRow + x * 4;
                // Copy B, G, R, A directly
                output[dst] = shape[src];
                output[dst + 1] = shape[src + 1];
                output[dst + 2] = shape[src + 2];
                output[dst + 3] = shape[src + 3];
            }
        }
    }

    private static void ExtractMaskedColor(OutduplPointerShapeInfo shapeInfo, ReadOnlySpan<byte> shape, byte[] output, int width, int height)
    {
        int pitch = (int)shapeInfo.Pitch;
        int outStride = width * 4;

        for (int y = 0; y < height; y++)
        {
            int srcRow = y * pitch;
            int dstRow = y * outStride;

            for (int x = 0; x < width; x++)
            {
                int src = srcRow + x * 4;
                if (src + 3 >= shape.Length) return;

                int dst = dstRow + x * 4;

                if (shape[src + 3] == 0)
                {
                    // Regular draw
                    output[dst] = shape[src];
                    output[dst + 1] = shape[src + 1];
                    output[dst + 2] = shape[src + 2];
                    output[dst + 3] = 255;
                }
                else
                {
                    // XOR draw -> special GPU mask (A=0, R=255)
                    output[dst] = 0;
                    output[dst + 1] = 0;
                    output[dst + 2] = 255;
                    output[dst + 3] = 0;
                }
            }
        }
    }

    private static void ExtractMonochrome(OutduplPointerShapeInfo shapeInfo, ReadOnlySpan<byte> shape, byte[] output, int width, int height)
    {
        int pitch = (int)shapeInfo.Pitch;
        int xorOffset = pitch * height;
        int outStride = width * 4;

        for (int y = 0; y < height; y++)
        {
            int andRow = y * pitch;
            int xorRow = xorOffset + y * pitch;
            int dstRow = y * outStride;

            for (int x = 0; x < width; x++)
            {
                int byteIndex = x >> 3;
                int mask = 0x80 >> (x & 7);
                
                if (andRow + byteIndex >= shape.Length || xorRow + byteIndex >= shape.Length)
                    return;

                bool andBit = (shape[andRow + byteIndex] & mask) != 0;
                bool xorBit = (shape[xorRow + byteIndex] & mask) != 0;

                int dst = dstRow + x * 4;

                if (!andBit && !xorBit)
                {
                    // Black
                    output[dst] = 0;
                    output[dst + 1] = 0;
                    output[dst + 2] = 0;
                    output[dst + 3] = 255;
                }
                else if (!andBit && xorBit)
                {
                    // White
                    output[dst] = 255;
                    output[dst + 1] = 255;
                    output[dst + 2] = 255;
                    output[dst + 3] = 255;
                }
                else if (andBit && xorBit)
                {
                    // Invert -> special GPU mask (A=0, R=255)
                    output[dst] = 0;
                    output[dst + 1] = 0;
                    output[dst + 2] = 255;
                    output[dst + 3] = 0;
                }
                else
                {
                    // Transparent
                    output[dst] = 0;
                    output[dst + 1] = 0;
                    output[dst + 2] = 0;
                    output[dst + 3] = 0;
                }
            }
        }
    }
}
