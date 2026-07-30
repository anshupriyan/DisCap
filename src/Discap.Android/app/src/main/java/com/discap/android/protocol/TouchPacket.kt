package com.discap.android.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder

data class TouchPointerData(
    val id: Byte,
    val action: Byte,
    val normX: Float,
    val normY: Float,
    val pressure: Float
)

object TouchPacket {
    const val MAGIC_MTCH = 0x4843544D // "MTCH" in Little-Endian

    fun buildMultiTouchPacket(pointers: List<TouchPointerData>): ByteArray {
        val count = pointers.size.coerceAtMost(10)
        val packetSize = 5 + (count * 10)
        val buffer = ByteArray(packetSize)
        val bb = ByteBuffer.wrap(buffer).order(ByteOrder.LITTLE_ENDIAN)

        bb.putInt(MAGIC_MTCH)
        bb.put(count.toByte())

        for (i in 0 until count) {
            val p = pointers[i]
            val xShort = (p.normX.coerceIn(0f, 1f) * 65535f).toInt().toShort()
            val yShort = (p.normY.coerceIn(0f, 1f) * 65535f).toInt().toShort()
            val pressShort = (p.pressure.coerceIn(0f, 1f) * 65535f).toInt().toShort()

            bb.put(p.id)
            bb.put(p.action)
            bb.putShort(xShort)
            bb.putShort(yShort)
            bb.putShort(pressShort)
            bb.putShort(0) // Reserved
        }

        return buffer
    }
}
