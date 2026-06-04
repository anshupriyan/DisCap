package com.discap.android.receiver

import android.util.Log
import android.view.Surface
import com.discap.android.decoder.H264Decoder
import com.discap.android.decoder.Lz4Decoder
import java.io.DataInputStream
import java.io.EOFException
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder

class SocketReceiver(private val surface: Surface) {

    private var isRunning = false
    private var thread: Thread? = null

    private var h264Decoder: H264Decoder? = null
    private var lz4Decoder: Lz4Decoder? = null

    // 32-byte header structure
    // 0..3   Magic "DCAP"
    // 4      Version
    // 5      FrameType (1=LZ4, 2=NVENC)
    // 6..7   Width
    // 8..9   Height
    // 10..13 OriginalSize
    // 14..17 CompressedSize
    // 18..25 Timestamp
    // 26..29 SequenceNumber
    // 30..31 Flags

    fun start() {
        if (isRunning) return
        isRunning = true

        thread = Thread {
            receiveLoop()
        }.apply { start() }
    }

    fun stopReceiver() {
        isRunning = false
        thread?.interrupt()
        
        h264Decoder?.release()
        lz4Decoder?.release()
    }

    private fun receiveLoop() {
        val headerBuffer = ByteArray(32)
        var payloadBuffer = ByteArray(2 * 1024 * 1024)

        while (isRunning) {
            var socket: Socket? = null
            try {
                Log.i("Discap.Net", "Connecting to 127.0.0.1:53516 (via ADB reverse)...")
                socket = Socket("127.0.0.1", 53516)
                socket.tcpNoDelay = true
                socket.receiveBufferSize = 2 * 1024 * 1024
                Log.i("Discap.Net", "Connected to host.")

                val input = DataInputStream(socket.getInputStream())

                while (isRunning) {
                    // Read exactly 32 bytes of header
                    input.readFully(headerBuffer)

                    val bb = ByteBuffer.wrap(headerBuffer).order(ByteOrder.LITTLE_ENDIAN)
                    val magic = bb.getInt()
                    if (magic != 0x50414344) { // "DCAP" in little-endian (0x44 0x43 0x41 0x50)
                        Log.e("Discap.Net", "Invalid magic header! Disconnecting.")
                        break
                    }

                    val version = bb.get()
                    val frameType = bb.get() // 1 = LZ4, 2 = NVENC
                    val width = bb.getShort().toInt()
                    val height = bb.getShort().toInt()
                    val originalSize = bb.getInt()
                    val compressedSize = bb.getInt()
                    // Ignoring the rest for decoding

                    // Resize payload buffer if needed
                    if (compressedSize > payloadBuffer.size) {
                        payloadBuffer = ByteArray(compressedSize)
                    }

                    // Read exactly compressedSize bytes of payload
                    input.readFully(payloadBuffer, 0, compressedSize)

                    // Route to appropriate decoder
                    if (frameType.toInt() == 2) {
                        // NVENC H.264
                        if (h264Decoder == null || h264Decoder!!.width != width || h264Decoder!!.height != height) {
                            h264Decoder?.release()
                            h264Decoder = H264Decoder(surface, width, height)
                            h264Decoder?.start()
                        }
                        h264Decoder?.decode(payloadBuffer, 0, compressedSize)
                    } else if (frameType.toInt() == 1) {
                        // LZ4
                        if (lz4Decoder == null || lz4Decoder!!.width != width || lz4Decoder!!.height != height) {
                            lz4Decoder = Lz4Decoder(surface, width, height)
                        }
                        lz4Decoder?.decode(payloadBuffer, compressedSize, originalSize)
                    }
                }

            } catch (e: EOFException) {
                Log.w("Discap.Net", "Host closed connection.")
            } catch (e: Exception) {
                if (isRunning) {
                    Log.e("Discap.Net", "Socket error: ${e.message}")
                    Thread.sleep(1000) // Wait before reconnecting
                }
            } finally {
                try { socket?.close() } catch (e: Exception) {}
            }
        }
    }
}
