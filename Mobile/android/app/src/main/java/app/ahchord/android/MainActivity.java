package app.ahchord.android;

import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.DownloadManager;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.database.Cursor;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.provider.Settings;
import android.util.Log;
import android.widget.Toast;

import androidx.core.app.NotificationCompat;
import androidx.core.content.FileProvider;

import com.getcapacitor.BridgeActivity;

import org.json.JSONObject;

import java.io.File;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;

/**
 * MT-01/MT-02: in-app update for the shell. The web app ships inside the APK
 * (standalone shell), so the APK *is* the app — the only update path is
 * self-updating: check {server}/api/app/version once at startup (startup-only
 * by rule — no resume polling, no timers); when its versionCode is newer, a
 * dialog offers download (DownloadManager) +
 * install. The server address comes from the AhChord plugin (the web layer
 * reports it after first-run setup); a dev shell with capacitor's server.url
 * set falls back to the bridge URL.
 *
 * Android 10+ silently blocks opening the installer from the background, so install is
 * offered two ways: directly when the app is in the foreground, and via a "tap to install"
 * notification (a notification tap is user-initiated, which the system allows). Android 8+
 * asks once to allow installs from this app; the flow returns to the install as soon as
 * the setting is granted.
 *
 * Silent on version-check failures (no build, unreachable server, no sidecar) — the app
 * opens normally; an update is an offer, never a gate. Plain dialogs: D-pad friendly on TV.
 */
public class MainActivity extends BridgeActivity {

    private static final String TAG = "AhChordUpdate";
    private static final String UPDATE_FILE = "ahchord-update.apk";
    private static final String CHANNEL = "app-updates";
    private static final int NOTIFY_ID = 1;
    private static final String EXTRA_INSTALL = "ahchord.installNow";

