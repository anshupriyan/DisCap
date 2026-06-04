package com.discap.android.decoder

import android.media.MediaCodec
import android.media.MediaFormat
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer

class H264Decoder(private val surface: Surface, val width: Int, val height: Int) {

    private var codec: MediaCodec? = null
    private var isConfigured = false

    fun start() {
        try {
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height)
            
            codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            
            // Output directly to the Surface for zero-copy rendering
            codec?.configure(format, surface, null, 0)
            codec?.start()
            isConfigured = true
            Log.i("Discap.H264", "Decoder started for ${width}x${height}")
        } catch (e: Exception) {
            Log.e("Discap.H264", "Failed to start MediaCodec: ${e.message}")
            isConfigured = false
        }
    }

    fun decode(nalData: ByteArray, offset: Int, length: Int) {
        if (!isConfigured) return
        val codec = codec ?: return

        try {
            // 1. Feed NAL unit to decoder
            val inputBufferIndex = codec.dequeueInputBuffer(10000) // 10ms timeout
            if (inputBufferIndex >= 0) {
                val inputBuffer: ByteBuffer? = codec.getInputBuffer(inputBufferIndex)
                if (inputBuffer != null) {
                    inputBuffer.clear()
                    inputBuffer.put(nalData, offset, length)
                    // We don't have accurate PTS here yet, just using system time for now
                    val pts = System.nanoTime() / 1000
                    codec.queueInputBuffer(inputBufferIndex, 0, length, pts, 0)
                }
            }

            // 2. Pull decoded frames from decoder and render
            val bufferInfo = MediaCodec.BufferInfo()
            var outputBufferIndex = codec.dequeueOutputBuffer(bufferInfo, 0)
            
            while (outputBufferIndex >= 0) {
                // true = render to surface immediately
                codec.releaseOutputBuffer(outputBufferIndex, true)
                outputBufferIndex = codec.dequeueOutputBuffer(bufferInfo, 0)
            }
            
            if (outputBufferIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                Log.i("Discap.H264", "Output format changed: ${codec.outputFormat}")
            }
        } catch (e: Exception) {
            Log.e("Discap.H264", "Decode error: ${e.message}")
        }
    }

    fun release() {
        isConfigured = false
        try {
            codec?.stop()
            codec?.release()
        } catch (e: Exception) {}
        codec = null
    }
}
