package com.inex.locker.companion;

import android.content.Context;
import android.util.Base64;
import androidx.annotation.NonNull;
import androidx.biometric.BiometricManager;
import androidx.biometric.BiometricPrompt;
import androidx.core.content.ContextCompat;
import androidx.fragment.app.FragmentActivity;

import java.util.concurrent.Executor;

import javax.crypto.Cipher;

public class BiometricHelper {

    public interface BiometricCallback {
        void onSuccess(String authType);
        void onFailure(String error);
        void onCancelled();
    }

    public static boolean isBiometricAvailable(Context context) {
        BiometricManager bm = BiometricManager.from(context);
        int canAuth = bm.canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_STRONG);
        return canAuth == BiometricManager.BIOMETRIC_SUCCESS;
    }

    /**
     * Shows the system fingerprint dialog.
     *
     * The PC request that is currently pending decides what happens:
     *  - "enroll"            : fingerprint check, then a NEW hardware key is created (binds the PC to the fingers
     *                          enrolled on this phone right now). Public key goes back to the PC.
     *  - request with key    : the dialog is bound to the Keystore key (CryptoObject). Only a real fingerprint
     *                          can decrypt the PC's blob. PIN / pattern can NOT approve this.
     *  - anything else       : plain approval (old behaviour).
     */
    public static void prompt(FragmentActivity activity, String title, String subtitle, BiometricCallback callback) {
        Executor executor = ContextCompat.getMainExecutor(activity);

        final boolean pending = CompanionListenerService.getPendingSocket() != null;
        final String actionType = CompanionListenerService.getPendingActionType();
        final String wrapped = CompanionListenerService.getPendingWrapped();
        final boolean enroll = pending && "enroll".equalsIgnoreCase(actionType);
        final boolean needKey = pending && !enroll && wrapped != null && !wrapped.isEmpty();

        BiometricPrompt.PromptInfo.Builder pib = new BiometricPrompt.PromptInfo.Builder();
        BiometricPrompt.CryptoObject crypto = null;

        if (enroll) {
            pib.setTitle("Locker fingerprint setup")
               .setSubtitle("Touch the sensor to link this fingerprint with your PC")
               .setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_STRONG)
               .setNegativeButtonText("Cancel")
               .setConfirmationRequired(false);
        } else if (needKey) {
            try {
                Cipher c = BiometricKeys.getDecryptCipher();
                crypto = new BiometricPrompt.CryptoObject(c);
            } catch (BiometricKeys.KeyProblem e) {
                // fingerprints changed / key wiped: tell the PC so it can ask for the password and re-link
                CompanionListenerService.setPendingFailReason(e.reason);
                callback.onFailure(e.reason);
                return;
            }
            pib.setTitle(title)
               .setSubtitle(subtitle)
               .setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_STRONG)
               .setNegativeButtonText("Cancel")
               .setConfirmationRequired(false);
        } else {
            pib.setTitle(title)
               .setSubtitle(subtitle)
               .setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_STRONG | BiometricManager.Authenticators.DEVICE_CREDENTIAL);
        }

        final BiometricPrompt.CryptoObject cryptoFinal = crypto;

        BiometricPrompt biometricPrompt = new BiometricPrompt(activity, executor, new BiometricPrompt.AuthenticationCallback() {
            @Override
            public void onAuthenticationSucceeded(@NonNull BiometricPrompt.AuthenticationResult result) {
                super.onAuthenticationSucceeded(result);
                try {
                    if (enroll) {
                        byte[] pub = BiometricKeys.createKeyAndGetPublic();
                        CompanionListenerService.setPendingPub(Base64.encodeToString(pub, Base64.NO_WRAP));
                    } else if (needKey) {
                        Cipher c = result.getCryptoObject() != null ? result.getCryptoObject().getCipher() : null;
                        if (c == null) throw new IllegalStateException("no crypto object");
                        byte[] u = c.doFinal(Base64.decode(wrapped, Base64.NO_WRAP));
                        CompanionListenerService.setPendingU(u);
                    }
                } catch (Exception e) {
                    String reason = enroll ? "enroll_failed" : "decrypt_failed";
                    CompanionListenerService.setPendingFailReason(reason);
                    callback.onFailure(reason);
                    return;
                }
                int type = result.getAuthenticationType();
                String authName = (type == BiometricPrompt.AUTHENTICATION_RESULT_TYPE_BIOMETRIC || enroll || needKey)
                        ? "fingerprint" : "device_credential";
                callback.onSuccess(authName);
            }

            @Override
            public void onAuthenticationError(int errorCode, @NonNull CharSequence errString) {
                super.onAuthenticationError(errorCode, errString);
                if (errorCode == BiometricPrompt.ERROR_USER_CANCELED || errorCode == BiometricPrompt.ERROR_NEGATIVE_BUTTON) {
                    callback.onCancelled();
                } else {
                    callback.onFailure(errString.toString());
                }
            }

            @Override
            public void onAuthenticationFailed() {
                super.onAuthenticationFailed();
            }
        });

        if (cryptoFinal != null) {
            biometricPrompt.authenticate(pib.build(), cryptoFinal);
        } else {
            biometricPrompt.authenticate(pib.build());
        }
    }
}
