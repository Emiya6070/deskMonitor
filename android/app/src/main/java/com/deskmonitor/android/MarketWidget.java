package com.deskmonitor.android;

import android.app.PendingIntent;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.graphics.Color;
import android.os.Bundle;
import android.view.View;
import android.widget.RemoteViews;
import androidx.work.*;
import org.json.JSONObject;
import java.math.BigDecimal;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.concurrent.TimeUnit;

public final class MarketWidget extends AppWidgetProvider {
    static final String REFRESH = "com.deskmonitor.android.REFRESH";
    private static String periodic(int id) { return "market.periodic." + id; }
    private static String manual(int id) { return "market.manual." + id; }

    static boolean exists(Context context, int id) {
        for (int active : ids(context)) if (active == id) return true;
        return false;
    }
    static int[] ids(Context context) {
        return AppWidgetManager.getInstance(context).getAppWidgetIds(new ComponentName(context, MarketWidget.class));
    }
    static void schedule(Context context, int id, int minutes) {
        PeriodicWorkRequest request = new PeriodicWorkRequest.Builder(RefreshWorker.class, minutes, TimeUnit.MINUTES)
                .setInputData(new Data.Builder().putInt("widgetId", id).build())
                .setConstraints(new Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
                .setInitialDelay(minutes, TimeUnit.MINUTES).build();
        WorkManager.getInstance(context).enqueueUniquePeriodicWork(periodic(id), ExistingPeriodicWorkPolicy.UPDATE, request);
    }
    static void refresh(Context context, int id) {
        if (new WidgetStore(context).config(id) == null || !exists(context, id)) return;
        render(context, id, "已请求刷新 · 等待系统调度");
        WorkManager.getInstance(context).enqueueUniqueWork(manual(id), ExistingWorkPolicy.KEEP,
                new OneTimeWorkRequest.Builder(RefreshWorker.class)
                        .setInputData(new Data.Builder().putInt("widgetId", id).build()).build());
    }
    @Override public void onUpdate(Context context, AppWidgetManager manager, int[] ids) {
        for (int id : ids) {
            WidgetStore.Config config = new WidgetStore(context).config(id);
            render(context, id, null);
            if (config != null) { schedule(context, id, config.minutes); refresh(context, id); }
        }
    }
    @Override public void onReceive(Context context, Intent intent) {
        super.onReceive(context, intent);
        if (REFRESH.equals(intent.getAction())) refresh(context, intent.getIntExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, -1));
    }
    @Override public void onAppWidgetOptionsChanged(Context context, AppWidgetManager manager, int id, Bundle options) {
        render(context, id, null);
    }
    @Override public void onDeleted(Context context, int[] ids) {
        WidgetStore store = new WidgetStore(context);
        for (int id : ids) {
            store.delete(id);
            WorkManager.getInstance(context).cancelUniqueWork(periodic(id));
            WorkManager.getInstance(context).cancelUniqueWork(manual(id));
        }
    }
    @Override public void onDisabled(Context context) { new WidgetStore(context).clearCache(); }
    static void renderAll(Context context) { for (int id : ids(context)) render(context, id, null); }

