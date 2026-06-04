package com.discap.android.decoder

import android.graphics.Bitmap
import android.graphics.Rect
import android.util.Log
import android.view.Surface
import net.jpountz.lz4.LZ4Factory
import java.nio.ByteBuffer

class Lz4Decoder(private val surface: Surface, val width: Int, val height: Int) {

    private val decompressor = LZ4Factory.fastestJavaInstance().safeDecompressor()
    private val bitmap: Bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
    private var decompressedBuffer: ByteArray? = null

    fun decode(compressedData: ByteArray, compressedSize: Int, originalSize: Int) {
        try {
            if (decompressedBuffer == null || decompressedBuffer!!.size < originalSize) {
                decompressedBuffer = ByteArray(originalSize)
            }
            val dest = decompressedBuffer!!

            // Decompress LZ4 block
            val decompressedBytes = decompressor.decompress(compressedData, 0, compressedSize, dest, 0)
            if (decompressedBytes != originalSize) {
                Log.w("Discap.LZ4", "Size mismatch! Expected $originalSize, got $decompressedBytes")
                return
            }

            // The host sends BGRA byte array. Bitmap.Config.ARGB_8888 expects RGBA (or ABGR depending on endianness).
            // Android uses ARGB_8888 where memory layout is R, G, B, A (little-endian means A, R, G, B in int).
            // We can do a quick color swizzle (swap R and B) if needed, but for now we just load it directly.
            // A more optimized approach is doing this in a native JNI block or using RenderScript.
            // We swap B and R in pure Java for now:
            for (i in 0 until originalSize step 4) {
                val b = dest[i]
                dest[i] = dest[i + 2]     // R <- B
                dest[i + 2] = b           // B <- R
                // A is dest[i+3], G is dest[i+1] — left untouched
            }

            val byteBuffer = ByteBuffer.wrap(dest, 0, originalSize)
            bitmap.copyPixelsFromBuffer(byteBuffer)

            // Draw bitmap to Surface
            val canvas = surface.lockHardwareCanvas()
            if (canvas != null) {
                canvas.drawBitmap(bitmap, null, Rect(0, 0, width, height), null)
                surface.unlockCanvasAndPost(canvas)
            }
            
        } catch (e: Exception) {
            Log.e("Discap.LZ4", "Decompression error: ${e.message}")
        }
    }
    
    fun release() {
        bitmap.recycle()
    }
}
