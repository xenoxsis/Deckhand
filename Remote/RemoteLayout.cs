using System.Windows;
using System.Windows.Controls;

namespace Deckhand;

/// <summary>
/// The panel's tiles as the tablet has to draw them: one flat grid per group, with a
/// definite cell for every button.
///
/// The panel nests panels — a folder tile is a label with its ▾ beside it, above a
/// command list of its own — and the page has no equivalent, so the nesting is
/// flattened here onto columns fine enough that every level still lines up. Reading
/// the buttons in order and letting the page flow them, as this used to, put each
/// folder's ▾ on the row below its label: a group one tile wide has no room beside it.
///
/// Walks the elements the panel code built rather than the visual tree, which for a
/// freshly rendered panel hasn't been expanded from its templates yet.
/// </summary>
internal static class RemoteLayout
{
    /// <summary>
    /// Marks a Grid whose last column is a narrow trailing one — the ▾ beside a folder
    /// label — rather than an equal share of the row. Set by whoever builds the Grid, so
    /// this code needn't guess a column's intent from its width.
    /// </summary>
    internal const string NarrowTail = "narrow-tail";

    /// <summary>
    /// The same, for a pair drawn as one control divided by a line — an app tile, which
    /// goes to the program on the wide side and starts another copy on the narrow one.
    /// Placed identically; only the page's styling differs.
    /// </summary>
    internal const string JoinedTail = "joined-tail";

    /// <summary>
    /// Where one button goes: its 1-based row and column and the columns it covers, in
    /// the column count returned alongside it. <paramref name="Trailing"/> marks a button
    /// that shares the cell of the one before it rather than taking columns of its own —
    /// the ▾ beside a folder label, which the page gives a fixed width. The columns on a
    /// trailing button are its leader's, so nothing downstream has to special-case them.
    /// <paramref name="Joined"/> says that pair is meant to read as a single split control
    /// rather than two tiles side by side. <paramref name="Indent"/> is how far in from its
    /// cell the button sits, in device independent pixels — the panel indents a folder's
    /// command list under its header, and this is that same offset.
    /// </summary>
    internal readonly record struct Cell(Button Button, int Row, int Column, int Span,
                                         bool Trailing = false, int Indent = 0,
                                         bool Joined = false);

    /// <summary>Whether a grid's last column is a narrow trailing button of either kind.</summary>
    private static bool HasTail(Grid grid) =>
        (string?)grid.Tag is NarrowTail or JoinedTail;

    /// <summary>
    /// One group's buttons, in the order they're laid out, over as many columns as it
    /// takes to place them. Hidden elements are skipped, along with the rows they would
    /// have taken — a folder tile's collapsed command list is on the panel but not on it.
    /// </summary>
    internal static (int Columns, List<Cell> Cells) Of(DependencyObject body)
    {
        int columns = Divisions(body);
        var cells = new List<Cell>();
        Lay(body, columns, top: 1, left: 1, indent: 0, cells);
        return (columns, cells);
    }

    /// <summary>
    /// How many columns a subtree needs to divide its row evenly. A grid multiplies:
    /// each of its cells is a row in miniature, so the row has to divide into the
    /// grid's columns and each of those into whatever that cell holds.
    /// </summary>
    private static int Divisions(DependencyObject node)
    {
        switch (node)
        {
            case UIElement { Visibility: not Visibility.Visible }:
            case Button:
                return 1;

            // A trailing button takes no columns of its own, so a narrow-tailed grid needs
            // only what its leader needs — it is one tile wide like any other.
            case Grid grid when HasTail(grid):
            {
                int inside = 1;
                foreach (UIElement child in grid.Children) inside = Lcm(inside, Divisions(child));
                return inside;
            }

            case Grid grid:
            {
                int inside = 1;
                foreach (UIElement child in grid.Children) inside = Lcm(inside, Divisions(child));
                return Width(grid) * inside;
            }

            case Panel panel:
            {
                int inside = 1;
                foreach (UIElement child in panel.Children) inside = Lcm(inside, Divisions(child));
                return inside;
            }

            case Border { Child: { } inner }:
                return Divisions(inner);

            case ContentControl { Content: DependencyObject content }:
                return Divisions(content);

            default:
                return 1;
        }
    }

