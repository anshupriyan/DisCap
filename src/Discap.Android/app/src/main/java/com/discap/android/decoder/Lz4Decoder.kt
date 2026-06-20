package com.discap.android.decoder

import android.graphics.Bitmap
import android.graphics.Rect
import android.graphics.Paint
import android.graphics.ColorMatrix
import android.graphics.ColorMatrixColorFilter
import android.util.Log
import android.view.Surface
import net.jpountz.lz4.LZ4Factory
import java.nio.ByteBuffer

class Lz4Decoder(private val surface: Surface, val width: Int, val height: Int) {

    private val decompressor = LZ4Factory.fastestJavaInstance().safeDecompressor()
    private val bitmap: Bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
    private var decompressedBuffer: ByteArray? = null

    // Hardware-accelerated color swapping (BGRA to RGBA)
    private val paint = Paint().apply {
        val matrix = ColorMatrix(floatArrayOf(
            0f, 0f, 1f, 0f, 0f, // Red becomes Blue
            0f, 1f, 0f, 0f, 0f, // Green stays Green
            1f, 0f, 0f, 0f, 0f, // Blue becomes Red
            0f, 0f, 0f, 1f, 0f  // Alpha stays Alpha
        ))
        colorFilter = ColorMatrixColorFilter(matrix)
        isFilterBitmap = true // Enable bilinear filtering during scaling
    }

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

            val byteBuffer = ByteBuffer.wrap(dest, 0, originalSize)
            bitmap.copyPixelsFromBuffer(byteBuffer)

            // Draw bitmap to Surface, scaling to fit the entire screen
            val canvas = surface.lockHardwareCanvas()
            if (canvas != null) {
                val srcRect = Rect(0, 0, width, height)
                val dstRect = Rect(0, 0, canvas.width, canvas.height)
                canvas.drawBitmap(bitmap, srcRect, dstRect, paint)
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
