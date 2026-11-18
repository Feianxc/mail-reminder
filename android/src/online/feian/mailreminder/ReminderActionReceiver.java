package online.feian.mailreminder;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public final class ReminderActionReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context, Intent intent) {
        String id = intent.getStringExtra("alertId");
        if (!SecurityPolicy.validAlertId(id)) return;
        AppState state = new AppState(context);
        boolean queued = state.queueSeen(intent.getStringExtra("bindingNodeId"), id);
        RelayService.cancelReminder(context, intent, id);
        if (queued && state.enabled() && !state.token().isEmpty()) RelayService.start(context);
    }
}