    private long downloadId = -1;
    private BroadcastReceiver downloadReceiver;
    private boolean resumed;
    private boolean installAfterSettings;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        registerPlugin(AhChordPlugin.class); // web → shell: setServer(address)
        super.onCreate(savedInstanceState);
        new Thread(this::checkForUpdate, "ahchord-update").start();
        if (getIntent().hasExtra(EXTRA_INSTALL))
            runOnUiThread(this::launchInstall); // tapped "update ready" notification
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent); // singleTask: notification taps arrive here on a live activity
        if (intent.hasExtra(EXTRA_INSTALL))
            runOnUiThread(this::launchInstall);
    }

    @Override
    public void onResume() {
        super.onResume();
        resumed = true;
        // Came back from the "allow installs" settings page: continue the install.
        if (installAfterSettings && canInstallPackages()) {
            installAfterSettings = false;
            launchInstall();
        }
        // (MT-01 reloaded the page on every warm start — with server.url serving live
        // web content. The standalone shell bundles the app in the APK, so there is
        // nothing fresher to load: the web app updates by updating the APK.)
        // Update checks are startup-only (ST-126 rule): the version check runs in
        // onCreate; a warm start from recents does not poll. Silent on failures —
        // the app opens normally; an update is an offer, never a gate. Plain
        // dialogs: D-pad friendly on TV.
    }

    @Override
    public void onPause() {
        super.onPause();
        resumed = false;
    }

    @Override
    public void onDestroy() {
        if (downloadReceiver != null) {
            try { unregisterReceiver(downloadReceiver); } catch (Exception ignored) { }
            downloadReceiver = null;
        }
        super.onDestroy();
    }

    // --- version check -----------------------------------------------------------

    private void checkForUpdate() {
        try {
            // MT-02: the address the web app saved via AhChord.setServer; dev shells with
            // capacitor's server.url set fall back to the bridge URL. No address (first
            // run) → nothing to check.
            String url = getSharedPreferences(AhChordPlugin.PREFS, MODE_PRIVATE)
                .getString(AhChordPlugin.KEY_SERVER, null);
            if (url == null && getBridge() != null) url = getBridge().getServerUrl();
            if (url == null) return;
            final String base = url.replaceAll("/+$", "");

            HttpURLConnection conn = (HttpURLConnection) new URL(base + "/api/app/version").openConnection();
            conn.setConnectTimeout(5000);
            conn.setReadTimeout(5000);
            if (conn.getResponseCode() != HttpURLConnection.HTTP_OK) return; // 404 = no build published

            JSONObject body = new JSONObject(readAll(conn));
            int latest = body.optInt("versionCode", -1);
            if (latest <= BuildConfig.VERSION_CODE) return;

            String name = body.optString("versionName", String.valueOf(latest));
            runOnUiThread(() -> promptInstall(base, name));
        } catch (Exception e) {
            Log.i(TAG, "update check skipped: " + e.getMessage());
        }
    }

    private void promptInstall(String base, String versionName) {
        new android.app.AlertDialog.Builder(this)
            .setTitle("Update available")
            .setMessage("AhChord app " + versionName + " is on your server (installed: "
                + BuildConfig.VERSION_NAME + "). Download and install?")
            .setPositiveButton("Download", (d, w) -> startDownload(base))
            .setNegativeButton("Not now", null)
            .show();
    }

    // --- download ------------------------------------------------------------------

    private void startDownload(String base) {
        File dir = getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS);
        File target = new File(dir, UPDATE_FILE);
        if (target.exists() && !target.delete())
            Log.w(TAG, "stale update apk not removable: " + target);

        registerDownloadReceiver();

        DownloadManager.Request req = new DownloadManager.Request(
                Uri.parse(base + "/api/app/download"))
            .setTitle("AhChord update")
            .setDescription("Downloading… tap the download notification when it finishes to install.")
            .setMimeType("application/vnd.android.package-archive")
            .setDestinationUri(Uri.fromFile(target))
            .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED);

        DownloadManager dm = (DownloadManager) getSystemService(Context.DOWNLOAD_SERVICE);
        downloadId = dm.enqueue(req);
        Toast.makeText(this, "Downloading the update…", Toast.LENGTH_SHORT).show();
    }

    private void registerDownloadReceiver() {
        downloadReceiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context ctx, Intent intent) {
                if (intent.getLongExtra(DownloadManager.EXTRA_DOWNLOAD_ID, -1) != downloadId) return;
                if (!downloadSuccessful()) return;
                // The installer cannot be opened from the background (Android 10+):
                // the notification is the always-works path, the direct launch the
                // fast path for when the app is visible.
                notifyUpdateReady();
                if (resumed) launchInstall();
            }
        };
        IntentFilter filter = new IntentFilter(DownloadManager.ACTION_DOWNLOAD_COMPLETE);
        if (Build.VERSION.SDK_INT >= 33)
            registerReceiver(downloadReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
        else
            registerReceiver(downloadReceiver, filter);
    }

    private boolean downloadSuccessful() {
        DownloadManager dm = (DownloadManager) getSystemService(Context.DOWNLOAD_SERVICE);
        try (Cursor c = dm.query(new DownloadManager.Query().setFilterById(downloadId))) {
            return c != null && c.moveToFirst()
                && c.getInt(c.getColumnIndexOrThrow(DownloadManager.COLUMN_STATUS))
                    == DownloadManager.STATUS_SUCCESSFUL;
        }
    }

    // --- install ---------------------------------------------------------------------

    private void notifyUpdateReady() {
        Intent open = new Intent(this, MainActivity.class)
            .putExtra(EXTRA_INSTALL, true)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        PendingIntent tap = PendingIntent.getActivity(this, NOTIFY_ID, open,
            PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        if (Build.VERSION.SDK_INT >= 26) {
            nm.createNotificationChannel(new NotificationChannel(
                CHANNEL, "App updates", NotificationManager.IMPORTANCE_HIGH));
        }
        android.app.Notification n = new NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.mipmap.ic_launcher)
            .setContentTitle("AhChord update ready")
            .setContentText("Tap to install the downloaded update")
            .setAutoCancel(true)
            .setOngoing(false)
            .setContentIntent(tap)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .build();
        try {
            nm.notify(NOTIFY_ID, n);
        } catch (SecurityException e) {
            // Android 13+ POST_NOTIFICATIONS not granted: the DownloadManager's own
            // completion notification (tap → installer) is the fallback.
            Toast.makeText(this, "Update downloaded — open your downloads notification to install it",
                Toast.LENGTH_LONG).show();
        }
    }

    private boolean canInstallPackages() {
        return Build.VERSION.SDK_INT < 26 || getPackageManager().canRequestPackageInstalls();
    }

    private void launchInstall() {
        File target = new File(getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS), UPDATE_FILE);
        if (!target.exists()) {
            Toast.makeText(this, "The update download is gone — start the update again", Toast.LENGTH_LONG).show();
            return;
        }

        // Android 8+: installing needs the user's one-time "install unknown apps" grant.
        if (!canInstallPackages()) {
            installAfterSettings = true;
            new android.app.AlertDialog.Builder(this)
                .setTitle("Allow app installs")
                .setMessage("Allow AhChord to install apps, then come back here — the install continues by itself.")
                .setPositiveButton("Open settings", (d, w) -> {
                    try {
                        startActivity(new Intent(
                            Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + getPackageName())));
                    } catch (Exception ignored) { /* no settings page (some TV builds) */ }
                })
                .setNegativeButton("Cancel", null)
                .show();
            return;
        }

        try {
            Uri apkUri = FileProvider.getUriForFile(this, getPackageName() + ".fileprovider", target);
            Intent install = new Intent(Intent.ACTION_VIEW)
                .setDataAndType(apkUri, "application/vnd.android.package-archive")
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            startActivity(install);
        } catch (Exception e) {
            Log.w(TAG, "no install viewer: " + e.getMessage());
            Toast.makeText(this, "Update downloaded: " + target.getName(), Toast.LENGTH_LONG).show();
        }
    }

    private static String readAll(HttpURLConnection conn) throws Exception {
        // Manual loop: InputStream.readAllBytes is API 33+ (minSdk here is 23).
        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
        byte[] buf = new byte[4096];
        try (InputStream in = conn.getInputStream()) {
            int n;
            while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
        }
        return out.toString("UTF-8");
    }
}
