package com.inex.locker.companion;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.IBinder;
import androidx.core.app.NotificationCompat;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.io.PrintWriter;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;

public class CompanionListenerService extends Service {

    public static final String CHANNEL_ID = "locker_service_channel";
    public static final String ALERT_CHANNEL_ID = "locker_alert_channel";
    public static final int SERVICE_NOTIFICATION_ID = 1001;
    public static final int ALERT_NOTIFICATION_ID = 1002;
    public static final int PORT = 58821;

    private static Socket sPendingAuthSocket = null;
    private static String sPendingTarget = null;      // text shown to the user
    private static String sPendingRawTarget = "";
    private static String sPendingVaultId = "";     // exact bytes the PC signed
    private static String sPendingNonce = "";
    private static String sPendingActionType = "unlock";
    private static String sPendingWrapped = "";        // RSA blob from the PC (empty = plain approval / enroll)
    private static byte[] sPendingU = null;            // released by the fingerprint-bound key
    private static String sPendingPub = "";            // enroll: new public key (base64)
    private static String sPendingFailReason = "";
    private static final java.util.LinkedHashSet<String> sSeenNonces = new java.util.LinkedHashSet<>();
    private ServerSocket mServerSocket;
    private boolean mIsRunning = false;

    public static synchronized Socket getPendingSocket() {
        return sPendingAuthSocket;
    }

    public static synchronized String getPendingTarget() {
        return sPendingTarget;
    }

    public static synchronized String getPendingActionType() {
        return sPendingActionType;
    }

    public static synchronized String getPendingWrapped() {
        return sPendingWrapped;
    }

    public static synchronized void setPendingU(byte[] u) {
        sPendingU = u;
    }

    public static synchronized void setPendingPub(String pubB64) {
        sPendingPub = pubB64 != null ? pubB64 : "";
    }

    public static synchronized void setPendingFailReason(String r) {
        sPendingFailReason = r != null ? r : "";
    }

    public static synchronized void completePendingAuth(boolean approved, String authType) {
        final String rawTarget = sPendingRawTarget;
        final String vaultId = sPendingVaultId;
        final String nonce = sPendingNonce;
        final String actionType = sPendingActionType;
        final String type = authType != null ? authType : "fingerprint";
        final Context ctx = (sInstance != null) ? sInstance : MainActivity.sInstance;
        final String secret = PhoneAuth.loadSecret(ctx);
        // we can only prove an approval if we still hold the pairing secret
        final boolean signedOk = approved && !secret.isEmpty() && !nonce.isEmpty();

        // fingerprint-bound data (see BiometricKeys): U for unlock/lock, new public key for enroll
        final boolean isEnroll = "enroll".equalsIgnoreCase(actionType);
        final boolean hadKeyBlob = sPendingWrapped != null && !sPendingWrapped.isEmpty();
        final String pubB64 = sPendingPub;
        final String failReason = sPendingFailReason;
        String uEncTmp = "";
        if (signedOk && sPendingU != null) {
            try {
                uEncTmp = PhoneAuth.encryptForPc(secret, nonce, sPendingU);
            } catch (Exception ignored) {
                uEncTmp = "";
            }
        }
        if (sPendingU != null) java.util.Arrays.fill(sPendingU, (byte) 0);
        sPendingU = null;
        sPendingPub = "";
        sPendingFailReason = "";
        sPendingWrapped = "";
        final String uEnc = uEncTmp;

        if (sPendingAuthSocket != null && !sPendingAuthSocket.isClosed()) {
            final Socket socket = sPendingAuthSocket;
            sPendingAuthSocket = null;
            sPendingTarget = null;
            new Thread(() -> {
                try {
                    java.io.OutputStream os = socket.getOutputStream();
                    JSONObject reply = new JSONObject();
                    reply.put("status", signedOk ? "approved" : "rejected");
                    reply.put("type", type);
                    if (!signedOk && !failReason.isEmpty()) {
                        reply.put("reason", failReason); // key_invalid / no_key / ... (PC just falls back to the password)
                    }
                    if (signedOk) {
                        // bound to the PC's nonce: a recorded or forged reply is useless
                        reply.put("mac", PhoneAuth.sign(secret, "auth_reply", "approved", type, actionType, rawTarget, nonce));
                        if (!uEnc.isEmpty() || !pubB64.isEmpty()) {
                            reply.put("pub", pubB64);
                            reply.put("u", uEnc);
                            reply.put("dmac", PhoneAuth.sign(secret, "auth_data", actionType, nonce, pubB64, uEnc));
                        }
                        if (vaultId != null && !vaultId.isEmpty()) {
                            reply.put("vault_token", PhoneAuth.computeVaultToken(ctx, vaultId));
                        }
                    }
                    byte[] bytes = (reply.toString() + "\n").getBytes(StandardCharsets.UTF_8);
                    os.write(bytes);
                    os.flush();
                    try { socket.shutdownOutput(); } catch (Exception ignored) {}
                    Thread.sleep(800);
                    socket.close();
                } catch (Exception ignored) {
                    try { socket.close(); } catch (Exception ignored2) {}
                }
            }).start();
        }

        // Also proactively notify PC port 58820 as backup channel (signed, single use nonce)
        // (skipped for enroll and for fingerprint-key requests: those only travel on the reply socket)
        if (signedOk && ctx != null && !isEnroll && !hadKeyBlob) {
            notifyPcDirect(ctx, rawTarget, type, actionType, nonce, vaultId);
        }

        sPendingRawTarget = "";
        sPendingNonce = "";

        if (sInstance != null) {
            sInstance.dismissNotification();
        }
    }

