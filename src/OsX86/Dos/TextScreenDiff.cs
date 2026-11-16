using OsX86.Engine;

namespace OsX86.Dos;

/// <summary>
/// The comparison by row of decision 214 A: which rows of the text screen changed between two reads
/// (their characters or their attributes), and whether the cursor moved. Only those rows are sent
/// to a terminal that can take rows apart; nothing is sent when nothing changed.
/// </summary>
public static class TextScreenDiff
{
    /// <summary>
    /// The rows of <paramref name="current"/> that differ from <paramref name="previous"/>; every row
    /// when there was no previous screen or its size differs.
    /// </summary>
    public static IReadOnlyList<int> ChangedRows(EngineTextScreen? previous, EngineTextScreen current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null || previous.Columns != current.Columns || previous.Rows != current.Rows || previous.Lines.Count != current.Lines.Count)
        {
            return [.. Enumerable.Range(0, current.Lines.Count)];
        }

        var changed = new List<int>();
        for (int row = 0; row < current.Lines.Count; row++)
        {
            if (!string.Equals(previous.Lines[row], current.Lines[row], StringComparison.Ordinal) || !SameAttributes(previous, current, row))
            {
                changed.Add(row);
            }
        }

        return changed;
    }

    /// <summary>Whether the cursor of <paramref name="current"/> is not the one of <paramref name="previous"/> (moved, shown or hidden).</summary>
    public static bool CursorChanged(EngineTextScreen? previous, EngineTextScreen current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return previous is null || previous.Cursor != current.Cursor || previous.BlinkEnabled != current.BlinkEnabled;
    }

    private static bool SameAttributes(EngineTextScreen previous, EngineTextScreen current, int row)
    {
        if (previous.Attributes is null && current.Attributes is null)
        {
            return true;
        }

        for (int column = 0; column < current.Columns; column++)
        {
            if (previous.AttributeAt(row, column) != current.AttributeAt(row, column))
            {
                return false;
            }
        }

        return true;
    }
}
