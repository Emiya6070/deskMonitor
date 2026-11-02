package com.deskmonitor.android;

import android.appwidget.AppWidgetHost;
import android.appwidget.AppWidgetHostView;
import android.appwidget.AppWidgetManager;
import android.content.ComponentName;
import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.os.Bundle;
import android.view.View;
import android.view.ViewGroup;
import android.widget.TextView;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import java.math.BigDecimal;
import java.io.File;
import java.io.FileOutputStream;
import java.util.List;
import java.util.ArrayList;
import static org.junit.Assert.*;

@RunWith(AndroidJUnit4.class)
public class WidgetIntegrationTest {
    @Test public void mediumCardsShowFourToSixRowsWithoutClipping() throws Exception {
        var instrumentation = InstrumentationRegistry.getInstrumentation();
        Context context = instrumentation.getTargetContext();
        AppWidgetManager manager = AppWidgetManager.getInstance(context);
        AppWidgetHost host = new AppWidgetHost(context, 9027);
        WidgetStore store = new WidgetStore(context);
        instrumentation.getUiAutomation().adoptShellPermissionIdentity("android.permission.BIND_APPWIDGET");
        int id = host.allocateAppWidgetId();
        List<String> keys = new ArrayList<>();
        List<JSONObject> previous = new ArrayList<>();
        try {
            Bundle options = new Bundle();
            options.putInt(AppWidgetManager.OPTION_APPWIDGET_MIN_WIDTH, 300);
            options.putInt(AppWidgetManager.OPTION_APPWIDGET_MAX_WIDTH, 300);
            options.putInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT, 280);
            options.putInt(AppWidgetManager.OPTION_APPWIDGET_MAX_HEIGHT, 280);
            assertTrue(manager.bindAppWidgetIdIfAllowed(id, new ComponentName(context, MarketWidget.class), options));
            instrumentation.waitForIdleSync();
            long now = System.currentTimeMillis();
            for (MarketData.Market market : new MarketData.Market[]{MarketData.Market.SPOT, MarketData.Market.USDT}) {
                List<String> symbols = MarketData.symbols("BTC ETH SOL XRP 1000SHIB DOGE", market);
                for (int i = 0; i < symbols.size(); i++) {
                    String key = store.key(market, symbols.get(i));
                    keys.add(key);
                    previous.add(store.read(key));
                    String price = i == 0 ? "1234567.89" : i == 4 ? "0.00000123" : "123.45";
                    JSONObject cache = new JSONObject().put("quote", new MarketData.Quote(new BigDecimal(price),
                            new BigDecimal(i % 2 == 0 ? "2.5" : "-12.34"), now - i * 60_000L, now).json())
                            .put("funding", new JSONObject().put("percent", "0.01").put("observed", now)
                                    .put("fetched", now).put("next", now + 3600_000));
                    if (i == 5) cache.put("error", "HTTP 503");
                    store.write(key, cache);
                }
                for (int count = 4; count <= 6; count++) {
                    int expected = count;
                    store.save(id, new WidgetStore.Config(market, symbols.subList(0, count), count == 5, 15));
                    instrumentation.runOnMainSync(() -> {
                        MarketWidget.render(context, id, null);
                        AppWidgetHostView view = host.createView(context, id, manager.getAppWidgetInfo(id));
                        view.setPadding(0, 0, 0, 0); // Options describe the provider's content bounds.
                        capture(context, view, "widget-medium-" + market.name() + "-" + expected + ".png", 300, 280);
                        ViewGroup rows = view.findViewById(R.id.rows);
                        assertEquals(expected, rows.getChildCount());
                        for (int rowIndex = 0; rowIndex < expected; rowIndex++) {
                            View row = rows.getChildAt(rowIndex);
                            assertTrue("Row exceeds available height", row.getBottom() <= rows.getHeight());
                            for (int cell : new int[]{R.id.symbol, R.id.price, R.id.change}) {
                                TextView text = row.findViewById(cell);
                                assertTrue("Cell has no space", text.getWidth() > 0);
                                assertEquals(text.getText().toString(), 0, text.getLayout().getEllipsisCount(0));
                            }
                            assertEquals(View.GONE, row.findViewById(R.id.details).getVisibility());
                        }
                        TextView footer = view.findViewById(R.id.footer);
                        assertTrue(footer.getText().toString().contains("最早快照"));
                        String oldest = new java.text.SimpleDateFormat("MM-dd HH:mm", java.util.Locale.getDefault())
                                .format(new java.util.Date(now - (expected - 1) * 60_000L));
                        assertTrue(footer.getText().toString(), footer.getText().toString().contains(oldest));
                        if (expected == 6) assertTrue(footer.getText().toString().contains("†待更新"));
                    });
                }
            }
            android.content.res.Configuration larger = new android.content.res.Configuration(context.getResources().getConfiguration());
            larger.fontScale = 1.5f;
            Context largeTextContext = context.createConfigurationContext(larger);
            instrumentation.runOnMainSync(() -> {
                MarketWidget.render(largeTextContext, id, null);
                AppWidgetHostView view = host.createView(largeTextContext, id, manager.getAppWidgetInfo(id));
                view.setPadding(0, 0, 0, 0);
                capture(largeTextContext, view, "widget-large-text.png", 300, 280);
                ViewGroup rows = view.findViewById(R.id.rows);
                assertTrue(rows.getChildCount() < 6);
                assertTrue(rows.getChildAt(rows.getChildCount() - 1).getBottom() <= rows.getHeight());
                assertTrue(allText(view).contains("拉高组件查看其余"));
            });
            options.putInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT, 700);
            options.putInt(AppWidgetManager.OPTION_APPWIDGET_MAX_HEIGHT, 700);
            manager.updateAppWidgetOptions(id, options);
            instrumentation.waitForIdleSync();
            instrumentation.runOnMainSync(() -> {
                MarketWidget.render(context, id, null);
                AppWidgetHostView view = host.createView(context, id, manager.getAppWidgetInfo(id));
                view.setPadding(0, 0, 0, 0);
                capture(context, view, "widget-tall-details.png", 300, 700);
                ViewGroup rows = view.findViewById(R.id.rows);
                assertEquals(6, rows.getChildCount());
                assertEquals(View.VISIBLE, rows.getChildAt(5).findViewById(R.id.funding).getVisibility());
                assertTrue(rows.getChildAt(5).getBottom() <= rows.getHeight());
            });
        } finally {
            for (int i = 0; i < keys.size(); i++) store.write(keys.get(i), previous.get(i));
            new MarketWidget().onDeleted(context, new int[]{id});
            host.deleteHost();
            instrumentation.getUiAutomation().dropShellPermissionIdentity();
        }
    }
    @Test public void realHostRendersCachedQuoteAndIndependentFundingFailure() throws Exception {
        var instrumentation = InstrumentationRegistry.getInstrumentation();
        Context context = instrumentation.getTargetContext();
        AppWidgetManager manager = AppWidgetManager.getInstance(context);
        AppWidgetHost host = new AppWidgetHost(context, 9026);
        instrumentation.getUiAutomation().adoptShellPermissionIdentity("android.permission.BIND_APPWIDGET");
        int id = host.allocateAppWidgetId();
        try {
            Bundle options = new Bundle();
            options.putInt(AppWidgetManager.OPTION_APPWIDGET_MIN_WIDTH, 300);
            options.putInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT, 340);
            assertTrue("Host binding requires emulator shell privileges", manager.bindAppWidgetIdIfAllowed(id,
                    new ComponentName(context, MarketWidget.class), options));
            instrumentation.waitForIdleSync();
            WidgetStore store = new WidgetStore(context);
            store.save(id, new WidgetStore.Config(MarketData.Market.USDC, List.of("BTCUSDC", "ETHUSDC"), false, 15));
            long now = System.currentTimeMillis();
            JSONObject cache = new JSONObject().put("quote", new MarketData.Quote(new BigDecimal("65432.12"),
                    new BigDecimal("2.50"), now, now).json()).put("fundingError", "network unavailable");
            String key = store.key(MarketData.Market.USDC, "BTCUSDC");
            JSONObject previous = store.read(key);
            store.write(key, cache);
            try {
                instrumentation.runOnMainSync(() -> {
                    host.startListening();
                    MarketWidget.render(context, id, null);
                    AppWidgetHostView view = host.createView(context, id, manager.getAppWidgetInfo(id));
                    String text = allText(view);
                    assertTrue(text, text.contains("65,432.12"));
                    assertTrue(text, text.contains("+2.50%"));
                    assertTrue(text, text.contains("费率 — · 更新失败"));
                    assertTrue(text, text.contains("USDC 永续"));
                    assertTrue(text, text.contains("等待首次行情"));
                    capture(context, view, "widget-dark.png");
                    host.stopListening();
                });
                cache.put("error", "HTTP 503");
                store.write(key, cache);
                instrumentation.runOnMainSync(() -> {
                    MarketWidget.render(context, id, null);
                    AppWidgetHostView view = host.createView(context, id, manager.getAppWidgetInfo(id));
                    assertTrue(allText(view).contains("更新失败 / 缓存"));
                });
                store.save(id, new WidgetStore.Config(MarketData.Market.USDC, List.of("BTCUSDC", "ETHUSDC"), true, 30));
                instrumentation.runOnMainSync(() -> {
                    MarketWidget.render(context, id, null);
                    AppWidgetHostView view = host.createView(context, id, manager.getAppWidgetInfo(id));
                    assertTrue(allText(view).contains("约 30 分钟更新"));
                    capture(context, view, "widget-light-error.png");
                });
            } finally { store.write(key, previous); }
            new MarketWidget().onDeleted(context, new int[]{id});
            assertNull(store.config(id));
        } finally {
            host.deleteHost();
            instrumentation.getUiAutomation().dropShellPermissionIdentity();
        }
    }
    @Test public void preferencesKeepWidgetsAndMarketsSeparate() {
        Context context = InstrumentationRegistry.getInstrumentation().getTargetContext();
        WidgetStore store = new WidgetStore(context);
        try {
            store.save(-4001, new WidgetStore.Config(MarketData.Market.SPOT, List.of("BTCUSDT"), false, 15));
            store.save(-4002, new WidgetStore.Config(MarketData.Market.USDT, List.of("BTCUSDT"), true, 60));
            assertNotEquals(store.key(MarketData.Market.SPOT, "BTCUSDT"), store.key(MarketData.Market.USDT, "BTCUSDT"));
            assertEquals(MarketData.Market.SPOT, store.config(-4001).market);
            assertEquals(60, store.config(-4002).minutes);
            store.delete(-4001);
            assertNull(store.config(-4001));
            assertNotNull(store.config(-4002));
        } finally { store.delete(-4001); store.delete(-4002); }
    }
    private static void capture(Context context, View view, String name) {
        capture(context, view, name, 300, 340);
    }
    private static void capture(Context context, View view, String name, int widthDp, int heightDp) {
        float density = context.getResources().getDisplayMetrics().density;
        int width = Math.round(widthDp * density), height = Math.round(heightDp * density);
        view.measure(View.MeasureSpec.makeMeasureSpec(width, View.MeasureSpec.EXACTLY), View.MeasureSpec.makeMeasureSpec(height, View.MeasureSpec.EXACTLY));
        view.layout(0, 0, width, height);
        Bitmap bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888);
        view.draw(new Canvas(bitmap));
        try (FileOutputStream output = new FileOutputStream(new File(context.getExternalFilesDir(null), name))) {
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, output);
        } catch (Exception e) { throw new AssertionError(e); }
        finally { bitmap.recycle(); }
    }
    private static String allText(View view) {
        StringBuilder result = new StringBuilder();
        if (view instanceof TextView) result.append(((TextView) view).getText()).append('\n');
        if (view instanceof ViewGroup) for (int i = 0; i < ((ViewGroup) view).getChildCount(); i++)
            result.append(allText(((ViewGroup) view).getChildAt(i)));
        return result.toString();
    }
}
