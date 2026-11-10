package com.inex.locker.companion;

import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.VibrationEffect;
import android.os.Vibrator;
import android.webkit.JavascriptInterface;
import android.webkit.WebView;
import androidx.fragment.app.FragmentActivity;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.io.PrintWriter;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;

public class WebAppInterface {

    private final FragmentActivity mActivity;
    private final WebView mWebView;

    public WebAppInterface(FragmentActivity activity, WebView webView) {
        mActivity = activity;
        mWebView = webView;
    }

    @JavascriptInterface
    public void authenticateBiometric(String title, String subtitle, String callbackFn) {
        mActivity.runOnUiThread(() -> {
            BiometricHelper.prompt(mActivity, title, subtitle, new BiometricHelper.BiometricCallback() {
                @Override
                public void onSuccess(String authType) {
                    CompanionListenerService.completePendingAuth(true, authType);
                    deliverResult(callbackFn, true, authType, null);
                }

                @Override
                public void onFailure(String error) {
                    deliverResult(callbackFn, false, null, error);
                }

                @Override
                public void onCancelled() {
                    CompanionListenerService.completePendingAuth(false, null);
                    deliverResult(callbackFn, false, null, "cancelled");
                }
            });
        });
    }

    @JavascriptInterface
    public void sendBiometricApproval(String pcIp, int port, String secret, String target, String authType) {
        // No-op. Approvals are only sent via CompanionListenerService.completePendingAuth.
    }

    @JavascriptInterface
    public void saveWindowsPinToNative(String pin) {
        try {
            android.content.SharedPreferences sp = mActivity.getSharedPreferences("locker_companion", Context.MODE_PRIVATE);
            sp.edit().putString("windows_pin", pin != null ? pin : "").apply();
        } catch (Exception ignored) {}
    }

    @JavascriptInterface
    public String getWindowsPinFromNative() {
        try {
            android.content.SharedPreferences sp = mActivity.getSharedPreferences("locker_companion", Context.MODE_PRIVATE);
            return sp.getString("windows_pin", "");
        } catch (Exception ignored) {
            return "";
        }
    }

    @JavascriptInterface
    public void startQrScan() {
        mActivity.runOnUiThread(() -> {
            if (mActivity instanceof MainActivity) {
                ((MainActivity) mActivity).startQrScanner();
            }
        });
    }

