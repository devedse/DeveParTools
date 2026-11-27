using System.Text;
using DeveParTools.Core.Operations;

namespace DeveParTools.Cli;

/// <summary>
/// Renders the block map in the terminal: one character per block (or per group of blocks for large sets, showing
/// the worst state in the group), coloured with ANSI codes and with a distinct glyph per state so it also works
/// without colour.
/// </summary>
internal static class TerminalBlockMap
{
    /// <summary>Glyph, ANSI colour and severity (higher = worse) per state.</summary>
    internal static (char Glyph, string Color, int Severity) Style(BlockState s) => s switch
    {
        BlockState.Found => ('█', "32", 1),
        BlockState.Processed => ('█', "32", 1),
        BlockState.FoundMoved => ('▓', "36", 2),
        BlockState.FoundElsewhere => ('▒', "34", 3),
        BlockState.FoundStored => ('=', "34", 3),
        BlockState.Repaired => ('◆', "92", 4),
        BlockState.Repairing => ('◇', "93", 5),
        BlockState.Recoverable => ('░', "33", 6),
        BlockState.Damaged => ('x', "31", 7),
        BlockState.Missing => ('·', "31", 8),
        BlockState.Unrecoverable => ('!', "91", 9),
        BlockState.Scanning => ('?', "37", 0),
        _ => (' ', "90", 0),
    };

    public static string Render(BlockStateMap map, int width, bool useColor)
    {
        int n = map.InputBlockCount;
        var sb = new StringBuilder();
        if (n == 0)
        {
            return sb.ToString();
        }

        width = Math.Max(10, width);
        int maxCells = width * 12;
        int perCell = (n + maxCells - 1) / maxCells;
        int cells = (n + perCell - 1) / perCell;
        var states = new byte[n];
        map.CopyStates(states);
        sb.AppendLine(perCell == 1 ? $"Block map ({n} blocks):" : $"Block map ({n} blocks, {perCell} per cell, worst state shown):");
        for (int c = 0; c < cells; c++)
        {
            var worst = BlockState.Unknown;
            int worstSev = -1;
            for (int i = c * perCell; i < Math.Min(n, (c + 1) * perCell); i++)
            {
                var s = (BlockState)states[i];
                int sev = Style(s).Severity;
                if (sev > worstSev)
                {
                    worstSev = sev;
                    worst = s;
                }
            }

            var (glyph, color, _) = Style(worst);
            sb.Append(useColor ? $"\u001b[{color}m{glyph}\u001b[0m" : glyph.ToString());
            if ((c + 1) % width == 0)
            {
                sb.AppendLine();
            }
        }

        if (cells % width != 0)
        {
            sb.AppendLine();
        }

        var counts = map.CountStates();
        var legend = Enum.GetValues<BlockState>().Where(s => counts[(int)s] > 0).Select(s =>
        {
            var (g, col, _) = Style(s);
            return (useColor ? $"\u001b[{col}m{g}\u001b[0m" : g.ToString()) + $" {s} {counts[(int)s]}";
        });
        sb.AppendLine(string.Join("   ", legend));
        return sb.ToString();
    }
}
