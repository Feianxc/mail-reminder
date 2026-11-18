package online.feian.mailreminder;

public final class SecurityPolicyTest {
    private static int checks;
    public static void main(String[] args) throws Exception {
        SecurityPolicy.trustedDownload("https://blog.feian.online/mail-reminder/download/mail-reminder-android-1.3.0.apk");
        check(true, "official HTTPS APK is accepted");
        for (String url : new String[] {
            "http://blog.feian.online/mail-reminder/a.apk", "https://evil.example/mail-reminder/a.apk",
            "https://blog.feian.online:8443/mail-reminder/a.apk", "https://name@blog.feian.online/mail-reminder/a.apk",
            "https://blog.feian.online/other/a.apk", "https://blog.feian.online/mail-reminder/../a.apk",
            "https://blog.feian.online/mail-reminder/%2e%2e/a.apk",
            "https://blog.feian.online/mail-reminder/a.zip", "https://blog.feian.online/mail-reminder/a.apk#secret",
            "https://feian.online/mail-reminder/a.apk"
        }) {
            boolean rejected = false;
            try { SecurityPolicy.trustedDownload(url); } catch (IllegalArgumentException expected) { rejected = true; }
            check(rejected, "untrusted update rejected " + url);
        }
        check(SecurityPolicy.validAlertId("0123456789abcdef0123456789abcdef"), "wire alert id accepted");
        check(!SecurityPolicy.validAlertId("bad-id") && !SecurityPolicy.validAlertId(null), "invalid alert ids rejected");
        check(SecurityPolicy.recent(880000, 1000000) && SecurityPolicy.recent(1120000, 1000000), "time tolerance endpoints accepted");
        check(!SecurityPolicy.recent(879999, 1000000) && !SecurityPolicy.recent(1120001, 1000000), "stale and future messages rejected");
        check(SecurityPolicy.NOTIFICATION_TIMEOUT_MS == 5000L, "system notification auto-cancels after five seconds");
        check(SecurityPolicy.sha256("abc".getBytes("UTF-8")).equals("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"), "SHA-256 known vector");
        check("收到一封加急邮件，请立刻处理".equals(SecurityPolicy.MESSAGE), "Chinese urgent message preserved");
        System.out.println("PASS " + checks + " Android host checks");
    }
    private static void check(boolean result, String name) {
        if (!result) throw new AssertionError(name);
        checks++; System.out.println("PASS " + name);
    }
}
