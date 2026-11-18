package online.feian.mailreminder;

import android.content.Context;
import android.content.SharedPreferences;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.Map;
import org.json.JSONArray;
import org.json.JSONObject;

final class AppState {
    final SharedPreferences prefs;
    private final ReceiptQueue receipts;

    AppState(Context context) {
        prefs = context.getSharedPreferences("mail-reminder", Context.MODE_PRIVATE);
        receipts = new ReceiptQueue(new ReceiptQueue.Store() {
            @Override public String token() { return AppState.this.token(); }
            @Override public Map<String, Boolean> read() {
                Map<String, Boolean> result = new LinkedHashMap<>();
                try {
                    JSONObject pending = new JSONObject(prefs.getString("pendingAck", "{}"));
                    Iterator<String> ids = pending.keys();
                    while (ids.hasNext()) {
                        String id = ids.next();
                        if (SecurityPolicy.validAlertId(id)) result.put(id, pending.getBoolean(id));
                    }
                } catch (Exception ignored) { }
                return result;
            }
            @Override public boolean write(Map<String, Boolean> pending) {
                return prefs.edit().putString("pendingAck", new JSONObject(pending).toString()).commit();
            }
        });
    }

    String token() { return prefs.getString("token", ""); }
    String nodeId() { return prefs.getString("nodeId", ""); }
    String name() { return prefs.getString("name", "我的手机"); }
    String subnet() { return prefs.getString("subnet", "尚未绑定"); }
    long cursor() { return prefs.getLong("cursor", 0); }
    boolean enabled() { return prefs.getBoolean("enabled", false); }
    boolean matches(String token) { return token != null && !token.isEmpty() && token.equals(token()); }

    void setEnabled(boolean enabled) {
        synchronized (ReceiptQueue.LOCK) { prefs.edit().putBoolean("enabled", enabled).commit(); }
    }

    boolean disable(String token) {
        synchronized (ReceiptQueue.LOCK) {
            return matches(token) && prefs.edit().putBoolean("enabled", false).commit();
        }
    }

    void bind(JSONObject binding) throws Exception {
        synchronized (ReceiptQueue.LOCK) {
            String token = binding.getString("token");
            String node = binding.getString("nodeId");
            if (token.length() < 24 || node.length() < 4) throw new IllegalArgumentException("服务器返回的绑定信息不完整");
            boolean saved = prefs.edit().clear().putString("token", token).putString("nodeId", node)
                    .putString("name", binding.getString("name")).putString("subnet", binding.getString("subnet"))
                    .putString("roomId", binding.getString("roomId")).putLong("cursor", binding.getLong("cursor"))
                    .putBoolean("enabled", true).commit();
            if (!saved) throw new IllegalStateException("绑定信息未能保存");
        }
    }

    void clear(String token) {
        synchronized (ReceiptQueue.LOCK) { if (matches(token)) prefs.edit().clear().commit(); }
    }

    void saveName(String token, String name) {
        synchronized (ReceiptQueue.LOCK) { if (matches(token)) prefs.edit().putString("name", name).commit(); }
    }

    void saveCursor(String token, long cursor) {
        synchronized (ReceiptQueue.LOCK) { if (matches(token)) prefs.edit().putLong("cursor", cursor).commit(); }
    }

    boolean processed(String token, String id) {
        synchronized (ReceiptQueue.LOCK) {
            if (!matches(token)) return false;
            try {
                JSONArray ids = new JSONArray(prefs.getString("processed", "[]"));
                for (int index = 0; index < ids.length(); index++) if (id.equals(ids.getString(index))) return true;
            } catch (Exception ignored) { }
            return false;
        }
    }

    void markProcessed(String token, String id) {
        synchronized (ReceiptQueue.LOCK) {
            if (!matches(token)) return;
            try {
                JSONArray previous = new JSONArray(prefs.getString("processed", "[]"));
                JSONArray ids = new JSONArray();
                for (int index = Math.max(0, previous.length() - 127); index < previous.length(); index++) ids.put(previous.getString(index));
                ids.put(id);
                prefs.edit().putString("processed", ids.toString()).commit();
            } catch (Exception ignored) { }
        }
    }

    boolean queueAck(String token, String id, boolean seen) { return receipts.enqueue(token, id, seen); }
    Map<String, Boolean> pendingAcks(String token) { return receipts.snapshot(token); }
    void removeAck(String token, String id, boolean deliveredSeen) { receipts.remove(token, id, deliveredSeen); }

    boolean queueSeen(String node, String id) {
        synchronized (ReceiptQueue.LOCK) {
            String token = token();
            // Older notifications had no binding node. Accept only an ID received by this binding.
            if (node == null ? !processed(token, id) : !node.equals(nodeId())) return false;
            return queueAck(token, id, true);
        }
    }

    void receipt(String token, String id, String sender, boolean seen) {
        synchronized (ReceiptQueue.LOCK) {
            if (!matches(token)) return;
            try {
                JSONObject cache = new JSONObject(prefs.getString("receiptCache", "{}"));
                if (!cache.has(id) && cache.length() >= 32) cache.remove(cache.keys().next());
                JSONObject record = cache.optJSONObject(id);
                if (record == null) record = new JSONObject().put("received", new JSONObject()).put("seen", new JSONObject());
                JSONObject received = record.getJSONObject("received");
                JSONObject known = record.getJSONObject("seen");
                received.put(sender, true);
                if (seen) known.put(sender, true);
                cache.put(id, record);
                prefs.edit().putString("receiptCache", cache.toString()).commit();
                if (id.equals(prefs.getString("lastAlertId", ""))) updateReceiptText(record);
            } catch (Exception ignored) { }
        }
    }

    void sent(String token, String id) {
        synchronized (ReceiptQueue.LOCK) {
            if (!matches(token)) return;
            prefs.edit().putString("lastAlertId", id).putString("lastResult", "已发送，等待软件回执").commit();
            try {
                JSONObject record = new JSONObject(prefs.getString("receiptCache", "{}")).optJSONObject(id);
                if (record != null) updateReceiptText(record);
            } catch (Exception ignored) { }
        }
    }

    private void updateReceiptText(JSONObject record) throws Exception {
        prefs.edit().putString("lastResult", record.getJSONObject("received").length() + " 台软件收到，" +
                record.getJSONObject("seen").length() + " 人已知晓").commit();
    }
}
