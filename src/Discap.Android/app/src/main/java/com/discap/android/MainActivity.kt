package com.discap.android

import android.app.Activity
import android.graphics.Color
import android.os.Bundle
import android.util.Log
import android.view.Gravity
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.SeekBar
import android.widget.TextView
import com.discap.android.receiver.SocketReceiver
import com.discap.android.receiver.UsbReceiver
import com.discap.android.decoder.H264Decoder
import android.hardware.usb.UsbManager
import android.hardware.usb.UsbAccessory
import android.content.Intent

class MainActivity : Activity(), SurfaceHolder.Callback {

    private lateinit var surfaceView: SurfaceView
    private lateinit var settingsPanel: LinearLayout
    private lateinit var statsView: TextView
    private lateinit var bitrateValue: TextView
    private var socketReceiver: SocketReceiver? = null
    private var usbReceiver: UsbReceiver? = null
    private var isUsbMode = false

    private var bitrateMbps = 20
    private var fpsCap = 60
    private var resolutionScale = 100
    private var encoderMode = ENCODER_AUTO
    private var showStats = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        window.decorView.systemUiVisibility = (View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                or View.SYSTEM_UI_FLAG_FULLSCREEN
                or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN)

        surfaceView = SurfaceView(this)
        surfaceView.holder.addCallback(this)
        surfaceView.setOnTouchListener { _, event -> sendTouch(event) }

