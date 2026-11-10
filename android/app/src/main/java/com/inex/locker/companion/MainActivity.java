package com.inex.locker.companion;

import android.Manifest;
import android.annotation.SuppressLint;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.os.Build;
import android.os.Bundle;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.webkit.WebChromeClient;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import androidx.activity.result.ActivityResultLauncher;
import androidx.annotation.NonNull;
import androidx.core.app.ActivityCompat;
import androidx.core.content.ContextCompat;
import androidx.fragment.app.FragmentActivity;

import com.journeyapps.barcodescanner.ScanContract;
import com.journeyapps.barcodescanner.ScanOptions;

public class MainActivity extends FragmentActivity {

    public static volatile MainActivity sInstance = null;
    public static volatile boolean sIsForeground = false;
    private WebView mWebView;
    private WebAppInterface mBridge;

    private final ActivityResultLauncher<ScanOptions> mBarcodeLauncher = registerForActivityResult(
        new ScanContract(),
        result -> {
            if (result.getContents() == null) {
                if (mWebView != null) {
                    mWebView.evaluateJavascript("if (window.onQrScanCancelled) window.onQrScanCancelled();", null);
                }
            } else {
                String scanned = result.getContents();
                if (mBridge != null) {
                    mBridge.handleScannedQr(scanned);
                }
            }
        }
    );

    public void startQrScanner() {
        ScanOptions options = new ScanOptions();
        options.setPrompt("Scan Locker Pro QR Code on PC Monitor");
        options.setBeepEnabled(true);
        options.setOrientationLocked(true);
        options.setCaptureActivity(PortraitCaptureActivity.class);
        options.setBarcodeImageEnabled(false);
        options.setDesiredBarcodeFormats(ScanOptions.QR_CODE);
        mBarcodeLauncher.launch(options);
    }

    @Override
    protected void onResume() {
        super.onResume();
        sInstance = this;
        sIsForeground = true;
    }

    @Override
    protected void onPause() {
        super.onPause();
        sIsForeground = false;
        if (sInstance == this) {
            if (isFinishing()) sInstance = null;
        }
    }

    @Override
    protected void onDestroy() {
        if (sInstance == this) {
            sInstance = null;
        }
        super.onDestroy();
    }

    public void triggerIncomingAuth(String target) {
        triggerIncomingAuth(target, false);
    }

    public void notifyUnpairedFromPc() {
        runOnUiThread(() -> {
            if (mWebView != null) {
                mWebView.evaluateJavascript("if (window.onPcUnpaired) window.onPcUnpaired();", null);
            }
        });
    }

    public void triggerIncomingAuth(String target, boolean fromNotification) {
        runOnUiThread(() -> {
            final String safeTarget = (target != null && !target.isEmpty()) ? target : "Protected Resource";

            // Visual indicator in WebView
            if (mWebView != null) {
                mWebView.evaluateJavascript("if (window.handleIncomingAuthPrompt) { window.handleIncomingAuthPrompt(" + org.json.JSONObject.quote(safeTarget) + "); }", null);
            }

            // Pop up native biometric dialog instantly!
            BiometricHelper.prompt(this, "Locker Security Request", "Authorize access to: " + safeTarget, new BiometricHelper.BiometricCallback() {
                @Override
                public void onSuccess(String authType) {
                    CompanionListenerService.completePendingAuth(true, authType);
                    if (mWebView != null) {
                        mWebView.evaluateJavascript("showToast('Authorized! PC unlocked.'); if (window.hideIncomingAuthBanner) window.hideIncomingAuthBanner();", null);
                    }
                    if (fromNotification) {
                        // Passkey style: return immediately to previous app/screen!
                        moveTaskToBack(true);
                    }
                }

                @Override
                public void onFailure(String error) {
                    CompanionListenerService.completePendingAuth(false, null);
                    if (mWebView != null) {
                        mWebView.evaluateJavascript("showToast('Authentication failed'); if (window.hideIncomingAuthBanner) window.hideIncomingAuthBanner();", null);
                    }
                }

                @Override
                public void onCancelled() {
                    CompanionListenerService.completePendingAuth(false, null);
                    if (mWebView != null) {
                        mWebView.evaluateJavascript("showToast('Auth Cancelled'); if (window.hideIncomingAuthBanner) window.hideIncomingAuthBanner();", null);
                    }
                    if (fromNotification) {
                        moveTaskToBack(true);
                    }
                }
            });
        });
    }

    @SuppressLint("SetJavaScriptEnabled")
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        // Immersive dark window
        Window window = getWindow();
        window.clearFlags(WindowManager.LayoutParams.FLAG_TRANSLUCENT_STATUS);
        window.addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS);
        window.setStatusBarColor(Color.parseColor("#000000"));
        window.setNavigationBarColor(Color.parseColor("#000000"));
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            window.getDecorView().setSystemUiVisibility(0); // dark icons off (light text)
        }

        mWebView = new WebView(this);
        setContentView(mWebView);

        WebView.setWebContentsDebuggingEnabled((getApplicationInfo().flags & android.content.pm.ApplicationInfo.FLAG_DEBUGGABLE) != 0);

        WebSettings settings = mWebView.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setAllowFileAccess(true);
        settings.setAllowFileAccessFromFileURLs(false);
        settings.setAllowUniversalAccessFromFileURLs(false);
        settings.setDatabaseEnabled(true);
        settings.setMediaPlaybackRequiresUserGesture(false);
        settings.setCacheMode(WebSettings.LOAD_DEFAULT);

        mBridge = new WebAppInterface(this, mWebView);
        mWebView.addJavascriptInterface(mBridge, "Android");

        mWebView.setWebViewClient(new WebViewClient());
        mWebView.setWebChromeClient(new WebChromeClient());

        mWebView.setBackgroundColor(Color.parseColor("#000000"));
        mWebView.loadUrl("file:///android_asset/www/index.html");

        checkNotificationPermission();

        mBridge.startListenerService();

        handleIncomingIntent(getIntent());
    }

    private void checkNotificationPermission() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                ActivityCompat.requestPermissions(this, new String[]{Manifest.permission.POST_NOTIFICATIONS}, 101);
            }
        }
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        handleIncomingIntent(intent);
    }

    private void handleIncomingIntent(Intent intent) {
        if (intent != null && intent.getBooleanExtra("trigger_auth", false)) {
            String target = intent.getStringExtra("target");
            if (target == null) target = "Protected Item";
            final String safeTarget = target;
            mWebView.postDelayed(() -> {
                triggerIncomingAuth(safeTarget, true);
            }, 150);
        }
    }

    @Override
    public void onBackPressed() {
        if (mWebView.canGoBack()) {
            mWebView.goBack();
        } else {
            super.onBackPressed();
        }
    }
}
