package com.discap.android.overlay

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.view.View

class CursorOverlayView(context: Context) : View(context) {

    private var cursorBitmap: Bitmap? = null
    private var hotspotX: Int = 0
    private var hotspotY: Int = 0
    private var hostX: Int = 0
    private var hostY: Int = 0
    private var hostWidth: Int = 1920
    private var hostHeight: Int = 1080
    private var isVisible: Boolean = false
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)

    fun updatePosition(x: Int, y: Int, visible: Boolean, width: Int, height: Int) {
        this.hostX = x
        this.hostY = y
        this.isVisible = visible
        if (width > 0) this.hostWidth = width
        if (height > 0) this.hostHeight = height
        invalidate()
    }

    fun updateShape(bitmap: Bitmap?, hX: Int, hY: Int) {
        this.cursorBitmap = bitmap
        this.hotspotX = hX
        this.hotspotY = hY
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)

        val bmp = cursorBitmap
        if (!isVisible || bmp == null || bmp.isRecycled || width == 0 || height == 0) {
            return
        }

        val scaleX = width.toFloat() / hostWidth.toFloat()
        val scaleY = height.toFloat() / hostHeight.toFloat()

        val viewX = hostX * scaleX - (hotspotX * scaleX)
        val viewY = hostY * scaleY - (hotspotY * scaleY)

        canvas.save()
        canvas.translate(viewX, viewY)
        canvas.scale(scaleX, scaleY)
        canvas.drawBitmap(bmp, 0f, 0f, paint)
        canvas.restore()
    }
}
