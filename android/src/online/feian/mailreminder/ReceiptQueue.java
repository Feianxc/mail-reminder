package online.feian.mailreminder;

import java.util.LinkedHashMap;
import java.util.Map;

final class ReceiptQueue {
    // Shared by Activity, receiver and service instances in this app process.
    static final Object LOCK = new Object();
    static final int LIMIT = 128;

    interface Store {
        String token();
        Map<String, Boolean> read();
        boolean write(Map<String, Boolean> pending);
    }

    private final Store store;
    ReceiptQueue(Store store) { this.store = store; }

    boolean enqueue(String token, String id, boolean seen) {
        synchronized (LOCK) {
            if (!matches(token) || !SecurityPolicy.validAlertId(id)) return false;
            Map<String, Boolean> pending = store.read();
            if (!pending.containsKey(id) && pending.size() >= LIMIT) return false;
            pending.put(id, seen || Boolean.TRUE.equals(pending.get(id)));
            return store.write(pending);
        }
    }

    Map<String, Boolean> snapshot(String token) {
        synchronized (LOCK) {
            return matches(token) ? new LinkedHashMap<>(store.read()) : new LinkedHashMap<>();
        }
    }

    void remove(String token, String id, boolean deliveredSeen) {
        synchronized (LOCK) {
            if (!matches(token)) return;
            Map<String, Boolean> pending = store.read();
            // Keep a SEEN queued while an earlier ACK request was in flight.
            if (pending.containsKey(id) && (deliveredSeen || !pending.get(id))) {
                pending.remove(id);
                store.write(pending);
            }
        }
    }

    private boolean matches(String token) {
        return token != null && !token.isEmpty() && token.equals(store.token());
    }
}
