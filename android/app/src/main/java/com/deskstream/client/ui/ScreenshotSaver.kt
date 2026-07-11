package com.deskstream.client.ui

import android.content.ContentValues
import android.content.Context
import android.graphics.Bitmap
import android.os.Build
import android.os.Environment
import android.os.Handler
import android.os.HandlerThread
import android.provider.MediaStore
import android.util.Log
import android.view.PixelCopy
import android.view.SurfaceView
import com.deskstream.client.R
import java.io.File
import java.io.FileOutputStream
import java.text.SimpleDateFormat
import java.util.Locale

/**
 * Captures the frame currently shown on [SurfaceView] and saves it as a PNG.
 *
 * This has no protocol traffic and never touches the receive/decode hot path -- it is a
 * one-shot read of whatever the decoder most recently rendered. [PixelCopy.request] runs its
 * callback on a dedicated background [HandlerThread] (never the main thread), and the PNG
 * encode + file write is dispatched to a plain background [Thread] from there, matching the
 * rest of this codebase's concurrency style (see VideoDecoder's callback/teardown threads)
 * rather than pulling in coroutines for a single one-off operation.
 */
object ScreenshotSaver {

    private const val TAG = "ScreenshotSaver"

    private val handlerThread = HandlerThread("ScreenshotSaverCallback").apply { start() }
    private val handler = Handler(handlerThread.looper)

    /**
     * @param frameRendered Guard against capturing an empty surface: PixelCopy on a
     * [SurfaceView] that has never received a decoded frame returns solid black. Callers must
     * only invoke this once a stream is active and at least one frame has been rendered.
     * @param onResult Invoked off the main thread (from the save thread, or synchronously for
     * the immediate "no frame yet" rejection) with whether the save succeeded and either the
     * saved location or an error description. Callers must hop back to the main thread before
     * touching views.
     */
    fun capture(
        context: Context,
        surfaceView: SurfaceView,
        videoWidth: Int,
        videoHeight: Int,
        frameRendered: Boolean,
        onResult: (success: Boolean, message: String) -> Unit
    ) {
        if (!frameRendered || videoWidth <= 0 || videoHeight <= 0) {
            onResult(false, context.getString(R.string.screenshot_no_frame))
            return
        }

        val bitmap = Bitmap.createBitmap(videoWidth, videoHeight, Bitmap.Config.ARGB_8888)
        val appContext = context.applicationContext
        try {
            PixelCopy.request(surfaceView, bitmap, { result ->
                if (result != PixelCopy.SUCCESS) {
                    Log.w(TAG, "PixelCopy failed with result=$result")
                    bitmap.recycle()
                    onResult(false, appContext.getString(R.string.screenshot_failed))
                    return@request
                }
                Thread({
                    val location = try {
                        saveBitmap(appContext, bitmap)
                    } catch (e: Exception) {
                        Log.w(TAG, "failed to save screenshot", e)
                        null
                    } finally {
                        bitmap.recycle()
                    }
                    if (location != null) {
                        onResult(true, location)
                    } else {
                        onResult(false, appContext.getString(R.string.screenshot_save_failed))
                    }
                }, "ScreenshotSave").start()
            }, handler)
        } catch (e: Exception) {
            Log.w(TAG, "PixelCopy.request failed", e)
            bitmap.recycle()
            onResult(false, appContext.getString(R.string.screenshot_failed))
        }
    }

    /** Returns a human-readable save location, or throws on failure. */
    private fun saveBitmap(context: Context, bitmap: Bitmap): String {
        val filename = "DeskStream_" +
            SimpleDateFormat("yyyyMMdd_HHmmss", Locale.US).format(System.currentTimeMillis()) +
            ".png"

        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            // API 29+: MediaStore write into the shared Pictures/DeskStream collection. No
            // storage permission is needed for an app's own MediaStore inserts.
            val resolver = context.contentResolver
            val values = ContentValues().apply {
                put(MediaStore.Images.Media.DISPLAY_NAME, filename)
                put(MediaStore.Images.Media.MIME_TYPE, "image/png")
                put(MediaStore.Images.Media.RELATIVE_PATH, Environment.DIRECTORY_PICTURES + "/DeskStream")
                put(MediaStore.Images.Media.IS_PENDING, 1)
            }
            val uri = resolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values)
                ?: throw IllegalStateException("MediaStore insert failed")
            try {
                val stream = resolver.openOutputStream(uri)
                    ?: throw IllegalStateException("Could not open MediaStore output stream")
                stream.use { out ->
                    if (!bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)) {
                        throw IllegalStateException("PNG encoder rejected the bitmap")
                    }
                }
                values.clear()
                values.put(MediaStore.Images.Media.IS_PENDING, 0)
                if (resolver.update(uri, values, null, null) != 1) {
                    throw IllegalStateException("Could not publish MediaStore image")
                }
            } catch (e: Exception) {
                resolver.delete(uri, null, null)
                throw e
            }
            "Pictures/DeskStream/$filename"
        } else {
            // API 26-28: app-specific external Pictures directory. No permission is needed
            // because it lives under the app's own external-files sandbox.
            val dir = context.getExternalFilesDir(Environment.DIRECTORY_PICTURES)
                ?: throw IllegalStateException("External files directory unavailable")
            if (!dir.exists()) dir.mkdirs()
            val file = File(dir, filename)
            try {
                FileOutputStream(file).use { out ->
                    if (!bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)) {
                        throw IllegalStateException("PNG encoder rejected the bitmap")
                    }
                }
            } catch (e: Exception) {
                file.delete()
                throw e
            }
            file.absolutePath
        }
    }
}
