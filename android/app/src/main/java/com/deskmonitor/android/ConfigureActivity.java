package com.deskmonitor.android;

import android.app.Activity;
import android.appwidget.AppWidgetManager;
import android.content.ComponentName;
import android.content.Intent;
import android.graphics.Color;
import android.os.Bundle;
import android.text.InputType;
import android.view.View;
import android.view.WindowInsets;
import android.widget.*;

/** A launcher landing page and a separate configuration for each widget instance. */
public final class ConfigureActivity extends Activity {
    private int widgetId;
    private LinearLayout content;
    private Spinner market, interval;
    private EditText symbols;
    private Switch light;
    private TextView error;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        setResult(RESULT_CANCELED);
        widgetId = getIntent().getIntExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, AppWidgetManager.INVALID_APPWIDGET_ID);
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(dp(24), dp(24), dp(24), dp(24));
        scroll.addView(content);
        setContentView(scroll);
        // Android 15 enforces edge-to-edge. Insets also keep configuration clear of the IME.
        scroll.setOnApplyWindowInsetsListener((view, insets) -> {
            if (android.os.Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.ime());
                view.setPadding(bars.left, bars.top, bars.right, bars.bottom);
            } else {
                view.setPadding(insets.getSystemWindowInsetLeft(), insets.getSystemWindowInsetTop(),
                        insets.getSystemWindowInsetRight(), insets.getSystemWindowInsetBottom());
            }
            return insets;
        });
        text("DESKMONITOR / ANDROID", 12, "#16735D");
        text(widgetId == AppWidgetManager.INVALID_APPWIDGET_ID ? "把行情放到桌面" : "配置行情小组件", 30, "#20382C");
        text("随手查看价格、24h 涨跌与永续资金费率。", 15, "#52695A");
        if (widgetId == AppWidgetManager.INVALID_APPWIDGET_ID) { landing(); return; }
        if (!MarketWidget.exists(this, widgetId)) {
            text("此小组件已被移除，请从桌面重新添加。", 16, "#BB354A");
            return;
        }
        WidgetStore.Config config = new WidgetStore(this).config(widgetId);
        label("行情市场");
        market = spinner(new String[]{"USDT 现货", "USDT 永续", "USDC 永续"});
        market.setSelection(config == null ? 0 : config.market.ordinal());
        label("交易对 · 按输入顺序显示");
        symbols = new EditText(this);
        symbols.setSingleLine(false);
        symbols.setMinLines(2);
        symbols.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS | InputType.TYPE_TEXT_FLAG_MULTI_LINE);
        symbols.setHint("BTC, ETH, SOL");
        symbols.setText(config == null ? "BTC, ETH" : String.join(", ", config.symbols));
        content.addView(symbols);
        text("输入 1–6 个币种或完整交易对，用逗号或空格分隔。中尺寸优先展示全部价格与涨跌，拉高组件可查看逐项时间和费率。切换计价币时请同步修改完整代码。", 13, "#52695A");
        label("后台更新间隔");
        interval = spinner(new String[]{"约 15 分钟", "约 30 分钟", "约 60 分钟"});
        interval.setSelection(config == null || config.minutes == 15 ? 0 : config.minutes == 30 ? 1 : 2);
        light = new Switch(this);
        light.setText("浅色小组件");
        light.setMinHeight(dp(56));
        light.setChecked(config != null && config.light);
        content.addView(light);
        if (state != null) {
            market.setSelection(state.getInt("market"));
            interval.setSelection(state.getInt("interval"));
            symbols.setText(state.getString("symbols"));
            light.setChecked(state.getBoolean("light"));
        }
        error = text("", 14, "#BB354A");
        button("保存并刷新", this::save);
        text("每个小组件可独立配置市场和币种；混合观察现货与合约时，可添加多个小组件。", 13, "#52695A");
        notes();
    }

    private void landing() {
        text("长按桌面空白处 → 小组件 → DeskMonitor 行情，拖到桌面并选择币种。", 17, "#20382C");
        AppWidgetManager manager = AppWidgetManager.getInstance(this);
        button("添加桌面小组件", () -> {
            if (manager.isRequestPinAppWidgetSupported()) {
                boolean requested = manager.requestPinAppWidget(new ComponentName(this, MarketWidget.class), null, null);
                if (!requested) Toast.makeText(this, "请长按桌面手动添加", Toast.LENGTH_LONG).show();
            } else Toast.makeText(this, "此桌面不支持快捷添加，请长按桌面手动添加", Toast.LENGTH_LONG).show();
        });
        int[] ids = MarketWidget.ids(this);
        if (ids.length > 0) {
            label("已添加的小组件");
            WidgetStore store = new WidgetStore(this);
            for (int id : ids) {
                WidgetStore.Config config = store.config(id);
                String title = config == null ? "待配置的小组件 #" + id
                        : config.market.label + " · " + String.join(", ", config.symbols);
                button(title, () -> startActivity(new Intent(this, ConfigureActivity.class).putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, id)));
            }
        }
        notes();
    }
    private void save() {
        try {
            if (!MarketWidget.exists(this, widgetId)) throw new IllegalArgumentException("此小组件已被移除");
            MarketData.Market selected = MarketData.Market.values()[market.getSelectedItemPosition()];
            WidgetStore.Config config = new WidgetStore.Config(selected, MarketData.symbols(symbols.getText().toString(), selected),
                    light.isChecked(), new int[]{15, 30, 60}[interval.getSelectedItemPosition()]);
            new WidgetStore(this).save(widgetId, config);
            // A previous request may still be fetching the old selection.
            androidx.work.WorkManager.getInstance(this).cancelUniqueWork("market.manual." + widgetId);
            MarketWidget.schedule(this, widgetId, config.minutes);
            MarketWidget.refresh(this, widgetId);
            setResult(RESULT_OK, new Intent().putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, widgetId));
            finish();
        } catch (IllegalArgumentException e) { error.setText(e.getMessage()); }
    }
    @Override protected void onSaveInstanceState(Bundle outState) {
        if (symbols != null) {
            outState.putInt("market", market.getSelectedItemPosition());
            outState.putInt("interval", interval.getSelectedItemPosition());
            outState.putString("symbols", symbols.getText().toString());
            outState.putBoolean("light", light.isChecked());
        }
        super.onSaveInstanceState(outState);
    }
    private void notes() {
        label("关于行情快照");
        text("来源：Binance 公开行情，无需 API Key。价格单位为所选市场的 USDT / USDC；24h 为滚动涨跌，绿涨红跌。", 14, "#52695A");
        text("后台刷新由 Android 调度，省电、休眠或断网时可能延迟。点击组件上的“刷新”可请求更新。紧凑卡片底部显示已展示行情中最早的交易所时间，拉高后显示逐项时间；数据始终为快照，不提供秒级实时行情。", 14, "#52695A");
        text("刷新失败保留上次行情。渲染时超过 20 分钟的快照变淡；休眠期间界面也可能停止更新，请以显示的时间为准。资金费率为当期费率，结算前可能变化；正值表示多头付给空头。", 14, "#52695A");
        text("首版支持 Android 8.0 及以上，仅含币安行情；暂未移植美股、趋势图、消息源和用量功能。", 13, "#52695A");
    }
    private void label(String value) { text(value, 15, "#20382C"); }
    private TextView text(String value, int size, String color) {
        TextView view = new TextView(this);
        view.setText(value); view.setTextSize(size); view.setTextColor(Color.parseColor(color));
        view.setPadding(0, dp(12), 0, dp(8));
        content.addView(view);
        return view;
    }
    private Spinner spinner(String[] values) {
        Spinner spinner = new Spinner(this);
        ArrayAdapter<String> adapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, values);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        spinner.setAdapter(adapter);
        content.addView(spinner, new LinearLayout.LayoutParams(-1, dp(52)));
        return spinner;
    }
    private void button(String value, Runnable action) {
        Button button = new Button(this);
        button.setText(value); button.setAllCaps(false); button.setMinHeight(dp(52));
        button.setOnClickListener(view -> action.run());
        content.addView(button, new LinearLayout.LayoutParams(-1, -2));
    }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
}
