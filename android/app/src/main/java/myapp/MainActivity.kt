package {{APP_PACKAGE}}

import android.annotation.SuppressLint
import android.os.Bundle
import android.os.Message
import android.view.KeyEvent
import android.webkit.WebChromeClient
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback

class MainActivity : ComponentActivity() {

    lateinit var webView: WebView
    lateinit var androidBridge: AndroidBridge

    private val baseUrl = "{{BASE_URL}}"

    @SuppressLint("SetJavaScriptEnabled", "AddJavascriptInterface")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        webView = WebView(this)
        setContentView(webView)

        // --- WebView Settings ---
        webView.settings.apply {
            javaScriptEnabled = true
            allowFileAccess = true
            allowContentAccess = true
            allowFileAccessFromFileURLs = true
            allowUniversalAccessFromFileURLs = true
            domStorageEnabled = true
            databaseEnabled = true
            mediaPlaybackRequiresUserGesture = false

            setSupportMultipleWindows(false)
            loadWithOverviewMode = true
            useWideViewPort = true
            builtInZoomControls = true
            displayZoomControls = false
            setSupportZoom(true)

            javaScriptCanOpenWindowsAutomatically = false
            loadsImagesAutomatically = true
        }

        webView.isFocusable = true
        webView.isFocusableInTouchMode = true
        webView.requestFocus()

        // --- JS Bridge ---
        androidBridge = AndroidBridge(this, webView, baseUrl)
        webView.addJavascriptInterface(androidBridge, "Android")

        // --- WebViewClient ---
        webView.webViewClient = object : WebViewClient() {
            override fun shouldOverrideUrlLoading(view: WebView?, url: String?): Boolean {
                return if (url != null && url.startsWith(baseUrl)) {
                    false
                } else {
                    Toast.makeText(
                        this@MainActivity,
                        "Blocked external navigation",
                        Toast.LENGTH_SHORT
                    ).show()
                    true
                }
            }

            override fun onPageFinished(view: WebView?, url: String?) {
                view?.evaluateJavascript(
                    "console.log('Android bridge ready:', !!window.Android)",
                    null
                )
            }
        }

        // --- Block popups ---
        webView.webChromeClient = object : WebChromeClient() {
            override fun onCreateWindow(
                view: WebView?,
                isDialog: Boolean,
                isUserGesture: Boolean,
                resultMsg: Message?
            ): Boolean = false
        }

        // --- Back button → JS ---
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                webView.evaluateJavascript(
                    "window.onNativeKey && window.onNativeKey(4)",
                    null
                )
            }
        })

        webView.loadUrl(baseUrl)
    }

    // --- Permission result forwarding ---
    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        androidBridge.onPermissionResult(requestCode, grantResults)
    }

    // --- DPAD / media keys ---
    @SuppressLint("RestrictedApi")
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (event.action == KeyEvent.ACTION_DOWN) {
            val handled = when (event.keyCode) {
                KeyEvent.KEYCODE_DPAD_UP,
                KeyEvent.KEYCODE_DPAD_DOWN,
                KeyEvent.KEYCODE_DPAD_LEFT,
                KeyEvent.KEYCODE_DPAD_RIGHT,
                KeyEvent.KEYCODE_DPAD_CENTER,
                KeyEvent.KEYCODE_BACK,
                KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE -> {
                    webView.post {
                        webView.evaluateJavascript(
                            "window.onNativeKey && window.onNativeKey(${event.keyCode})",
                            null
                        )
                    }
                    true
                }
                else -> false
            }
            if (handled) return true
        }
        return super.dispatchKeyEvent(event)
    }
}