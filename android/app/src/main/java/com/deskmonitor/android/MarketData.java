package com.deskmonitor.android;

import org.json.JSONObject;
import java.math.BigDecimal;
import java.math.MathContext;
import java.text.DecimalFormat;
import java.text.DecimalFormatSymbols;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;

/** Market identity and pure parsing shared by workers, rendering and JVM tests. */
final class MarketData {
    enum Market {
        SPOT("USDT 现货", "USDT"), USDT("USDT 永续", "USDT"), USDC("USDC 永续", "USDC");
        final String label, quote;
        Market(String label, String quote) { this.label = label; this.quote = quote; }
    }

    static List<String> symbols(String input, Market market) {
        LinkedHashSet<String> result = new LinkedHashSet<>();
        for (String token : input.toUpperCase(Locale.ROOT).trim().split("[,，\\s]+")) {
            if (token.isEmpty()) continue;
            if (!token.matches("[A-Z0-9]{1,24}")) throw new IllegalArgumentException("代码仅支持英文字母和数字");
            if (!token.endsWith(market.quote)) {
                if (token.endsWith("USDT") || token.endsWith("USDC"))
                    throw new IllegalArgumentException("计价币与所选市场不一致");
                token += market.quote;
            }
            if (token.length() <= market.quote.length()) throw new IllegalArgumentException("请输入完整币种代码");
            result.add(token);
        }
        if (result.isEmpty() || result.size() > 6) throw new IllegalArgumentException("每个小组件请选择 1–6 个交易对");
        return new ArrayList<>(result);
    }

    static final class Quote {
        final BigDecimal last, change;
        final long observed, fetched;
        Quote(BigDecimal last, BigDecimal change, long observed, long fetched) {
            this.last = last; this.change = change; this.observed = observed; this.fetched = fetched;
        }
        JSONObject json() throws Exception {
            return new JSONObject().put("last", last.toPlainString()).put("change", change.toPlainString())
                    .put("observed", observed).put("fetched", fetched);
        }
        static Quote cached(JSONObject json) throws Exception {
            return new Quote(new BigDecimal(json.getString("last")), new BigDecimal(json.getString("change")),
                    json.getLong("observed"), json.getLong("fetched"));
        }
    }

    static Quote parseQuote(String body, String symbol, long now) throws Exception {
        JSONObject json = new JSONObject(body);
        if (!symbol.equals(json.getString("symbol"))) throw new IllegalArgumentException("行情代码不匹配");
        BigDecimal last = positive(json, "lastPrice"), open = positive(json, "openPrice");
        BigDecimal high = positive(json, "highPrice"), low = positive(json, "lowPrice");
        if (high.compareTo(low) < 0 || last.compareTo(high) > 0 || last.compareTo(low) < 0)
            throw new IllegalArgumentException("行情价格范围异常");
        long observed = timestamp(json, "closeTime", now, false);
        return new Quote(last,
                last.divide(open, MathContext.DECIMAL128).subtract(BigDecimal.ONE).multiply(new BigDecimal("100")), observed, now);
    }

    static JSONObject parseFunding(String body, String symbol, long now) throws Exception {
        JSONObject json = new JSONObject(body);
        if (!symbol.equals(json.getString("symbol"))) throw new IllegalArgumentException("资金费率代码不匹配");
        BigDecimal rate = new BigDecimal(json.getString("lastFundingRate"));
        if (rate.abs().compareTo(BigDecimal.ONE) > 0) throw new IllegalArgumentException("资金费率异常");
        return new JSONObject().put("percent", rate.multiply(new BigDecimal("100")).toPlainString())
                .put("next", timestamp(json, "nextFundingTime", now, true))
                .put("observed", timestamp(json, "time", now, false)).put("fetched", now);
    }

    static boolean stale(long observed, long fetched, long now) {
        return now - observed > 20 * 60_000L || now - fetched > 20 * 60_000L
                || observed > now + 60_000 || fetched > now + 60_000;
    }
    static String price(BigDecimal value) {
        return format(value, value.compareTo(new BigDecimal("1000")) >= 0 ? "#,##0.00"
                : value.compareTo(BigDecimal.ONE) >= 0 ? "0.00##" : "0.00##########");
    }
    static String percent(BigDecimal value, boolean funding) {
        return (value.signum() > 0 ? "+" : "") + format(value, funding ? "0.0000##" : "0.00") + "%";
    }
    private static String format(BigDecimal value, String pattern) {
        return new DecimalFormat(pattern, DecimalFormatSymbols.getInstance(Locale.US)).format(value);
    }
    private static BigDecimal positive(JSONObject json, String key) throws Exception {
        BigDecimal value = new BigDecimal(json.getString(key));
        if (value.signum() <= 0) throw new IllegalArgumentException("无效价格：" + key);
        return value;
    }
    private static long timestamp(JSONObject json, String key, long now, boolean future) throws Exception {
        long value = json.getLong(key);
        if (value < 1_230_768_000_000L || value > 253_402_300_799_999L || (!future && value > now + 60_000))
            throw new IllegalArgumentException("行情时间戳异常");
        return value;
    }
}
