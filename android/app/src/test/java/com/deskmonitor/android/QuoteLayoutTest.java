package com.deskmonitor.android;

import org.junit.Test;
import static org.junit.Assert.*;

public class QuoteLayoutTest {
    @Test public void mediumCardsFitFourToSixQuotesForEitherMarket() {
        for (boolean perpetual : new boolean[]{false, true}) {
            for (int count = 4; count <= 6; count++) {
                QuoteLayout layout = new QuoteLayout(280, 1, count, perpetual);
                assertEquals(count, layout.capacity);
                assertFalse(layout.detailed);
            }
        }
        assertEquals(4, new QuoteLayout(230, 1, 4, true).capacity);
    }
    @Test public void largerTextReducesCapacityAndTallCardsRestoreDetails() {
        assertTrue(new QuoteLayout(280, 1.5f, 6, true).capacity < 6);
        assertEquals(6, new QuoteLayout(440, 1.5f, 6, true).capacity);
        assertTrue(new QuoteLayout(700, 1, 6, true).detailed);
        assertTrue(new QuoteLayout(300, 1, 2, false).detailed);
        assertEquals(1, new QuoteLayout(100, 2, 6, true).capacity);
    }
}
