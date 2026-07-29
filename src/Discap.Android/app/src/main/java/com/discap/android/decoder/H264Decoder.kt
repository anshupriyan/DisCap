package com.discap.android.decoder

import android.media.MediaCodec
import android.media.MediaFormat
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer

class H264Decoder(private val surface: Surface, var width: Int, var height: Int) {

    var onResolutionDetected: ((width: Int, height: Int) -> Unit)? = null
    private var codec: MediaCodec? = null
    private var isConfigured = false
    @Volatile private var isRunning = false
    private var outputThread: Thread? = null

    fun start() {
        try {
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height)
            format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1) // Critical for zero-latency decoding
            format.setInteger(MediaFormat.KEY_MAX_WIDTH, 3840)
            format.setInteger(MediaFormat.KEY_MAX_HEIGHT, 2160)
            
            codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            
            // Output directly to the Surface for zero-copy rendering
            codec?.configure(format, surface, null, 0)
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

    fun decode(nalData: ByteArray, offset: Int, length: Int, timestampUs: Long) {
        if (!isConfigured) return
        val codec = codec ?: return

        // Parse NAL unit type
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

        var flags = 0
        if (pendingCsdAfterFlush) {
            if (nalType == 7 || nalType == 8) {
                flags = MediaCodec.BUFFER_FLAG_CODEC_CONFIG
            } else if (nalType == 5) {
                pendingCsdAfterFlush = false
                Log.i("Discap.H264", "[DEC] CSD flush complete — IDR received, decoder reconfigured")
            }
        }

        try {
            // 1. Feed NAL unit to decoder
            val inputBufferIndex = codec.dequeueInputBuffer(10000) // 10ms timeout
            if (inputBufferIndex >= 0) {
                val inputBuffer: ByteBuffer? = codec.getInputBuffer(inputBufferIndex)
                if (inputBuffer != null) {
                    inputBuffer.clear()
                    inputBuffer.put(nalData, offset, length)
                    codec.queueInputBuffer(inputBufferIndex, 0, length, timestampUs, flags)
                }
            }
        } catch (e: Exception) {
            Log.e("Discap.H264", "Decode error: ${e.message}")
        }
    }

    private fun drainOutput() {
        val bufferInfo = MediaCodec.BufferInfo()
        var framesRendered = 0
        var framesDropped = 0
        var totalDequeueMs = 0.0
        var lastLogTime = System.currentTimeMillis()

        while (isRunning) {
            try {
                val codec = codec ?: break

                // Dequeue first available output buffer (10ms timeout)
                val t0 = System.nanoTime()
                var outputBufferIndex = codec.dequeueOutputBuffer(bufferInfo, 10000)
                val t1 = System.nanoTime()

                if (outputBufferIndex >= 0) {
                    // Got a decoded frame. Now check if more are immediately available —
                    // if so, this frame is stale and we should skip to the newest one.
                    // This prevents BLASTBufferQueue overflow during post-idle bursts
                    // where multiple frames arrive faster than the surface can consume.
                    val nextInfo = MediaCodec.BufferInfo()
                    var newestIndex = outputBufferIndex

                    while (true) {
                        val nextIndex = codec.dequeueOutputBuffer(nextInfo, 0) // non-blocking
                        if (nextIndex >= 0) {
                            // A newer frame is available — drop the older one without rendering
                            codec.releaseOutputBuffer(newestIndex, false)
                            framesDropped++
                            newestIndex = nextIndex
                        } else {
                            break
                        }
                    }

                    // Render the newest frame immediately (true = render now).
                    // We avoid scheduled renderTimestampNs because it holds buffers
                    // in the BLASTBufferQueue waiting for their presentation time,
                    // which causes "Can't acquire next buffer" overflow on bursts.
                    codec.releaseOutputBuffer(newestIndex, true)

                    val dequeueMs = (t1 - t0) / 1000000.0
                    totalDequeueMs += dequeueMs
                    framesRendered++

                    val now = System.currentTimeMillis()
                    if (now - lastLogTime >= 1000) {
                        val avgDequeue = if (framesRendered > 0) totalDequeueMs / framesRendered else 0.0
                        Log.i("Discap.H264", "[DEC-STATS] FPS: $framesRendered | Dropped: $framesDropped | Avg dequeue: ${String.format("%.2f", avgDequeue)}ms")
                        framesRendered = 0
                        framesDropped = 0
                        totalDequeueMs = 0.0
                        lastLogTime = now
                    }
                } else if (outputBufferIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    val newFormat = codec.outputFormat
                    Log.i("Discap.H264", "Output format changed: $newFormat")
                    val fmtW = if (newFormat.containsKey(MediaFormat.KEY_WIDTH)) newFormat.getInteger(MediaFormat.KEY_WIDTH) else width
                    val fmtH = if (newFormat.containsKey(MediaFormat.KEY_HEIGHT)) newFormat.getInteger(MediaFormat.KEY_HEIGHT) else height
                    onResolutionDetected?.invoke(fmtW, fmtH)
                }
            } catch (e: Exception) {
                Log.e("Discap.H264", "Drain error: ${e.message}")
                break
            }
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
