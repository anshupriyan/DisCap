package com.discap.android.overlay

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.Paint
import android.os.SystemClock
import android.view.Choreographer
import android.view.View

class CursorOverlayView(context: Context) : View(context) {

    init {
        setBackgroundColor(Color.TRANSPARENT)
    }

    private var hasReceivedFirstPacket: Boolean = false
    private var cursorBitmap: Bitmap? = null
    
    // Target position from network
    private var targetX: Float = 0f
    private var targetY: Float = 0f

    // Rendered interpolated position (144Hz VSYNC)
    private var renderX: Float = 0f
    private var renderY: Float = 0f

    // Velocity vector (px/ms) for dead-reckoning during network jitter gaps
    private var velocityX: Float = 0f
    private var velocityY: Float = 0f
    private var lastPacketTimeMs: Long = 0L

    private var hotspotX: Int = 0
    private var hotspotY: Int = 0
    private var isCursorVisible: Boolean = true

    var desktopWidth: Int = 1920
    var desktopHeight: Int = 1200

    private val paint = Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG)
    private val matrix = Matrix()

    private var isInterpolatingLoopRunning: Boolean = false

    private val frameCallback = object : Choreographer.FrameCallback {
        override fun doFrame(frameTimeNs: Long) {
            if (!isInterpolatingLoopRunning || !isCursorVisible) {
                isInterpolatingLoopRunning = false
                return
            }

            val nowMs = SystemClock.uptimeMillis()
            val packetAgeMs = nowMs - lastPacketTimeMs

            // Smooth LERP towards target position
            val dx = targetX - renderX
            val dy = targetY - renderY

            renderX += dx * 0.45f
            renderY += dy * 0.45f

            // Dead reckoning: Extrapolate up to 35ms if network packet was delayed by socket jitter
            if (packetAgeMs in 8L..35L) {
                renderX += velocityX * 2.0f
                renderY += velocityY * 2.0f
            }

            invalidate()
            Choreographer.getInstance().postFrameCallback(this)
        }
    }

    private fun startInterpolationLoop() {
        if (!isInterpolatingLoopRunning) {
            isInterpolatingLoopRunning = true
            Choreographer.getInstance().postFrameCallback(frameCallback)
        }
    }

    fun updatePosition(x: Int, y: Int, visible: Boolean) {
        val nowMs = SystemClock.uptimeMillis()
        val newTargetX = x.toFloat()
        val newTargetY = y.toFloat()

        if (!hasReceivedFirstPacket) {
            hasReceivedFirstPacket = true
            renderX = newTargetX
            renderY = newTargetY
        } else if (lastPacketTimeMs > 0L) {
            val dt = (nowMs - lastPacketTimeMs).coerceAtLeast(1L)
            val pDx = newTargetX - targetX
            val pDy = newTargetY - targetY
            
            // Smooth velocity estimation
            velocityX = 0.5f * velocityX + 0.5f * (pDx / dt)
            velocityY = 0.5f * velocityY + 0.5f * (pDy / dt)
        }

        lastPacketTimeMs = nowMs
        targetX = newTargetX
        targetY = newTargetY
        isCursorVisible = visible
        visibility = if (visible) VISIBLE else INVISIBLE

        if (visible) {
            startInterpolationLoop()
        }
    }

    fun updateShape(bitmap: Bitmap?, hX: Int, hY: Int) {
        this.cursorBitmap = bitmap
        this.hotspotX = hX
        this.hotspotY = hY
        invalidate()
    }

    private var defaultBitmap: Bitmap? = null

    private fun getDefaultCursorBitmap(): Bitmap {
        if (defaultBitmap == null) {
            val size = 32
            val bmp = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
            val canvas = Canvas(bmp)
            val p = Paint(Paint.ANTI_ALIAS_FLAG)

            // Black outer circle
            p.color = Color.BLACK
            canvas.drawCircle(size / 2f, size / 2f, 14f, p)

            // White inner circle
            p.color = Color.WHITE
            canvas.drawCircle(size / 2f, size / 2f, 10f, p)

            defaultBitmap = bmp
        }
        return defaultBitmap!!
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)

        if (!hasReceivedFirstPacket || !isCursorVisible || desktopWidth <= 0 || desktopHeight <= 0) return

        val viewW = width.toFloat()
        val viewH = height.toFloat()
        if (viewW <= 0f || viewH <= 0f) return

        val desktopAspect = desktopWidth.toFloat() / desktopHeight.toFloat()
        val viewAspect = viewW / viewH

        val drawnW: Float
        val drawnH: Float
        val offsetX: Float
        val offsetY: Float

        if (viewAspect > desktopAspect) {
            drawnH = viewH
            drawnW = viewH * desktopAspect
            offsetX = (viewW - drawnW) / 2f
            offsetY = 0f
        } else {
            drawnW = viewW
            drawnH = viewW / desktopAspect
            offsetY = (viewH - drawnH) / 2f
            offsetX = 0f
        }

        val scaleX = drawnW / desktopWidth.toFloat()
        val scaleY = drawnH / desktopHeight.toFloat()

        val bitmap = cursorBitmap
        if (bitmap != null) {
            val cursorScale = 1.25f
            val finalScaleX = scaleX * cursorScale
            val finalScaleY = scaleY * cursorScale

            val drawX = offsetX + (renderX - hotspotX.toFloat() * cursorScale) * scaleX
            val drawY = offsetY + (renderY - hotspotY.toFloat() * cursorScale) * scaleY

            matrix.reset()
            matrix.postScale(finalScaleX, finalScaleY)
            matrix.postTranslate(drawX, drawY)
            canvas.drawBitmap(bitmap, matrix, paint)
        } else {
            val scaledX = offsetX + renderX * scaleX
            val scaledY = offsetY + renderY * scaleY

            // Draw black outer border
            paint.style = Paint.Style.FILL
            paint.color = Color.BLACK
            canvas.drawCircle(scaledX, scaledY, 22f, paint)

            // Draw white inner circle
            paint.color = Color.WHITE
            canvas.drawCircle(scaledX, scaledY, 20f, paint)
        }
    }
}
