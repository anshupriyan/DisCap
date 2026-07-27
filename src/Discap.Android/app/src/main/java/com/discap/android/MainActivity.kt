package com.discap.android

import android.app.Activity
import android.graphics.Color
import android.graphics.PixelFormat
import android.graphics.SurfaceTexture
import android.os.Bundle
import android.util.Log
import android.view.Gravity
import android.view.MotionEvent
import android.view.Surface
import android.view.TextureView
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

class MainActivity : Activity(), TextureView.SurfaceTextureListener {

    private lateinit var textureView: TextureView
    private var activeSurface: Surface? = null
    private lateinit var cursorOverlayView: com.discap.android.overlay.CursorOverlayView
    private lateinit var cursorManager: com.discap.android.overlay.CursorManager
    private lateinit var settingsPanel: LinearLayout
    private lateinit var statsView: TextView
    private lateinit var bitrateValue: TextView
    private var socketReceiver: SocketReceiver? = null
    private var usbReceiver: UsbReceiver? = null
    private var isUsbMode = false

    private var bitrateMbps = 20
    private var fpsCap = 0  // 0 = Native (no cap, matches display refresh rate)
    private var resolutionScale = 100
    private var encoderMode = ENCODER_AUTO
    private var showStats = false
    private var cqLevel = 28  // NVENC Target Quality (range 15-40, default 28)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        val display = windowManager.defaultDisplay
        val modes = display.supportedModes
        var maxHz = 0f
        var bestMode = -1
        for (mode in modes) {
            if (mode.refreshRate > maxHz) {
                maxHz = mode.refreshRate
                bestMode = mode.modeId
            }
        }
        val params = window.attributes
        if (bestMode != -1) {
            params.preferredDisplayModeId = bestMode
        }
        window.attributes = params
        Log.i("Discap", "[SURF] Supported modes: ${modes.joinToString { "${it.refreshRate}Hz" }}, Selected best mode: $maxHz Hz")
        
        window.decorView.systemUiVisibility = (View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                or View.SYSTEM_UI_FLAG_FULLSCREEN
                or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN)

        textureView = TextureView(this)
        textureView.surfaceTextureListener = this
        textureView.setOnTouchListener { _, event -> sendTouch(event) }

        cursorOverlayView = com.discap.android.overlay.CursorOverlayView(this).apply {
            isClickable = false
            isFocusable = false
        }
        cursorManager = com.discap.android.overlay.CursorManager(cursorOverlayView)

