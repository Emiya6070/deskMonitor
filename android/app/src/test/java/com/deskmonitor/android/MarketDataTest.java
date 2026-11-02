package com.deskmonitor.android;

import org.junit.Test;
import java.math.BigDecimal;
import java.util.List;
import static org.junit.Assert.*;

public class MarketDataTest {
    private static final long NOW = 1_790_000_000_000L;
    private String ticker(String symbol, String last, long timestamp) {
        return "{\"symbol\":\"" + symbol + "\",\"lastPrice\":\"" + last
                + "\",\"openPrice\":\"100\",\"highPrice\":\"120\",\"lowPrice\":\"80\",\"closeTime\":" + timestamp + "}";
    }
    @Test public void normalizesAndDeduplicatesSymbolsInOrder() {
        assertEquals(List.of("BTCUSDT", "ETHUSDT"), MarketData.symbols("btc, BTCUSDT，eth", MarketData.Market.SPOT));
        assertEquals(List.of("BTCUSDC"), MarketData.symbols("btc", MarketData.Market.USDC));
    }
    @Test public void rejectsWrongCurrencyAndUnsafePaths() {
        for (String input : List.of("BTCUSDT", "BTC/USDC", "USDC", "", "BTC ETH SOL XRP ADA DOGE BNB"))
            assertThrows(IllegalArgumentException.class, () -> MarketData.symbols(input, MarketData.Market.USDC));
    }
    @Test public void acceptsSixUniqueSymbolsAndDeduplicatesBeforeLimit() {
        assertEquals(List.of("BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "ADAUSDT", "DOGEUSDT"),
                MarketData.symbols("BTC ETH SOL XRP ADA DOGE BTC", MarketData.Market.SPOT));
    }
    @Test public void rollingReturnUsesOpenPriceAndKeepsSourceTime() throws Exception {
        MarketData.Quote quote = MarketData.parseQuote(ticker("BTCUSDT", "110", NOW - 5000), "BTCUSDT", NOW);
        assertEquals(0, new BigDecimal("10").compareTo(quote.change));
        assertEquals(NOW - 5000, quote.observed);
        assertEquals(NOW, MarketData.Quote.cached(quote.json()).fetched);
    }
    @Test public void rejectsWrongSymbolsInvalidRangesAndFutureData() {
        assertThrows(Exception.class, () -> MarketData.parseQuote(ticker("ETHUSDT", "100", NOW), "BTCUSDT", NOW));
        assertThrows(Exception.class, () -> MarketData.parseQuote(ticker("BTCUSDT", "121", NOW), "BTCUSDT", NOW));
        assertThrows(Exception.class, () -> MarketData.parseQuote(ticker("BTCUSDT", "0", NOW), "BTCUSDT", NOW));
        assertThrows(Exception.class, () -> MarketData.parseQuote(ticker("BTCUSDT", "100", NOW + 61_000), "BTCUSDT", NOW));
    }
    @Test public void fundingIsPercentNotAprAndAllowsElapsedSettlement() throws Exception {
        var funding = MarketData.parseFunding("{\"symbol\":\"BTCUSDC\",\"lastFundingRate\":\"-0.0001\",\"time\":"
                + NOW + ",\"nextFundingTime\":" + (NOW - 1) + "}", "BTCUSDC", NOW);
        assertEquals(0, new BigDecimal("-0.01").compareTo(new BigDecimal(funding.getString("percent"))));
        assertEquals(NOW - 1, funding.getLong("next"));
    }
    @Test public void staleChecksBothClocksAndClockRollback() {
        assertFalse(MarketData.stale(NOW, NOW, NOW));
        assertTrue(MarketData.stale(NOW - 1_200_001, NOW, NOW));
        assertTrue(MarketData.stale(NOW, NOW - 1_200_001, NOW));
        assertTrue(MarketData.stale(NOW + 61_000, NOW, NOW));
    }
    @Test public void formattingPreservesSmallPricesAndSigns() {
        assertEquals("65,432.12", MarketData.price(new BigDecimal("65432.123")));
        assertEquals("0.00000123", MarketData.price(new BigDecimal("0.00000123")));
        assertEquals("+10.00%", MarketData.percent(BigDecimal.TEN, false));
        assertEquals("-0.0100%", MarketData.percent(new BigDecimal("-0.01"), true));
    }
}
