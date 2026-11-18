package online.feian.mailreminder;

import java.net.HttpURLConnection;

/** Independent receive and receipt loops, each with its own cancellable connection. */
final class RelayWorkers implements AutoCloseable {
    interface Step {
        long run() throws Exception;
        long failed(Exception failure);
    }

    static final class ConnectionSlot {
        private HttpURLConnection connection;
        private boolean closed;

        synchronized void attach(HttpURLConnection next) {
            if (closed) {
                next.disconnect();
                throw new IllegalStateException("Connection worker has stopped");
            }
            connection = next;
        }

        synchronized void clear() { connection = null; }

        void disconnectRequest() {
            HttpURLConnection previous;
            synchronized (this) { previous = connection; }
            if (previous != null) previous.disconnect();
        }

        void close() {
            HttpURLConnection previous;
            synchronized (this) {
                closed = true;
                previous = connection;
                connection = null;
            }
            if (previous != null) previous.disconnect();
        }
    }

    final ConnectionSlot pollConnection = new ConnectionSlot();
    final ConnectionSlot ackConnection = new ConnectionSlot();
    private final Runner poll;
    private final Runner acknowledgements;

    RelayWorkers(Step pollStep, Step ackStep) {
        poll = new Runner("mail-reminder-poll", pollStep);
        acknowledgements = new Runner("mail-reminder-ack", ackStep);
    }

    void start() { poll.thread.start(); acknowledgements.thread.start(); }
    void wakeReceipts() { acknowledgements.wake(); }
    void networkAvailable() { poll.wake(); acknowledgements.wake(); }

    void networkChanged() {
        pollConnection.disconnectRequest();
        ackConnection.disconnectRequest();
        networkAvailable();
    }

    @Override public void close() {
        poll.stop();
        acknowledgements.stop();
        pollConnection.close();
        ackConnection.close();
    }

    boolean awaitStopped(long milliseconds) throws InterruptedException {
        long deadline = System.nanoTime() + milliseconds * 1_000_000L;
        poll.thread.join(Math.max(1, milliseconds));
        long remaining = (deadline - System.nanoTime()) / 1_000_000L;
        if (remaining > 0) acknowledgements.thread.join(remaining);
        return !poll.thread.isAlive() && !acknowledgements.thread.isAlive();
    }

    private static final class Runner implements Runnable {
        private final Object signal = new Object();
        private final Step step;
        final Thread thread;
        private boolean running = true;
        private long wakeVersion;

        Runner(String name, Step step) {
            this.step = step;
            thread = new Thread(this, name);
        }

        void wake() {
            synchronized (signal) { wakeVersion++; signal.notifyAll(); }
        }

        void stop() {
            synchronized (signal) { running = false; signal.notifyAll(); }
            thread.interrupt();
        }

        @Override public void run() {
            while (true) {
                long observedWake;
                synchronized (signal) {
                    if (!running) return;
                    observedWake = wakeVersion;
                }
                long delay;
                try { delay = step.run(); }
                catch (Exception failure) {
                    synchronized (signal) { if (!running) return; }
                    delay = step.failed(failure);
                }
                synchronized (signal) {
                    if (!running) return;
                    if (delay > 0 && wakeVersion == observedWake) {
                        try { signal.wait(delay); }
                        catch (InterruptedException interrupted) { if (!running) return; }
                    }
                }
            }
        }
    }
}