    static void render(Context context, int id, String status) {
        AppWidgetManager manager = AppWidgetManager.getInstance(context);
        WidgetStore store = new WidgetStore(context);
        WidgetStore.Config config = store.config(id);
        RemoteViews views = new RemoteViews(context.getPackageName(), R.layout.widget_market);
        Intent settings = new Intent(context, ConfigureActivity.class).putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, id);
        PendingIntent configure = PendingIntent.getActivity(context, id, settings, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        views.setOnClickPendingIntent(R.id.settings, configure);
        views.setOnClickPendingIntent(R.id.title, configure);
        Intent refresh = new Intent(context, MarketWidget.class).setAction(REFRESH)
                .putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, id).addFlags(Intent.FLAG_RECEIVER_FOREGROUND);
        views.setOnClickPendingIntent(R.id.refresh, PendingIntent.getBroadcast(context, id, refresh,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE));
        if (config == null) {
            views.setTextViewText(R.id.footer, "点击设置，选择要查看的行情");
            manager.updateAppWidget(id, views);
            return;
        }
        int primary = Color.parseColor(config.light ? "#20382C" : "#F0F6EF");
        int secondary = Color.parseColor(config.light ? "#52695A" : "#BCCDC2");
        int up = Color.parseColor(config.light ? "#16704A" : "#92DEAC");
        int down = Color.parseColor(config.light ? "#BB354A" : "#FFA4AB");
        views.setInt(R.id.widget_root, "setBackgroundResource", config.light ? R.drawable.widget_light : R.drawable.widget_dark);
        views.setTextColor(R.id.title, primary);
        views.setTextColor(R.id.settings, secondary);
        views.setTextColor(R.id.refresh, up);
        views.setTextColor(R.id.footer, secondary);
        views.setTextViewText(R.id.title, "币安 · " + config.market.label);
        for (int heading : new int[]{R.id.symbol_heading, R.id.price_heading, R.id.change_heading})
            views.setTextColor(heading, secondary);
        views.setTextViewText(R.id.price_heading, "价格 / " + config.market.quote);
        views.removeAllViews(R.id.rows);
        Bundle options = manager.getAppWidgetOptions(id);
        // Launcher option bounds describe portrait and landscape, not one fixed rectangle.
        boolean landscape = context.getResources().getConfiguration().orientation
                == android.content.res.Configuration.ORIENTATION_LANDSCAPE;
        int height = options.getInt(landscape ? AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT
                : AppWidgetManager.OPTION_APPWIDGET_MAX_HEIGHT, 0);
        if (height <= 0) height = options.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT, 280);
        float fontScale = context.getResources().getConfiguration().fontScale;
        QuoteLayout layout = new QuoteLayout(height, fontScale, config.symbols.size(), config.market != MarketData.Market.SPOT);
        int shown = 0;
        long now = System.currentTimeMillis();
        long oldest = Long.MAX_VALUE;
        boolean attention = false;
        for (String symbol : config.symbols) {
            if (shown++ >= layout.capacity) break;
            RemoteViews row = new RemoteViews(context.getPackageName(), R.layout.widget_row);
            String base = symbol.substring(0, symbol.length() - config.market.quote.length());
            row.setInt(R.id.quote_line, "setMinimumHeight", Math.round(layout.lineHeight * context.getResources().getDisplayMetrics().density));
            row.setTextColor(R.id.symbol, primary);
            row.setTextColor(R.id.details, secondary);
            row.setTextColor(R.id.funding, secondary);
            row.setTextColor(R.id.price, primary);
            row.setTextViewText(R.id.price, "—");
            row.setTextViewText(R.id.change, "—");
            row.setTextColor(R.id.change, secondary);
            JSONObject cache = store.read(store.key(config.market, symbol));
            String error = cache.optString("error", "");
            String description = error.isEmpty() ? "等待首次行情" : error;
            String quoteDescription = "价格 — · 24h 涨跌 —";
            boolean needsAttention = true;
            try {
                MarketData.Quote quote = MarketData.Quote.cached(cache.getJSONObject("quote"));
                boolean stale = MarketData.stale(quote.observed, quote.fetched, now) || !error.isEmpty();
                needsAttention = stale;
                oldest = Math.min(oldest, quote.observed);
                row.setTextViewText(R.id.price, MarketData.price(quote.last));
                row.setTextColor(R.id.price, stale ? secondary : primary);
                row.setTextViewText(R.id.change, MarketData.percent(quote.change, false));
                quoteDescription = "价格 " + MarketData.price(quote.last) + " · 24h 涨跌 " + MarketData.percent(quote.change, false);
                row.setTextColor(R.id.change, stale ? secondary : quote.change.signum() > 0 ? up : quote.change.signum() < 0 ? down : primary);
                description = time(quote.observed)
                        + (error.isEmpty() ? stale ? " · 旧快照" : " · 快照" : " · 更新失败 / 缓存");
            } catch (Exception ignored) { /* No fabricated prices before a valid response. */ }
            row.setTextViewText(R.id.details, description);
            row.setViewVisibility(R.id.details, layout.detailed ? View.VISIBLE : View.GONE);
            String funding = "";
            if (config.market == MarketData.Market.SPOT) row.setViewVisibility(R.id.funding, View.GONE);
            else {
                funding = "费率 —";
                try {
                    JSONObject f = cache.getJSONObject("funding");
                    boolean stale = cache.has("fundingError") || MarketData.stale(f.getLong("observed"), f.getLong("fetched"), now);
                    needsAttention |= stale || f.getLong("next") <= now;
                    funding = "费率 " + MarketData.percent(new BigDecimal(f.getString("percent")), true)
                            + " · " + (f.getLong("next") <= now ? "结算待更新" : "结算 " + time(f.getLong("next")))
                            + (stale ? " · 旧快照" : "");
                } catch (Exception ignored) {
                    needsAttention = true;
                    if (cache.has("fundingError")) funding += " · 更新失败";
                }
                row.setTextViewText(R.id.funding, funding);
                row.setViewVisibility(R.id.funding, layout.detailed ? View.VISIBLE : View.GONE);
            }
            attention |= needsAttention;
            row.setTextViewText(R.id.symbol, base + (needsAttention ? " †" : ""));
            row.setContentDescription(R.id.row, symbol + " · " + quoteDescription + " · " + description + (funding.isEmpty() ? "" : " · " + funding));
            row.setOnClickPendingIntent(R.id.row, configure);
            views.addView(R.id.rows, row);
        }
        int hidden = Math.max(0, config.symbols.size() - layout.capacity);
        String footer = oldest == Long.MAX_VALUE ? "等待首次行情" : "最早快照 " + time(oldest);
        if (attention) footer += " · †待更新";
        if (hidden > 0) footer += "\n拉高组件查看其余 " + hidden + " 项";
        else if (status != null) footer += "\n" + status;
        else footer += "\n约 " + config.minutes + " 分钟更新"
                    + (!layout.detailed && config.market != MarketData.Market.SPOT ? " · 拉高看费率" : "");
        views.setTextViewText(R.id.footer, footer);
        manager.updateAppWidget(id, views);
    }
    private static String time(long millis) {
        return new SimpleDateFormat("MM-dd HH:mm", Locale.getDefault()).format(new Date(millis));
    }
}
