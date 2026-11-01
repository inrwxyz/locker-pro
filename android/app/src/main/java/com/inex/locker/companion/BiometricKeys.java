package com.inex.locker.companion;

import android.os.Build;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyPermanentlyInvalidatedException;
import android.security.keystore.KeyProperties;

import java.security.Key;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.KeyStore;
import java.security.PrivateKey;

import javax.crypto.Cipher;

/**
 * Hardware-backed key that only a fingerprint can use.
 *
 * - RSA key pair lives inside the Android Keystore (TEE / StrongBox); the private key never leaves it.
 * - Every use of the private key needs a fresh BIOMETRIC_STRONG authentication (no PIN / pattern).
 * - setInvalidatedByBiometricEnrollment(true): if a NEW fingerprint is added (or all are removed) the key
 *   is destroyed by Android. So only the fingerprints that existed when the key was created can ever open it.
 *
 * The PC stores RSA-OAEP(pub, U). Only this phone, after a fingerprint, can turn it back into U.
 */
final class BiometricKeys {
    static final String ALIAS = "locker_bio_unlock_v2";
    // SHA-1 OAEP/MGF1 is what both the Android Keystore and .NET (RSAEncryptionPadding.OaepSHA1) agree on.
    static final String TRANSFORM = "RSA/ECB/OAEPWithSHA-1AndMGF1Padding";

    private BiometricKeys() {}

    /** reason: "no_key" (never enrolled / wiped) or "key_invalid" (fingerprints changed) */
    static final class KeyProblem extends Exception {
        final String reason;
        KeyProblem(String reason, Throwable cause) {
            super(reason, cause);
            this.reason = reason;
        }
    }

    /** Creates a fresh key (replacing any old one) and returns its X.509 public key bytes. */
    static byte[] createKeyAndGetPublic() throws Exception {
        KeyStore ks = KeyStore.getInstance("AndroidKeyStore");
        ks.load(null);
        if (ks.containsAlias(ALIAS)) ks.deleteEntry(ALIAS);

        KeyPairGenerator kpg = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_RSA, "AndroidKeyStore");
        KeyGenParameterSpec.Builder b = new KeyGenParameterSpec.Builder(
                ALIAS, KeyProperties.PURPOSE_DECRYPT | KeyProperties.PURPOSE_ENCRYPT)
                .setKeySize(2048)
                .setDigests(KeyProperties.DIGEST_SHA1, KeyProperties.DIGEST_SHA256)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_RSA_OAEP)
                .setUserAuthenticationRequired(true)
                .setInvalidatedByBiometricEnrollment(true);
        if (Build.VERSION.SDK_INT >= 30) {
            b.setUserAuthenticationParameters(0, KeyProperties.AUTH_BIOMETRIC_STRONG);
        }
        kpg.initialize(b.build());
        KeyPair kp = kpg.generateKeyPair();
        return kp.getPublic().getEncoded();
    }

    /** Cipher ready for BiometricPrompt.CryptoObject. Throws KeyProblem if the key is gone / invalidated. */
    static Cipher getDecryptCipher() throws KeyProblem {
        try {
            KeyStore ks = KeyStore.getInstance("AndroidKeyStore");
            ks.load(null);
            Key k = ks.getKey(ALIAS, null);
            if (!(k instanceof PrivateKey)) throw new KeyProblem("no_key", null);
            Cipher c = Cipher.getInstance(TRANSFORM);
            c.init(Cipher.DECRYPT_MODE, (PrivateKey) k);
            return c;
        } catch (KeyProblem e) {
            throw e;
        } catch (Throwable t) {
            for (Throwable x = t; x != null; x = x.getCause()) {
                if (x instanceof KeyPermanentlyInvalidatedException) throw new KeyProblem("key_invalid", t);
            }
            throw new KeyProblem("no_key", t);
        }
    }

    static void deleteKey() {
        try {
            KeyStore ks = KeyStore.getInstance("AndroidKeyStore");
            ks.load(null);
            if (ks.containsAlias(ALIAS)) ks.deleteEntry(ALIAS);
        } catch (Exception ignored) {}
    }
}
