package online.feian.mailreminder;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.Uri;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import java.util.Map;
import org.json.JSONArray;
import org.json.JSONObject;

public final class RelayService extends Service {
    static final String STATUS_ACTION = "online.feian.mailreminder.STATUS";
    private static final String CONNECTION_CHANNEL = "connection";
    private static final String REMINDER_CHANNEL = "urgent-mail";
    private static final int REMINDER_ID = 2;
    private final Handler main = new Handler(Looper.getMainLooper());
    private AppState state;
    private volatile Session session;
    private ConnectivityManager connectivity;
    private ConnectivityManager.NetworkCallback networkCallback;
    private volatile Network currentNetwork;

    static void start(Context context) {
        context.startForegroundService(new Intent(context, RelayService.class).setAction("refresh"));
    }

    static void stop(Context context) {
        new AppState(context).setEnabled(false);
        context.stopService(new Intent(context, RelayService.class));
    }

    static String notificationWarning(Context context) {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        if (!manager.areNotificationsEnabled()) return "通知权限未开启，系统提醒无法显示";
        NotificationChannel channel = manager.getNotificationChannel(REMINDER_CHANNEL);
        if (channel != null && channel.getImportance() == NotificationManager.IMPORTANCE_NONE)
            return "加急邮件通知频道已关闭";
        return "";
    }

    static void cancelReminder(Context context, Intent intent, String id) {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        manager.cancel(id, REMINDER_ID);
        // Notifications already posted by 1.3.0 used an integer ID without a tag.
        if (intent.hasExtra("notificationId")) {
            int legacyId = intent.getIntExtra("notificationId", 0);
            if (legacyId > REMINDER_ID) manager.cancel(legacyId);
        }
    }

    @Override public void onCreate() {
        super.onCreate();
        state = new AppState(this);
        createChannels();
        Notification foreground = connectionNotification("正在连接 FEIAN");
        if (Build.VERSION.SDK_INT >= 34) startForeground(1, foreground, ServiceInfo.FOREGROUND_SERVICE_TYPE_REMOTE_MESSAGING);
        else startForeground(1, foreground);
        connectivity = getSystemService(ConnectivityManager.class);
        networkCallback = new ConnectivityManager.NetworkCallback() {
            @Override public void onAvailable(Network network) {
                if (network.equals(currentNetwork)) return;
                currentNetwork = network;
                Session active = session;
                if (active != null) active.workers.networkChanged();
            }
            @Override public void onLost(Network network) {
                if (!network.equals(currentNetwork)) return;
                currentNetwork = null;
                Session active = session;
                if (active != null) active.workers.networkChanged();
            }
        };
        try { connectivity.registerDefaultNetworkCallback(networkCallback); }
        catch (SecurityException denied) { networkCallback = null; }
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (!state.enabled() || state.token().isEmpty()) {
            closeSession();
            stopForeground(STOP_FOREGROUND_REMOVE);
            stopSelf();
            return START_NOT_STICKY;
        }
        String token = state.token();
        Session active = session;
        if (active == null || !active.token.equals(token)) {
            closeSession();
            active = new Session(token);
            session = active;
            active.workers.start();
        }
        // Activity resumes and human acknowledgments wake only the receipt loop.
        active.workers.wakeReceipts();
        active.publishStatus(active.status);
        return START_STICKY;
    }

    private void closeSession() {
        Session previous = session;
        session = null;
        if (previous != null) previous.workers.close();
    }

    private final class Session {
        final String token;
        final RelayWorkers workers;
        volatile String status = "正在连接 FEIAN";

