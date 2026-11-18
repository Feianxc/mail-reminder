package online.feian.mailreminder;

import java.net.URI;
import java.security.MessageDigest;

final class SecurityPolicy {
    static final String BASE_URL = "https://blog.feian.online/mail-reminder/";
    static final String MESSAGE = "收到一封加急邮件，请立刻处理";
    static final long NOTIFICATION_TIMEOUT_MS = 5000L;
    static final int VERSION_CODE = 131;
    static final String USER_AGENT = "MailReminderAndroid/1.3.1";

    static URI trustedDownload(String value) {
        URI uri = URI.create(value);
        if (!"https".equalsIgnoreCase(uri.getScheme()) || !"blog.feian.online".equalsIgnoreCase(uri.getHost()) ||
                (uri.getPort() != -1 && uri.getPort() != 443) || uri.getRawUserInfo() != null || uri.getFragment() != null ||
                uri.getPath() == null || uri.getPath().contains("..") || !uri.normalize().getPath().startsWith("/mail-reminder/") ||
                !uri.getPath().toLowerCase(java.util.Locale.ROOT).endsWith(".apk")) {
            throw new IllegalArgumentException("更新地址必须是 FEIAN 的 HTTPS 安卓安装包");
        }
        return uri;
    }

    static boolean validAlertId(String id) {
        return id != null && id.matches("[a-fA-F0-9]{32}");
    }

    static boolean recent(long messageTime, long now) {
        return messageTime >= now - 120000L && messageTime <= now + 120000L;
    }

    static String sha256(byte[] bytes) throws Exception {
        return hex(MessageDigest.getInstance("SHA-256").digest(bytes));
    }

    static String hex(byte[] bytes) {
        StringBuilder result = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) result.append(String.format(java.util.Locale.ROOT, "%02x", value & 255));
        return result.toString();
    }
}
