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
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.atomic.AtomicInteger
import kotlin.math.max

class SocketReceiver(
    private val surface: Surface,
    private val cursorManager: com.discap.android.overlay.CursorManager? = null,
    private val onVideoSizeChanged: ((Int, Int) -> Unit)? = null,
    private val statsCallback: ((FrameStats) -> Unit)? = null
) {

    private var isRunning = false
    private var networkThread: Thread? = null
    private var decoderThread: Thread? = null

    private var h264Decoder: H264Decoder? = null
    private var lz4Decoder: Lz4Decoder? = null

    val sender = SocketSender()

    fun setTouchActive(active: Boolean) {
        h264Decoder?.isTouchActive = active
    }

    data class FrameStats(
        val fps: Double,
        val bitrateMbps: Double,
        val latencyMs: Double,
        val encoderType: String
    )

    /** NAL packet data class for the network→decoder queue */
    data class NalPacket(
        val frameType: Int,
        val data: ByteArray,
        val size: Int,
        val width: Int,
        val height: Int,
        val timestampUs: Long
    )

    /** Lock-free queue between network thread and decoder thread */
    private val nalQueue = ConcurrentLinkedQueue<NalPacket>()
    private val queueDepth = AtomicInteger(0)

    /** Soft cap for queue overflow detection — triggers flush + PLI */
    private val QUEUE_OVERFLOW_THRESHOLD = 60

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

        networkThread = Thread {
            android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_DISPLAY)
            networkReceiveLoop()
        }.apply {
            name = "DisCap-NetRecv"
            start()
        }
    }

    fun stopReceiver() {
        isRunning = false
        networkThread?.interrupt()
        decoderThread?.interrupt()

        h264Decoder?.release()
        lz4Decoder?.release()
    }

    /**
     * Network thread: reads from TCP socket as fast as possible, enqueues
     * NAL packets to the lock-free queue. NEVER blocks on the decoder.
     */
    private fun networkReceiveLoop() {
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

                // Start the decoder feeder thread for this connection
                startDecoderThread()

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
                    val frameType = bb.get()
                    val fTypeInt = frameType.toInt() and 0xFF
                    val width = bb.getShort().toInt()
                    val height = bb.getShort().toInt()
                    val originalSize = bb.getInt()
                    val compressedSize = bb.getInt()
                    val timestampUs = bb.getLong()
                    bb.getInt() // sequence number
                    bb.getShort() // flags

                    Log.d("Discap.Net", "[HDR] RCV type=$fTypeInt size=$compressedSize w=$width h=$height")

                    // Resize payload buffer if needed
                    if (compressedSize > payloadBuffer.size) {
                        payloadBuffer = ByteArray(compressedSize)
                    }

                    // Read exactly compressedSize bytes of payload
                    input.readFully(payloadBuffer, 0, compressedSize)
                    
                    if (fTypeInt == 3) {
                        Log.d("DISCAP-CURSOR", "📥 PACKET ARRIVED: Type 3, Size=$compressedSize")
                        val copy = payloadBuffer.copyOf(compressedSize)
                        cursorManager?.onCursorPosReceived(copy)
                        continue
                    } else if (fTypeInt == 4) {
                        Log.d("DISCAP-CURSOR", "📥 SHAPE PACKET ARRIVED! Size=$compressedSize")
                        val copy = payloadBuffer.copyOf(compressedSize)
                        cursorManager?.onCursorShapeReceived(copy)
                        continue
                    }

                    if (width != lastWidth || height != lastHeight) {
                        lastWidth = width
                        lastHeight = height
                        cursorManager?.setDesktopSize(width, height)
                        onVideoSizeChanged?.invoke(width, height)
                    }

                    // Enqueue to lock-free queue (ZERO blocking — network thread never waits)
                    val nalCopy = payloadBuffer.copyOf(compressedSize)
                    nalQueue.offer(NalPacket(fTypeInt, nalCopy, compressedSize, width, height, timestampUs))
                    val depth = queueDepth.incrementAndGet()

                    // Overflow detection: flush + PLI
                    if (depth > QUEUE_OVERFLOW_THRESHOLD) {
                        Log.w("Discap.Net", "[OVERFLOW] Frame queue depth ($depth) > threshold ($QUEUE_OVERFLOW_THRESHOLD). Flushing queue, resetting decoder, and requesting IDR.")
                        nalQueue.clear()
                        queueDepth.set(0)
                        h264Decoder?.flush()
                        sender.sendPliRequest()
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
                // Stop decoder thread for this connection
                decoderThread?.interrupt()
                decoderThread = null
                nalQueue.clear()
                queueDepth.set(0)
                try { socket?.close() } catch (e: Exception) {}
            }
        }
    }

    /**
     * Decoder feeder thread: pulls frame packets from the lock-free queue
     * and feeds them to the appropriate decoder. Uses Hold-and-Retry
     * to guarantee ZERO frame drops when MediaCodec input buffers are busy.
     */
    private fun startDecoderThread() {
        decoderThread?.interrupt()
        decoderThread = Thread {
            android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_DISPLAY)
            Log.i("Discap.Net", "[DECODER-THREAD] Decoder feeder thread started.")

            var pendingPacket: NalPacket? = null

            while (isRunning && !Thread.currentThread().isInterrupted) {
                try {
                    if (pendingPacket == null) {
                        pendingPacket = nalQueue.poll()
                        if (pendingPacket != null) {
                            queueDepth.decrementAndGet()
                        }
                    }

                    val packet = pendingPacket
                    if (packet != null) {
                        val success = if (packet.frameType == 2) {
                            // NVENC H.264
                            if (h264Decoder == null || h264Decoder!!.width != packet.width || h264Decoder!!.height != packet.height) {
                                h264Decoder?.release()
                                h264Decoder = H264Decoder(surface, packet.width, packet.height)
                                h264Decoder?.onResolutionDetected = onVideoSizeChanged
                                h264Decoder?.start()
                            }
                            h264Decoder?.decode(packet.data, 0, packet.size, packet.timestampUs) ?: false
                        } else if (packet.frameType == 1) {
                            // LZ4
                            if (lz4Decoder == null || lz4Decoder!!.width != packet.width || lz4Decoder!!.height != packet.height) {
                                lz4Decoder = Lz4Decoder(surface, packet.width, packet.height)
                            }
                            lz4Decoder?.decode(packet.data, packet.size, packet.width * packet.height * 4)
                            true
                        } else {
                            true
                        }

                        if (success) {
                            pendingPacket = null // Successfully fed to decoder
                        } else {
                            // Codec busy — hold packet reference and retry on next loop tick
                            Thread.sleep(1)
                        }
                    } else {
                        // Queue empty — brief sleep to avoid busy-spinning
                        Thread.sleep(1)
                    }
                } catch (e: InterruptedException) {
                    break
                } catch (e: Exception) {
                    if (isRunning) {
                        Log.e("Discap.Net", "[DECODER-THREAD] Error: ${e.message}")
                    }
                }
            }

            Log.i("Discap.Net", "[DECODER-THREAD] Decoder feeder thread exiting.")
        }.apply {
            name = "DisCap-Decoder"
            start()
        }
    }
}