        Session(String token) {
            this.token = token;
            workers = new RelayWorkers(new RelayWorkers.Step() {
                private int retrySeconds = 1;
                @Override public long run() throws Exception {
                    if (!current()) return 60_000;
                    JSONObject response;
                    try {
                        response = Api.call("GET", "api/mobile/poll?cursor=" + state.cursor(), token, null,
                                connection -> workers.pollConnection.attach(connection));
                    } finally { workers.pollConnection.clear(); }
                    if (!current()) return 60_000;
                    JSONArray packets = response.getJSONArray("packets");
                    for (int index = 0; index < packets.length(); index++) {
                        JSONObject packet;
                        try {
                            JSONObject envelope = new JSONObject(packets.getString(index));
                            packet = new JSONObject(envelope.getString("Payload"));
                        } catch (org.json.JSONException malformed) { continue; }
                        if (!process(packet)) return 60_000;
                    }
                    synchronized (ReceiptQueue.LOCK) {
                        if (!current()) return 60_000;
                        state.saveCursor(token, response.getLong("cursor"));
                    }
                    workers.wakeReceipts();
                    publishStatus("已连接 · " + state.subnet());
                    retrySeconds = 1;
                    return 0;
                }
                @Override public long failed(Exception failure) {
                    if (!current()) return 60_000;
                    if (unauthorized(failure)) return 60_000;
                    publishStatus("连接中断，正在重试");
                    long delay = retrySeconds * 1000L;
                    retrySeconds = Math.min(8, retrySeconds * 2);
                    return delay;
                }
            }, new RelayWorkers.Step() {
                private int retrySeconds = 1;
                @Override public long run() throws Exception {
                    if (!current()) return 60_000;
                    Map<String, Boolean> pending = state.pendingAcks(token);
                    if (pending.isEmpty()) return 60_000;
                    Map.Entry<String, Boolean> receipt = pending.entrySet().iterator().next();
                    String id = receipt.getKey();
                    boolean seen = receipt.getValue();
                    try {
                        Api.call("POST", "api/mobile/ack", token,
                                new JSONObject().put("alertId", id).put("seen", seen),
                                connection -> workers.ackConnection.attach(connection));
                        state.removeAck(token, id, seen);
                    } catch (Api.Failure failure) {
                        if (failure.status == 404 || failure.status == 410) state.removeAck(token, id, true);
                        else throw failure;
                    } finally { workers.ackConnection.clear(); }
                    receiptStatus(state.pendingAcks(token).isEmpty() ? "回执已同步" : "正在同步回执");
                    retrySeconds = 1;
                    return 0;
                }
                @Override public long failed(Exception failure) {
                    if (!current()) return 60_000;
                    if (unauthorized(failure)) return 60_000;
                    receiptStatus("回执待同步，正在重试");
                    long delay = retrySeconds * 1000L;
                    retrySeconds = Math.min(8, retrySeconds * 2);
                    return delay;
                }
            });
        }

        boolean current() { return session == this && state.enabled() && state.matches(token); }

        private boolean unauthorized(Exception failure) {
            if (!(failure instanceof Api.Failure) || ((Api.Failure)failure).status != 401) return false;
            if (state.disable(token)) {
                publishStatus("绑定已失效，请重新绑定");
                main.post(() -> {
                    if (session != this || !state.matches(token)) return;
                    closeSession();
                    stopForeground(STOP_FOREGROUND_REMOVE);
                    stopSelf();
                });
            }
            return true;
        }

        private boolean process(JSONObject packet) {
            synchronized (ReceiptQueue.LOCK) {
                if (!current()) return false;
                if (packet.optInt("Version") != 1) return true;
                String type = packet.optString("Type");
                String id = packet.optString("Id");
                if (!SecurityPolicy.validAlertId(id)) return true;
                if ("alert".equals(type)) {
                    if (state.nodeId().equals(packet.optString("SenderId")) ||
                            !SecurityPolicy.recent(packet.optLong("AlertTime"), System.currentTimeMillis())) return true;
                    if (!state.queueAck(token, id, false)) receiptStatus("回执队列已满，部分软件回执未入队");
                    if (!state.processed(token, id) && showReminder(packet)) state.markProcessed(token, id);
                    workers.wakeReceipts();
                } else if (("ack".equals(type) || "seen".equals(type)) &&
                        state.nodeId().equals(packet.optString("TargetId"))) {
                    state.receipt(token, id, packet.optString("SenderId"), "seen".equals(type));
                    sendBroadcast(new Intent(STATUS_ACTION).setPackage(getPackageName()));
                }
                return true;
            }
        }

