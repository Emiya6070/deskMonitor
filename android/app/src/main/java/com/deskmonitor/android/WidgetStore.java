package com.deskmonitor.android;

import android.content.Context;
import android.content.SharedPreferences;
import org.json.JSONObject;
import java.util.List;

final class WidgetStore {
    private final SharedPreferences prefs;
    WidgetStore(Context context) { prefs = context.getSharedPreferences("market_widgets", Context.MODE_PRIVATE); }
    static final class Config {
        final MarketData.Market market;
        final List<String> symbols;
        final boolean light;
        final int minutes;
        Config(MarketData.Market market, List<String> symbols, boolean light, int minutes) {
            this.market = market; this.symbols = symbols; this.light = light; this.minutes = minutes;
        }
    }
    Config config(int id) {
        try {
            String text = prefs.getString("config." + id, null);
            if (text == null) return null;
            JSONObject json = new JSONObject(text);
            MarketData.Market market = MarketData.Market.valueOf(json.getString("market"));
            int minutes = json.getInt("minutes");
            if (minutes != 15 && minutes != 30 && minutes != 60) return null;
            return new Config(market, MarketData.symbols(json.getString("symbols"), market), json.getBoolean("light"), minutes);
        } catch (Exception ignored) { return null; }
    }
    void save(int id, Config config) {
        try {
            JSONObject json = new JSONObject().put("market", config.market.name()).put("symbols", String.join(",", config.symbols))
                    .put("light", config.light).put("minutes", config.minutes);
            prefs.edit().putString("config." + id, json.toString()).apply();
        } catch (Exception e) { throw new IllegalArgumentException(e); }
    }
    String key(MarketData.Market market, String symbol) { return market.name() + "." + symbol; }
    JSONObject read(String key) {
        try { return new JSONObject(prefs.getString(key, "{}")); }
        catch (Exception ignored) { return new JSONObject(); }
    }
    void write(String key, JSONObject json) { prefs.edit().putString(key, json.toString()).apply(); }
    void writeForWidget(int id, String key, JSONObject json) {
        synchronized (WidgetStore.class) { if (config(id) != null) write(key, json); }
    }
    void delete(int id) {
        synchronized (WidgetStore.class) { prefs.edit().remove("config." + id).apply(); }
    }
    void clearCache() {
        synchronized (WidgetStore.class) {
            SharedPreferences.Editor edit = prefs.edit();
            for (String key : prefs.getAll().keySet()) if (!key.startsWith("config.")) edit.remove(key);
            edit.apply();
        }
    }
}
