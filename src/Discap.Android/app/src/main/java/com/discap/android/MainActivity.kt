package com.discap.android

import android.app.Activity
import android.content.Intent
import android.graphics.Color
import android.hardware.usb.UsbAccessory
import android.hardware.usb.UsbManager
import android.opengl.GLSurfaceView
import android.os.Bundle
import android.util.Log
import android.view.Gravity
import android.view.MotionEvent
import android.view.Surface
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.SeekBar
import android.widget.TextView
import com.discap.android.receiver.SocketReceiver
import com.discap.android.receiver.UsbReceiver
import com.discap.android.renderer.OpenGLRenderer
import javax.microedition.khronos.egl.EGLConfig
import javax.microedition.khronos.opengles.GL10

class MainActivity : Activity() {

    private lateinit var glSurfaceView: GLSurfaceView
    private var activeSurface: Surface? = null
    private lateinit var cursorOverlayView: com.discap.android.overlay.CursorOverlayView
    private lateinit var cursorManager: com.discap.android.overlay.CursorManager
    private lateinit var settingsPanel: LinearLayout
    private lateinit var statsView: TextView
    private lateinit var bitrateValue: TextView
    private var socketReceiver: SocketReceiver? = null
    private var usbReceiver: UsbReceiver? = null
    private var isUsbMode = false

    private var openGLRenderer: OpenGLRenderer? = null
    private var casSharpeningPercent = 50
    private lateinit var detectedStreamResLabel: TextView
    private lateinit var casSharpnessValueLabel: TextView
    private var currentStreamW = 1920
    private var currentStreamH = 1080
    private var bitrateMbps = 20
    private var fpsCap = 0  // 0 = Native (no cap, matches display refresh rate)
    private var resolutionScale = 100
    private var encoderMode = ENCODER_AUTO
    private var showStats = false
    private var cqLevel = 28  // NVENC Target Quality (range 15-40, default 28)
    private var upscaleTargetTier = 200
    private var scaleModeOrdinal = OpenGLRenderer.ScaleMode.STRETCH.ordinal
    private var gpuTimingEnabled = true

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        loadSavedSettings()

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

        glSurfaceView = GLSurfaceView(this).apply {
            setEGLContextClientVersion(3)
            val renderer = object : GLSurfaceView.Renderer {
                private var surfaceWidth = 0
                private var surfaceHeight = 0

                override fun onSurfaceCreated(gl: GL10?, config: EGLConfig?) {
                    Log.i("Discap-GL", "[GL] EGL Surface created. Initializing OpenGL ES 3.0 AMD CAS Renderer...")
                    val rendererGL = OpenGLRenderer().also { openGLRenderer = it }
                    rendererGL.initializeGL()

                    rendererGL.onFrameAvailableListener = {
                        requestRender()
                    }

                    val codecSurface = rendererGL.surface ?: return
                    activeSurface = codecSurface

                    runOnUiThread {
                        applyRendererSettings()
                        startReceiversWithSurface(codecSurface)
                    }
                }

                override fun onSurfaceChanged(gl: GL10?, width: Int, height: Int) {
                    surfaceWidth = width
                    surfaceHeight = height
                }

                override fun onDrawFrame(gl: GL10?) {
                    openGLRenderer?.drawFrame(surfaceWidth, surfaceHeight)
                }
            }
            setRenderer(renderer)
            renderMode = GLSurfaceView.RENDERMODE_CONTINUOUSLY
            setOnTouchListener { _, event -> sendTouch(event) }
        }

        cursorOverlayView = com.discap.android.overlay.CursorOverlayView(this).apply {
            isClickable = false
            isFocusable = false
        }
        cursorManager = com.discap.android.overlay.CursorManager(cursorOverlayView)

