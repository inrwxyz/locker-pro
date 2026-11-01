package com.inex.locker.companion;

import android.content.Context;
import android.content.SharedPreferences;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.Locale;

import javax.crypto.Cipher;
import javax.crypto.Mac;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

/**
 * Message authentication shared with the PC (AppLocker.Core/PhoneAuth.cs).
 * The canonical byte layout MUST stay identical on both sides:
 *   purpose + "\n" + "len:value|len:value|..."   (len = UTF-16 length, same as C# string.Length)
 * HMAC-SHA256, key = UTF-8 bytes of the shared secret string, output = lowercase hex.
 */
final class PhoneAuth {
    private static final SecureRandom RNG = new SecureRandom();

    private PhoneAuth() {}

    static String newNonce() {
        byte[] b = new byte[16];
        RNG.nextBytes(b);
        return hex(b);
    }

    static boolean isValidNonce(String n) {
        if (n == null || n.length() < 16 || n.length() > 64) return false;
        for (int i = 0; i < n.length(); i++) {
            char c = n.charAt(i);
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }

    static String sign(String secret, String purpose, String... fields) {
        if (secret == null || secret.isEmpty()) return "";
        try {
            Mac mac = Mac.getInstance("HmacSHA256");
            mac.init(new SecretKeySpec(secret.getBytes(StandardCharsets.UTF_8), "HmacSHA256"));
            return hex(mac.doFinal(canonical(purpose, fields)));
        } catch (Exception e) {
            return "";
        }
    }

    static boolean verify(String secret, String macHex, String purpose, String... fields) {
        if (secret == null || secret.isEmpty() || macHex == null || macHex.isEmpty()) return false;
        String want = sign(secret, purpose, fields);
        if (want.isEmpty()) return false;
        return MessageDigest.isEqual(
                want.getBytes(StandardCharsets.US_ASCII),
                macHex.trim().toLowerCase(Locale.ROOT).getBytes(StandardCharsets.US_ASCII));
    }

    /** Shared secret saved at pairing time (kept native; the pong no longer returns it). */
    static String loadSecret(Context ctx) {
        if (ctx == null) return "";
        SharedPreferences sp = ctx.getSharedPreferences("locker_companion", Context.MODE_PRIVATE);
        String s = sp.getString("pc_secret", "");
        if (s == null || s.isEmpty()) {
            SharedPreferences sp2 = ctx.getSharedPreferences("locker_native_prefs", Context.MODE_PRIVATE);
            s = sp2.getString("pc_secret", "");
        }
        return s == null ? "" : s;
    }

    private static byte[] canonical(String purpose, String[] fields) {
        StringBuilder sb = new StringBuilder(purpose).append('\n');
        for (int i = 0; i < fields.length; i++) {
            if (i > 0) sb.append('|');
            String f = fields[i] == null ? "" : fields[i];
            sb.append(f.length()).append(':').append(f);
        }
        return sb.toString().getBytes(StandardCharsets.UTF_8);
    }

    private static String hex(byte[] b) {
        char[] d = "0123456789abcdef".toCharArray();
        char[] out = new char[b.length * 2];
        for (int i = 0; i < b.length; i++) {
            out[i * 2] = d[(b[i] >> 4) & 0xF];
            out[i * 2 + 1] = d[b[i] & 0xF];
        }
        return new String(out);
    }

    /**
     * Encrypts the fingerprint-released key U for the trip back to the PC.
     * Key = HMAC-SHA256(pairing secret, "u-key\n" + nonce), AES-256-GCM, AAD = nonce.
     * Output (base64) = iv(12) | ciphertext | tag(16). Must match PhoneCompanionService.DecryptU on the PC.
     */
    static String encryptForPc(String secret, String nonce, byte[] data) throws Exception {
        Mac mac = Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(secret.getBytes(StandardCharsets.UTF_8), "HmacSHA256"));
        byte[] tk = mac.doFinal(("u-key\n" + nonce).getBytes(StandardCharsets.UTF_8));
        byte[] iv = new byte[12];
        RNG.nextBytes(iv);
        Cipher c = Cipher.getInstance("AES/GCM/NoPadding");
        c.init(Cipher.ENCRYPT_MODE, new SecretKeySpec(tk, "AES"), new GCMParameterSpec(128, iv));
        c.updateAAD(nonce.getBytes(StandardCharsets.UTF_8));
        byte[] ct = c.doFinal(data);
        byte[] out = new byte[iv.length + ct.length];
        System.arraycopy(iv, 0, out, 0, iv.length);
        System.arraycopy(ct, 0, out, iv.length, ct.length);
        return android.util.Base64.encodeToString(out, android.util.Base64.NO_WRAP);
    }

    /** Persistent hardware seed for cross-PC vault biometric unlocking */
    static String getOrGenerateVaultMasterSeed(Context ctx) {
        if (ctx == null) return "fallback_seed";
        SharedPreferences sp = ctx.getSharedPreferences("locker_companion", Context.MODE_PRIVATE);
        String seed = sp.getString("vault_master_seed", "");
        if (seed == null || seed.isEmpty()) {
            byte[] b = new byte[32];
            RNG.nextBytes(b);
            seed = hex(b);
            sp.edit().putString("vault_master_seed", seed).apply();
        }
        return seed;
    }

    static String computeVaultToken(Context ctx, String vaultId) {
        String masterSeed = getOrGenerateVaultMasterSeed(ctx);
        return sign(masterSeed, "vault_token", vaultId != null ? vaultId : "default_vault");
    }

}
