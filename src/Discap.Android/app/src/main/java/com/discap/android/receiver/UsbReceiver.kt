package com.discap.android.receiver

import android.os.ParcelFileDescriptor
import android.os.SystemClock
import android.util.Log
import com.discap.android.decoder.H264Decoder
import java.io.FileInputStream
import java.io.InputStream

class UsbReceiver(private val pfd: ParcelFileDescriptor) {

    private var running = false
    private var thread: Thread? = null
    private var decoder: H264Decoder? = null
    private val TAG = "UsbReceiver"

    fun setDecoder(decoder: H264Decoder) {
        this.decoder = decoder
    }

    fun start() {
        if (running) return
        running = true
        thread = Thread { receiveLoop() }
        thread?.start()
    }

    fun stop() {
        running = false
        try {
            pfd.close()
        } catch (e: Exception) {
            Log.e(TAG, "Error closing ParcelFileDescriptor", e)
        }
        thread?.interrupt()
        thread = null
    }

    private fun receiveLoop() {
        val inputStream: InputStream = FileInputStream(pfd.fileDescriptor)
        val buffer = ByteArray(65536)
        Log.i(TAG, "Starting USB receive loop")

        try {
            while (running) {
                val bytesRead = inputStream.read(buffer)
                if (bytesRead < 0) {
                    Log.i(TAG, "USB stream ended")
                    break
                }
                if (bytesRead > 0) {
                    // Feed directly to decoder.
                    decoder?.decode(buffer, 0, bytesRead)
                }
            }
        } catch (e: Exception) {
            if (running) {
                Log.e(TAG, "USB Receive error", e)
            }
        } finally {
            Log.i(TAG, "USB receive loop exited")
            stop()
        }
    }
}
