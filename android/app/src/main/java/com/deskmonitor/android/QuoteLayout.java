package com.deskmonitor.android;

/** Height budget in dp matching the widget's padding, header, labels and two-line footer. */
final class QuoteLayout {
    final int capacity, lineHeight;
    final boolean detailed;

    QuoteLayout(int height, float fontScale, int count, boolean perpetual) {
        int minimumLineHeight = (int) Math.ceil(18 * fontScale);
        int available = height - 78 - 3 * (int) Math.ceil(14 * fontScale);
        int compactRow = minimumLineHeight + 8;
        int detailRow = compactRow + 3 + 2 * (int) Math.ceil(15 * fontScale)
                + (perpetual ? 2 + 2 * (int) Math.ceil(15 * fontScale) : 0);
        detailed = available >= count * detailRow;
        capacity = Math.max(1, Math.min(count, available / (detailed ? detailRow : compactRow)));
        // Four and five quotes use the same card area with more breathing room.
        lineHeight = detailed ? minimumLineHeight : Math.max(minimumLineHeight, available / capacity - 8);
    }
}
