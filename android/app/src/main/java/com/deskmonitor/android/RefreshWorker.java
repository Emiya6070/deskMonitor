package com.deskmonitor.android;

import android.content.Context;
import androidx.annotation.NonNull;
import androidx.work.Worker;
import androidx.work.WorkerParameters;
import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.IOException;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

public final class RefreshWorker extends Worker {
    // Periodic and tap-triggered jobs may overlap; serialize cache writes in this process.
    private static final Object LOCK = new Object();
    public RefreshWorker(@NonNull Context context, @NonNull WorkerParameters parameters) { super(context, parameters); }

    @NonNull @Override public Result doWork() {
        synchronized (LOCK) {
            int id = getInputData().getInt("widgetId", -1);
            Context context = getApplicationContext();
            WidgetStore store = new WidgetStore(context);
            WidgetStore.Config config = store.config(id);
            if (config == null || !MarketWidget.exists(context, id) || isStopped()) return Result.success();
            MarketWidget.render(context, id, "正在更新快照…");
            for (String symbol : config.symbols) {
                if (isStopped() || store.config(id) == null) break;
                String key = store.key(config.market, symbol);
                JSONObject cache = store.read(key);
                try {
                    String base = config.market == MarketData.Market.SPOT
                            ? "https://data-api.binance.vision/api/v3/ticker/24hr?symbol="
                            : "https://fapi.binance.com/fapi/v1/ticker/24hr?symbol=";
                    MarketData.Quote quote = MarketData.parseQuote(get(base + symbol), symbol, System.currentTimeMillis());
                    cache.put("quote", quote.json());
                    cache.remove("error");
                } catch (Exception e) { put(cache, "error", error(e)); }
                if (config.market != MarketData.Market.SPOT && !isStopped()) {
                    try {
                        JSONObject funding = MarketData.parseFunding(get("https://fapi.binance.com/fapi/v1/premiumIndex?symbol=" + symbol),
                                symbol, System.currentTimeMillis());
                        cache.put("funding", funding);
                        cache.remove("fundingError");
                    } catch (Exception e) { put(cache, "fundingError", error(e)); }
                }
                // Deleting the last widget must not repopulate its cleared cache.
                if (!isStopped()) store.writeForWidget(id, key, cache);
            }
            if (store.config(id) != null && !isStopped()) MarketWidget.renderAll(context);
            return Result.success(); // Network failures retain cache; next periodic run or tap retries.
        }
    }

    private String get(String address) throws IOException {
        HttpURLConnection connection = (HttpURLConnection) new URL(address).openConnection();
        connection.setConnectTimeout(8000);
        connection.setReadTimeout(8000);
        connection.setInstanceFollowRedirects(false);
        connection.setRequestProperty("Accept", "application/json");
        connection.setRequestProperty("User-Agent", "DeskMonitor-Android/0.1");
        try {
            int status = connection.getResponseCode();
            if (status != 200) throw new IOException("HTTP " + status);
            try (InputStream input = connection.getInputStream(); ByteArrayOutputStream output = new ByteArrayOutputStream()) {
                byte[] buffer = new byte[4096];
                int count;
                while ((count = input.read(buffer)) != -1) {
                    if (isStopped()) throw new IOException("已取消");
                    if (output.size() + count > 65536) throw new IOException("响应过大");
                    output.write(buffer, 0, count);
                }
                return output.toString(StandardCharsets.UTF_8.name());
            }
        } finally { connection.disconnect(); }
    }
    private static String error(Exception e) {
        String message = e.getMessage();
        if (message != null && message.matches("HTTP [0-9]{3}")) return message + " · 请检查代码或网络";
        if (e instanceof IOException) return "网络不可用或超时";
        return "数据校验失败";
    }
    private static void put(JSONObject object, String key, String value) {
        try { object.put(key, value); } catch (Exception ignored) { /* strings are always JSON-safe */ }
    }
}
