package com.discap.android.receiver

import android.util.Log
import java.io.DataOutputStream
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder

class SocketSender {
    private var outputStream: DataOutputStream? = null
    private var lastSettingsPacket: ByteArray? = null

    // Call this from SocketReceiver when a connection is established
    fun attachSocket(socket: Socket) {
        try {
            outputStream = DataOutputStream(socket.getOutputStream())
            lastSettingsPacket?.let { writePacket(it) }
        } catch (e: Exception) {
            Log.e("Discap.Input", "Failed to attach output stream: ${e.message}")
        }
    }

    fun detachSocket() {
        outputStream = null
    }

    @Synchronized
    private fun writePacket(buffer: ByteArray) {
        val out = outputStream ?: return
        out.write(buffer)
        out.flush()
    }

    /**
     * Sends an 8-byte input packet.
     * Magic "INPT" (4 bytes)
     * X (2 bytes, normalized 0..65535)
     * Y (2 bytes, normalized 0..65535)
     * Action (1 byte: 0=Up, 1=Down, 2=Move)
     * Button (1 byte: 0=None, 1=Left, 2=Right)
     * Pressure (1 byte) -> Wait, 4+2+2+1+1+1 = 11 bytes.
     * Let's define the 12-byte layout:
     * 0..3: "INPT"
     * 4..5: X (ushort)
     * 6..7: Y (ushort)
     * 8: Action
     * 9: Button
     * 10: Pressure (0-255)
     * 11: Reserved
     */
    fun sendInput(xNorm: Float, yNorm: Float, action: Byte, button: Byte, pressure: Byte = 0) {
        if (outputStream == null) return
        
        val xInt = (xNorm.coerceIn(0f, 1f) * 65535).toInt().toShort()
        val yInt = (yNorm.coerceIn(0f, 1f) * 65535).toInt().toShort()

        val buffer = ByteArray(12)
        val bb = ByteBuffer.wrap(buffer).order(ByteOrder.LITTLE_ENDIAN)
        bb.putInt(0x54504E49) // "INPT" in little-endian (0x49 0x4E 0x50 0x54)
        bb.putShort(xInt)
        bb.putShort(yInt)
        bb.put(action)
        bb.put(button)
        bb.put(pressure)
        bb.put(0) // Reserved

        try {
            // Using a separate thread to avoid blocking the UI thread
            Thread {
                try {
                    writePacket(buffer)
                } catch (e: Exception) {
                    Log.e("Discap.Input", "Failed to send input: ${e.message}")
                    outputStream = null
                }
            }.start()
        } catch (e: Exception) {}
    }

    fun sendSettings(
        bitrateMbps: Int,
        fpsCap: Int,
        resolutionScale: Int,
        encoderMode: Int,
        showStats: Boolean
    ) {
        val buffer = ByteArray(12)
        val bb = ByteBuffer.wrap(buffer).order(ByteOrder.LITTLE_ENDIAN)
        bb.putInt(0x4C525443) // "CTRL"
        bb.put(bitrateMbps.coerceIn(5, 50).toByte())
        bb.put(fpsCap.toByte())
        bb.put(resolutionScale.toByte())
        bb.put(encoderMode.toByte())
        bb.put(if (showStats) 1 else 0)
        bb.put(0)
        bb.put(0)
        bb.put(0)
        lastSettingsPacket = buffer

        Thread {
            try {
                writePacket(buffer)
            } catch (e: Exception) {
                Log.e("Discap.Input", "Failed to send settings: ${e.message}")
                outputStream = null
            }
        }.start()
    }
}
