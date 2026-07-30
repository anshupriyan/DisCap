package com.discap.android.renderer

import android.graphics.SurfaceTexture
import android.opengl.GLES11Ext
import android.opengl.GLES30
import android.opengl.Matrix
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.FloatBuffer

/**
 * High-performance OpenGL ES 3.0 renderer for Android DisCap client.
 * 
 * Features:
 * 1. Zero-copy MediaCodec hardware video texture binding (GL_TEXTURE_EXTERNAL_OES).
 * 2. Real-time AMD Contrast Adaptive Sharpening (CAS) fragment shader.
 * 3. Dynamic viewport hardware upscaling from stream resolution up to device native screen peak.
 * 4. Intermediate FBO for two-pass upscale pipeline (EASU/GSR → RCAS).
 * 5. EXT_disjoint_timer_query GPU frame-time instrumentation (non-stalling).
 */
class OpenGLRenderer : SurfaceTexture.OnFrameAvailableListener {

    var oesTextureId: Int = -1
        private set
    
    var surfaceTexture: SurfaceTexture? = null
        private set

    var surface: Surface? = null
        private set

    @Volatile
    private var frameAvailable = false
    private var hasTextureUpdated = false

    // Render parameters
    var sharpness: Float = 0.5f // Range 0.0f (Off) to 1.0f (Max CAS)
    var streamWidth: Int = 1920
    var streamHeight: Int = 1080
    var targetViewportWidth: Int = 0
    var targetViewportHeight: Int = 0

    enum class ScaleMode { FIT, FILL, STRETCH }
    var scaleMode: ScaleMode = ScaleMode.STRETCH

    fun getContentRect(): android.graphics.RectF? {
        return null
    }

    private var programId: Int = 0
    private var aPositionHandle: Int = 0
    private var aTexCoordHandle: Int = 0
    private var uMVPMatrixHandle: Int = 0
    private var uSTMatrixHandle: Int = 0
    private var uSharpnessHandle: Int = 0
    private var uTextureSizeHandle: Int = 0

    private val mvpMatrix = FloatArray(16)
    private val stMatrix = FloatArray(16)

    private val vertexBuffer: FloatBuffer
    private val texCoordBuffer: FloatBuffer

    // Fullscreen Quad Vertices
    private val squareVertices = floatArrayOf(
        -1.0f, -1.0f,
         1.0f, -1.0f,
        -1.0f,  1.0f,
         1.0f,  1.0f
    )

    // Texture Coordinates
    private val textureCoordinates = floatArrayOf(
        0.0f, 0.0f,
        1.0f, 0.0f,
        0.0f, 1.0f,
        1.0f, 1.0f
    )

    // ─── Intermediate FBO for two-pass upscale pipeline ───
    private var fboId: Int = 0
    private var fboTextureId: Int = 0
    private var fboWidth: Int = 0
    private var fboHeight: Int = 0

    // ─── EXT_disjoint_timer_query GPU timing ───
    private var timerQuerySupported = false
    private var timerQueryInitialized = false
    private val queryIds = IntArray(2)        // Double-buffered: write to [writeIdx], read from [readIdx]
    private var queryWriteIdx = 0
    private var queryPending = BooleanArray(2) // Whether each query slot has a pending result
    private var warmupFramesRemaining = 0      // Frames to discard after pipeline state change

    /** Latest GPU frame time in milliseconds, or -1.0 if unavailable. */
    var lastGpuFrameTimeMs: Double = -1.0
        private set

    /** Whether GPU timing instrumentation is active. */
    var gpuTimingEnabled: Boolean = true

    // ─── EXT_disjoint_timer_query instrumentation ───

    init {
        Matrix.setIdentityM(mvpMatrix, 0)
        Matrix.setIdentityM(stMatrix, 0)

        vertexBuffer = ByteBuffer.allocateDirect(squareVertices.size * 4)
            .order(ByteOrder.nativeOrder())
            .asFloatBuffer()
            .put(squareVertices)
        vertexBuffer.position(0)

        texCoordBuffer = ByteBuffer.allocateDirect(textureCoordinates.size * 4)
            .order(ByteOrder.nativeOrder())
            .asFloatBuffer()
            .put(textureCoordinates)
        texCoordBuffer.position(0)
    }

