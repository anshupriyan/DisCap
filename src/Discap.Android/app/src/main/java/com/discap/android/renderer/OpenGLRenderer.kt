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
        Log.i("DisCap-GL", "[GL] OpenGL ES 3.0 AMD CAS Renderer initialized (${streamWidth}x${streamHeight}) with OES texture #$oesTextureId")
    }

    fun updateStreamResolution(width: Int, height: Int) {
        if (width > 0 && height > 0 && (streamWidth != width || streamHeight != height)) {
            streamWidth = width
            streamHeight = height
            Log.i("DisCap-GL", "[GL] Updated stream resolution to ${width}x${height}")
        }
    }

    var onFrameAvailableListener: (() -> Unit)? = null

    override fun onFrameAvailable(surfaceTexture: SurfaceTexture?) {
        frameAvailable = true
        onFrameAvailableListener?.invoke()
    }

    fun updateTexture() {
        if (frameAvailable) {
            try {
                surfaceTexture?.updateTexImage()
                surfaceTexture?.getTransformMatrix(stMatrix)
                frameAvailable = false
                hasTextureUpdated = true
            } catch (e: Exception) {
                Log.e("DisCap-GL", "[GL] updateTexImage failed: ${e.message}")
            }
        }
    }

    fun drawFrame(screenWidth: Int, screenHeight: Int) {
        updateTexture()

        GLES30.glClearColor(0.0f, 0.0f, 0.0f, 1.0f)
        GLES30.glClear(GLES30.GL_COLOR_BUFFER_BIT)

        if (programId == 0 || !hasTextureUpdated) return

        GLES30.glUseProgram(programId)

        // Calculate aspect-ratio fit within target viewport or screen
        val targetW = if (targetViewportWidth > 0) targetViewportWidth else screenWidth
        val targetH = if (targetViewportHeight > 0) targetViewportHeight else screenHeight

        val videoW = if (streamWidth > 0) streamWidth else 1920
        val videoH = if (streamHeight > 0) streamHeight else 1080

        val videoRatio = videoW.toFloat() / videoH.toFloat()
        val targetRatio = targetW.toFloat() / targetH.toFloat()

        val renderW: Int
        val renderH: Int
        if (videoRatio > targetRatio) {
            renderW = targetW
            renderH = (targetW / videoRatio).toInt()
        } else {
            renderH = targetH
            renderW = (targetH * videoRatio).toInt()
        }

        val offsetX = (screenWidth - renderW) / 2
        val offsetY = (screenHeight - renderH) / 2

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
    }

    companion object {
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
