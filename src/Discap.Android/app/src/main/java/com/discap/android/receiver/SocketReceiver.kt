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
import kotlin.math.max

class SocketReceiver(
    private val surface: Surface,
    private val onVideoSizeChanged: ((Int, Int) -> Unit)? = null,
    private val statsCallback: ((FrameStats) -> Unit)? = null
) {

    private var isRunning = false
    private var thread: Thread? = null

    private var h264Decoder: H264Decoder? = null
    private var lz4Decoder: Lz4Decoder? = null

    val sender = SocketSender()

    data class FrameStats(
        val fps: Double,
        val bitrateMbps: Double,
        val latencyMs: Double,
        val encoderType: String
    )

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
                sender.attachSocket(socket)
                Log.i("Discap.Net", "Connected to host.")

                val input = DataInputStream(socket.getInputStream())
                var statsFrames = 0
                var statsBytes = 0L
                var statsStartNs = System.nanoTime()
                var streamBaseUs: Long? = null
                var lastTimestampUs: Long = -1
                var lastWidth = 0
                var lastHeight = 0

                while (isRunning) {
                    // Read exactly 32 bytes of header
                    input.readFully(headerBuffer)

                    val bb = ByteBuffer.wrap(headerBuffer).order(ByteOrder.LITTLE_ENDIAN)
                    val magic = bb.getInt()
                    if (magic != 0x44434150) { // "DCAP" = 0x44434150
                        Log.e("Discap.Net", "Invalid magic header! Expected 0x44434150, got 0x${Integer.toHexString(magic)}. Disconnecting.")
                        break
                    }

                    val version = bb.get()
                    val frameType = bb.get() // 1 = LZ4, 2 = NVENC
                    val width = bb.getShort().toInt()
                    val height = bb.getShort().toInt()
                    val originalSize = bb.getInt()
                    val compressedSize = bb.getInt()
                    val timestampUs = bb.getLong()
                    bb.getInt() // sequence number
                    bb.getShort() // flags

                    // Resize payload buffer if needed
                    if (compressedSize > payloadBuffer.size) {
                        payloadBuffer = ByteArray(compressedSize)
                    }

                    // Read exactly compressedSize bytes of payload
                    input.readFully(payloadBuffer, 0, compressedSize)
                    
                    if (width != lastWidth || height != lastHeight) {
                        lastWidth = width
                        lastHeight = height
                        onVideoSizeChanged?.invoke(width, height)
                    }
                    
                    Log.i("Discap.Net", "[RCV] Packet received: type=$frameType size=$compressedSize")

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

                    if (timestampUs != lastTimestampUs) {
                        statsFrames++
                        lastTimestampUs = timestampUs
                    }
                    statsBytes += 32L + compressedSize
                    val nowNs = System.nanoTime()
                    val nowUs = nowNs / 1000
                    if (streamBaseUs == null) {
                        streamBaseUs = nowUs - timestampUs
                    }

                    val statsElapsedNs = nowNs - statsStartNs
                    if (statsElapsedNs >= 1_000_000_000L) {
                        val elapsedSec = statsElapsedNs / 1_000_000_000.0
                        val fps = statsFrames / elapsedSec
                        val bitrate = statsBytes * 8.0 / elapsedSec / 1_000_000.0
                        val latency = max(0.0, (nowUs - streamBaseUs!! - timestampUs) / 1000.0)
                        val encoder = if (frameType.toInt() == 2) "H.264" else "LZ4"
                        statsCallback?.invoke(FrameStats(fps, bitrate, latency, encoder))
                        statsFrames = 0
                        statsBytes = 0
                        statsStartNs = nowNs
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
                sender.detachSocket()
                try { socket?.close() } catch (e: Exception) {}
            }
        }
    }
}