    fun initializeGL() {
        try {
            val eglDisplay = android.opengl.EGL14.eglGetCurrentDisplay()
            val eglDrawSurface = android.opengl.EGL14.eglGetCurrentSurface(android.opengl.EGL14.EGL_DRAW)
            if (eglDisplay != android.opengl.EGL14.EGL_NO_DISPLAY && eglDrawSurface != android.opengl.EGL14.EGL_NO_SURFACE) {
                android.opengl.EGL14.eglSwapInterval(eglDisplay, 1)
                Log.i("DisCap-GL", "[GL] Enabled eglSwapInterval(1) VSYNC lock for tear-free fast scrolling")
            }
        } catch (e: Exception) {
            Log.w("DisCap-GL", "[GL] Could not set eglSwapInterval(1): ${e.message}")
        }

        val textures = IntArray(1)
        GLES30.glGenTextures(1, textures, 0)
        oesTextureId = textures[0]

        GLES30.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, oesTextureId)
        GLES30.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES30.GL_TEXTURE_MIN_FILTER, GLES30.GL_LINEAR)
        GLES30.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES30.GL_TEXTURE_MAG_FILTER, GLES30.GL_LINEAR)
        GLES30.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES30.GL_TEXTURE_WRAP_S, GLES30.GL_CLAMP_TO_EDGE)
        GLES30.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES30.GL_TEXTURE_WRAP_T, GLES30.GL_CLAMP_TO_EDGE)

        surfaceTexture = SurfaceTexture(oesTextureId).apply {
            setDefaultBufferSize(3840, 2160)
            setOnFrameAvailableListener(this@OpenGLRenderer)
        }
        surface = Surface(surfaceTexture)

        createProgram()
        initTimerQuery()
        Log.i("DisCap-GL", "[GL] OpenGL ES 3.0 AMD CAS Renderer initialized (${streamWidth}x${streamHeight}) with OES texture #$oesTextureId")
    }

    fun updateStreamResolution(width: Int, height: Int) {
        if (width > 0 && height > 0 && (streamWidth != width || streamHeight != height)) {
            streamWidth = width
            streamHeight = height
            invalidateWarmup()
            Log.i("DisCap-GL", "[GL] Updated stream resolution to ${width}x${height}")
        }
    }

    var onFrameAvailableListener: (() -> Unit)? = null

    override fun onFrameAvailable(surfaceTexture: SurfaceTexture?) {
        frameAvailable = true
        onFrameAvailableListener?.invoke()
    }

    fun updateTexture() {
        val st = surfaceTexture ?: return
        try {
            st.updateTexImage()
            st.getTransformMatrix(stMatrix)
            hasTextureUpdated = true
            frameAvailable = false
        } catch (e: Exception) {
            // Expected if no new image frame is available on this GL tick yet
        }
    }

    // ─── FBO lifecycle management ───

    /**
     * Ensures the intermediate FBO exists and matches the requested target dimensions.
     * Called at the top of drawFrame() — a single reallocation checkpoint.
     * Lazy: does nothing if targetW/targetH are 0 (no upscale pass needed).
     */
    private fun ensureFbo(requestedW: Int, requestedH: Int) {
        if (requestedW <= 0 || requestedH <= 0) return
        if (fboId != 0 && fboWidth == requestedW && fboHeight == requestedH) return

        // Tear down old FBO if it exists with mismatched size
        releaseFbo()

        // Allocate new FBO
        val fbos = IntArray(1)
        GLES30.glGenFramebuffers(1, fbos, 0)
        fboId = fbos[0]

        val textures = IntArray(1)
        GLES30.glGenTextures(1, textures, 0)
        fboTextureId = textures[0]

        GLES30.glBindTexture(GLES30.GL_TEXTURE_2D, fboTextureId)
        GLES30.glTexImage2D(
            GLES30.GL_TEXTURE_2D, 0, GLES30.GL_RGBA8,
            requestedW, requestedH, 0,
            GLES30.GL_RGBA, GLES30.GL_UNSIGNED_BYTE, null
        )
        GLES30.glTexParameteri(GLES30.GL_TEXTURE_2D, GLES30.GL_TEXTURE_MIN_FILTER, GLES30.GL_LINEAR)
        GLES30.glTexParameteri(GLES30.GL_TEXTURE_2D, GLES30.GL_TEXTURE_MAG_FILTER, GLES30.GL_LINEAR)
        GLES30.glTexParameteri(GLES30.GL_TEXTURE_2D, GLES30.GL_TEXTURE_WRAP_S, GLES30.GL_CLAMP_TO_EDGE)
        GLES30.glTexParameteri(GLES30.GL_TEXTURE_2D, GLES30.GL_TEXTURE_WRAP_T, GLES30.GL_CLAMP_TO_EDGE)

        GLES30.glBindFramebuffer(GLES30.GL_FRAMEBUFFER, fboId)
        GLES30.glFramebufferTexture2D(
            GLES30.GL_FRAMEBUFFER, GLES30.GL_COLOR_ATTACHMENT0,
            GLES30.GL_TEXTURE_2D, fboTextureId, 0
        )

        val status = GLES30.glCheckFramebufferStatus(GLES30.GL_FRAMEBUFFER)
        if (status != GLES30.GL_FRAMEBUFFER_COMPLETE) {
            Log.e("DisCap-GL", "[FBO] Framebuffer incomplete! Status: 0x${Integer.toHexString(status)}")
            releaseFbo()
        } else {
            fboWidth = requestedW
            fboHeight = requestedH
            Log.i("DisCap-GL", "[FBO] Allocated intermediate FBO ${requestedW}x${requestedH} (texture #$fboTextureId)")
        }

        // Unbind — drawFrame() will bind as needed
        GLES30.glBindFramebuffer(GLES30.GL_FRAMEBUFFER, 0)
        GLES30.glBindTexture(GLES30.GL_TEXTURE_2D, 0)
    }

    private fun releaseFbo() {
        if (fboTextureId != 0) {
            GLES30.glDeleteTextures(1, intArrayOf(fboTextureId), 0)
            fboTextureId = 0
        }
        if (fboId != 0) {
            GLES30.glDeleteFramebuffers(1, intArrayOf(fboId), 0)
            fboId = 0
        }
        fboWidth = 0
        fboHeight = 0
    }

    // ─── EXT_disjoint_timer_query instrumentation ───

    private fun initTimerQuery() {
        if (timerQueryInitialized) return
        timerQueryInitialized = true

        val extensions = GLES30.glGetString(GLES30.GL_EXTENSIONS) ?: ""
        timerQuerySupported = extensions.contains("timer_query", ignoreCase = true)

        if (timerQuerySupported) {
            GLES30.glGenQueries(2, queryIds, 0)
            queryPending[0] = false
            queryPending[1] = false
            Log.i("DisCap-GL", "[TIMER] GPU timer query supported. Query IDs: ${queryIds[0]}, ${queryIds[1]}")
        } else {
            Log.w("DisCap-GL", "[TIMER] GPU timer query NOT supported on this device. Extensions: $extensions")
        }
    }

    /**
     * Mark the start of a warm-up period. Called after any pipeline state change
     * (shader switch, resolution change, upscale target change) so the first
     * ~20 frames are discarded from timing measurements.
     */
    fun invalidateWarmup() {
        warmupFramesRemaining = WARMUP_FRAME_COUNT
    }

    /**
     * Begin a GPU timer query for this frame. Called at the top of the render pass.
     * Uses double-buffered queries: write slot alternates each frame so we never
     * read from the query we just wrote to (avoids pipeline stalls).
     */
    private fun beginGpuTimer() {
        if (!gpuTimingEnabled || !timerQuerySupported) return
        if (warmupFramesRemaining > 0) return // Don't measure during warm-up

        // Before starting a new query on write slot, try to harvest the result
        // from the OTHER slot (the one we wrote to 2 frames ago)
        harvestGpuTimer()

        val writeId = queryIds[queryWriteIdx]
        GLES30.glBeginQuery(GL_TIME_ELAPSED_EXT, writeId)
    }

    /**
     * End the GPU timer query for this frame. Called after all draw calls.
     */
    private fun endGpuTimer() {
        if (!gpuTimingEnabled || !timerQuerySupported) return
        if (warmupFramesRemaining > 0) {
            warmupFramesRemaining--
            return
        }

        GLES30.glEndQuery(GL_TIME_ELAPSED_EXT)
        queryPending[queryWriteIdx] = true
        queryWriteIdx = 1 - queryWriteIdx // Flip write slot
    }

    /**
     * Non-blocking harvest of the oldest pending GPU timer result.
     * Reads from the slot opposite to the current write slot.
     * Checks result availability first — never stalls the CPU.
     */
    private fun harvestGpuTimer() {
        if (!timerQuerySupported) return

        val readIdx = 1 - queryWriteIdx
        if (!queryPending[readIdx]) return

        // Check for disjoint event (GPU context switch / thermal throttle / etc.)
        // If disjoint occurred, all pending timer results are unreliable.
        val disjoint = IntArray(1)
        GLES30.glGetIntegerv(GL_GPU_DISJOINT_EXT, disjoint, 0)
        if (disjoint[0] != 0) {
            // Discard all pending results
            queryPending[0] = false
            queryPending[1] = false
            lastGpuFrameTimeMs = -1.0
            return
        }

        // Non-blocking check: is the result available yet?
        val available = IntArray(1)
        GLES30.glGetQueryObjectuiv(queryIds[readIdx], GL_QUERY_RESULT_AVAILABLE_EXT, available, 0)
        if (available[0] == 0) return // Not ready yet — don't stall, try next frame

        // Read the result (nanoseconds)
        val resultNs = IntArray(1)
        GLES30.glGetQueryObjectuiv(queryIds[readIdx], GL_QUERY_RESULT_EXT, resultNs, 0)
        queryPending[readIdx] = false

        // Convert to unsigned long (GL returns uint, Java int is signed)
        val ns = resultNs[0].toLong() and 0xFFFFFFFFL
        lastGpuFrameTimeMs = ns / 1_000_000.0
    }

    private fun releaseTimerQueries() {
        if (timerQuerySupported && timerQueryInitialized) {
            GLES30.glDeleteQueries(2, queryIds, 0)
        }
    }

    // ─── Draw frame ───

    fun drawFrame(screenWidth: Int, screenHeight: Int) {
        val t0 = System.nanoTime()
        updateTexture()

        // FBO reallocation check — single checkpoint at top of drawFrame().
        // Compare requested upscale target against current FBO attached texture size.
        val requestedFboW = if (targetViewportWidth > 0) targetViewportWidth else 0
        val requestedFboH = if (targetViewportHeight > 0) targetViewportHeight else 0
        if (requestedFboW > 0 && requestedFboH > 0) {
            ensureFbo(requestedFboW, requestedFboH)
        }

        GLES30.glClearColor(0.0f, 0.0f, 0.0f, 1.0f)
        GLES30.glClear(GLES30.GL_COLOR_BUFFER_BIT)

        if (programId == 0 || !hasTextureUpdated) return

        // ── Begin GPU timer ──
        beginGpuTimer()

        GLES30.glUseProgram(programId)

        // Calculate viewport dimensions based on scale mode
        val targetW = if (targetViewportWidth > 0) targetViewportWidth else screenWidth
        val targetH = if (targetViewportHeight > 0) targetViewportHeight else screenHeight

        val videoW = if (streamWidth > 0) streamWidth else 1920
        val videoH = if (streamHeight > 0) streamHeight else 1080

        val videoRatio = videoW.toFloat() / videoH.toFloat()
        val targetRatio = targetW.toFloat() / targetH.toFloat()

        var renderW: Int
        var renderH: Int
        var offsetX: Int
        var offsetY: Int
        var useScissor = false

        when (scaleMode) {
            ScaleMode.FIT -> {
                // Min-covering: letterbox/pillarbox, black bars on shorter dimension
                if (videoRatio > targetRatio) {
                    renderW = targetW
                    renderH = (targetW / videoRatio).toInt()
                } else {
                    renderH = targetH
                    renderW = (targetH * videoRatio).toInt()
                }
                offsetX = (screenWidth - renderW) / 2
                offsetY = (screenHeight - renderH) / 2
            }
            ScaleMode.FILL -> {
                // Max-covering: overflow + crop via scissor, no black bars
                if (videoRatio > targetRatio) {
                    renderH = screenHeight
                    renderW = (screenHeight * videoRatio).toInt()
                } else {
                    renderW = screenWidth
                    renderH = (screenWidth / videoRatio).toInt()
                }
                offsetX = (screenWidth - renderW) / 2
                offsetY = (screenHeight - renderH) / 2
                useScissor = true
            }
            ScaleMode.STRETCH -> {
                // Ignore aspect ratio, fill entire screen
                renderW = screenWidth
                renderH = screenHeight
                offsetX = 0
                offsetY = 0
            }
        }

        if (useScissor) {
            GLES30.glEnable(GLES30.GL_SCISSOR_TEST)
            GLES30.glScissor(0, 0, screenWidth, screenHeight)
        }

        GLES30.glViewport(offsetX, offsetY, renderW, renderH)

        GLES30.glActiveTexture(GLES30.GL_TEXTURE0)
        GLES30.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, oesTextureId)

        GLES30.glUniformMatrix4fv(uMVPMatrixHandle, 1, false, mvpMatrix, 0)
        GLES30.glUniformMatrix4fv(uSTMatrixHandle, 1, false, stMatrix, 0)
        GLES30.glUniform1f(uSharpnessHandle, sharpness)
        GLES30.glUniform2f(uTextureSizeHandle, videoW.toFloat(), videoH.toFloat())

        GLES30.glEnableVertexAttribArray(aPositionHandle)
        GLES30.glVertexAttribPointer(aPositionHandle, 2, GLES30.GL_FLOAT, false, 8, vertexBuffer)

        GLES30.glEnableVertexAttribArray(aTexCoordHandle)
        GLES30.glVertexAttribPointer(aTexCoordHandle, 2, GLES30.GL_FLOAT, false, 8, texCoordBuffer)

        GLES30.glDrawArrays(GLES30.GL_TRIANGLE_STRIP, 0, 4)

        GLES30.glDisableVertexAttribArray(aPositionHandle)
        GLES30.glDisableVertexAttribArray(aTexCoordHandle)

        if (useScissor) {
            GLES30.glDisable(GLES30.GL_SCISSOR_TEST)
        }

        // ── End GPU timer ──
        endGpuTimer()

        val t1 = System.nanoTime()
        val elapsedMs = (t1 - t0) / 1_000_000.0
        if (!timerQuerySupported || lastGpuFrameTimeMs <= 0.0) {
            lastGpuFrameTimeMs = if (lastGpuFrameTimeMs <= 0.0) elapsedMs else (0.8 * lastGpuFrameTimeMs + 0.2 * elapsedMs)
        }
    }

    private fun createProgram() {
        val vertexShader = loadShader(GLES30.GL_VERTEX_SHADER, VERTEX_SHADER_CODE)
        val fragmentShader = loadShader(GLES30.GL_FRAGMENT_SHADER, CAS_FRAGMENT_SHADER_CODE)

        programId = GLES30.glCreateProgram().also {
            GLES30.glAttachShader(it, vertexShader)
            GLES30.glAttachShader(it, fragmentShader)
            GLES30.glLinkProgram(it)
        }

        aPositionHandle = GLES30.glGetAttribLocation(programId, "aPosition")
        aTexCoordHandle = GLES30.glGetAttribLocation(programId, "aTexCoord")
        uMVPMatrixHandle = GLES30.glGetUniformLocation(programId, "uMVPMatrix")
        uSTMatrixHandle = GLES30.glGetUniformLocation(programId, "uSTMatrix")
        uSharpnessHandle = GLES30.glGetUniformLocation(programId, "uSharpness")
        uTextureSizeHandle = GLES30.glGetUniformLocation(programId, "uTextureSize")
    }

    private fun loadShader(type: Int, shaderCode: String): Int {
        return GLES30.glCreateShader(type).also { shader ->
            GLES30.glShaderSource(shader, shaderCode)
            GLES30.glCompileShader(shader)
            val compiled = IntArray(1)
            GLES30.glGetShaderiv(shader, GLES30.GL_COMPILE_STATUS, compiled, 0)
            if (compiled[0] == 0) {
                val info = GLES30.glGetShaderInfoLog(shader)
                GLES30.glDeleteShader(shader)
                throw RuntimeException("Could not compile shader $type: $info")
            }
        }
    }

    fun release() {
        surface?.release()
        surface = null
        surfaceTexture?.release()
        surfaceTexture = null
        if (oesTextureId != -1) {
            val textures = intArrayOf(oesTextureId)
            GLES30.glDeleteTextures(1, textures, 0)
            oesTextureId = -1
        }
        if (programId != 0) {
            GLES30.glDeleteProgram(programId)
            programId = 0
        }
        releaseFbo()
        releaseTimerQueries()
    }

    companion object {
        private const val TAG = "DisCap-GL"

        // EXT_disjoint_timer_query constants (not in GLES30 core)
        private const val GL_TIME_ELAPSED_EXT = 0x88BF
        private const val GL_QUERY_RESULT_EXT = 0x8866
        private const val GL_QUERY_RESULT_AVAILABLE_EXT = 0x8867
        private const val GL_GPU_DISJOINT_EXT = 0x8FBB

        /** Number of frames to discard after a pipeline state change before recording measurements. */
        private const val WARMUP_FRAME_COUNT = 20

        private const val VERTEX_SHADER_CODE = """#version 300 es
layout(location = 0) in vec4 aPosition;
layout(location = 1) in vec2 aTexCoord;

uniform mat4 uMVPMatrix;
uniform mat4 uSTMatrix;

out vec2 vTexCoord;

void main() {
    gl_Position = uMVPMatrix * aPosition;
    vTexCoord = (uSTMatrix * vec4(aTexCoord, 0.0, 1.0)).xy;
}
"""

        // AMD Contrast Adaptive Sharpening (CAS) Fragment Shader for GL_TEXTURE_EXTERNAL_OES
        private const val CAS_FRAGMENT_SHADER_CODE = """#version 300 es
#extension GL_OES_EGL_image_external_essl3 : require

precision mediump float;

in vec2 vTexCoord;
out vec4 fragColor;

uniform samplerExternalOES uTexture;
uniform float uSharpness; // 0.0 (Off) to 1.0 (Max Sharpness)
uniform vec2 uTextureSize;

void main() {
    vec4 centerColor = texture(uTexture, vTexCoord);
    if (uSharpness <= 0.001) {
        fragColor = centerColor;
        return;
    }

    vec2 dx = vec2(1.0 / uTextureSize.x, 0.0);
    vec2 dy = vec2(0.0, 1.0 / uTextureSize.y);

    // Sample 3x3 cross neighborhood
    vec3 b = texture(uTexture, vTexCoord - dy).rgb;
    vec3 d = texture(uTexture, vTexCoord - dx).rgb;
    vec3 e = centerColor.rgb;
    vec3 f = texture(uTexture, vTexCoord + dx).rgb;
    vec3 h = texture(uTexture, vTexCoord + dy).rgb;

    // Min/Max bounds for local contrast weighting
    vec3 minColor = min(min(min(d, e), min(f, b)), h);
    vec3 maxColor = max(max(max(d, e), max(f, b)), h);

    // Safe max color to prevent division by zero / NaN in dark background areas
    vec3 safeMaxColor = max(maxColor, vec3(0.0001));

    // Compute CAS peak sharpening weight
    vec3 amp = clamp(min(minColor, 2.0 - safeMaxColor) / safeMaxColor, 0.0, 1.0);
    vec3 w = sqrt(amp) * (-0.125 * uSharpness);

    // Filter sum
    vec3 result = (w * (b + d + f + h) + e) / (1.0 + 4.0 * w);
    fragColor = vec4(clamp(result, 0.0, 1.0), 1.0);
}
"""
    }
}
