package com.discap.android.decoder

import android.media.MediaCodec
import android.media.MediaFormat
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer

class H264Decoder(private val surface: Surface, var width: Int, var height: Int, var displayRefreshRateHz: Float = 144f) {

    var onResolutionDetected: ((width: Int, height: Int) -> Unit)? = null
    private var codec: MediaCodec? = null
    private var isConfigured = false
    @Volatile private var isRunning = false
    private var outputThread: Thread? = null

    /** Flag set during active touch/scroll interactions to reduce target queue depth to 1 */
    @Volatile var isTouchActive: Boolean = false

    fun start() {
        try {
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height)
            format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1) // Critical for zero-latency decoding
            format.setInteger(MediaFormat.KEY_PRIORITY, 0) // Real-time priority
            format.setInteger(MediaFormat.KEY_MAX_WIDTH, 3840)
            format.setInteger(MediaFormat.KEY_MAX_HEIGHT, 2160)

            // Dual-ended color lock: Force Full Range (0-255 sRGB) BT.709 color space
            if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.N) {
                format.setInteger(MediaFormat.KEY_COLOR_RANGE, MediaFormat.COLOR_RANGE_FULL)
                format.setInteger(MediaFormat.KEY_COLOR_STANDARD, MediaFormat.COLOR_STANDARD_BT709)
                format.setInteger(MediaFormat.KEY_COLOR_TRANSFER, MediaFormat.COLOR_TRANSFER_SDR_VIDEO)
            }
            
            codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            
            // Output directly to the Surface for zero-copy rendering
            codec?.configure(format, surface, null, 0)

            // Surface frame rate hint for optimal VSYNC scheduling on Android 11+ (API 30+)
            if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.R) {
                try {
                    surface.setFrameRate(
                        displayRefreshRateHz,
                        Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE,
                        Surface.CHANGE_FRAME_RATE_ONLY_IF_SEAMLESS
                    )
                } catch (e: Exception) {
                    Log.w("Discap.H264", "Failed to set surface frame rate: ${e.message}")
                }
            }

            codec?.start()
            isConfigured = true
            isRunning = true
            outputThread = Thread { drainOutput() }
            outputThread?.start()
            onResolutionDetected?.invoke(width, height)
            Log.i("Discap.H264", "[DEC] MediaCodec configured: ${MediaFormat.MIMETYPE_VIDEO_AVC} ${width}x${height}")
        } catch (e: Exception) {
            Log.e("Discap.H264", "Failed to start MediaCodec: ${e.message}")
            isConfigured = false
        }
    }

    private var pendingCsdAfterFlush = false

    fun flush() {
        try {
            codec?.flush()
            pendingCsdAfterFlush = true
            Log.i("Discap.H264", "[DEC] flushed MediaCodec pipeline")
        } catch (e: Exception) {
            Log.e("Discap.H264", "Failed to flush MediaCodec: ${e.message}")
        }
    }

    fun flush(newWidth: Int, newHeight: Int) {
        try {
            codec?.flush()
            width = newWidth
            height = newHeight
            pendingCsdAfterFlush = true
            Log.i("Discap.H264", "[DEC] flushed and updated resolution to ${newWidth}x${newHeight}")
        } catch (e: Exception) {
            Log.e("Discap.H264", "Failed to flush MediaCodec: ${e.message}")
        }
    }

    fun decode(nalData: ByteArray, offset: Int, length: Int, timestampUs: Long): Boolean {
        if (!isConfigured) return false
        val codec = codec ?: return false

        // Parse NAL unit type of first NAL in payload (for logging/CSD tracking)
        var nalType = -1
        var headerIndex = offset
        if (length >= 4) {
            if (nalData[offset] == 0.toByte() && nalData[offset + 1] == 0.toByte()) {
                if (nalData[offset + 2] == 1.toByte()) {
                    headerIndex = offset + 3
                } else if (nalData[offset + 2] == 0.toByte() && nalData[offset + 3] == 1.toByte()) {
                    headerIndex = offset + 4
                }
            }
        }
        if (headerIndex < offset + length) {
            nalType = nalData[headerIndex].toInt() and 0x1F
        }

        // Combined Annex-B payloads contain inline SPS/PPS/Slice with start codes.
        // MediaCodec natively extracts inline Annex-B headers when flags = 0.
        val flags = 0

        // Clear CSD state on any valid frame (Intra-Refresh P-slice type 1 or IDR type 5)
        if (pendingCsdAfterFlush && (nalType == 1 || nalType == 5)) {
            pendingCsdAfterFlush = false
            Log.i("Discap.H264", "[DEC] CSD flush complete — first frame received (nalType=$nalType)")
        }

        return try {
            // Feed Access Unit to decoder
            val inputBufferIndex = codec.dequeueInputBuffer(10000) // 10ms timeout
            if (inputBufferIndex >= 0) {
                val inputBuffer: ByteBuffer? = codec.getInputBuffer(inputBufferIndex)
                if (inputBuffer != null) {
                    inputBuffer.clear()
                    inputBuffer.put(nalData, offset, length)
                    // Use local Android System.nanoTime() to prevent SurfaceFlinger buffer queue accumulation from PC/Tablet clock drift
                    val localPresentationUs = System.nanoTime() / 1000L
                    codec.queueInputBuffer(inputBufferIndex, 0, length, localPresentationUs, flags)
                    true
                } else {
                    false
                }
            } else {
                false // Codec busy, caller will retry this packet
            }
        } catch (e: Exception) {
            Log.e("Discap.H264", "Decode error: ${e.message}")
            false
        }
    }

    private data class DecodedFrame(
        val index: Int,
        val presentationTimeUs: Long,
        val dequeueTimeNs: Long
    )

    private fun detectDisplayRefreshRate(): Float {
        return if (displayRefreshRateHz > 0f) displayRefreshRateHz else 144f
    }

    private fun drainOutput() {
        android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_DISPLAY)
        val bufferInfo = MediaCodec.BufferInfo()
        val pendingQueue = java.util.ArrayDeque<DecodedFrame>()

        val TARGET_BUFFER_DEPTH = 2
        val MAX_BUFFER_DEPTH = 4

        var framesRendered = 0
        var framesDropped = 0
        var totalCadenceMs = 0.0
        var cadenceSamples = 0
        var totalQueueDepth = 0L
        var depthSamples = 0L

        var lastReleaseTimeNs = 0L
        var lastLogTime = System.currentTimeMillis()

        // Fixed target release cadence derived strictly from the display panel refresh rate
        val actualRefreshRate = detectDisplayRefreshRate()
        val targetIntervalNs = (1_000_000_000L / actualRefreshRate.toDouble()).toLong()
        val targetMs = targetIntervalNs / 1_000_000.0

        Log.i("Discap.H264", "[DEC-PACING] Initialized steady-clock presentation buffer. Panel refresh rate: ${actualRefreshRate}Hz (constant target: ${String.format("%.2f", targetMs)}ms), target depth: $TARGET_BUFFER_DEPTH, max depth: $MAX_BUFFER_DEPTH")

        while (isRunning) {
            try {
                val codec = codec ?: break

                // 1. Dequeue decoded output buffers from MediaCodec (non-blocking 1ms timeout)
                val outputBufferIndex = codec.dequeueOutputBuffer(bufferInfo, 1000)

                if (outputBufferIndex >= 0) {
                    // ZERO-LATENCY IMMEDIATE RELEASE: Render frame the exact microsecond MediaCodec finishes decoding it
                    codec.releaseOutputBuffer(outputBufferIndex, true)
                    framesRendered++

                    // Drain any additional immediately available output buffers
                    while (true) {
                        val nextIndex = codec.dequeueOutputBuffer(bufferInfo, 0)
                        if (nextIndex >= 0) {
                            codec.releaseOutputBuffer(nextIndex, true)
                            framesRendered++
                        } else {
                            break
                        }
                    }
                } else if (outputBufferIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    val newFormat = codec.outputFormat
                    Log.i("Discap.H264", "Output format changed: $newFormat")
                    val fmtW = if (newFormat.containsKey(MediaFormat.KEY_WIDTH)) newFormat.getInteger(MediaFormat.KEY_WIDTH) else width
                    val fmtH = if (newFormat.containsKey(MediaFormat.KEY_HEIGHT)) newFormat.getInteger(MediaFormat.KEY_HEIGHT) else height
                    onResolutionDetected?.invoke(fmtW, fmtH)
                }

                // 4. PERIODIC STATS LOGGING (every 1 second)
                val nowMs = System.currentTimeMillis()
                if (nowMs - lastLogTime >= 1000) {
                    framesRendered = 0
                    framesDropped = 0
                    totalCadenceMs = 0.0
                    cadenceSamples = 0
                    totalQueueDepth = 0L
                    depthSamples = 0L
                    lastLogTime = nowMs
                }

                // Small sleep to prevent CPU spinning when waiting for next tick
                if (pendingQueue.isEmpty()) {
                    Thread.sleep(1)
                } else {
                    val sleepMs = ((lastReleaseTimeNs + targetIntervalNs - System.nanoTime()) / 1_000_000L).coerceIn(0L, 2L)
                    if (sleepMs > 0) {
                        Thread.sleep(sleepMs)
                    }
                }

            } catch (e: Exception) {
                if (isRunning) {
                    Log.e("Discap.H264", "Paced drain error: ${e.message}")
                }
                break
            }
        }

        // Clean up any remaining unrendered buffers in queue on stop
        while (!pendingQueue.isEmpty()) {
            try {
                val f = pendingQueue.removeFirst()
                codec?.releaseOutputBuffer(f.index, false)
            } catch (e: Exception) {}
        }
    }

    fun release() {
        isConfigured = false
        isRunning = false
        try {
            outputThread?.interrupt()
            outputThread?.join(100)
        } catch (e: Exception) {}
        
        try {
            codec?.stop()
            codec?.release()
        } catch (e: Exception) {}
        codec = null
    }
}
