package com.inex.locker.companion;

import android.app.NotificationManager;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.Bundle;
import android.view.WindowManager;

import androidx.fragment.app.FragmentActivity;

/**
 * Passkey-style approval prompt.
 *
 * Fully transparent activity that shows ONLY the system fingerprint / face dialog and then closes
 * itself. The Locker Key dashboard is never opened, so after approving you are back exactly where
 * you were (previous app, home screen or lock screen).
 */
public class AuthPromptActivity extends FragmentActivity {

    public static final String EXTRA_TARGET = "target";
    public static final String EXTRA_ACTION_TYPE = "action_type";

    private boolean mPrompting = false;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        // show over the lock screen so the prompt appears even when the phone is locked
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) {
            setShowWhenLocked(true);
            setTurnScreenOn(true);
        } else {
            getWindow().addFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED
                    | WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON);
        }
        overridePendingTransition(0, 0);

        showPrompt(getIntent());
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        showPrompt(intent);
    }

    private void showPrompt(Intent intent) {
        if (mPrompting) return;

        // Request already answered / timed out on the PC: nothing to approve, just disappear.
        if (CompanionListenerService.getPendingSocket() == null) {
            closeSilently();
            return;
        }

        String target = intent != null ? intent.getStringExtra(EXTRA_TARGET) : null;
        if (target == null || target.isEmpty()) target = CompanionListenerService.getPendingTarget();
        if (target == null || target.isEmpty()) target = "Protected Resource";

        String actionType = intent != null ? intent.getStringExtra(EXTRA_ACTION_TYPE) : null;
        if (actionType == null || actionType.isEmpty()) actionType = CompanionListenerService.getPendingActionType();
        boolean isLock = "lock".equalsIgnoreCase(actionType);

        mPrompting = true;
        BiometricHelper.prompt(
                this,
                isLock ? "Locker Lock Request" : "Locker Security Request",
                (isLock ? "Confirm locking: " : "Authorize access to: ") + target,
                new BiometricHelper.BiometricCallback() {
                    @Override
                    public void onSuccess(String authType) {
                        CompanionListenerService.completePendingAuth(true, authType);
                        closeSilently();
                    }

                    @Override
                    public void onFailure(String error) {
                        // terminal error (too many attempts, hardware busy...) -> tell the PC "no"
                        CompanionListenerService.completePendingAuth(false, null);
                        closeSilently();
                    }

                    @Override
                    public void onCancelled() {
                        CompanionListenerService.completePendingAuth(false, null);
                        closeSilently();
                    }
                });
    }

    private void closeSilently() {
        mPrompting = false;
        try {
            NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
            if (nm != null) nm.cancel(CompanionListenerService.ALERT_NOTIFICATION_ID);
        } catch (Exception ignored) {}

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
            finishAndRemoveTask();
        } else {
            finish();
        }
        overridePendingTransition(0, 0);
    }
}