        private boolean showReminder(JSONObject packet) {
            String id = packet.optString("Id");
            String node = state.nodeId();
            Intent seen = new Intent(RelayService.this, ReminderActionReceiver.class)
                    .setData(Uri.parse("mail-reminder://seen/" + id))
                    .putExtra("alertId", id).putExtra("bindingNodeId", node);
            PendingIntent action = PendingIntent.getBroadcast(RelayService.this, 0, seen,
                    PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
            Intent open = new Intent(RelayService.this, MainActivity.class)
                    .setData(Uri.parse("mail-reminder://open/" + id))
                    .putExtra("seenAlertId", id).putExtra("bindingNodeId", node)
                    .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
            PendingIntent content = PendingIntent.getActivity(RelayService.this, 0, open,
                    PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
            String title = packet.optBoolean("IsTest") ? "联机测试提醒" : SecurityPolicy.MESSAGE;
            String detail = packet.optString("Name", "同事") + " · " + state.subnet();
            Notification notification = new Notification.Builder(RelayService.this, REMINDER_CHANNEL)
                    .setSmallIcon(R.drawable.app_icon).setContentTitle(title).setContentText(detail)
                    .setStyle(new Notification.BigTextStyle().bigText(title + "\n" + detail))
                    .setCategory(Notification.CATEGORY_EMAIL).setContentIntent(content)
                    .addAction(new Notification.Action.Builder(null, "知道了", action).build())
                    .setAutoCancel(true).setOnlyAlertOnce(true).setTimeoutAfter(SecurityPolicy.NOTIFICATION_TIMEOUT_MS)
                    .build();
            boolean posted = false;
            String warning = notificationWarning(RelayService.this);
            if (warning.isEmpty()) {
                try {
                    getSystemService(NotificationManager.class).notify(id, REMINDER_ID, notification);
                    posted = true;
                } catch (SecurityException denied) { warning = "请开启通知权限，才能显示收到的提醒"; }
            }
            state.prefs.edit().putString("lastIncoming", packet.optString("Name", "同事") + " 发来提醒")
                    .putString("notificationWarning", warning).commit();
            sendBroadcast(new Intent(STATUS_ACTION).setPackage(getPackageName()).putExtra("incoming", title));
            return posted;
        }

        void publishStatus(String message) {
            synchronized (ReceiptQueue.LOCK) {
                if (session != this || !state.matches(token)) return;
                status = message;
                String warning = notificationWarning(RelayService.this);
                String shown = warning.isEmpty() ? message : message + "\n" + warning;
                if (shown.equals(state.prefs.getString("connectionStatus", ""))) return;
                state.prefs.edit().putString("connectionMessage", message).putString("connectionStatus", shown).commit();
                try { getSystemService(NotificationManager.class).notify(1, connectionNotification(shown)); }
                catch (SecurityException denied) { }
                sendBroadcast(new Intent(STATUS_ACTION).setPackage(getPackageName()));
            }
        }

        private void receiptStatus(String message) {
            synchronized (ReceiptQueue.LOCK) {
                if (!state.matches(token)) return;
                if (message.equals(state.prefs.getString("receiptStatus", ""))) return;
                state.prefs.edit().putString("receiptStatus", message).commit();
                sendBroadcast(new Intent(STATUS_ACTION).setPackage(getPackageName()));
            }
        }
    }

    private void createChannels() {
        NotificationManager manager = getSystemService(NotificationManager.class);
        NotificationChannel connection = new NotificationChannel(CONNECTION_CHANNEL, "后台连接状态", NotificationManager.IMPORTANCE_LOW);
        connection.setDescription("持续接收绑定网段的邮件提醒");
        connection.setShowBadge(false);
        NotificationChannel urgent = new NotificationChannel(REMINDER_CHANNEL, "加急邮件提醒", NotificationManager.IMPORTANCE_HIGH);
        urgent.setDescription("收到新提醒时展示，5秒后自动关闭");
        urgent.enableVibration(true);
        manager.createNotificationChannel(connection);
        manager.createNotificationChannel(urgent);
    }

    private Notification connectionNotification(String message) {
        Intent open = new Intent(this, MainActivity.class);
        PendingIntent pending = PendingIntent.getActivity(this, 0, open, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, CONNECTION_CHANNEL).setSmallIcon(R.drawable.app_icon)
                .setContentTitle("邮件提醒助手").setContentText(message).setContentIntent(pending)
                .setOngoing(true).setOnlyAlertOnce(true).setCategory(Notification.CATEGORY_SERVICE).build();
    }

    @Override public void onDestroy() {
        closeSession();
        if (networkCallback != null) {
            try { connectivity.unregisterNetworkCallback(networkCallback); }
            catch (IllegalArgumentException ignored) { }
        }
        main.removeCallbacksAndMessages(null);
        stopForeground(STOP_FOREGROUND_REMOVE);
        super.onDestroy();
    }

    @Override public IBinder onBind(Intent intent) { return null; }
}
