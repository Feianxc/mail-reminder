package online.feian.mailreminder;

import com.sun.net.httpserver.HttpServer;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.InetSocketAddress;
import java.net.URL;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;

public final class AndroidConcurrencyTest {
    private static int checks;

    public static void main(String[] args) throws Exception {
        queueChecks();
        blockedAckDoesNotBlockHttpPoll();
        failedAckDoesNotBlockPoll();
        wakeAndCancellationChecks();
        System.out.println("PASS " + checks + " Android concurrency host checks");
    }

    private static void queueChecks() throws Exception {
        MemoryStore storage = new MemoryStore();
        ReceiptQueue activity = new ReceiptQueue(storage);
        ReceiptQueue receiver = new ReceiptQueue(storage);
        String id = id(1);
        check(activity.snapshot("old").isEmpty(), "zero queued receipts remain a collection");
        check(activity.enqueue("old", id, false) && activity.snapshot("old").size() == 1,
                "one receipt persists as one map entry");
        check(receiver.enqueue("old", id, true) && activity.snapshot("old").get(id),
                "receiver upgrades queued software ACK to human SEEN");
        activity.remove("old", id, false);
        check(activity.snapshot("old").get(id), "late ACK completion preserves concurrent SEEN");
        activity.remove("old", id, true);
        check(activity.snapshot("old").isEmpty(), "SEEN completion removes delivered receipt");

        CountDownLatch start = new CountDownLatch(1);
        AtomicReference<Throwable> failure = new AtomicReference<>();
        List<Thread> writers = new ArrayList<>();
        for (int owner = 0; owner < 4; owner++) {
            final int base = owner * 24;
            Thread thread = new Thread(() -> {
                try {
                    start.await();
                    for (int index = 1; index <= 24; index++) {
                        ReceiptQueue queue = index % 2 == 0 ? activity : receiver;
                        if (!queue.enqueue("old", id(base + index), index % 3 == 0))
                            throw new AssertionError("Concurrent enqueue rejected");
                    }
                } catch (Throwable error) { failure.compareAndSet(null, error); }
            });
            writers.add(thread);
            thread.start();
        }
        start.countDown();
        for (Thread writer : writers) writer.join(3000);
        check(failure.get() == null && writers.stream().noneMatch(Thread::isAlive), "concurrent component writers complete");
        check(activity.snapshot("old").size() == 96, "N receipts survive concurrent read-modify-write without loss");
        for (int index = 97; index <= 128; index++) activity.enqueue("old", id(index), false);
        check(activity.snapshot("old").size() == 128 && !activity.enqueue("old", id(129), false),
                "receipt queue has a bounded capacity");
        check(receiver.enqueue("old", id(1), true), "full queue still allows an existing receipt upgrade");

        synchronized (ReceiptQueue.LOCK) {
            storage.token = "new";
            storage.pending.clear();
        }
        check(activity.enqueue("new", id, true), "new binding can queue its own receipt");
        activity.remove("old", id, true);
        check(!receiver.enqueue("old", id(2), true) && receiver.snapshot("old").isEmpty(),
                "old binding cannot read or enqueue new binding receipts");
        check(activity.snapshot("new").get(id), "old in-flight completion cannot erase a new binding receipt");
        check(!receiver.enqueue("", id(3), false) && !receiver.enqueue("new", "bad", true),
                "empty sessions and invalid alert IDs are rejected");
        storage.failWrites = true;
        check(!activity.enqueue("new", id(3), false) && !activity.snapshot("new").containsKey(id(3)),
                "persistence failures are returned without claiming queue success");
    }

