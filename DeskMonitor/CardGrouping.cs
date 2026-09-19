using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DeskMonitor;

public static class CardGrouping
{
    // Card controls remain direct children so feed updates keep their existing routing.
    public static void Apply(CardColumnsPanel panel, IReadOnlyDictionary<string, CardPlacement> placements, double scale)
    {
        var cards = panel.Children.OfType<FrameworkElement>().ToArray();
        panel.FreeLayout = cards.Any(c => c.Tag is string key && placements.TryGetValue(key, out var p) && (p.Column != 0 || p.Group.Length > 0));
        if (!panel.FreeLayout) return;
        var autoGroups = new Dictionary<string, int>();
        foreach (var card in cards)
            if (card.Tag is string key && placements.TryGetValue(key, out var placement) && placement.Column > 0 && placement.Group.Length > 0)
                autoGroups.TryAdd(placement.Group, placement.Column - 1);
        var entries = cards.Select((card, index) =>
        {
            var p = card.Tag is string key && placements.TryGetValue(key, out var value) ? value : new CardPlacement();
            var column = p.Column == 0 ? index % panel.Columns : p.Column - 1;
            if (p.Column == 0 && p.Group.Length > 0)
            {
                if (autoGroups.TryGetValue(p.Group, out var existing)) column = existing;
                else autoGroups[p.Group] = column;
            }
            if (panel.Columns == 1) column = 0;
            return (Card: card, Column: column, p.Group);
        }).ToArray();
        panel.Children.Clear();
        foreach (var group in entries.GroupBy(e => (e.Column, e.Group)))
        {
            if (group.Key.Group.Length > 0)
            {
                var title = new TextBlock { Text = group.Key.Group, FontSize = 11 * scale, FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5, 7, 5, 7) };
                title.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                CardColumnsPanel.SetCardColumn(title, group.Key.Column);
                panel.Children.Add(title);
            }
            foreach (var entry in group)
            {
                CardColumnsPanel.SetCardColumn(entry.Card, entry.Column);
                panel.Children.Add(entry.Card);
            }
        }
        panel.InvalidateMeasure();
    }
}
