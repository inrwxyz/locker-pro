package com.inex.locker.companion;

import android.Manifest;
import android.annotation.SuppressLint;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.PowerManager;
import android.provider.Settings;
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

import com.google.mlkit.vision.barcode.common.Barcode;
import com.google.mlkit.vision.codescanner.GmsBarcodeScanner;
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions;
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning;

public class MainActivity extends FragmentActivity {

    public static volatile MainActivity sInstance = null;
    public static volatile boolean sIsForeground = false;
    private WebView mWebView;
    private WebAppInterface mBridge;

    // Google code scanner (same scan UI as other Inex apps). No camera permission needed.
    public void startQrScanner() {
        GmsBarcodeScannerOptions options = new GmsBarcodeScannerOptions.Builder()
            .setBarcodeFormats(Barcode.FORMAT_QR_CODE)
            .build();
        GmsBarcodeScanner scanner = GmsBarcodeScanning.getClient(this, options);
        scanner.startScan()
            .addOnSuccessListener(barcode -> {
                String scanned = barcode.getRawValue();
                if (mBridge != null && scanned != null) {
                    mBridge.handleScannedQr(scanned);
                }
            })
            .addOnCanceledListener(() -> {
                if (mWebView != null) {
                    mWebView.evaluateJavascript("if (window.onQrScanCancelled) window.onQrScanCancelled();", null);
                }
            })
            .addOnFailureListener(e -> {
                if (mWebView != null) {
                    String msg = org.json.JSONObject.quote("Scanner error: " + (e.getMessage() != null ? e.getMessage() : "try again"));
                    mWebView.evaluateJavascript("if (window.onPairError) window.onPairError(" + msg + ");", null);
                }
            });
    }

    @Override
    protected void onResume() {
        super.onResume();
        sInstance = this;
        sIsForeground = true;
        syncPendingAuthOnResume();
    }

    private void syncPendingAuthOnResume() {
        if (mWebView == null) return;
        String pendingJson = CompanionListenerService.getPendingAuthJson();
        if (pendingJson != null && !pendingJson.isEmpty()) {
            try {
                org.json.JSONObject p = new org.json.JSONObject(pendingJson);
                String target = p.optString("target", "Protected Resource");
                String actionType = p.optString("actionType", "unlock");
                mWebView.post(() -> triggerIncomingAuth(target, false, actionType));
                return;
            } catch (Exception ignored) {}
        }

        if (mBridge != null) {
            android.content.SharedPreferences sp = getSharedPreferences("locker_companion", Context.MODE_PRIVATE);
            String ip = sp.getString("pc_ip", "");
            if (ip != null && !ip.isEmpty() && !PhoneAuth.loadSecret(this).isEmpty()) {
                mBridge.checkPairStatus(ip, 58820, "_onResumeSync");
            }
        }
    }

    public void clearIncomingAuthFromPc() {
        runOnUiThread(() -> {
            if (mWebView != null) {
                mWebView.evaluateJavascript("if (window.hideIncomingAuthBanner) window.hideIncomingAuthBanner();", null);
            }
        });
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
        triggerIncomingAuth(target, false, CompanionListenerService.getPendingActionType());
    }

    public void triggerIncomingAuth(String target, boolean fromNotification) {
        triggerIncomingAuth(target, fromNotification, CompanionListenerService.getPendingActionType());
    }

    public void notifyUnpairedFromPc() {
        runOnUiThread(() -> {
            if (mWebView != null) {
                mWebView.evaluateJavascript("if (window.onPcUnpaired) window.onPcUnpaired();", null);
            }
        });
    }

    public void triggerIncomingAuth(String target, boolean fromNotification, String actionType) {
        runOnUiThread(() -> {
            final String safeTarget = (target != null && !target.isEmpty()) ? target : "Protected Resource";
            final String safeAction = (actionType != null && !actionType.isEmpty()) ? actionType : "unlock";

            // Visual indicator in WebView
            if (mWebView != null) {
                mWebView.evaluateJavascript("if (window.handleIncomingAuthPrompt) { window.handleIncomingAuthPrompt("
                        + org.json.JSONObject.quote(safeTarget) + ", " + org.json.JSONObject.quote(safeAction) + ", false); }", null);
            }

            boolean isLock = "lock".equalsIgnoreCase(safeAction);
            // Pop up native biometric dialog instantly!
            BiometricHelper.prompt(this,
                    isLock ? "Locker Lock Request" : "Locker Security Request",
                    (isLock ? "Confirm locking: " : "Authorize access to: ") + safeTarget,
                    new BiometricHelper.BiometricCallback() {
                @Override
                public void onSuccess(String authType) {
                    CompanionListenerService.completePendingAuth(true, authType);
                    if (mWebView != null) {
                        mWebView.evaluateJavascript("showToast('Authorized! PC updated.'); if (window.hideIncomingAuthBanner) window.hideIncomingAuthBanner();", null);
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
                    } else {
                        mWebView.postDelayed(() -> syncPendingAuthOnResume(), 300);
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
        requestIgnoreBatteryOptimizations();

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

    public void requestIgnoreBatteryOptimizations() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
            if (pm != null && !pm.isIgnoringBatteryOptimizations(getPackageName())) {
                try {
                    @SuppressLint("BatteryLife")
                    Intent intent = new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS);
                    intent.setData(Uri.parse("package:" + getPackageName()));
                    startActivity(intent);
                } catch (Exception e) {
                    try {
                        Intent intent = new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS);
                        startActivity(intent);
                    } catch (Exception ignored) {}
                }
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