    /** Backup push to the PC. Carries only a MAC + the nonce of a request the PC issued, never the secret. */
    public static void notifyPcDirect(Context context, String rawTarget, String authType, String actionType, String nonce, String vaultId) {
        new Thread(() -> {
            try {
                android.content.SharedPreferences sp = context.getSharedPreferences("locker_companion", Context.MODE_PRIVATE);
                String ip = sp.getString("pc_ip", "");
                if (ip.isEmpty()) {
                    android.content.SharedPreferences sp2 = context.getSharedPreferences("locker_native_prefs", Context.MODE_PRIVATE);
                    ip = sp2.getString("pc_ip", "");
                }
                String secret = PhoneAuth.loadSecret(context);
                if (ip.isEmpty() || secret.isEmpty() || nonce == null || nonce.isEmpty()) return;

                String type = authType != null ? authType : "fingerprint";
                String target = rawTarget != null ? rawTarget : "";
                try (Socket s = new Socket()) {
                    s.connect(new java.net.InetSocketAddress(ip, 58820), 4000);
                    PrintWriter pw = new PrintWriter(new OutputStreamWriter(s.getOutputStream(), StandardCharsets.UTF_8), true);
                    JSONObject req = new JSONObject();
                    req.put("action", "biometric_approved");
                    req.put("target", target);
                    req.put("biometricType", type);
                    req.put("actionType", actionType);
                    req.put("nonce", nonce);
                    req.put("mac", PhoneAuth.sign(secret, "approved", target, type, actionType, nonce));
                    if (vaultId != null && !vaultId.isEmpty()) {
                        req.put("vault_token", PhoneAuth.computeVaultToken(context, vaultId));
                    }
                    pw.println(req.toString());
                    pw.flush();
                    Thread.sleep(300);
                }
            } catch (Exception ignored) {}
        }).start();
    }

    private static volatile CompanionListenerService sInstance = null;

    public void dismissNotification() {
        try {
            NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
            if (nm != null) nm.cancel(ALERT_NOTIFICATION_ID);
        } catch (Exception ignored) {}
    }

    @Override
    public void onCreate() {
        super.onCreate();
        sInstance = this;
        createNotificationChannels();
        startForeground(SERVICE_NOTIFICATION_ID, buildForegroundNotification());
        startListening();
    }