    public void handleScannedQr(String scanned) {
        new Thread(() -> {
            try {
                if (scanned == null || scanned.trim().isEmpty()) {
                    deliverQrError("Empty QR code scanned");
                    return;
                }

                String ip = "";
                int port = 58820;
                String secret = "";
                String pcName = "Locker PC";
                String session = "";

                if (scanned.trim().startsWith("{")) {
                    JSONObject json = new JSONObject(scanned);
                    ip = json.optString("ip");
                    port = json.optInt("port", 58820);
                    secret = json.optString("secret");
                    pcName = json.optString("pcName", "Locker PC");
                    session = json.optString("session");
                } else if (scanned.startsWith("locker://")) {
                    android.net.Uri uri = android.net.Uri.parse(scanned);
                    ip = uri.getQueryParameter("ip");
                    String pStr = uri.getQueryParameter("port");
                    if (pStr != null && !pStr.isEmpty()) {
                        try { port = Integer.parseInt(pStr); } catch (Exception ignored) {}
                    }
                    secret = uri.getQueryParameter("secret");
                    pcName = uri.getQueryParameter("pc");
                    if (pcName == null) pcName = "Locker PC";
                    session = uri.getQueryParameter("session");
                }

                if (ip == null || ip.isEmpty() || secret == null || secret.isEmpty() || session == null || session.isEmpty()) {
                    deliverQrError("Invalid QR Code. Please scan the QR code from Locker Pro.");
                    return;
                }

                Socket socket = new Socket();
                socket.connect(new InetSocketAddress(ip, port), 6000);
                socket.setSoTimeout(6000);
                PrintWriter writer = new PrintWriter(new OutputStreamWriter(socket.getOutputStream(), StandardCharsets.UTF_8), true);
                BufferedReader reader = new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8));

                String deviceId = Build.ID != null ? Build.ID : "Android";
                String deviceName = Build.MODEL != null ? Build.MODEL : "Android Phone";
                String mac = PhoneAuth.sign(secret, "pair_qr", session, deviceId);

                JSONObject req = new JSONObject();
                req.put("action", "pair_qr");
                req.put("session", session);
                req.put("deviceId", deviceId);
                req.put("deviceName", deviceName);
                req.put("mac", mac);

                writer.println(req.toString());
                writer.flush();

                String resp = reader.readLine();
                try { socket.close(); } catch (Exception ignored) {}

                if (resp != null) {
                    if (resp.startsWith("\uFEFF")) resp = resp.substring(1);
                    JSONObject respJson = new JSONObject(resp);
                    if ("paired".equals(respJson.optString("status"))) {
                        String serverPcName = respJson.optString("pcName", pcName);
                        final String finalIp = ip;
                        final String finalPcName = serverPcName;
                        savePairedPCToNative(serverPcName, ip, secret);
                        vibrate(120);
                        mActivity.runOnUiThread(() -> {
                            if (mWebView != null) {
                                mWebView.evaluateJavascript("if (window.onPairSuccess) window.onPairSuccess("
                                    + JSONObject.quote(finalPcName) + ", "
                                    + JSONObject.quote(finalIp) + ");", null);
                            }
                        });
                        return;
                    } else {
                        deliverQrError("Pairing error: " + respJson.optString("status", "Rejected by PC"));
                        return;
                    }
                }
                deliverQrError("No response from PC at " + ip);
            } catch (Exception e) {
                deliverQrError("Connection error: " + (e.getMessage() != null ? e.getMessage() : e.toString()));
            }
        }).start();
    }

    private void deliverQrError(String error) {
        mActivity.runOnUiThread(() -> {
            if (mWebView != null) {
                mWebView.evaluateJavascript("if (window.onPairError) window.onPairError(" + JSONObject.quote(error) + ");", null);
            }
        });
    }

    @JavascriptInterface
    public void checkPairStatus(String pcIp, int port, String callbackFn) {
        new Thread(() -> {
            Socket socket = null;
            try {
                socket = new Socket();
                socket.connect(new InetSocketAddress(pcIp, port), 4000);
                socket.setSoTimeout(4000);
                PrintWriter writer = new PrintWriter(new OutputStreamWriter(socket.getOutputStream(), StandardCharsets.UTF_8), true);
                BufferedReader reader = new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8));

                JSONObject req = new JSONObject();
                req.put("action", "ping");
                String mySecret = PhoneAuth.loadSecret(mActivity);
                if (!mySecret.isEmpty()) {
                    String ts = String.valueOf(System.currentTimeMillis() / 1000L);
                    String nonce = PhoneAuth.newNonce();
                    req.put("ts", ts);
                    req.put("nonce", nonce);
                    req.put("mac", PhoneAuth.sign(mySecret, "ping", ts, nonce));
                }
                writer.println(req.toString());
                writer.flush();

                String resp = reader.readLine();
                if (resp != null) {
                    try {
                        JSONObject json = new JSONObject(resp);
                        if ("pong".equals(json.optString("status"))) {
                            if (json.optBoolean("authenticated", false)) {
                                String pcName = json.optString("pcName", "Locker PC");
                                if (!mySecret.isEmpty()) {
                                    savePairedPCToNative(pcName, pcIp, mySecret);
                                }
                            } else if (!json.optBoolean("paired", true) && !mySecret.isEmpty()) {
                                // PC was unpaired while phone was offline: sync local state cleanly
                                clearPairedPCFromNative();
                                mActivity.runOnUiThread(() -> {
                                    if (mWebView != null) {
                                        mWebView.evaluateJavascript("if (window.onPcUnpaired) window.onPcUnpaired();", null);
                                    }
                                });
                            }
                        }
                    } catch (Exception ignored) {}
                }
                deliverResult(callbackFn, true, resp, null);
            } catch (Exception e) {
                deliverResult(callbackFn, false, null, e.getMessage());
            } finally {
                if (socket != null) {
                    try { socket.close(); } catch (Exception ignored) {}
                }
            }
        }).start();
    }

    @JavascriptInterface
    public String getDeviceName() {
        return Build.MODEL != null ? Build.MODEL : "Android Device";
    }

    /** "Test Connection" button: same signed ping as checkPairStatus, result goes to the JS callback. */
    @JavascriptInterface
    public void pingPC(String pcIp, int port, String callbackFn) {
        checkPairStatus(pcIp, port, callbackFn);
    }

    @JavascriptInterface
    public void startListenerService() {
        Intent intent = new Intent(mActivity, CompanionListenerService.class);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            mActivity.startForegroundService(intent);
        } else {
            mActivity.startService(intent);
        }
    }

    @JavascriptInterface
    public void vibrate(long ms) {
        Vibrator v = (Vibrator) mActivity.getSystemService(Context.VIBRATOR_SERVICE);
        if (v != null) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                v.vibrate(VibrationEffect.createOneShot(ms, VibrationEffect.DEFAULT_AMPLITUDE));
            } else {
                v.vibrate(ms);
            }
        }
    }

    @JavascriptInterface
    public void copyToClipboard(String text) {
        mActivity.runOnUiThread(() -> {
            ClipboardManager cm = (ClipboardManager) mActivity.getSystemService(Context.CLIPBOARD_SERVICE);
            if (cm != null) {
                cm.setPrimaryClip(ClipData.newPlainText("Locker", text));
            }
        });
    }

    public void savePairedPCToNative(String name, String ip, String secret) {
        try {
            android.content.SharedPreferences sp = mActivity.getSharedPreferences("locker_companion", Context.MODE_PRIVATE);
            String sealed = PhoneAuth.sealLocalSecret(secret);
            sp.edit()
                    .putString("pc_name", name)
                    .putString("pc_ip", ip)
                    .putString("pc_secret", sealed)
                    .putLong("paired_at", System.currentTimeMillis())
                    .apply();
        } catch (Exception ignored) {}
    }

    @JavascriptInterface
    public String getPairedPCFromNative() {
        try {
            android.content.SharedPreferences sp = mActivity.getSharedPreferences("locker_companion", Context.MODE_PRIVATE);
            String secret = PhoneAuth.loadSecret(mActivity);
            if (secret.isEmpty()) return "null";

            JSONObject obj = new JSONObject();
            obj.put("name", sp.getString("pc_name", "Locker PC"));
            obj.put("ip", sp.getString("pc_ip", ""));
            obj.put("pairedAt", sp.getLong("paired_at", 0));
            return obj.toString();
        } catch (Exception e) {
            return "null";
        }
    }

    @JavascriptInterface
    public void unpairFromPC(String pcIp, int port) {
        final String secret = PhoneAuth.loadSecret(mActivity);
        final String targetIp = (pcIp != null && !pcIp.isEmpty())
                ? pcIp
                : mActivity.getSharedPreferences("locker_companion", Context.MODE_PRIVATE).getString("pc_ip", "");
        clearPairedPCFromNative();
        if (targetIp == null || targetIp.isEmpty() || secret.isEmpty()) return;

        new Thread(() -> {
            Socket socket = null;
            try {
                socket = new Socket();
                socket.connect(new InetSocketAddress(targetIp, port > 0 ? port : 58820), 2500);
                socket.setSoTimeout(2500);
                PrintWriter writer = new PrintWriter(new OutputStreamWriter(socket.getOutputStream(), StandardCharsets.UTF_8), true);
                String ts = String.valueOf(System.currentTimeMillis() / 1000L);
                String nonce = PhoneAuth.newNonce();
                JSONObject req = new JSONObject();
                req.put("action", "unpair");
                req.put("ts", ts);
                req.put("nonce", nonce);
                req.put("mac", PhoneAuth.sign(secret, "unpair", ts, nonce));
                writer.println(req.toString());
                writer.flush();
            } catch (Exception ignored) {
            } finally {
                if (socket != null) {
                    try { socket.close(); } catch (Exception ignored) {}
                }
            }
        }).start();
    }

    @JavascriptInterface
    public void clearPairedPCFromNative() {
        try {
            android.content.SharedPreferences sp = mActivity.getSharedPreferences("locker_companion", Context.MODE_PRIVATE);
            sp.edit().remove("pc_name").remove("pc_ip").remove("pc_secret").remove("paired_at").apply();
            android.content.SharedPreferences sp2 = mActivity.getSharedPreferences("locker_native_prefs", Context.MODE_PRIVATE);
            sp2.edit().clear().apply();
        } catch (Exception ignored) {}
        BiometricKeys.deleteKey(); // unpair = the fingerprint key dies with it
    }

    private void deliverResult(String callbackFn, boolean success, String data, String error) {
        if (callbackFn == null || callbackFn.isEmpty()) return;
        mActivity.runOnUiThread(() -> {
            String safeData = (data != null) ? JSONObject.quote(data) : "null";
            String safeError = (error != null) ? JSONObject.quote(error) : "null";
            String js = String.format(java.util.Locale.US, "%s(%b, %s, %s);", callbackFn, success, safeData, safeError);
            mWebView.evaluateJavascript(js, null);
        });
    }
}