    private static void blockedAckDoesNotBlockHttpPoll() throws Exception {
        CountDownLatch ackEntered = new CountDownLatch(1);
        CountDownLatch releaseAck = new CountDownLatch(1);
        CountDownLatch pollEntered = new CountDownLatch(1);
        CountDownLatch releaseAlert = new CountDownLatch(1);
        CountDownLatch delivered = new CountDownLatch(1);
        AtomicInteger polls = new AtomicInteger();
        AtomicInteger ackDone = new AtomicInteger();
        AtomicReference<Throwable> failure = new AtomicReference<>();
        HttpServer server = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        ExecutorService serverThreads = Executors.newCachedThreadPool();
        server.setExecutor(serverThreads);
        server.createContext("/ack", exchange -> {
            ackEntered.countDown();
            try { releaseAck.await(5, TimeUnit.SECONDS); } catch (InterruptedException ignored) { }
            byte[] body = "ok".getBytes("UTF-8");
            exchange.sendResponseHeaders(200, body.length);
            exchange.getResponseBody().write(body);
            exchange.close();
        });
        server.createContext("/poll", exchange -> {
            polls.incrementAndGet();
            pollEntered.countDown();
            try { releaseAlert.await(5, TimeUnit.SECONDS); } catch (InterruptedException ignored) { }
            byte[] body = "alert".getBytes("UTF-8");
            exchange.sendResponseHeaders(200, body.length);
            exchange.getResponseBody().write(body);
            exchange.close();
        });
        server.start();
        String base = "http://127.0.0.1:" + server.getAddress().getPort();
        AtomicReference<RelayWorkers> owner = new AtomicReference<>();
        RelayWorkers workers = new RelayWorkers(new RelayWorkers.Step() {
            @Override public long run() throws Exception {
                HttpURLConnection connection = open(base + "/poll");
                owner.get().pollConnection.attach(connection);
                try (InputStream response = connection.getInputStream()) {
                    if (response.read() == 'a') delivered.countDown();
                } finally { connection.disconnect(); owner.get().pollConnection.clear(); }
                return 60_000;
            }
            @Override public long failed(Exception error) { failure.set(error); return 60_000; }
        }, new RelayWorkers.Step() {
            @Override public long run() throws Exception {
                HttpURLConnection connection = open(base + "/ack");
                owner.get().ackConnection.attach(connection);
                try (InputStream response = connection.getInputStream()) { response.read(); }
                finally { connection.disconnect(); owner.get().ackConnection.clear(); }
                ackDone.incrementAndGet();
                return 60_000;
            }
            @Override public long failed(Exception error) { failure.set(error); return 60_000; }
        });
        owner.set(workers);
        try {
            workers.start();
            check(ackEntered.await(2, TimeUnit.SECONDS) && pollEntered.await(2, TimeUnit.SECONDS),
                    "real HTTP ACK and long poll run concurrently");
            workers.wakeReceipts();
            Thread.sleep(80);
            check(polls.get() == 1 && delivered.getCount() == 1 && ackDone.get() == 0,
                    "human receipt wake leaves both in-flight HTTP requests intact");
            long started = System.nanoTime();
            releaseAlert.countDown();
            check(delivered.await(1, TimeUnit.SECONDS) && ackDone.get() == 0 && failure.get() == null,
                    "alert is received while the real HTTP ACK remains blocked");
            System.out.println("HTTP poll delivery while ACK blocked ms=" + (System.nanoTime() - started) / 1_000_000.0);
        } finally {
            releaseAck.countDown();
            releaseAlert.countDown();
            workers.close();
            check(workers.awaitStopped(3000), "real HTTP worker threads stop after cancellation");
            server.stop(0);
            serverThreads.shutdownNow();
        }
    }

    private static void failedAckDoesNotBlockPoll() throws Exception {
        AtomicInteger polls = new AtomicInteger();
        CountDownLatch failed = new CountDownLatch(1);
        CountDownLatch threePolls = new CountDownLatch(3);
        RelayWorkers workers = new RelayWorkers(new RelayWorkers.Step() {
            @Override public long run() { polls.incrementAndGet(); threePolls.countDown(); return 20; }
            @Override public long failed(Exception error) { throw new AssertionError(error); }
        }, new RelayWorkers.Step() {
            @Override public long run() throws Exception { throw new IOException("injected ACK failure"); }
            @Override public long failed(Exception error) { failed.countDown(); return 60_000; }
        });
        try {
            workers.start();
            check(failed.await(1, TimeUnit.SECONDS) && threePolls.await(1, TimeUnit.SECONDS) && polls.get() >= 3,
                    "ACK failure and its backoff never pause the poll worker");
        } finally {
            workers.close();
            check(workers.awaitStopped(2000), "failure-backoff workers stop promptly");
        }
    }

