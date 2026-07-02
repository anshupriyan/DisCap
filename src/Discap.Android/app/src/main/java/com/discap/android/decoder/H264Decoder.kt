package com.discap.android.decoder

import android.media.MediaCodec
import android.media.MediaFormat
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer

class H264Decoder(private val surface: Surface, var width: Int, var height: Int) {

    private var codec: MediaCodec? = null
    private var isConfigured = false
    @Volatile private var isRunning = false
    private var outputThread: Thread? = null
    @Volatile private var streamBaseUs: Long = -1L

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

        if (streamBaseUs == -1L) {
            streamBaseUs = (System.nanoTime() / 1000) - timestampUs
        }

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
        var totalInputMs = 0.0
        var totalDequeueMs = 0.0
        var lastLogTime = System.currentTimeMillis()

        while (isRunning) {
            try {
                val codec = codec ?: break
                val t0 = System.nanoTime()
                val outputBufferIndex = codec.dequeueOutputBuffer(bufferInfo, 10000)
                
                if (outputBufferIndex >= 0) {
                    val t1 = System.nanoTime()
                    val ptsUs = bufferInfo.presentationTimeUs
                    val renderTimestampNs = (streamBaseUs + ptsUs) * 1000
                    codec.releaseOutputBuffer(outputBufferIndex, renderTimestampNs)
                    
                    val dequeueMs = (t1 - t0) / 1000000.0
                    totalDequeueMs += dequeueMs
                    framesRendered++
                    
                    val now = System.currentTimeMillis()
                    if (now - lastLogTime >= 1000) {
                        Log.i("Discap.H264", "[DEC-STATS] FPS: $framesRendered | Avg dequeue: ${String.format("%.2f", totalDequeueMs/framesRendered)}ms")
                        framesRendered = 0
                        totalDequeueMs = 0.0
                        totalInputMs = 0.0
                        lastLogTime = now
                    }
                } else if (outputBufferIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    Log.i("Discap.H264", "Output format changed: ${codec.outputFormat}")
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
