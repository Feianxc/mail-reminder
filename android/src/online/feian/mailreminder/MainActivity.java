package online.feian.mailreminder;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.app.NotificationManager;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.DashPathEffect;
import android.graphics.Paint;
import android.graphics.RadialGradient;
import android.graphics.Shader;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.provider.Settings;
import android.text.InputFilter;
import android.text.InputType;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import org.json.JSONObject;

public final class MainActivity extends Activity {
    private final ExecutorService io = Executors.newSingleThreadExecutor();
    private final Handler handler = new Handler();
    private AppState state;
    private TextView statusText;
    private TextView resultText;
    private TextView incomingText;
    private TextView transientNotice;
    private EditText nameField;
    private EditText codeField;
    private View sendView;
    private boolean cosmos;
    private int accent;
    private boolean working;
    private long nextSendMs;
    private final BroadcastReceiver receiver = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) {
            refreshStatus();
            String incoming = intent.getStringExtra("incoming");
            if (incoming != null && transientNotice != null) {
                transientNotice.setText(incoming);
                transientNotice.setVisibility(View.VISIBLE);
                handler.removeCallbacks(hideNotice);
                handler.postDelayed(hideNotice, SecurityPolicy.NOTIFICATION_TIMEOUT_MS);
            }
        }
    };
    private final Runnable hideNotice = () -> { if (transientNotice != null) transientNotice.setVisibility(View.GONE); };

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        state = new AppState(this);
        render();
        acknowledgeIntent(getIntent());
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        acknowledgeIntent(intent);
    }

    @Override protected void onStart() {
        super.onStart();
        IntentFilter filter = new IntentFilter(RelayService.STATUS_ACTION);
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED);
        else registerReceiver(receiver, filter);
    }

    @Override protected void onResume() {
        super.onResume();
        if (state.enabled() && !state.token().isEmpty()) RelayService.start(this);
        refreshStatus();
        UpdateManager.resumeInstall(this);
    }

    @Override protected void onStop() { unregisterReceiver(receiver); super.onStop(); }

    @Override protected void onDestroy() {
        handler.removeCallbacksAndMessages(null);
        io.shutdown();
        super.onDestroy();
    }

    private void render() {
        cosmos = state.prefs.getBoolean("cosmos", false);
        accent = Color.parseColor(cosmos ? "#E7C39D" : "#FFB45A");
        getWindow().setStatusBarColor(Color.parseColor(cosmos ? "#060714" : "#0A0C10"));
        getWindow().setNavigationBarColor(Color.parseColor(cosmos ? "#060714" : "#0A0C10"));
        FrameLayout root = new FrameLayout(this);
        root.addView(new Backdrop(this, cosmos), new FrameLayout.LayoutParams(-1, -1));
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setClipToPadding(false);
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(dp(24), dp(24), dp(24), dp(32));
        scroll.addView(content, new ScrollView.LayoutParams(-1, -2));
        root.addView(scroll);
        setContentView(root);

        TextView title = text("邮件提醒助手", 26, Color.WHITE);
        title.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        content.addView(title);
        TextView subtitle = text("手机与桌面，同步接收加急提醒", 13, muted());
        subtitle.setPadding(0, dp(9), 0, dp(20)); content.addView(subtitle);
        LinearLayout themeRow = row();
        Button tech = button("高科技", false, () -> changeTheme(false));
        Button star = button("宇宙星空", false, () -> changeTheme(true));
        tech.setTextColor(cosmos ? muted() : accent); star.setTextColor(cosmos ? accent : muted());
        themeRow.addView(tech, weighted()); themeRow.addView(star, weighted()); content.addView(themeRow);

        if (state.token().isEmpty()) renderBinding(content);
        else renderBound(content);

        LinearLayout tools = card(content);
        tools.addView(button("检查更新 · 1.3.1", false, () -> UpdateManager.check(this)));
        tools.addView(button("通知权限设置", false, this::notificationSettings));
        tools.addView(button("后台与电池设置", false, () -> startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + getPackageName())))));
        TextView help = text("后台接收使用一条常驻连接通知。请在系统电池设置允许本应用后台运行；系统强制停止后需重新打开应用。", 12, muted());
        help.setLineSpacing(dp(4), 1); help.setPadding(0, dp(12), 0, 0); tools.addView(help);
        refreshStatus();
    }

    private void renderBinding(LinearLayout content) {
        LinearLayout binding = card(content);
        binding.addView(text("绑定桌面网段", 18, Color.WHITE));
        TextView instructions = text("在电脑托盘菜单打开“手机绑定”，输入电脑生成的 8 位绑定码。", 13, muted());
        instructions.setPadding(0, dp(12), 0, dp(18)); instructions.setLineSpacing(dp(4), 1); binding.addView(instructions);
        binding.addView(text("手机名称", 12, muted()));
        nameField = input(state.name(), false); binding.addView(nameField, fieldParams());
        binding.addView(text("桌面绑定码", 12, muted()));
        codeField = input("", true); codeField.setHint("8 位数字"); binding.addView(codeField, fieldParams());
        binding.addView(button("绑定并开启提醒", true, this::pair));
        statusText = text("通过 FEIAN 连接到你的桌面网段", 12, muted()); statusText.setPadding(0, dp(14), 0, 0); binding.addView(statusText);
    }

    private void renderBound(LinearLayout content) {
        LinearLayout connection = card(content);
        connection.addView(text("已绑定  " + state.subnet(), 18, Color.WHITE));
        statusText = text("正在连接", 12, accent); statusText.setPadding(0, dp(10), 0, dp(12)); connection.addView(statusText);
        LinearLayout profile = row();
        nameField = input(state.name(), false);
        profile.addView(nameField, new LinearLayout.LayoutParams(0, dp(48), 1));
        Button saveName = button("保存名称", false, this::saveName);
        profile.addView(saveName, new LinearLayout.LayoutParams(dp(100), dp(48))); connection.addView(profile);

        FrameLayout orb = new FrameLayout(this);
        GradientDrawable fill = new GradientDrawable(GradientDrawable.Orientation.TL_BR,
                new int[] { Color.parseColor(cosmos ? "#534568" : "#30313B"), Color.parseColor(cosmos ? "#15132D" : "#11151D") });
        fill.setShape(GradientDrawable.OVAL); fill.setStroke(dp(1), accent); orb.setBackground(fill);
        orb.setElevation(dp(7)); orb.setContentDescription("发送加急邮件提醒"); orb.setClickable(true);
        LinearLayout label = new LinearLayout(this); label.setGravity(Gravity.CENTER); label.setOrientation(LinearLayout.VERTICAL); label.setPadding(dp(24), 0, dp(24), 0);
        TextView symbol = text(cosmos ? "✦" : "✉", 28, accent); symbol.setGravity(Gravity.CENTER); label.addView(symbol);
        TextView message = text("收到一封加急邮件\n请立刻处理", 20, Color.WHITE); message.setGravity(Gravity.CENTER); message.setLineSpacing(dp(5), 1); label.addView(message);
        TextView hint = text("点击发送给绑定网段", 11, muted()); hint.setPadding(0, dp(12), 0, 0); hint.setGravity(Gravity.CENTER); label.addView(hint);
        orb.addView(label, new FrameLayout.LayoutParams(-1, -1));
        LinearLayout.LayoutParams orbParams = new LinearLayout.LayoutParams(dp(238), dp(238)); orbParams.gravity = Gravity.CENTER_HORIZONTAL; orbParams.topMargin = dp(28); orbParams.bottomMargin = dp(24);
        content.addView(orb, orbParams); sendView = orb;
        orb.setOnClickListener(view -> send());
        orb.setOnTouchListener((view, event) -> {
            if (event.getAction() == MotionEvent.ACTION_DOWN) view.animate().scaleX(0.97f).scaleY(0.97f).setDuration(100).start();
            if (event.getAction() == MotionEvent.ACTION_UP || event.getAction() == MotionEvent.ACTION_CANCEL) view.animate().scaleX(1).scaleY(1).setDuration(160).start();
            return false;
        });
        LinearLayout receipts = card(content);
        receipts.addView(text("本次回执", 12, muted()));
        resultText = text("尚未发送提醒", 15, Color.WHITE); resultText.setPadding(0, dp(12), 0, dp(8)); receipts.addView(resultText);
        incomingText = text("", 12, muted()); receipts.addView(incomingText);
        transientNotice = text("", 15, accent); transientNotice.setPadding(0, dp(12), 0, 0); transientNotice.setVisibility(View.GONE); receipts.addView(transientNotice);
        LinearLayout controls = row();
        controls.addView(button(state.enabled() ? "停止后台接收" : "开启后台接收", false, this::toggleBackground), weighted());
        controls.addView(button("解除绑定", false, this::unbind), weighted()); receipts.addView(controls);
    }

    private void pair() {
        String code = codeField.getText().toString().trim();
        String name = nameField.getText().toString().trim();
        if (!code.matches("[0-9]{8}") || name.isEmpty()) { toast("请输入名称和 8 位绑定码"); return; }
        run("正在绑定…", () -> {
            JSONObject body = new JSONObject().put("code", code).put("name", name);
            if (!state.token().isEmpty()) body.put("previousToken", state.token());
            JSONObject binding = Api.call("POST", "api/mobile/pair", "", body);
            state.bind(binding);
        }, () -> { render(); notificationPermission(); RelayService.start(this); toast("绑定成功"); });
    }

    private void saveName() {
        String name = nameField.getText().toString().trim();
        if (name.isEmpty()) { toast("请输入名称"); return; }
        run("正在同步名称…", () -> {
            String token = state.token();
            Api.call("POST", "api/mobile/name", token, new JSONObject().put("name", name));
            state.saveName(token, name);
        }, () -> toast("名称已同步"));
    }

    private void send() {
        long now = android.os.SystemClock.elapsedRealtime();
        if (working || now < nextSendMs) return;
        if (!state.enabled()) { toast("请先开启后台接收"); return; }
        nextSendMs = now + 5000;
        sendView.setAlpha(0.6f); handler.postDelayed(() -> { if (sendView != null) sendView.setAlpha(1); }, 5000);
        run("正在发送…", () -> {
            String token = state.token();
            JSONObject response = Api.call("POST", "api/mobile/alert", token, new JSONObject().put("isTest", false));
            state.sent(token, response.getString("alertId"));
        }, this::refreshStatus);
    }

    private void unbind() {
        new AlertDialog.Builder(this).setTitle("解除网段绑定？").setMessage("解绑后停止接收此网段的提醒。")
                .setNegativeButton("取消", null).setPositiveButton("解除绑定", (dialog, which) -> run("正在解绑…", () -> {
                    String token = state.token();
                    Api.call("POST", "api/mobile/unbind", token, new JSONObject());
                    RelayService.stop(this); state.clear(token);
                }, this::render)).show();
    }

    private void toggleBackground() {
        if (state.enabled()) RelayService.stop(this);
        else { state.setEnabled(true); notificationPermission(); RelayService.start(this); }
        render();
    }

    private void changeTheme(boolean value) {
        String draftName = nameField == null ? null : nameField.getText().toString();
        String draftCode = codeField == null ? null : codeField.getText().toString();
        state.prefs.edit().putBoolean("cosmos", value).apply(); render();
        if (draftName != null && nameField != null) nameField.setText(draftName);
        if (draftCode != null && codeField != null) codeField.setText(draftCode);
    }

    private void refreshStatus() {
        if (statusText == null) return;
        if (state.token().isEmpty()) return;
        String status = state.enabled() ? state.prefs.getString("connectionMessage",
                state.prefs.getString("connectionStatus", "正在连接 FEIAN")) : "后台接收已暂停";
        String warning = RelayService.notificationWarning(this);
        if (!warning.isEmpty() && !status.contains(warning)) status += "\n" + warning;
        statusText.setText(status);
        if (resultText != null) {
            String result = state.prefs.getString("lastResult", "尚未发送提醒");
            String receipts = state.prefs.getString("receiptStatus", "");
            resultText.setText(receipts.isEmpty() ? result : result + "\n" + receipts);
        }
        if (incomingText != null) incomingText.setText(state.prefs.getString("lastIncoming", ""));
    }

    private void acknowledgeIntent(Intent intent) {
        String id = intent.getStringExtra("seenAlertId");
        if (!SecurityPolicy.validAlertId(id)) return;
        intent.removeExtra("seenAlertId");
        boolean queued = state.queueSeen(intent.getStringExtra("bindingNodeId"), id);
        RelayService.cancelReminder(this, intent, id);
        if (queued && state.enabled()) RelayService.start(this);
    }

    private void notificationPermission() {
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[] { Manifest.permission.POST_NOTIFICATIONS }, 10);
    }

    private void notificationSettings() {
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED) notificationPermission();
        else startActivity(new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, getPackageName()));
    }

    private interface Work { void run() throws Exception; }
    private void run(String message, Work work, Runnable success) {
        if (working) return;
        working = true; if (statusText != null) statusText.setText(message);
        io.execute(() -> {
            try { work.run(); runOnUiThread(() -> { if (!isDestroyed()) { working = false; success.run(); refreshStatus(); } }); }
            catch (Exception error) { runOnUiThread(() -> { working = false; if (!isDestroyed()) { toast(error.getMessage() == null ? "操作失败，请检查网络" : error.getMessage()); refreshStatus(); } }); }
        });
    }

    private LinearLayout card(LinearLayout parent) {
        LinearLayout card = new LinearLayout(this); card.setOrientation(LinearLayout.VERTICAL); card.setPadding(dp(20), dp(20), dp(20), dp(20));
        card.setBackground(round(cosmos ? 0xB0141530 : 0xFF11151D, dp(16), cosmos ? 0x555A5B80 : 0xFF29303B));
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2); params.topMargin = dp(18); parent.addView(card, params); return card;
    }
    private LinearLayout row() { LinearLayout row = new LinearLayout(this); row.setOrientation(LinearLayout.HORIZONTAL); row.setGravity(Gravity.CENTER_VERTICAL); return row; }
    private LinearLayout.LayoutParams weighted() { LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(0, dp(44), 1); p.setMargins(dp(3), dp(5), dp(3), 0); return p; }
    private LinearLayout.LayoutParams fieldParams() { LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(-1, dp(48)); p.topMargin = dp(7); p.bottomMargin = dp(18); return p; }
    private TextView text(String value, int size, int color) { TextView t = new TextView(this); t.setText(value); t.setTextSize(size); t.setTextColor(color); return t; }
    private EditText input(String value, boolean code) {
        EditText field = new EditText(this); field.setText(value); field.setTextSize(15); field.setTextColor(Color.WHITE); field.setHintTextColor(muted()); field.setSingleLine(true);
        field.setPadding(dp(12), 0, dp(12), 0); field.setBackground(round(0xFF141B28, dp(8), 0xFF3D4758));
        field.setFilters(new InputFilter[] { new InputFilter.LengthFilter(code ? 8 : 24) });
        field.setInputType(code ? InputType.TYPE_CLASS_NUMBER : InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_WORDS);
        return field;
    }
    private Button button(String label, boolean primary, Runnable action) {
        Button b = new Button(this); b.setText(label); b.setTextSize(13); b.setAllCaps(false); b.setTextColor(primary ? 0xFF171008 : Color.WHITE); b.setMinHeight(dp(40));
        b.setPadding(dp(12), dp(5), dp(12), dp(5)); b.setBackground(round(primary ? accent : (cosmos ? 0xFF252B43 : 0xFF1D2430), dp(9), primary ? accent : 0xFF3B4658));
        b.setOnClickListener(view -> action.run()); return b;
    }
    private GradientDrawable round(int color, int radius, int stroke) { GradientDrawable drawable = new GradientDrawable(); drawable.setColor(color); drawable.setCornerRadius(radius); drawable.setStroke(dp(1), stroke); return drawable; }
    private int muted() { return cosmos ? 0xFFADB1CB : 0xFF9BA7B8; }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private void toast(String value) { Toast.makeText(this, value, Toast.LENGTH_LONG).show(); }

    private static final class Backdrop extends View {
        private final boolean cosmos;
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        Backdrop(Context context, boolean cosmos) { super(context); this.cosmos = cosmos; }
        @Override protected void onDraw(Canvas canvas) {
            canvas.drawColor(cosmos ? 0xFF060714 : 0xFF0A0C10);
            if (!cosmos) return;
            paint.setShader(new RadialGradient(getWidth() * 0.95f, getHeight() * 0.15f, getWidth() * 0.9f, new int[] { 0xFF34305B, 0x00060714 }, null, Shader.TileMode.CLAMP));
            canvas.drawRect(0, 0, getWidth(), getHeight(), paint); paint.setShader(null);
            paint.setColor(0x779FAECC);
            for (int i = 0; i < 65; i++) { float x = ((i * 73 + 19) % 997) / 997f * getWidth(); float y = ((i * 163 + 47) % 997) / 997f * getHeight(); canvas.drawCircle(x, y, i % 5 == 0 ? 1.5f : 0.8f, paint); }
        }
    }
}
