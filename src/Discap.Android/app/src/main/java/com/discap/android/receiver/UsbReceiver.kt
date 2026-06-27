package com.discap.android.receiver

import android.os.ParcelFileDescriptor
import android.util.Log
import android.view.Surface
import com.discap.android.decoder.H264Decoder
import com.discap.android.decoder.Lz4Decoder
import java.io.DataInputStream
import java.io.EOFException
import java.io.FileInputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.max

class UsbReceiver(
    private val pfd: ParcelFileDescriptor,
    private val surface: Surface,
    private val onVideoSizeChanged: ((Int, Int) -> Unit)? = null,
    private val statsCallback: ((SocketReceiver.FrameStats) -> Unit)? = null
) {
    private var isRunning = false
    private var thread: Thread? = null

    private var h264Decoder: H264Decoder? = null
    private var lz4Decoder: Lz4Decoder? = null

    fun start() {
        if (isRunning) return
        isRunning = true
        thread = Thread { receiveLoop() }.apply { start() }
    }

    fun stop() {
        isRunning = false
        try { pfd.close() } catch (e: Exception) {}
        thread?.interrupt()
        thread = null
        h264Decoder?.release()
        lz4Decoder?.release()
    }

    private fun receiveLoop() {
        Log.i("Discap.Usb", "[AOAP] Accessory opened, starting USB read loop")
        val headerBuffer = ByteArray(32)
        var payloadBuffer = ByteArray(2 * 1024 * 1024)

        try {
            val input = DataInputStream(FileInputStream(pfd.fileDescriptor))
            var statsFrames = 0
            var statsBytes = 0L
            var statsStartNs = System.nanoTime()
            var streamBaseUs: Long? = null
            var lastTimestampUs: Long = -1
            var lastWidth = 0
            var lastHeight = 0

            while (isRunning) {
                input.readFully(headerBuffer)

                val bb = ByteBuffer.wrap(headerBuffer).order(ByteOrder.LITTLE_ENDIAN)
                val magic = bb.getInt()
                if (magic != 0x44434150) { // "DCAP"
                    Log.e("Discap.Usb", "Invalid magic header! Expected 0x44434150, got 0x${Integer.toHexString(magic)}.")
                    break
                }

                bb.get() // version
                val frameType = bb.get() // 1 = LZ4, 2 = NVENC
                val width = bb.getShort().toInt()
                val height = bb.getShort().toInt()
                val originalSize = bb.getInt()
                val compressedSize = bb.getInt()
                val timestampUs = bb.getLong()

                if (compressedSize > payloadBuffer.size) {
                    payloadBuffer = ByteArray(compressedSize)
                }

                input.readFully(payloadBuffer, 0, compressedSize)

                if (width != lastWidth || height != lastHeight) {
                    lastWidth = width
                    lastHeight = height
                    onVideoSizeChanged?.invoke(width, height)
                }

                if (frameType.toInt() == 2) {
                    if (h264Decoder == null || h264Decoder!!.width != width || h264Decoder!!.height != height) {
                        h264Decoder?.release()
                        h264Decoder = H264Decoder(surface, width, height)
                        h264Decoder?.start()
                    }
                    h264Decoder?.decode(payloadBuffer, 0, compressedSize)
                } else if (frameType.toInt() == 1) {
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
                    statsCallback?.invoke(SocketReceiver.FrameStats(fps, bitrate, latency, encoder))
                    statsFrames = 0
                    statsBytes = 0
                    statsStartNs = nowNs
                }
            }
        } catch (e: EOFException) {
            Log.w("Discap.Usb", "Host closed USB connection.")
        } catch (e: Exception) {
            if (isRunning) {
                Log.e("Discap.Usb", "USB Receive error", e)
            }
        } finally {
            Log.i("Discap.Usb", "USB receive loop exited")
            stop()
        }
    }
}
