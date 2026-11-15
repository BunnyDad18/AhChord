package app.ahchord.android;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.Log;

import androidx.security.crypto.EncryptedSharedPreferences;
import androidx.security.crypto.MasterKey;

import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;

/**
 * MT-02: tiny bridge between the bundled web app and the shell.
 *
 * setServer/getServer: the first-run server address (plain prefs; MainActivity's
 * in-app update flow polls {url}/api/app/version from here — the address is not secret).
 *
 * MT-02 secure token storage: setToken/getToken/clearToken keep the JWT in
 * EncryptedSharedPreferences (Android Keystore master key, AES-256-GCM) — the web
 * layer (src/api/tokenStore.ts) keeps it out of localStorage in the shell. If the
 * Keystore is unusable (corruption / key invalidation — rare), token storage falls
 * back to plain prefs so sign-in keeps working; re-login is the worst case, the
 * library (MT-04 cache) never rides on the token store.
 *
 * The first Keystore init takes ~100 ms on the calling thread — load() warms it
 * on a background thread so the boot-time getToken is never the first caller.
 */
@CapacitorPlugin(name = "AhChord")
public class AhChordPlugin extends Plugin {

    static final String PREFS = "ahchord_shell";
    static final String KEY_SERVER = "server_url";
    private static final String SECURE_PREFS = "ahchord_secure";
    private static final String KEY_TOKEN = "token";

    private SharedPreferences secure;

    @Override
    public void load() {
        super.load();
        new Thread(this::secure, "ahchord-keystore-warm").start();
    }

    @PluginMethod
    public void setServer(PluginCall call) {
        String url = call.getString("url");
        if (url == null || url.trim().isEmpty()) {
            call.reject("url required");
            return;
        }
        prefs().edit().putString(KEY_SERVER, url.trim()).apply();
        call.resolve();
    }

    @PluginMethod
    public void getServer(PluginCall call) {
        JSObject ret = new JSObject();
        ret.put("url", prefs().getString(KEY_SERVER, null));
        call.resolve(ret);
    }

    @PluginMethod
    public void setToken(PluginCall call) {
        String token = call.getString("token");
        SharedPreferences.Editor ed = secure().edit();
        if (token == null || token.isEmpty())
            ed.remove(KEY_TOKEN);
        else
            ed.putString(KEY_TOKEN, token);
        ed.apply();
        call.resolve();
    }

    @PluginMethod
    public void getToken(PluginCall call) {
        JSObject ret = new JSObject();
        ret.put("token", secure().getString(KEY_TOKEN, null));
        call.resolve(ret);
    }

    @PluginMethod
    public void clearToken(PluginCall call) {
        secure().edit().remove(KEY_TOKEN).apply();
        call.resolve();
    }

    private SharedPreferences prefs() {
        return getContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    private synchronized SharedPreferences secure() {
        if (secure != null) return secure;
        try {
            MasterKey key = new MasterKey.Builder(getContext())
                .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
                .build();
            secure = EncryptedSharedPreferences.create(
                getContext(),
                SECURE_PREFS,
                key,
                EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM);
        } catch (Exception e) {
            Log.w("AhChordPlugin", "encrypted prefs unavailable — falling back to plain prefs", e);
            secure = getContext().getSharedPreferences(SECURE_PREFS + "_plain", Context.MODE_PRIVATE);
        }
        return secure;
    }
}
