package com.discap.android.overlay

import android.graphics.Bitmap
import android.os.Handler
import android.os.Looper
import android.util.Log
import java.nio.ByteBuffer
import java.nio.ByteOrder

class CursorManager(private val overlayView: CursorOverlayView) {

    private val mainHandler = Handler(Looper.getMainLooper())

    fun setDesktopSize(width: Int, height: Int) {
        mainHandler.post {
            overlayView.desktopWidth = width
            overlayView.desktopHeight = height
        }
    }

    fun onCursorPosReceived(payload: ByteArray) {
        if (payload.size < 9) return

        val bb = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        val x = bb.int
        val y = bb.int
        val visible = bb.get().toInt() != 0

        mainHandler.post {
            overlayView.updatePosition(x, y, visible)
        }
    }

    fun onCursorShapeReceived(payload: ByteArray) {
        if (payload.size < 28) return

        val bb = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        val type = bb.getInt(0)
        val width = bb.getInt(4)
        val height = bb.getInt(8)
        val pitch = bb.getInt(12)
        val hotspotX = bb.getInt(16)
        val hotspotY = bb.getInt(20)
        val bufferSize = bb.getInt(24)

        if (type == 1) {
            Log.i("Discap.Cursor", "Shape updated: Type $type (Using fallback)")
            mainHandler.post {
                overlayView.updateShape(null, 0, 0)
            }
            return
        }

        if (width <= 0 || height <= 0 || bufferSize <= 0 || payload.size < 28 + bufferSize) {
            return
        }

        val shapeBuffer = ByteArray(bufferSize)
        System.arraycopy(payload, 28, shapeBuffer, 0, bufferSize)

        var bitmap: Bitmap? = try {
            when (type) {
                2 -> decodeColorBitmap(width, height, shapeBuffer)            // TYPE_COLOR
                4 -> decodeMaskedColorBitmap(width, height, pitch, shapeBuffer) // TYPE_MASKED_COLOR
                0 -> {
                    Log.w("Discap.Cursor", "[CURSOR] Type is 0, assuming Type 2 (Color) fallback bitmap")
                    decodeColorBitmap(width, height, shapeBuffer)
                }
                else -> {
                    Log.i("Discap.Cursor", "Shape updated: Type $type (Using fallback)")
                    null
                }
            }
        } catch (e: Exception) {
            Log.e("Discap.Cursor", "Error decoding cursor shape: ${e.message}", e)
            null
        }

        if (bitmap != null && isBitmapEmpty(bitmap)) {
            bitmap = null
        }

        mainHandler.post {
            overlayView.updateShape(bitmap, hotspotX, hotspotY)
        }
    }

    private fun decodeColorBitmap(
        width: Int,
        height: Int,
        buffer: ByteArray
    ): Bitmap {
        val pixelCount = width * height
        val pixels = IntArray(pixelCount)

        // Pass 1: Check if alpha channel is used (at least one pixel has alpha > 0)
        var alphaChannelUsed = false
        for (i in 0 until pixelCount) {
            val offset = i * 4
            if (offset + 3 < buffer.size) {
                val aRaw = buffer[offset + 3].toInt() and 0xFF
                if (aRaw > 0) {
                    alphaChannelUsed = true
                    break
                }
            }
        }

        // Pass 2: Convert BGRA to ARGB
        for (i in 0 until pixelCount) {
            val offset = i * 4
            if (offset + 3 < buffer.size) {
                val b = buffer[offset + 0].toInt() and 0xFF
                val g = buffer[offset + 1].toInt() and 0xFF
                val r = buffer[offset + 2].toInt() and 0xFF
                val aRaw = buffer[offset + 3].toInt() and 0xFF

                val a = if (alphaChannelUsed) aRaw else 255
                pixels[i] = (a shl 24) or (r shl 16) or (g shl 8) or b
            }
        }

        return Bitmap.createBitmap(pixels, width, height, Bitmap.Config.ARGB_8888)
    }

    private fun decodeMaskedColorBitmap(
        width: Int,
        height: Int,
        pitch: Int,
        buffer: ByteArray
    ): Bitmap {
        val pixels = IntArray(width * height)
        val colorSize = width * height * 4
        val maskPitch = ((width + 31) / 32) * 4
        val colorRowPitch = if (pitch > 0) pitch else width * 4

        for (y in 0 until height) {
            val colorRow = y * colorRowPitch
            val andMaskRow = colorSize + y * maskPitch

            for (x in 0 until width) {
                val pixelIndex = y * width + x
                val colorOffset = colorRow + x * 4

                if (colorOffset + 3 < buffer.size) {
                    val b = buffer[colorOffset + 0].toInt() and 0xFF
                    val g = buffer[colorOffset + 1].toInt() and 0xFF
                    val r = buffer[colorOffset + 2].toInt() and 0xFF

                    val byteIndex = x / 8
                    val bitShift = 7 - (x % 8)
                    val andBit = if (andMaskRow + byteIndex < buffer.size) {
                        (buffer[andMaskRow + byteIndex].toInt() ushr bitShift) and 1
                    } else 0

                    val a = if (andBit == 1) 0 else 255
                    pixels[pixelIndex] = (a shl 24) or (r shl 16) or (g shl 8) or b
                }
            }
        }

        return Bitmap.createBitmap(pixels, width, height, Bitmap.Config.ARGB_8888)
    }

    private fun isBitmapEmpty(bitmap: Bitmap): Boolean {
        val width = bitmap.width
        val height = bitmap.height
        val pixels = IntArray(width * height)
        bitmap.getPixels(pixels, 0, width, 0, 0, width, height)
        for (p in pixels) {
            if ((p ushr 24) != 0) return false
        }
        return true
    }

    private fun createFallbackBitmap(): Bitmap {
        val size = 32
        val bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bitmap)
        val paint = Paint(Paint.ANTI_ALIAS_FLAG)

        // Black outer circle
        paint.color = Color.BLACK
        canvas.drawCircle(size / 2f, size / 2f, 14f, paint)

        // White inner circle
        paint.color = Color.WHITE
        canvas.drawCircle(size / 2f, size / 2f, 10f, paint)

        return bitmap
    }
}
