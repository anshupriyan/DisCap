package com.discap.android.overlay

import android.graphics.Bitmap
import android.graphics.Color
import java.nio.ByteBuffer
import java.nio.ByteOrder

class CursorManager(private val overlayView: CursorOverlayView) {

    fun handlePositionPacket(payload: ByteArray, desktopWidth: Int, desktopHeight: Int) {
        if (payload.size < 9) return
        val bb = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        val x = bb.getInt()
        val y = bb.getInt()
        val visible = bb.get().toInt() != 0

        overlayView.post {
            overlayView.updatePosition(x, y, visible, desktopWidth, desktopHeight)
        }
    }

    fun handleShapePacket(payload: ByteArray) {
        if (payload.size < 20) return
        val bb = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        val shapeType = bb.getInt()
        val width = bb.getInt()
        val height = bb.getInt()
        val hotspotX = bb.getInt()
        val hotspotY = bb.getInt()

        if (width <= 0 || height <= 0 || width > 256 || height > 256) return

        val pixelDataOffset = 20
        val pixelDataLen = payload.size - pixelDataOffset
        if (pixelDataLen <= 0) return

        val bitmap = decodeShapeToBitmap(shapeType, width, height, payload, pixelDataOffset, pixelDataLen)
        if (bitmap != null) {
            overlayView.post {
                overlayView.updateShape(bitmap, hotspotX, hotspotY)
            }
        }
    }

    private fun decodeShapeToBitmap(
        shapeType: Int,
        width: Int,
        height: Int,
        data: ByteArray,
        offset: Int,
        len: Int
    ): Bitmap? {
        return try {
            val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
            val pixels = IntArray(width * height)

            when (shapeType) {
                1 -> { // Monochrome (AND bitmask + XOR bitmask)
                    val maskPitch = (width + 7) / 8
                    val andOffset = offset
                    val xorOffset = offset + maskPitch * height

                    for (y in 0 until height) {
                        for (x in 0 until width) {
                            val byteIdx = y * maskPitch + (x / 8)
                            val bitMask = 0x80 shr (x % 8)

                            val andBit = if (andOffset + byteIdx < data.size) (data[andOffset + byteIdx].toInt() and bitMask) != 0 else false
                            val xorBit = if (xorOffset + byteIdx < data.size) (data[xorOffset + byteIdx].toInt() and bitMask) != 0 else false

                            val color = when {
                                !andBit && !xorBit -> Color.BLACK
                                !andBit && xorBit -> Color.WHITE
                                andBit && xorBit -> Color.WHITE
                                else -> Color.TRANSPARENT
                            }
                            pixels[y * width + x] = color
                        }
                    }
                }
                2, 4 -> { // Color / MaskedColor (BGRA format)
                    var srcIdx = offset
                    for (i in 0 until (width * height)) {
                        if (srcIdx + 3 < data.size) {
                            val b = data[srcIdx].toInt() and 0xFF
                            val g = data[srcIdx + 1].toInt() and 0xFF
                            val r = data[srcIdx + 2].toInt() and 0xFF
                            val a = data[srcIdx + 3].toInt() and 0xFF
                            pixels[i] = (a shl 24) or (r shl 16) or (g shl 8) or b
                            srcIdx += 4
                        }
                    }
                }
                else -> return null
            }

            bitmap.setPixels(pixels, 0, width, 0, 0, width, height)
            bitmap
        } catch (e: Exception) {
            null
        }
    }
}