    /// <summary>
    /// Places a subtree's buttons across the <paramref name="columns"/> columns starting
    /// at <paramref name="left"/>, from row <paramref name="top"/> down, and returns the
    /// row after the last one it used. <paramref name="indent"/> accumulates the left
    /// margins of the containers on the way down, which is how the panel sets a folder's
    /// command list in from its header — a tile's own margin is not part of it, since the
    /// page spaces tiles with a grid gap instead.
    /// </summary>
    private static int Lay(DependencyObject node, int columns, int top, int left, int indent,
                           List<Cell> cells)
    {
        if (node is Panel { Margin.Left: > 0 } inset) indent += (int)Math.Round(inset.Margin.Left);

        switch (node)
        {
            case UIElement { Visibility: not Visibility.Visible }:
                return top;

            case Button button:
                cells.Add(new Cell(button, top, left, columns, Indent: indent));
                return top + 1;

            case Grid grid:
                return LayGrid(grid, columns, top, left, indent, cells);

            // A StackPanel, or a WrapPanel whose wrapping can't be known before layout:
            // each child below the last, which is what the whole group used to get.
            case Panel panel:
                foreach (UIElement child in panel.Children)
                {
                    top = Lay(child, columns, top, left, indent, cells);
                }
                return top;

            case Border { Child: { } inner }:
                return Lay(inner, columns, top, left, indent, cells);

            case ContentControl { Content: DependencyObject content }:
                return Lay(content, columns, top, left, indent, cells);

            default:
                return top;
        }
    }

    /// <summary>
    /// A grid row by row. Each row of the panel's grid becomes as many rows on the page
    /// as its tallest cell needs, rather than one: a column of folder tiles, each a
    /// header above a command list, would otherwise pile the folders on top of each
    /// other. Row spans aren't used inside a group, so they aren't read here.
    /// </summary>
    private static int LayGrid(Grid grid, int columns, int top, int left, int indent,
                               List<Cell> cells)
    {
        if (HasTail(grid))
        {
            return LayTail(grid, columns, top, left, indent, cells,
                           joined: (string?)grid.Tag == JoinedTail);
        }

        int width = Width(grid);
        int share = columns / width; // Divisions() guarantees this divides

        foreach (var row in grid.Children.OfType<UIElement>()
                     .GroupBy(Grid.GetRow).OrderBy(row => row.Key))
        {
            int next = top;
            foreach (var child in row.OrderBy(Grid.GetColumn))
            {
                int column = Grid.GetColumn(child);
                int span = Math.Clamp(Grid.GetColumnSpan(child), 1, width - column) * share;
                next = Math.Max(next,
                    Lay(child, span, top, left + column * share, indent, cells));
            }
            top = next;
        }

        return top;
    }

    /// <summary>
    /// A label with a narrow button trailing it: the label takes the whole row, as any
    /// other tile would, and the ▾ is marked as sharing its cell. Its width is left to
    /// the page, which can spare it the 44px the panel does and give the label the rest —
    /// a share of the row would be a wide arrow on a wide group and a thin one on a
    /// narrow group, when what it needs is to be the same arrow on both.
    /// </summary>
    private static int LayTail(Grid grid, int columns, int top, int left, int indent,
                               List<Cell> cells, bool joined)
    {
        int next = top;
        bool first = true;

        foreach (var child in grid.Children.OfType<UIElement>().OrderBy(Grid.GetColumn))
        {
            if (first)
            {
                next = Lay(child, columns, top, left, indent, cells);
                first = false;
            }
            else if (child is Button trailing and { Visibility: Visibility.Visible })
            {
                cells.Add(new Cell(trailing, top, left, columns,
                                   Trailing: true, Indent: indent, Joined: joined));
            }
        }

        return next;
    }

    private static int Width(Grid grid) => Math.Max(grid.ColumnDefinitions.Count, 1);

    private static int Lcm(int a, int b)
    {
        int gcd = Gcd(a, b);
        return gcd == 0 ? 1 : a / gcd * b;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
