package online.feian.mailreminder;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import org.json.JSONObject;

final class Api {
    interface ConnectionObserver { void opened(HttpURLConnection connection); }
    static final class Failure extends Exception {
        final int status;
        Failure(int status, String message) { super(message); this.status = status; }
    }

    static JSONObject call(String method, String endpoint, String token, JSONObject body) throws Exception {
        return call(method, endpoint, token, body, null);
    }

    static JSONObject call(String method, String endpoint, String token, JSONObject body, ConnectionObserver observer) throws Exception {
        if (!endpoint.matches("[A-Za-z0-9_/?=&.-]+") || endpoint.contains("..")) throw new IllegalArgumentException("接口地址无效");
        HttpURLConnection connection = (HttpURLConnection)new URL(SecurityPolicy.BASE_URL + endpoint).openConnection();
        if (observer != null) observer.opened(connection);
        connection.setConnectTimeout(10000);
        connection.setReadTimeout(endpoint.startsWith("api/mobile/poll") ? 40000 : 15000);
        connection.setInstanceFollowRedirects(false);
        connection.setRequestMethod(method);
        connection.setRequestProperty("Accept", "application/json");
        connection.setRequestProperty("User-Agent", SecurityPolicy.USER_AGENT);
        if (token != null && !token.isEmpty()) connection.setRequestProperty("Authorization", "Bearer " + token);
        try {
            if (Thread.currentThread().isInterrupted()) throw new InterruptedException("请求已取消");
            if (body != null) {
                byte[] bytes = body.toString().getBytes(StandardCharsets.UTF_8);
                connection.setDoOutput(true);
                connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
                connection.setFixedLengthStreamingMode(bytes.length);
                if (Thread.currentThread().isInterrupted()) throw new InterruptedException("请求已取消");
                try (OutputStream output = connection.getOutputStream()) { output.write(bytes); }
            }
            if (Thread.currentThread().isInterrupted()) throw new InterruptedException("请求已取消");
            int status = connection.getResponseCode();
            InputStream response = status < 400 ? connection.getInputStream() : connection.getErrorStream();
            String text = response == null ? "{}" : read(response, 1024 * 1024);
            JSONObject result;
            try { result = new JSONObject(text); } catch (Exception error) { throw new Failure(status, "服务器响应无效（HTTP " + status + "）"); }
            if (status < 200 || status >= 300) {
                String message = result.optString("message", result.optString("error", "请求失败"));
                throw new Failure(status, message + "（HTTP " + status + "）");
            }
            return result;
        } finally { connection.disconnect(); }
    }

    static String read(InputStream input, int limit) throws Exception {
        try (InputStream stream = input; ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[8192];
            int count;
            while ((count = stream.read(buffer)) != -1) {
                if (output.size() + count > limit) throw new IllegalStateException("响应内容过大");
                output.write(buffer, 0, count);
            }
            return new String(output.toByteArray(), StandardCharsets.UTF_8);
        }
    }
}
