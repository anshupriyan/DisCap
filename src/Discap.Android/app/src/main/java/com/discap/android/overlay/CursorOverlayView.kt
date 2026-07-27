package com.discap.android.overlay

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.Paint
import android.util.Log
import android.view.View

class CursorOverlayView(context: Context) : View(context) {

    init {
        setBackgroundColor(Color.TRANSPARENT)
    }

    private var hasReceivedFirstPacket: Boolean = false
    private var cursorBitmap: Bitmap? = null
    private var cursorX: Float = 0f
    private var cursorY: Float = 0f
    private var hotspotX: Int = 0
    private var hotspotY: Int = 0
    private var isCursorVisible: Boolean = true

    var desktopWidth: Int = 1920
    var desktopHeight: Int = 1200

    private val paint = Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG)
    private val matrix = Matrix()

    private var isIBeam = false
    private var iBeamWidth = 0
    private var iBeamHeight = 0

    fun setIBeamMode(enabled: Boolean, w: Int, h: Int) {
        this.isIBeam = enabled
        this.iBeamWidth = w
        this.iBeamHeight = h
        postInvalidate()
    }

    fun updatePosition(x: Int, y: Int, visible: Boolean) {
        Log.d("DISCAP-CURSOR", "🔄 VIEW UPDATED: Invalidating...")
        this.hasReceivedFirstPacket = true
        this.cursorX = x.toFloat()
        this.cursorY = y.toFloat()
        this.isCursorVisible = visible
        this.visibility = if (visible) VISIBLE else INVISIBLE
        postInvalidate()
    }

    fun updateShape(bitmap: Bitmap?, hX: Int, hY: Int) {
        this.cursorBitmap = bitmap
        this.hotspotX = hX
        this.hotspotY = hY
        postInvalidate()
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

        val scaledX = offsetX + cursorX * scaleX
        val scaledY = offsetY + cursorY * scaleY

        if (isIBeam) {
            val centerX = scaledX
            val topY = scaledY - (iBeamHeight / 2f * scaleY)
            val bottomY = scaledY + (iBeamHeight / 2f * scaleY)
            val capWidth = iBeamWidth * 0.3f * scaleX

            val blackPaint = Paint().apply {
                style = Paint.Style.FILL
                color = Color.BLACK
                isAntiAlias = true
            }

            val whitePaint = Paint().apply {
                style = Paint.Style.FILL
                color = Color.WHITE
                isAntiAlias = true
            }

            val shadowWidth = 3f * scaleX
            val capHeight = 4f * scaleY
            val coreWidth = 1.5f * scaleX
            val coreInsetY = 2f * scaleY
            val capCoreInsetX = 2f * scaleX

            // 1. Solid Black Base / Shadow (Outline)
            canvas.drawRect(centerX - shadowWidth, topY, centerX + shadowWidth, bottomY, blackPaint) // Stem Base
            canvas.drawRect(centerX - capWidth, topY, centerX + capWidth, topY + capHeight, blackPaint) // Top Cap Base
            canvas.drawRect(centerX - capWidth, bottomY - capHeight, centerX + capWidth, bottomY, blackPaint) // Bottom Cap Base

            // 2. Solid White Core
            canvas.drawRect(centerX - coreWidth, topY + coreInsetY, centerX + coreWidth, bottomY - coreInsetY, whitePaint) // Stem Core
            canvas.drawRect(centerX - capWidth + capCoreInsetX, topY + coreInsetY, centerX + capWidth - capCoreInsetX, topY + capHeight, whitePaint) // Top Cap Core
            canvas.drawRect(centerX - capWidth + capCoreInsetX, bottomY - capHeight, centerX + capWidth - capCoreInsetX, bottomY - coreInsetY, whitePaint) // Bottom Cap Core

            return // Exit onDraw, do not draw the bitmap
        }

        val bitmap = cursorBitmap
        if (bitmap != null) {
            val drawX = offsetX + (cursorX - hotspotX.toFloat()) * scaleX
            val drawY = offsetY + (cursorY - hotspotY.toFloat()) * scaleY

            Log.d("DISCAP-CURSOR", "🎨 DRAWING BITMAP: ${bitmap.width}x${bitmap.height}, Hotspot=($hotspotX, $hotspotY)")
            matrix.reset()
            matrix.postScale(scaleX, scaleY)
            matrix.postTranslate(drawX, drawY)
            canvas.drawBitmap(bitmap, matrix, paint)
        } else {
            val scaledX = offsetX + cursorX * scaleX
            val scaledY = offsetY + cursorY * scaleY

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