    private void createNotificationChannels() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
            if (nm != null) {
                NotificationChannel serviceChannel = new NotificationChannel(
                        CHANNEL_ID,
                        "Locker Companion Service",
                        NotificationManager.IMPORTANCE_LOW
                );
                serviceChannel.setDescription("Keeps phone connected to Locker PC");
                nm.createNotificationChannel(serviceChannel);

                NotificationChannel alertChannel = new NotificationChannel(
                        ALERT_CHANNEL_ID,
                        "Locker Unlock Alerts",
                        NotificationManager.IMPORTANCE_HIGH
                );
                alertChannel.setDescription("Instant biometric unlock prompts from PC");
                alertChannel.enableVibration(true);
                alertChannel.setLockscreenVisibility(Notification.VISIBILITY_PUBLIC);
                nm.createNotificationChannel(alertChannel);
            }
        }
    }

    private Notification buildForegroundNotification() {
        Intent intent = new Intent(this, MainActivity.class);
        PendingIntent pi = PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE);

        return new NotificationCompat.Builder(this, CHANNEL_ID)
                .setContentTitle("Locker Companion Active")
                .setContentText("Listening for biometric unlock requests from PC")
                .setSmallIcon(android.R.drawable.ic_lock_lock)
                .setContentIntent(pi)
                .setOngoing(true)
                .build();
    }

    private void startListening() {
        mIsRunning = true;
        new Thread(() -> {
            try {
                mServerSocket = new ServerSocket(PORT);
                while (mIsRunning && !mServerSocket.isClosed()) {
                    Socket client = mServerSocket.accept();
                    handleIncomingClient(client);
                }
            } catch (Exception ignored) {
            }
        }).start();
    }

    private void handleIncomingClient(Socket client) {
        new Thread(() -> {
            try {
                client.setSoTimeout(8000); // a silent client can no longer hang this thread
                BufferedReader reader = new BufferedReader(new InputStreamReader(client.getInputStream(), StandardCharsets.UTF_8));
                String line = reader.readLine();
                if (line == null || line.trim().isEmpty() || line.length() > 4096) {
                    client.close();
                    return;
                }

                JSONObject obj = new JSONObject(line);
                String action = obj.optString("action", "");

                if ("auth_request".equals(action)) {
                    String rawTarget = obj.optString("target", "");
                    String actionType = obj.optString("action_type", "unlock");
                    String mode = obj.optString("mode", "Any");
                    String ts = obj.optString("timestamp", "");
                    String nonce = obj.optString("nonce", "");
                    String mac = obj.optString("mac", "");

                    // Only a request signed with OUR pairing secret may open a prompt. Anyone else on the
                    // Wi-Fi used to be able to pop a fake "Locker Security Request" and hijack the pending socket.
                    String secret = PhoneAuth.loadSecret(this);
                    String wrapped = obj.optString("wrapped", "");
                    boolean macOk = wrapped.isEmpty()
                            ? PhoneAuth.verify(secret, mac, "auth_request", actionType, rawTarget, mode, ts, nonce)
                            : PhoneAuth.verify(secret, mac, "auth_request", actionType, rawTarget, mode, ts, nonce, wrapped);
                    if (!PhoneAuth.isValidNonce(nonce) || !macOk) {
                        client.close();
                        return;
                    }
                    synchronized (CompanionListenerService.class) {
                        if (sSeenNonces.contains(nonce)) { // replayed request
                            client.close();
                            return;
                        }
                        sSeenNonces.add(nonce);
                        while (sSeenNonces.size() > 64) {
                            java.util.Iterator<String> it = sSeenNonces.iterator();
                            it.next();
                            it.remove();
                        }
                        if (sPendingAuthSocket != null) {
                            try { sPendingAuthSocket.close(); } catch (Exception ignored) {}
                        }
                        sPendingAuthSocket = client;
                        sPendingRawTarget = rawTarget;
                        sPendingVaultId = obj.optString("vaultId", "");
                        sPendingTarget = rawTarget.isEmpty() ? "Protected Resource" : rawTarget;
                        sPendingNonce = nonce;
                        sPendingActionType = actionType;
                        sPendingWrapped = wrapped;
                        sPendingU = null;
                        sPendingPub = "";
                        sPendingFailReason = "";
                    }

                    String shown = rawTarget.isEmpty() ? "Protected Resource" : rawTarget;
                    if (MainActivity.sInstance != null && MainActivity.sIsForeground) {
                        // dashboard is already on screen: prompt right there
                        MainActivity.sInstance.triggerIncomingAuth(shown);
                    } else {
                        // app is NOT open: never open the dashboard, only the passkey-style prompt
                        showPasskeyPrompt(shown, actionType);
                    }
                } else {
                    client.close();
                }
            } catch (Exception e) {
                try { client.close(); } catch (Exception ignored) {}
            }
        }).start();
    }

    /**
     * Passkey-style: a high-priority heads-up / full-screen notification whose only job is to open the
     * transparent AuthPromptActivity (system fingerprint dialog, no dashboard). Where Android allows
     * background activity starts (older versions) the dialog also pops up by itself.
     */
    private void showPasskeyPrompt(String target, String actionType) {
        Intent intent = new Intent(this, AuthPromptActivity.class);
        intent.putExtra(AuthPromptActivity.EXTRA_TARGET, target);
        intent.putExtra(AuthPromptActivity.EXTRA_ACTION_TYPE, actionType);
        intent.setFlags(Intent.FLAG_ACTIVITY_NEW_TASK
                | Intent.FLAG_ACTIVITY_CLEAR_TOP
                | Intent.FLAG_ACTIVITY_NO_ANIMATION
                | Intent.FLAG_ACTIVITY_EXCLUDE_FROM_RECENTS);

        PendingIntent pi = PendingIntent.getActivity(this, 1, intent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm != null) {
            boolean isLock = "lock".equalsIgnoreCase(actionType);
            boolean isEnroll = "enroll".equalsIgnoreCase(actionType);
            Notification alert = new NotificationCompat.Builder(this, ALERT_CHANNEL_ID)
                    .setContentTitle(isEnroll ? "Locker fingerprint setup" : (isLock ? "Locker Lock Request" : "Locker Security Request"))
                    .setContentText(isEnroll ? "Tap and touch the sensor to link your fingerprint" : ((isLock ? "Tap to confirm lock: " : "Tap to approve: ") + target))
                    .setSmallIcon(android.R.drawable.ic_lock_idle_lock)
                    .setPriority(NotificationCompat.PRIORITY_MAX)
                    .setCategory(NotificationCompat.CATEGORY_MESSAGE)
                    .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
                    .setDefaults(Notification.DEFAULT_ALL)
                    .setContentIntent(pi)
                    .setFullScreenIntent(pi, true)
                    .setTimeoutAfter(60000) // the PC only waits ~60s anyway
                    .setAutoCancel(true)
                    .build();
            nm.notify(ALERT_NOTIFICATION_ID, alert);
        }

        // Best effort direct start (works where background starts are still allowed).
        try {
            startActivity(intent);
        } catch (Exception ignored) {
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        mIsRunning = false;
        try {
            if (mServerSocket != null) mServerSocket.close();
        } catch (Exception ignored) {}
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
