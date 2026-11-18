package online.feian.mailreminder;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.provider.Settings;
import android.widget.Toast;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.security.MessageDigest;
import java.util.Arrays;
import org.json.JSONObject;

final class UpdateManager {
    private static volatile boolean busy;

    static void check(Activity activity) {
        if (busy) return;
        busy = true;
        new Thread(() -> {
            try {
                JSONObject info = Api.call("GET", "api/updates/android", "", null);
                int code = info.getInt("versionCode");
                if (code <= SecurityPolicy.VERSION_CODE) {
                    ui(activity, () -> Toast.makeText(activity, "当前已是最新版本", Toast.LENGTH_LONG).show());
                } else {
                    SecurityPolicy.trustedDownload(info.getString("url"));
                    String sha = info.getString("sha256");
                    if (!sha.matches("[a-fA-F0-9]{64}")) throw new IllegalStateException("更新校验值无效");
                    ui(activity, () -> new AlertDialog.Builder(activity).setTitle("发现新版本 " + info.optString("version"))
                        .setMessage(info.optString("notes", "更新邮件提醒助手") + "\n下载完成并验证后，将由系统询问安装。")
                        .setNegativeButton("稍后", null).setPositiveButton("下载并安装", (dialog, which) -> download(activity, info)).show());
                }
            } catch (Exception error) { showError(activity, error); }
            finally { busy = false; }
        }, "mail-reminder-update-check").start();
    }

    private static void download(Activity activity, JSONObject info) {
        if (busy) return;
        busy = true;
        Toast.makeText(activity, "正在下载更新…", Toast.LENGTH_LONG).show();
        new Thread(() -> {
            File partial = new File(activity.getCacheDir(), "updates/download.part");
            try {
                if (!partial.getParentFile().isDirectory() && !partial.getParentFile().mkdirs()) throw new IllegalStateException("无法创建更新目录");
                HttpURLConnection connection = (HttpURLConnection)SecurityPolicy.trustedDownload(info.getString("url")).toURL().openConnection();
                connection.setConnectTimeout(12000); connection.setReadTimeout(20000); connection.setInstanceFollowRedirects(false);
                connection.setRequestProperty("User-Agent", SecurityPolicy.USER_AGENT);
                MessageDigest digest = MessageDigest.getInstance("SHA-256");
                long size = 0;
                try {
                    if (connection.getResponseCode() != 200) throw new IllegalStateException("安装包下载失败（HTTP " + connection.getResponseCode() + "）");
                    try (InputStream input = connection.getInputStream(); FileOutputStream output = new FileOutputStream(partial)) {
                        byte[] buffer = new byte[16384]; int count;
                        while ((count = input.read(buffer)) != -1) {
                            size += count;
                            if (size > 128L * 1024 * 1024) throw new IllegalStateException("安装包超过允许大小");
                            digest.update(buffer, 0, count); output.write(buffer, 0, count);
                        }
                        output.getFD().sync();
                    }
                } finally { connection.disconnect(); }
                if (!SecurityPolicy.hex(digest.digest()).equalsIgnoreCase(info.getString("sha256"))) throw new IllegalStateException("安装包 SHA-256 验证失败");
                if (info.has("size") && info.getLong("size") != size) throw new IllegalStateException("安装包大小验证失败");
                verifyPackage(activity, partial, info.getInt("versionCode"));
                File verified = new File(partial.getParentFile(), "verified.apk");
                if (verified.exists() && !verified.delete()) throw new IllegalStateException("无法替换先前下载的安装包");
                if (!partial.renameTo(verified)) throw new IllegalStateException("无法保存安装包");
                new AppState(activity).prefs.edit().putBoolean("pendingInstall", true).commit();
                ui(activity, () -> install(activity));
            } catch (Exception error) {
                partial.delete(); showError(activity, error);
            } finally { busy = false; }
        }, "mail-reminder-update-download").start();
    }

    private static void verifyPackage(Activity activity, File file, int expectedCode) throws Exception {
        PackageManager manager = activity.getPackageManager();
        PackageInfo candidate = manager.getPackageArchiveInfo(file.getAbsolutePath(), PackageManager.GET_SIGNATURES);
        PackageInfo current = manager.getPackageInfo(activity.getPackageName(), PackageManager.GET_SIGNATURES);
        if (candidate == null || !activity.getPackageName().equals(candidate.packageName) || candidate.versionCode != expectedCode ||
                candidate.versionCode <= current.versionCode || candidate.signatures == null || candidate.signatures.length != 1 ||
                current.signatures == null || current.signatures.length != 1 ||
                !Arrays.equals(candidate.signatures[0].toByteArray(), current.signatures[0].toByteArray())) {
            throw new IllegalStateException("安装包名称、版本或签名不匹配");
        }
    }

    static void resumeInstall(Activity activity) {
        if (new AppState(activity).prefs.getBoolean("pendingInstall", false) && activity.getPackageManager().canRequestPackageInstalls()) install(activity);
    }

    private static void install(Activity activity) {
        if (!activity.getPackageManager().canRequestPackageInstalls()) {
            new AlertDialog.Builder(activity).setTitle("允许安装应用更新").setMessage("请在系统页面允许邮件提醒助手请求安装更新。最终安装由系统确认。")
                .setNegativeButton("稍后", null).setPositiveButton("前往设置", (dialog, which) ->
                    activity.startActivity(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + activity.getPackageName())))).show();
            return;
        }
        File file = new File(activity.getCacheDir(), "updates/verified.apk");
        if (!file.isFile()) { new AppState(activity).prefs.edit().putBoolean("pendingInstall", false).apply(); return; }
        Intent intent = new Intent(Intent.ACTION_VIEW).setDataAndType(UpdateProvider.UPDATE_URI, "application/vnd.android.package-archive")
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        intent.setClipData(ClipData.newRawUri("verified update", UpdateProvider.UPDATE_URI));
        try {
            new AppState(activity).prefs.edit().putBoolean("pendingInstall", false).apply();
            activity.startActivity(intent);
        } catch (Exception error) { showError(activity, error); }
    }

    private static void showError(Activity activity, Exception error) {
        ui(activity, () -> Toast.makeText(activity, "更新未完成 · " + (error.getMessage() == null ? "请稍后重试" : error.getMessage()), Toast.LENGTH_LONG).show());
    }

    private static void ui(Activity activity, Runnable action) { activity.runOnUiThread(() -> { if (!activity.isFinishing() && !activity.isDestroyed()) action.run(); }); }
}
