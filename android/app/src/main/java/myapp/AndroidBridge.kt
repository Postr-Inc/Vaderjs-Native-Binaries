import android.app.AlertDialog
import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.webkit.JavascriptInterface
import android.webkit.WebView
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import org.json.JSONArray
import java.io.File
import java.io.FileNotFoundException
import java.net.HttpURLConnection
import java.net.URL

class AndroidBridge(
    private val activity: ComponentActivity,
    private val webView: WebView,
    private val baseUrl: String
) {

    private val PERMISSION_REQUEST_CODE = 9001

    @JavascriptInterface
    fun showToast(message: String) {
        activity.runOnUiThread {
            Toast.makeText(activity, message, Toast.LENGTH_SHORT).show()
        }
    }

    @JavascriptInterface
    fun writeFile(path: String, content: String): Boolean {
        return try {
            activity.openFileOutput(path, Context.MODE_PRIVATE).use {
                it.write(content.toByteArray())
            }
            true
        } catch (e: Exception) {
            e.printStackTrace()
            false
        }
    }

    @JavascriptInterface
    fun readFile(path: String): String {
        return try {
            activity.openFileInput(path).bufferedReader().use { it.readText() }
        } catch (e: FileNotFoundException) {
            "{\"error\":\"File not found\"}"
        } catch (e: Exception) {
            e.printStackTrace()
            "{\"error\":\"${e.message}\"}"
        }
    }

    @JavascriptInterface
    fun deleteFile(path: String): Boolean {
        return try {
            activity.deleteFile(path)
        } catch (e: Exception) {
            e.printStackTrace()
            false
        }
    }

    @JavascriptInterface
    fun listFiles(): String {
        return try {
            val files = activity.fileList()
            JSONArray(files.toList()).toString()
        } catch (e: Exception) {
            e.printStackTrace()
            "[]"
        }
    }

    @JavascriptInterface
    fun hasPermission(name: String): Boolean {
        val permissions = mapPermission(name)
        return permissions.all {
            ContextCompat.checkSelfPermission(activity, it) ==
                    PackageManager.PERMISSION_GRANTED
        }
    }

    @JavascriptInterface
    fun requestPermission(name: String) {
        val permissions = mapPermission(name)

        if (permissions.isEmpty()) {
            sendPermissionResult(true)
            return
        }

        val granted = permissions.all {
            ContextCompat.checkSelfPermission(activity, it) ==
                    PackageManager.PERMISSION_GRANTED
        }

        if (granted) {
            sendPermissionResult(true)
            return
        }

        ActivityCompat.requestPermissions(
            activity,
            permissions,
            PERMISSION_REQUEST_CODE
        )
    }

    fun onPermissionResult(requestCode: Int, grantResults: IntArray) {
        if (requestCode != PERMISSION_REQUEST_CODE) return
        val granted = grantResults.all { it == PackageManager.PERMISSION_GRANTED }
        sendPermissionResult(granted)
    }

    private fun sendPermissionResult(granted: Boolean) {
        webView.post {
            webView.evaluateJavascript(
                "window.onNativePermissionResult && window.onNativePermissionResult($granted)",
                null
            )
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

    @JavascriptInterface
    fun nativeFetch(url: String, method: String): String {
        return try {
            val connection = URL(url).openConnection() as HttpURLConnection
            connection.requestMethod = method
            connection.connectTimeout = 5000
            connection.readTimeout = 5000
            val response = connection.inputStream.bufferedReader().use { it.readText() }
            connection.disconnect()
            response
        } catch (e: Exception) {
            "{\"error\":\"${e.message}\"}"
        }
    }

    @JavascriptInterface
    fun navigate(path: String?) {
        val clean = path?.trimStart('/') ?: ""
        webView.post {
            val finalUrl = "$baseUrl$clean/index.html".replace("//index", "/index")
            webView.loadUrl(finalUrl)
        }
    }

    private fun mapPermission(name: String): Array<String> {
        return when (name) {
            "storage" -> arrayOf(
                android.Manifest.permission.READ_EXTERNAL_STORAGE,
                android.Manifest.permission.WRITE_EXTERNAL_STORAGE
            )
            "camera" -> arrayOf(android.Manifest.permission.CAMERA)
            "microphone" -> arrayOf(android.Manifest.permission.RECORD_AUDIO)
            "notifications" -> arrayOf(android.Manifest.permission.POST_NOTIFICATIONS)
            else -> emptyArray()
        }
    }
}