        val root = FrameLayout(this)
        root.setBackgroundColor(Color.BLACK)
        root.addView(textureView, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            FrameLayout.LayoutParams.MATCH_PARENT
        ))
        root.addView(cursorOverlayView, FrameLayout.LayoutParams(
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

            addView(label("Target bitrate (Mbps) — actual usage scales with content, up to a high ceiling automatically"))
            bitrateValue = label(if (bitrateMbps >= 150) "Uncapped (Max Peak)" else "${bitrateMbps} Mbps")
            addView(bitrateValue)
            addView(SeekBar(this@MainActivity).apply {
                max = 145
                progress = bitrateMbps - 5
                setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                    override fun onProgressChanged(seekBar: SeekBar?, progress: Int, fromUser: Boolean) {
                        bitrateMbps = progress + 5
                        if (bitrateMbps >= 150) {
                            bitrateValue.text = "Uncapped (Max Peak)"
                        } else {
                            bitrateValue.text = "${bitrateMbps} Mbps"
                        }
                        if (fromUser) sendSettings()
                    }
                    override fun onStartTrackingTouch(seekBar: SeekBar?) {}
                    override fun onStopTrackingTouch(seekBar: SeekBar?) = sendSettings()
                })
            })

            addView(label("Target Quality / Compression Level (CQ) (Lower = Better Quality, Higher = More Compression)"))
            val cqValue = label("${cqLevel} (Default: 28)")
            addView(cqValue)
            addView(SeekBar(this@MainActivity).apply {
                max = 25  // range: 15 to 40
                progress = cqLevel - 15
                setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                    override fun onProgressChanged(seekBar: SeekBar?, progress: Int, fromUser: Boolean) {
                        cqLevel = progress + 15
                        var labelText = "$cqLevel"
                        if (cqLevel == 15) labelText += " (Best)"
                        if (cqLevel == 28) labelText += " (Default)"
                        if (cqLevel == 40) labelText += " (Lowest)"
                        cqValue.text = labelText
                        if (fromUser) sendSettings()
                    }
                    override fun onStartTrackingTouch(seekBar: SeekBar?) {}
                    override fun onStopTrackingTouch(seekBar: SeekBar?) = sendSettings()
                })
            })

            addView(label("FPS cap (Native = matches display refresh rate)"))
            addView(buttonRow(listOf("Native" to 0, "30" to 30, "60" to 60, "120" to 120, "144" to 144)) { fpsCap = it })

            addView(label("Resolution scale"))
            addView(buttonRow(listOf("50%" to 50, "75%" to 75, "100%" to 100)) {
                resolutionScale = it
                textureView.scaleX = it / 100f
                textureView.scaleY = it / 100f
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
            showStats,
            cqLevel
        )
    }

    private fun sendTouch(event: MotionEvent): Boolean {
        val sender = socketReceiver?.sender ?: return false

        val xNorm = event.x / textureView.width
        val yNorm = event.y / textureView.height

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

    override fun onSurfaceTextureAvailable(surfaceTexture: SurfaceTexture, width: Int, height: Int) {
        Log.i("Discap", "[SURF] TextureView SurfaceTexture available: ${width}x${height}")
        val surface = Surface(surfaceTexture)
        activeSurface = surface

        isUsbMode = false
        if (intent?.action == UsbManager.ACTION_USB_ACCESSORY_ATTACHED) {
            val accessory = intent?.getParcelableExtra<UsbAccessory>(UsbManager.EXTRA_ACCESSORY)
            if (accessory != null) {
                startUsbMode(accessory, surface)
            }
        }

        if (!isUsbMode) {
            Log.i("Discap", "Starting SocketReceiver (ADB Fallback)...")
            socketReceiver = SocketReceiver(surface, cursorManager, { w, h -> handleVideoSizeChanged(w, h) }) { stats ->
                runOnUiThread {
                    statsView.text = "FPS ${"%.1f".format(stats.fps)}  ${"%.1f".format(stats.bitrateMbps)} Mbps\n" +
                            "Latency ${"%.1f".format(stats.latencyMs)} ms  ${stats.encoderType}"
                }
            }
            socketReceiver?.start()
        }

        sendSettings()
    }

    override fun onSurfaceTextureSizeChanged(surfaceTexture: SurfaceTexture, width: Int, height: Int) {
    }

    override fun onSurfaceTextureDestroyed(surfaceTexture: SurfaceTexture): Boolean {
        Log.i("Discap", "SurfaceTexture destroyed. Stopping receiver...")
        socketReceiver?.stopReceiver()
        socketReceiver = null
        usbReceiver?.stop()
        usbReceiver = null
        activeSurface?.release()
        activeSurface = null
        return true
    }

    override fun onSurfaceTextureUpdated(surfaceTexture: SurfaceTexture) {
    }

    private fun handleVideoSizeChanged(videoWidth: Int, videoHeight: Int) {
        runOnUiThread {
            if (videoWidth == 0 || videoHeight == 0) return@runOnUiThread
            
            val parent = textureView.parent as? View ?: return@runOnUiThread
            val parentWidth = parent.width
            val parentHeight = parent.height
            if (parentWidth == 0 || parentHeight == 0) return@runOnUiThread
            
            val videoRatio = videoWidth.toFloat() / videoHeight.toFloat()
            val screenRatio = parentWidth.toFloat() / parentHeight.toFloat()
            
            val lp = textureView.layoutParams as FrameLayout.LayoutParams
            if (videoRatio > screenRatio) {
                // Video is wider than screen -> letterbox (black bars top/bottom)
                lp.width = parentWidth
                lp.height = (parentWidth / videoRatio).toInt()
            } else {
                // Video is taller than screen -> pillarbox (black bars left/right)
                lp.width = (parentHeight * videoRatio).toInt()
                lp.height = parentHeight
            }
            lp.gravity = Gravity.CENTER
            textureView.layoutParams = lp
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        socketReceiver?.stopReceiver()
        usbReceiver?.stop()
        activeSurface?.release()
        activeSurface = null
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    override fun onNewIntent(newIntent: Intent?) {
        super.onNewIntent(newIntent)
        setIntent(newIntent)
        if (newIntent?.action == UsbManager.ACTION_USB_ACCESSORY_ATTACHED) {
            val accessory = newIntent.getParcelableExtra<UsbAccessory>(UsbManager.EXTRA_ACCESSORY)
            val surface = activeSurface
            if (accessory != null && surface != null && surface.isValid) {
                startUsbMode(accessory, surface)
            }
        }
    }

    private fun startUsbMode(accessory: UsbAccessory, surface: Surface) {
        val usbManager = getSystemService(USB_SERVICE) as UsbManager
        try {
            val pfd = usbManager.openAccessory(accessory)
            if (pfd != null) {
                Log.i("Discap", "Starting UsbReceiver for AOA")
                isUsbMode = true
                
                socketReceiver?.stopReceiver()
                socketReceiver = null

                usbReceiver?.stop()
                usbReceiver = UsbReceiver(pfd, surface, cursorManager, { w, h -> handleVideoSizeChanged(w, h) }) { stats ->
                    runOnUiThread {
                        statsView.text = "FPS ${"%.1f".format(stats.fps)}  ${"%.1f".format(stats.bitrateMbps)} Mbps\n" +
                                "Latency ${"%.1f".format(stats.latencyMs)} ms  ${stats.encoderType} (USB)"
                        statsView.visibility = if (showStats) View.VISIBLE else View.GONE
                    }
                }
                usbReceiver?.start()
            }
        } catch (e: Exception) {
            Log.e("Discap", "Failed to open USB accessory", e)
        }
    }

    companion object {
        private const val ENCODER_AUTO = 0
        private const val ENCODER_H264 = 1
        private const val ENCODER_LZ4 = 2
    }
}