        val root = FrameLayout(this)
        root.setBackgroundColor(Color.BLACK)
        root.addView(surfaceView, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            FrameLayout.LayoutParams.MATCH_PARENT
        ))

        statsView = TextView(this).apply {
            setTextColor(Color.WHITE)
            setBackgroundColor(0x99000000.toInt())
            textSize = 13f
            setPadding(14, 10, 14, 10)
            visibility = View.GONE
        }
        root.addView(statsView, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.WRAP_CONTENT,
            FrameLayout.LayoutParams.WRAP_CONTENT,
            Gravity.TOP or Gravity.START
        ))

        settingsPanel = buildSettingsPanel()
        root.addView(settingsPanel, FrameLayout.LayoutParams(
            dp(300),
            FrameLayout.LayoutParams.WRAP_CONTENT,
            Gravity.TOP or Gravity.END
        ).apply {
            topMargin = dp(64)
            rightMargin = dp(12)
        })

        val settingsButton = Button(this).apply {
            text = "Settings"
            setOnClickListener {
                settingsPanel.visibility = if (settingsPanel.visibility == View.VISIBLE) View.GONE else View.VISIBLE
            }
        }
        root.addView(settingsButton, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.WRAP_CONTENT,
            FrameLayout.LayoutParams.WRAP_CONTENT,
            Gravity.TOP or Gravity.END
        ).apply {
            topMargin = dp(12)
            rightMargin = dp(12)
        })

        setContentView(root)
    }

    private fun buildSettingsPanel(): LinearLayout {
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(0xCC111111.toInt())
            setPadding(dp(12), dp(10), dp(12), dp(10))
            visibility = View.GONE

            addView(label("Bitrate"))
            bitrateValue = label("${bitrateMbps} Mbps")
            addView(bitrateValue)
            addView(SeekBar(this@MainActivity).apply {
                max = 45
                progress = bitrateMbps - 5
                setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                    override fun onProgressChanged(seekBar: SeekBar?, progress: Int, fromUser: Boolean) {
                        bitrateMbps = progress + 5
                        bitrateValue.text = "${bitrateMbps} Mbps"
                        if (fromUser) sendSettings()
                    }
                    override fun onStartTrackingTouch(seekBar: SeekBar?) {}
                    override fun onStopTrackingTouch(seekBar: SeekBar?) = sendSettings()
                })
            })

            addView(label("FPS cap"))
            addView(buttonRow(listOf("30" to 30, "60" to 60, "120" to 120)) { fpsCap = it })

            addView(label("Resolution scale"))
            addView(buttonRow(listOf("50%" to 50, "75%" to 75, "100%" to 100)) {
                resolutionScale = it
                surfaceView.scaleX = it / 100f
                surfaceView.scaleY = it / 100f
            })

            addView(label("Encoder"))
            addView(buttonRow(listOf("H.264" to ENCODER_H264, "LZ4" to ENCODER_LZ4, "Auto" to ENCODER_AUTO)) {
                encoderMode = it
            })

            addView(Button(this@MainActivity).apply {
                text = "Stats off"
                setOnClickListener {
                    showStats = !showStats
                    text = if (showStats) "Stats on" else "Stats off"
                    statsView.visibility = if (showStats) View.VISIBLE else View.GONE
                    sendSettings()
                }
            })
        }
    }

    private fun buttonRow(items: List<Pair<String, Int>>, setter: (Int) -> Unit): LinearLayout {
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            items.forEach { (text, value) ->
                addView(Button(this@MainActivity).apply {
                    this.text = text
                    setOnClickListener {
                        setter(value)
                        sendSettings()
                    }
                }, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
            }
        }
    }

    private fun label(text: String): TextView {
        return TextView(this).apply {
            this.text = text
            setTextColor(Color.WHITE)
            textSize = 13f
            setPadding(0, dp(6), 0, dp(2))
        }
    }

    private fun sendSettings() {
        socketReceiver?.sender?.sendSettings(
            bitrateMbps,
            fpsCap,
            resolutionScale,
            encoderMode,
            showStats
        )
    }

    private fun sendTouch(event: MotionEvent): Boolean {
        val sender = socketReceiver?.sender ?: return false

        val xNorm = event.x / surfaceView.width
        val yNorm = event.y / surfaceView.height

        val action = when (event.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> 1.toByte()
            MotionEvent.ACTION_MOVE -> 2.toByte()
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP, MotionEvent.ACTION_CANCEL -> 0.toByte()
            else -> return false
        }

        val button = if (action == 0.toByte()) 0.toByte() else 1.toByte()
        val pressure = (event.pressure * 255).toInt().toByte()

        sender.sendInput(xNorm, yNorm, action, button, pressure)
        return true
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        val width = surfaceView.width
        val height = surfaceView.height
        val isNull = holder.surface == null
        Log.i("Discap", "[SURF] Surface created: ${width}x${height}")
        Log.i("Discap", "[SURF] Surface passed to decoder: $isNull")

        isUsbMode = false
        if (intent?.action == UsbManager.ACTION_USB_ACCESSORY_ATTACHED) {
            val accessory = intent?.getParcelableExtra<UsbAccessory>(UsbManager.EXTRA_ACCESSORY)
            if (accessory != null) {
                val usbManager = getSystemService(USB_SERVICE) as UsbManager
                try {
                    val pfd = usbManager.openAccessory(accessory)
                    if (pfd != null) {
                        Log.i("Discap", "Starting UsbReceiver for AOA")
                        isUsbMode = true
                        
                        val decoder = H264Decoder(holder.surface, width, height)
                        decoder.start()
                        
                        usbReceiver = UsbReceiver(pfd)
                        usbReceiver?.setDecoder(decoder)
                        usbReceiver?.start()
                    }
                } catch (e: Exception) {
                    Log.e("Discap", "Failed to open USB accessory", e)
                }
            }
        }

        if (!isUsbMode) {
            Log.i("Discap", "Starting SocketReceiver (ADB Fallback)...")
            socketReceiver = SocketReceiver(holder.surface) { stats ->
                runOnUiThread {
                    statsView.text = "FPS ${"%.1f".format(stats.fps)}  ${"%.1f".format(stats.bitrateMbps)} Mbps\n" +
                            "Latency ${"%.1f".format(stats.latencyMs)} ms  ${stats.encoderType}"
                }
            }
            socketReceiver?.start()
        }

        sendSettings()
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        // Surface is consumed directly by MediaCodec and the LZ4 canvas path.
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        Log.i("Discap", "Surface destroyed. Stopping receiver...")
        socketReceiver?.stopReceiver()
        socketReceiver = null
        usbReceiver?.stop()
        usbReceiver = null
    }

    override fun onDestroy() {
        super.onDestroy()
        socketReceiver?.stopReceiver()
        usbReceiver?.stop()
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    companion object {
        private const val ENCODER_AUTO = 0
        private const val ENCODER_H264 = 1
        private const val ENCODER_LZ4 = 2
    }
}