    private static void wakeAndCancellationChecks() throws Exception {
        CountDownLatch firstAck = new CountDownLatch(1);
        CountDownLatch releaseFirst = new CountDownLatch(1);
        CountDownLatch secondAck = new CountDownLatch(1);
        AtomicInteger attempts = new AtomicInteger();
        RelayWorkers workers = new RelayWorkers(new RelayWorkers.Step() {
            @Override public long run() { return 60_000; }
            @Override public long failed(Exception error) { return 60_000; }
        }, new RelayWorkers.Step() {
            @Override public long run() throws Exception {
                if (attempts.incrementAndGet() == 1) {
                    firstAck.countDown();
                    releaseFirst.await();
                } else { secondAck.countDown(); }
                return 60_000;
            }
            @Override public long failed(Exception error) { return 60_000; }
        });
        FakeConnection poll = new FakeConnection();
        FakeConnection ack = new FakeConnection();
        try {
            workers.pollConnection.attach(poll);
            workers.ackConnection.attach(ack);
            workers.start();
            check(firstAck.await(1, TimeUnit.SECONDS), "receipt worker enters first operation");
            workers.wakeReceipts();
            releaseFirst.countDown();
            check(secondAck.await(1, TimeUnit.SECONDS), "receipt wake during an operation is not lost before sleeping");
            check(poll.disconnects.get() == 0 && ack.disconnects.get() == 0,
                    "receipt wake never disconnects poll or ACK sockets");
            workers.networkChanged();
            check(poll.disconnects.get() == 1 && ack.disconnects.get() == 1,
                    "network transition cancels both old-network requests");
        } finally {
            releaseFirst.countDown();
            workers.close();
            workers.close();
            check(workers.awaitStopped(2000), "service shutdown cancels both loops idempotently");
        }
        FakeConnection late = new FakeConnection();
        boolean rejected = false;
        try { workers.ackConnection.attach(late); } catch (IllegalStateException expected) { rejected = true; }
        check(rejected && late.disconnects.get() == 1, "stopped worker rejects and disconnects late connections");
    }

    private static HttpURLConnection open(String url) throws Exception {
        HttpURLConnection connection = (HttpURLConnection)new URL(url).openConnection();
        connection.setConnectTimeout(2000);
        connection.setReadTimeout(5000);
        return connection;
    }

    private static String id(int value) { return String.format(java.util.Locale.ROOT, "%032x", value); }

    private static final class MemoryStore implements ReceiptQueue.Store {
        String token = "old";
        Map<String, Boolean> pending = new LinkedHashMap<>();
        boolean failWrites;
        @Override public String token() { return token; }
        @Override public Map<String, Boolean> read() { Thread.yield(); return new LinkedHashMap<>(pending); }
        @Override public boolean write(Map<String, Boolean> value) {
            Thread.yield();
            if (failWrites) return false;
            pending = new LinkedHashMap<>(value);
            return true;
        }
    }

    private static final class FakeConnection extends HttpURLConnection {
        final AtomicInteger disconnects = new AtomicInteger();
        FakeConnection() throws Exception { super(new URL("http://127.0.0.1/")); }
        @Override public void connect() { }
        @Override public void disconnect() { disconnects.incrementAndGet(); }
        @Override public boolean usingProxy() { return false; }
    }

    private static void check(boolean result, String name) {
        if (!result) throw new AssertionError(name);
        checks++;
        System.out.println("PASS " + name);
    }
}
