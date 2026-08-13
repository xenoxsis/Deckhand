namespace Deckhand;

/// <summary>
/// Works out which cells of the dashboard grid each section gets. Pinned ranges
/// ("columns": "1-3") are honoured as written; everything else is packed into the
/// first gap it fits in, scanning left to right and top to bottom.
///
/// Rows beyond <c>layout.rows</c> are appended when the pinned sections leave a
/// section nowhere to go. Since the grid's rows are star-sized, that makes every
/// row a little shorter rather than pushing a group off the bottom — the whole
/// point of the fixed grid is that nothing ends up somewhere you can't reach.
/// </summary>
internal sealed class GridPlacer
{
    /// <summary>Backstop against a config that would otherwise grow rows forever.</summary>
    private const int MaxRows = 64;

    private readonly int _columns;
    private readonly List<bool[]> _rows = new();

    public GridPlacer(int columns, int rows)
    {
        _columns = columns;
        Grow(rows);
    }

    /// <summary>Rows reserved so far — at least layout.rows, more if sections overflowed.</summary>
    public int RowCount => _rows.Count;

    public (int Row, int Column, int RowSpan, int ColumnSpan) Place(GridRange? columns, GridRange? rows)
    {
        // Ranges are 1-based in the config.
        int? column = columns?.Start is { } c ? Math.Clamp(c - 1, 0, _columns - 1) : null;
        int? row = rows?.Start is { } r ? Math.Clamp(r - 1, 0, MaxRows - 1) : null;

        // Spans are trimmed to what's left from the start, so "11-99" on a 12-column
        // grid keeps its start and loses the overhang — sliding it back to column 1
        // would move a group the config was explicit about.
        int columnSpan = Math.Clamp(columns?.Length ?? _columns, 1, _columns - (column ?? 0));
        int rowSpan = Math.Clamp(rows?.Length ?? 1, 1, MaxRows - (row ?? 0));

        // Both axes pinned means the placement was spelled out, overlaps and all;
        // moving it would be second-guessing the config.
        if (column is null || row is null)
        {
            (row, column) = FindGap(row, column, rowSpan, columnSpan);
        }

        Grow(row.Value + rowSpan);
        Occupy(row.Value, column.Value, rowSpan, columnSpan);
        return (row.Value, column.Value, rowSpan, columnSpan);
    }

    private (int Row, int Column) FindGap(int? pinnedRow, int? pinnedColumn, int rowSpan, int columnSpan)
    {
        for (int row = pinnedRow ?? 0; row + rowSpan <= MaxRows; row++)
        {
            Grow(row + rowSpan);

            // A pinned column with a free row searches straight down that column.
            if (pinnedColumn is { } column)
            {
                if (IsFree(row, column, rowSpan, columnSpan)) return (row, column);
                continue;
            }

            for (int c = 0; c + columnSpan <= _columns; c++)
            {
                if (IsFree(row, c, rowSpan, columnSpan)) return (row, c);
            }
        }

        // Out of room: stack it on the pinned cell (or the first one) rather than
        // silently dropping the section.
        return (pinnedRow ?? 0, pinnedColumn ?? 0);
    }

    private bool IsFree(int row, int column, int rowSpan, int columnSpan)
    {
        for (int r = row; r < row + rowSpan; r++)
        {
            for (int c = column; c < column + columnSpan; c++)
            {
                if (_rows[r][c]) return false;
            }
        }
        return true;
    }

    private void Occupy(int row, int column, int rowSpan, int columnSpan)
    {
        for (int r = row; r < row + rowSpan; r++)
        {
            for (int c = column; c < column + columnSpan && c < _columns; c++)
            {
                _rows[r][c] = true;
            }
        }
    }

    private void Grow(int rows)
    {
        while (_rows.Count < Math.Min(rows, MaxRows)) _rows.Add(new bool[_columns]);
    }
}