        val root = FrameLayout(this).apply {
            setBackgroundColor(Color.BLACK)
            setOnTouchListener { _, event ->
                if (settingsPanel.visibility == View.VISIBLE) {
                    val rect = android.graphics.Rect()
                    settingsPanel.getGlobalVisibleRect(rect)
                    if (rect.contains(event.x.toInt(), event.y.toInt())) {
                        return@setOnTouchListener false
                    }
                }
                sendTouch(event)
            }
        }
        root.addView(glSurfaceView, FrameLayout.LayoutParams(
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

    private fun startReceiversWithSurface(surface: Surface) {
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
                    val gpuMs = openGLRenderer?.lastGpuFrameTimeMs ?: -1.0
                    val gpuStr = if (gpuMs > 0.0) "  GPU ${"%.1f".format(gpuMs)}ms" else ""
                    statsView.text = "FPS ${"%.1f".format(stats.fps)}  ${"%.1f".format(stats.bitrateMbps)} Mbps$gpuStr\n" +
                            "Latency ${"%.1f".format(stats.latencyMs)} ms  ${stats.encoderType}"
                }
            }
            socketReceiver?.start()
        }

        sendSettings()
    }

    private fun loadSavedSettings() {
        val prefs = getSharedPreferences("DiscapSettings", MODE_PRIVATE)
        bitrateMbps = prefs.getInt("bitrateMbps", 20)
        cqLevel = prefs.getInt("cqLevel", 28)
        casSharpeningPercent = prefs.getInt("casSharpeningPercent", 50)
        fpsCap = prefs.getInt("fpsCap", 0)
        upscaleTargetTier = prefs.getInt("upscaleTargetTier", 200)
        scaleModeOrdinal = prefs.getInt("scaleModeOrdinal", OpenGLRenderer.ScaleMode.STRETCH.ordinal)
        resolutionScale = prefs.getInt("resolutionScale", 100)
        encoderMode = prefs.getInt("encoderMode", ENCODER_AUTO)
        showStats = prefs.getBoolean("showStats", false)
        gpuTimingEnabled = prefs.getBoolean("gpuTimingEnabled", true)
    }

    private fun saveSettings() {
        val prefs = getSharedPreferences("DiscapSettings", MODE_PRIVATE)
        prefs.edit()
            .putInt("bitrateMbps", bitrateMbps)
            .putInt("cqLevel", cqLevel)
            .putInt("casSharpeningPercent", casSharpeningPercent)
            .putInt("fpsCap", fpsCap)
            .putInt("upscaleTargetTier", upscaleTargetTier)
            .putInt("scaleModeOrdinal", scaleModeOrdinal)
            .putInt("resolutionScale", resolutionScale)
            .putInt("encoderMode", encoderMode)
            .putBoolean("showStats", showStats)
            .putBoolean("gpuTimingEnabled", gpuTimingEnabled)
            .apply()
    }

    private fun applyRendererSettings() {
        val renderer = openGLRenderer ?: return
        renderer.sharpness = casSharpeningPercent / 100.0f
        renderer.scaleMode = OpenGLRenderer.ScaleMode.entries.getOrElse(scaleModeOrdinal) { OpenGLRenderer.ScaleMode.STRETCH }
        renderer.gpuTimingEnabled = gpuTimingEnabled
        applyUpscaleTargetTier(upscaleTargetTier)

        glSurfaceView.scaleX = resolutionScale / 100f
        glSurfaceView.scaleY = resolutionScale / 100f

        if (::statsView.isInitialized) {
            statsView.visibility = if (showStats) View.VISIBLE else View.GONE
        }
    }

    private fun applyUpscaleTargetTier(tier: Int) {
        upscaleTargetTier = tier
        val renderer = openGLRenderer ?: return
        val screenW = resources.displayMetrics.widthPixels
        val screenH = resources.displayMetrics.heightPixels
        when (tier) {
            100 -> {
                renderer.targetViewportWidth = currentStreamW
                renderer.targetViewportHeight = currentStreamH
            }
            115 -> {
                renderer.targetViewportWidth = (currentStreamW * 1.15f).toInt()
                renderer.targetViewportHeight = (currentStreamH * 1.15f).toInt()
            }
            200 -> {
                renderer.targetViewportWidth = screenW
                renderer.targetViewportHeight = screenH
            }
        }
        renderer.invalidateWarmup()
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
            val cqValueText = when (cqLevel) {
                15 -> "$cqLevel (Best)"
                28 -> "$cqLevel (Default)"
                40 -> "$cqLevel (Lowest)"
                else -> "$cqLevel"
            }
            val cqValue = label(cqValueText)
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

            detectedStreamResLabel = label("Stream Res: 1920x1080 -> Device Screen: ${resources.displayMetrics.widthPixels}x${resources.displayMetrics.heightPixels}")
            addView(detectedStreamResLabel)

            addView(label("AMD CAS Sharpening (Mobile GPU Post-Processing)"))
            val initialCasStr = when {
                casSharpeningPercent == 0 -> "0% (Off - Bilinear Soft)"
                casSharpeningPercent < 40 -> "$casSharpeningPercent% (Light CAS)"
                casSharpeningPercent in 40..65 -> "$casSharpeningPercent% (Balanced CAS)"
                else -> "$casSharpeningPercent% (Ultra Sharp CAS)"
            }
            casSharpnessValueLabel = label(initialCasStr)
            addView(casSharpnessValueLabel)
            addView(SeekBar(this@MainActivity).apply {
                max = 100
                progress = casSharpeningPercent
                setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                    override fun onProgressChanged(seekBar: SeekBar?, progress: Int, fromUser: Boolean) {
                        casSharpeningPercent = progress
                        val percentStr = when {
                            progress == 0 -> "0% (Off - Bilinear Soft)"
                            progress < 40 -> "$progress% (Light CAS)"
                            progress in 40..65 -> "$progress% (Balanced CAS)"
                            else -> "$progress% (Ultra Sharp CAS)"
                        }
                        casSharpnessValueLabel.text = percentStr
                        openGLRenderer?.sharpness = progress / 100.0f
                        saveSettings()
                    }
                    override fun onStartTrackingTouch(seekBar: SeekBar?) {}
                    override fun onStopTrackingTouch(seekBar: SeekBar?) { saveSettings() }
                })
            })

            addView(label("FPS cap (Native = matches display refresh rate)"))
            addView(buttonRow(listOf("Native" to 0, "30" to 30, "60" to 60, "120" to 120, "144" to 144)) { fpsCap = it })

            addView(label("GPU Hardware Upscale Target"))
            val screenW = resources.displayMetrics.widthPixels
            val screenH = resources.displayMetrics.heightPixels
            addView(buttonRow(listOf("1.0x Native" to 100, "1.15x Ultra" to 115, "Screen Native ($screenW x $screenH)" to 200)) { targetTier ->
                applyUpscaleTargetTier(targetTier)
                saveSettings()
            })

            addView(label("Scale Mode"))
            addView(buttonRow(listOf("Fit" to 0, "Fill" to 1, "Stretch" to 2)) {
                scaleModeOrdinal = it
                applyRendererSettings()
                saveSettings()
            })

            addView(label("Resolution scale"))
            addView(buttonRow(listOf("50%" to 50, "75%" to 75, "100%" to 100)) {
                resolutionScale = it
                applyRendererSettings()
                sendSettings()
            })

            addView(label("Encoder"))
            addView(buttonRow(listOf("H.264" to ENCODER_H264, "LZ4" to ENCODER_LZ4, "Auto" to ENCODER_AUTO)) {
                encoderMode = it
                sendSettings()
            })

            addView(Button(this@MainActivity).apply {
                text = if (showStats) "Stats on" else "Stats off"
                setOnClickListener {
                    showStats = !showStats
                    text = if (showStats) "Stats on" else "Stats off"
                    applyRendererSettings()
                    sendSettings()
                }
            })

            addView(Button(this@MainActivity).apply {
                text = if (gpuTimingEnabled) "GPU Timing on" else "GPU Timing off"
                setOnClickListener {
                    gpuTimingEnabled = !gpuTimingEnabled
                    text = if (gpuTimingEnabled) "GPU Timing on" else "GPU Timing off"
                    applyRendererSettings()
                    saveSettings()
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

        val location = IntArray(2)
        glSurfaceView.getLocationOnScreen(location)

        val relX = event.rawX - location[0]
        val relY = event.rawY - location[1]

        val viewW = if (glSurfaceView.width > 0) glSurfaceView.width.toFloat() else 1f
        val viewH = if (glSurfaceView.height > 0) glSurfaceView.height.toFloat() else 1f

        val xNorm = (relX / viewW).coerceIn(0f, 1f)
        val yNorm = (relY / viewH).coerceIn(0f, 1f)

        val action = when (event.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> 1.toByte()
            MotionEvent.ACTION_MOVE -> 2.toByte()
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP, MotionEvent.ACTION_CANCEL -> 0.toByte()
            else -> return false
        }

        val button = if (action == 0.toByte()) 0.toByte() else 1.toByte()
        val pressure = (event.pressure * 255).toInt().toByte()

        val isTouchActive = action == 1.toByte() || action == 2.toByte()
        socketReceiver?.setTouchActive(isTouchActive)

        Log.d("DisCap.Touch", "Sending touch: xNorm=$xNorm, yNorm=$yNorm, action=$action")
        sender.sendInput(xNorm, yNorm, action, button, pressure)
        return true
    }

    private fun handleVideoSizeChanged(videoWidth: Int, videoHeight: Int) {
        runOnUiThread {
            if (videoWidth == 0 || videoHeight == 0) return@runOnUiThread
            currentStreamW = videoWidth
            currentStreamH = videoHeight

            val screenW = resources.displayMetrics.widthPixels
            val screenH = resources.displayMetrics.heightPixels
            if (::detectedStreamResLabel.isInitialized) {
                detectedStreamResLabel.text = "Stream Res: ${videoWidth}x${videoHeight} -> Device Screen: ${screenW}x${screenH}"
            }

            openGLRenderer?.updateStreamResolution(videoWidth, videoHeight)
            
            val parent = glSurfaceView.parent as? View ?: return@runOnUiThread
            val parentWidth = parent.width
            val parentHeight = parent.height
            if (parentWidth == 0 || parentHeight == 0) return@runOnUiThread
            
            val videoRatio = videoWidth.toFloat() / videoHeight.toFloat()
            val screenRatio = parentWidth.toFloat() / parentHeight.toFloat()
            
            val lp = glSurfaceView.layoutParams as FrameLayout.LayoutParams
            if (videoRatio > screenRatio) {
                lp.width = parentWidth
                lp.height = (parentWidth / videoRatio).toInt()
            } else {
                lp.width = (parentHeight * videoRatio).toInt()
                lp.height = parentHeight
            }
            lp.gravity = Gravity.CENTER
            glSurfaceView.layoutParams = lp
        }
    }

    override fun onResume() {
        super.onResume()
        glSurfaceView.onResume()
    }

    override fun onPause() {
        super.onPause()
        glSurfaceView.onPause()
    }

    override fun onDestroy() {
        super.onDestroy()
        socketReceiver?.stopReceiver()
        usbReceiver?.stop()
        activeSurface?.release()
        activeSurface = null
        openGLRenderer?.release()
        openGLRenderer = null
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
                        val gpuMs = openGLRenderer?.lastGpuFrameTimeMs ?: -1.0
                        val gpuStr = if (gpuMs > 0.0) "  GPU ${"%.1f".format(gpuMs)}ms" else ""
                        statsView.text = "FPS ${"%.1f".format(stats.fps)}  ${"%.1f".format(stats.bitrateMbps)} Mbps$gpuStr\n" +
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
