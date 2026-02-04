import android.app.AlertDialog
import android.content.Context
import android.content.pm.PackageManager
import android.webkit.JavascriptInterface
import android.webkit.WebView
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import kotlinx.coroutines.*
import okhttp3.*
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.io.FileNotFoundException
import java.io.IOException
import java.util.concurrent.TimeUnit

class AndroidBridge(
    private val activity: ComponentActivity,
    private val webView: WebView,
    private val baseUrl: String
) {
    private val PERMISSION_REQUEST_CODE = 9001
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    
    // OkHttp client for HTTP requests
    private val httpClient by lazy {
        OkHttpClient.Builder()
            .connectTimeout(30, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .writeTimeout(30, TimeUnit.SECONDS)
            .addInterceptor { chain ->
                val request = chain.request().newBuilder()
                    .addHeader("User-Agent", "VaderJS-Native/1.0")
                    .build()
                chain.proceed(request)
            }
            .build()
    }

    /* ─────────────────────────────── */
    /* Secure Store (Encrypted)        */
    /* ─────────────────────────────── */

    private val masterKey by lazy {
        MasterKey.Builder(activity)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()
    }

    private val securePrefs by lazy {
        EncryptedSharedPreferences.create(
            activity,
            "secure_store",
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
        )
    }

    @JavascriptInterface
    fun secureStoreIsAvailable(): Boolean = true

    @JavascriptInterface
    fun secureStoreSet(key: String, value: String): Boolean {
    return try {
        securePrefs.edit().putString(key, value).apply()
        true
    } catch (e: Exception) {
        false
    }
}
    @JavascriptInterface
    fun secureStoreGet(key: String): String? =
        securePrefs.getString(key, null)

    @JavascriptInterface
    fun secureStoreDelete(key: String): Boolean {
        val existed = securePrefs.contains(key)
        securePrefs.edit().remove(key).apply()
        return existed
    }

    @JavascriptInterface
    fun secureStoreClear() {
        securePrefs.edit().clear().apply()
    }

    @JavascriptInterface
    fun secureStoreGetAll(): String =
        JSONObject(securePrefs.all.mapValues { it.value as String }).toString()

    /* ─────────────────────────────── */
    /* Native HTTP - FIXED VERSION     */
    /* ─────────────────────────────── */

    @JavascriptInterface
    fun nativeHttp(requestJson: String) {
        scope.launch {
            try {
                val req = JSONObject(requestJson)
                val url = req.getString("url")
                val method = req.optString("method", "GET").uppercase()
                val headers = req.optJSONObject("headers")
                val body = req.optString("body", null)
                val requestId = req.optString("id", "")

                // Build OkHttp request
                val requestBuilder = Request.Builder().url(url)

                // Add headers
                headers?.let {
                    it.keys().forEach { key ->
                        requestBuilder.addHeader(key, it.getString(key))
                    }
                }

                // Add body for POST, PUT, PATCH
                if (body != null && (method == "POST" || method == "PUT" || method == "PATCH")) {
                    val contentType = headers?.optString("Content-Type") ?: "application/json"
                    val requestBody = body.toRequestBody(contentType.toMediaType())
                    requestBuilder.method(method, requestBody)
                } else {
                    requestBuilder.method(method, null)
                }

                val request = requestBuilder.build()

                // Execute request
                val response = try {
                    httpClient.newCall(request).execute()
                } catch (e: IOException) {
                    sendHttpErrorResponse(requestId, "Network error: ${e.message}")
                    return@launch
                } catch (e: Exception) {
                    sendHttpErrorResponse(requestId, "Request failed: ${e.message}")
                    return@launch
                }

                // Read response body
                val responseBody = response.body?.string() ?: ""
                
                // Create response object
                val responseObj = JSONObject().apply {
                    put("id", requestId)
                    put("success", response.isSuccessful)
                    put("status", response.code)
                    put("body", responseBody)
                    
                    // Add headers
                    val headersObj = JSONObject()
                    response.headers.names().forEach { name ->
                        headersObj.put(name, response.headers.get(name) ?: "")
                    }
                    put("headers", headersObj)
                }

                // Send response back to WebView
                sendToWebView("nativeHttpResponse", responseObj.toString())

            } catch (e: Exception) {
                sendHttpErrorResponse("", "HTTP request error: ${e.message}")
            }
        }
    }

    // Helper to send error response
    private fun sendHttpErrorResponse(requestId: String, error: String) {
        val errorResponse = JSONObject().apply {
            put("id", requestId)
            put("success", false)
            put("error", error)
            put("status", 0)
            put("body", "")
        }
        sendToWebView("nativeHttpResponse", errorResponse.toString())
    }

    // Helper to safely send data to WebView
    private fun sendToWebView(eventName: String, data: String) {
        activity.runOnUiThread {
            try {
                val js = "if (window['$eventName']) window['$eventName']($data);"
                webView.evaluateJavascript(js, null)
            } catch (e: Exception) {
                // Log error or handle silently
            }
        }
    }

    /* ─────────────────────────────── */
    /* File APIs                       */
    /* ─────────────────────────────── */

    @JavascriptInterface
    fun writeFile(path: String, content: String): Boolean =
        try {
            activity.openFileOutput(path, Context.MODE_PRIVATE).use {
                it.write(content.toByteArray())
            }
            true
        } catch (e: Exception) {
            false
        }

    @JavascriptInterface
    fun readFile(path: String): String =
        try {
            activity.openFileInput(path).bufferedReader().use { it.readText() }
        } catch (_: FileNotFoundException) {
            "{\"error\":\"File not found\"}"
        }

    @JavascriptInterface
    fun deleteFile(path: String): Boolean =
        activity.deleteFile(path)

    @JavascriptInterface
    fun listFiles(): String =
        JSONArray(activity.fileList().toList()).toString()

    /* ─────────────────────────────── */
    /* UI Helpers                      */
    /* ─────────────────────────────── */

    @JavascriptInterface
    fun showToast(message: String) {
        activity.runOnUiThread {
            Toast.makeText(activity, message, Toast.LENGTH_SHORT).show()
        }
    }

    @JavascriptInterface
    fun showDialog(
        title: String,
        message: String,
        okText: String = "OK",
        cancelText: String = "Cancel"
    ) {
        activity.runOnUiThread {
            AlertDialog.Builder(activity)
                .setTitle(title)
                .setMessage(message)
                .setPositiveButton(okText) { _, _ ->
                    webView.evaluateJavascript(
                        "window.onNativeDialogResult && window.onNativeDialogResult(true)",
                        null
                    )
                }
                .setNegativeButton(cancelText) { _, _ ->
                    webView.evaluateJavascript(
                        "window.onNativeDialogResult && window.onNativeDialogResult(false)",
                        null
                    )
                }
                .setCancelable(false)
                .show()
        }
    }

    /* ─────────────────────────────── */
    /* Permissions                     */
    /* ─────────────────────────────── */

    @JavascriptInterface
    fun hasPermission(name: String): Boolean =
        mapPermission(name).all {
            ContextCompat.checkSelfPermission(activity, it) ==
                PackageManager.PERMISSION_GRANTED
        }

    @JavascriptInterface
    fun requestPermission(name: String) {
        val perms = mapPermission(name)
        ActivityCompat.requestPermissions(activity, perms, PERMISSION_REQUEST_CODE)
    }

    fun onPermissionResult(requestCode: Int, grantResults: IntArray) {
        if (requestCode != PERMISSION_REQUEST_CODE) return
        val granted = grantResults.all { it == PackageManager.PERMISSION_GRANTED }
        webView.post {
            webView.evaluateJavascript(
                "window.onNativePermissionResult && window.onNativePermissionResult($granted)",
                null
            )
        }
    }

    private fun mapPermission(name: String): Array<String> =
        when (name) {
            "storage" -> arrayOf(
                android.Manifest.permission.READ_EXTERNAL_STORAGE,
                android.Manifest.permission.WRITE_EXTERNAL_STORAGE
            )
            "camera" -> arrayOf(android.Manifest.permission.CAMERA)
            "microphone" -> arrayOf(android.Manifest.permission.RECORD_AUDIO)
            "notifications" -> arrayOf(android.Manifest.permission.POST_NOTIFICATIONS)
            else -> emptyArray()
        }

    /* ─────────────────────────────── */
    /* Navigation                      */
    /* ─────────────────────────────── */

    @JavascriptInterface
    fun navigate(path: String?) {
        val clean = path?.trimStart('/') ?: ""
        webView.post {
            val finalUrl = "$baseUrl$clean/index.html".replace("//index", "/index")
            webView.loadUrl(finalUrl)
        }
    }

    /* ─────────────────────────────── */
    /* Cleanup                         */
    /* ─────────────────────────────── */

    fun cleanup() {
        scope.cancel()
        httpClient.dispatcher.executorService.shutdown()
    }
}