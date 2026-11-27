using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DeveParTools.Core.Operations;

namespace DeveParTools.App.Controls;

/// <summary>
/// The block grid: every input block of a PAR set as a square, coloured by its live state (found in place,
/// found shifted, found in another file, damaged, missing, recoverable, being rebuilt, repaired, unrecoverable).
/// </summary>
/// <remarks>
/// <para><b>Level of detail.</b> When a set has more blocks than fit on screen, each cell represents a group of
/// consecutive blocks and shows the most important state in the group (missing beats found, and so on); the
/// tooltip shows the breakdown. Zooming (<see cref="CellSize"/>) shows fewer blocks per cell.</para>
/// <para><b>Accessibility.</b> Colour is never the only cue: moved, elsewhere, damaged, missing and repaired cells
/// also get a glyph (arrow, dot, cross, outline, check) once cells are large enough.</para>
/// <para><b>Live updates.</b> The engine writes states into the shared <see cref="BlockStateMap"/>; the page bumps
/// <see cref="Version"/> on a timer, which re-renders the grid. The engine never waits for the UI.</para>
/// </remarks>
public sealed class BlockGrid : Control
{
    public static readonly StyledProperty<BlockStateMap?> MapProperty = AvaloniaProperty.Register<BlockGrid, BlockStateMap?>(nameof(Map));
    public static readonly StyledProperty<long> VersionProperty = AvaloniaProperty.Register<BlockGrid, long>(nameof(Version));
    public static readonly StyledProperty<double> CellSizeProperty = AvaloniaProperty.Register<BlockGrid, double>(nameof(CellSize), 10);
    public static readonly StyledProperty<int> MaxCellsProperty = AvaloniaProperty.Register<BlockGrid, int>(nameof(MaxCells), 16384);
    public static readonly StyledProperty<IReadOnlySet<int>?> HighlightProperty = AvaloniaProperty.Register<BlockGrid, IReadOnlySet<int>?>(nameof(Highlight));
    public static readonly StyledProperty<IReadOnlySet<BlockState>?> HiddenStatesProperty = AvaloniaProperty.Register<BlockGrid, IReadOnlySet<BlockState>?>(nameof(HiddenStates));
    public static readonly StyledProperty<int> SelectedBlockProperty = AvaloniaProperty.Register<BlockGrid, int>(nameof(SelectedBlock), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>
    /// The selected block's details (<see cref="Describe"/>), kept up to date while the map changes. Binds one way
    /// to the view model, which shows it without having to know how the grid groups blocks.
    /// </summary>
    public static readonly StyledProperty<string?> SelectedBlockDescriptionProperty = AvaloniaProperty.Register<BlockGrid, string?>(nameof(SelectedBlockDescription), defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

    private byte[] _states = [];
    private int _columns = 1;
    private int _group = 1;
    private int _cells;
    private double _step;

    static BlockGrid()
    {
        AffectsRender<BlockGrid>(MapProperty, VersionProperty, CellSizeProperty, HighlightProperty, HiddenStatesProperty, SelectedBlockProperty);
        AffectsMeasure<BlockGrid>(MapProperty, CellSizeProperty, MaxCellsProperty);
    }

    public BlockStateMap? Map { get => GetValue(MapProperty); set => SetValue(MapProperty, value); }

    public long Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }

    public double CellSize { get => GetValue(CellSizeProperty); set => SetValue(CellSizeProperty, value); }

    public int MaxCells { get => GetValue(MaxCellsProperty); set => SetValue(MaxCellsProperty, value); }

    /// <summary>Blocks to emphasise (e.g. the blocks of the selected file); others are dimmed.</summary>
    public IReadOnlySet<int>? Highlight { get => GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }

    /// <summary>States filtered out via the legend.</summary>
    public IReadOnlySet<BlockState>? HiddenStates { get => GetValue(HiddenStatesProperty); set => SetValue(HiddenStatesProperty, value); }

    public int SelectedBlock { get => GetValue(SelectedBlockProperty); set => SetValue(SelectedBlockProperty, value); }

    public string? SelectedBlockDescription { get => GetValue(SelectedBlockDescriptionProperty); set => SetValue(SelectedBlockDescriptionProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedBlockProperty || change.Property == VersionProperty || change.Property == MapProperty)
        {
            UpdateSelectedBlockDescription();
        }
    }

    private void UpdateSelectedBlockDescription()
    {
        var map = Map;
        if (SelectedBlock < 0 || map is null || map.InputBlockCount == 0)
        {
            SelectedBlockDescription = null;
            return;
        }

        // A fresh snapshot of the states (rendering may not have happened yet), and the grouping for the current width.
        int n = map.InputBlockCount;
        if (_states.Length != n)
        {
            _states = new byte[n];
        }

        map.CopyStates(_states);
        Layout(n, Bounds.Width > 0 ? Bounds.Width : 600);
        SelectedBlockDescription = Describe(SelectedBlock);
    }

    /// <summary>Most important state first: what the grid shows for a group of blocks.</summary>
    public static int Severity(BlockState s) => s switch
    {
        BlockState.Unrecoverable => 10,
        BlockState.Missing => 9,
        BlockState.Damaged => 8,
        BlockState.Recoverable => 7,
        BlockState.Repairing => 6,
        BlockState.Repaired => 5,
        BlockState.FoundElsewhere or BlockState.FoundStored => 4,
        BlockState.FoundMoved => 3,
        BlockState.Scanning => 2,
        BlockState.Found or BlockState.Processed => 1,
        _ => 0,
    };

    public static string ResourceKey(BlockState s) => s switch
    {
        BlockState.Found or BlockState.Processed => "BlockFoundBrush",
        BlockState.FoundMoved => "BlockMovedBrush",
        BlockState.FoundElsewhere => "BlockElsewhereBrush",
        BlockState.FoundStored => "BlockStoredBrush",
        BlockState.Damaged => "BlockDamagedBrush",
        BlockState.Missing => "BlockMissingBrush",
        BlockState.Recoverable => "BlockRecoverableBrush",
        BlockState.Repairing => "BlockRepairingBrush",
        BlockState.Repaired => "BlockRepairedBrush",
        BlockState.Unrecoverable => "BlockUnrecoverableBrush",
        BlockState.Scanning => "BlockScanningBrush",
        _ => "BlockUnknownBrush",
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        var map = Map;
        if (map is null || map.InputBlockCount == 0)
        {
            return new Size(0, 0);
        }

        Layout(map.InputBlockCount, width);
        int rows = (_cells + _columns - 1) / _columns;
        return new Size(width, rows * _step);
    }

    /// <summary>The height to fit into: the visible part of the scroll viewer around the grid (or the given height).</summary>
    private double FitHeight => this.FindAncestorOfType<ScrollViewer>() is { } sv && sv.Bounds.Height > 0 ? sv.Bounds.Height : Math.Max(100, Bounds.Height);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Fit follows the space there is: re-measure when the surrounding scroll viewer changes size (window resized).
        if (this.FindAncestorOfType<ScrollViewer>() is { } sv)
        {
            sv.PropertyChanged += (_, args) =>
            {
                if (args.Property == BoundsProperty && CellSize < 2)
                {
                    InvalidateMeasure();
                }
            };
        }
    }

    /// <summary>
    /// Square size, columns and grouping. Fixed size: <see cref="CellSize"/>, with as many blocks per square as needed to
    /// stay within <see cref="MaxCells"/> squares. Fit (<see cref="CellSize"/> below 2): the largest square from 40 down
    /// to 3 pixels at which min(blocks, MaxCells) squares fit the width × height; when even 3 pixels are too big, blocks
    /// are grouped so that what fits is shown. Example: 3,000 blocks in 1,300 × 300 → 12 px (108 × 25 = 2,700 is not
    /// enough at 12; 11 px: 118 × 27 = 3,186), so 11 px squares, one block each.
    /// </summary>
    private void Layout(int blocks, double width)
    {
        int maxCells = Math.Max(1, MaxCells);
        if (CellSize >= 2)
        {
            _step = CellSize;
            _columns = Math.Max(1, (int)(width / _step));
            _group = Math.Max(1, (blocks + maxCells - 1) / maxCells);
        }
        else
        {
            double height = FitHeight;
            int target = Math.Min(blocks, maxCells);
            _step = 3;
            for (int s = 40; s >= 3; s--)
            {
                if ((long)(int)(width / s) * (int)(height / s) >= target)
                {
                    _step = s;
                    break;
                }
            }

            _columns = Math.Max(1, (int)(width / _step));
            long capacity = Math.Max(1, (long)_columns * Math.Max(1, (int)(height / _step)));
            _group = (int)Math.Max(1, (blocks + Math.Min(capacity, maxCells) - 1) / Math.Min(capacity, maxCells));
        }

        _cells = (blocks + _group - 1) / _group;
    }

    public override void Render(DrawingContext context)
    {
        var map = Map;
        if (map is null || map.InputBlockCount == 0 || Bounds.Width <= 0)
        {
            return;
        }

        int n = map.InputBlockCount;
        if (_states.Length != n)
        {
            _states = new byte[n];
        }

        map.CopyStates(_states);
        Layout(n, Bounds.Width);
        double size = _step > 4 ? _step - 1 : _step;
        double radius = _step >= 8 ? 2 : 0;
        var hidden = HiddenStates;
        var highlight = Highlight;
        var glyphPen = new Pen(Brush("BlockGlyphBrush") ?? Brushes.Black, Math.Max(1, _step / 9));
        var brushes = new Dictionary<BlockState, IBrush?>();
        IBrush? BrushFor(BlockState s)
        {
            if (!brushes.TryGetValue(s, out var b))
            {
                brushes[s] = b = Brush(ResourceKey(s));
            }

            return b;
        }

        for (int c = 0; c < _cells; c++)
        {
            var state = Worst(c, out _);
            if (hidden != null && hidden.Contains(state))
            {
                continue;
            }

            var rect = new Rect(c % _columns * _step, c / _columns * _step, size, size);
            bool dim = highlight != null && !Overlaps(highlight, c);
            using (context.PushOpacity(dim ? 0.25 : 1))
            {
                context.DrawRectangle(BrushFor(state), null, rect, radius, radius);
                if (_step >= 8)
                {
                    DrawGlyph(context, state, rect, glyphPen);
                }
            }
        }

        if (SelectedBlock >= 0 && SelectedBlock < n)
        {
            int c = SelectedBlock / _group;
            var rect = new Rect(c % _columns * _step - 1, c / _columns * _step - 1, size + 2, size + 2);
            context.DrawRectangle(null, new Pen(Brush("BlockSelectionBrush") ?? Brushes.White, 2), rect, radius, radius);
        }
    }

    private bool Overlaps(IReadOnlySet<int> blocks, int cell)
    {
        for (int i = cell * _group; i < Math.Min(_states.Length, (cell + 1) * _group); i++)
        {
            if (blocks.Contains(i))
            {
                return true;
            }
        }

        return false;
    }

    private BlockState Worst(int cell, out Dictionary<BlockState, int>? breakdown, bool wantBreakdown = false)
    {
        breakdown = wantBreakdown ? new Dictionary<BlockState, int>() : null;
        var worst = BlockState.Unknown;
        int worstSeverity = -1;
        for (int i = cell * _group; i < Math.Min(_states.Length, (cell + 1) * _group); i++)
        {
            var s = (BlockState)_states[i];
            if (breakdown != null)
            {
                breakdown[s] = breakdown.GetValueOrDefault(s) + 1;
            }

            int sev = Severity(s);
            if (sev > worstSeverity)
            {
                worstSeverity = sev;
                worst = s;
            }
        }

        return worst;
    }

    private static void DrawGlyph(DrawingContext ctx, BlockState state, Rect r, Pen pen)
    {
        double m = r.Width * 0.28;
        var inner = r.Deflate(m);
        switch (state)
        {
            case BlockState.FoundMoved:
                ctx.DrawLine(pen, inner.BottomLeft, inner.TopRight);
                ctx.DrawLine(pen, inner.TopRight, new Point(inner.Right - inner.Width / 2, inner.Top));
                break;
            case BlockState.FoundElsewhere:
                ctx.DrawEllipse(pen.Brush, null, inner.Center, inner.Width / 4, inner.Height / 4);
                break;
            case BlockState.FoundStored:
                ctx.DrawLine(pen, new Point(inner.Left, inner.Center.Y), new Point(inner.Right, inner.Center.Y));
                break;
            case BlockState.Damaged:
            case BlockState.Unrecoverable:
                ctx.DrawLine(pen, inner.TopLeft, inner.BottomRight);
                ctx.DrawLine(pen, inner.TopRight, inner.BottomLeft);
                break;
            case BlockState.Missing:
            case BlockState.Recoverable:
                ctx.DrawRectangle(null, pen, inner);
                break;
            case BlockState.Repaired:
                ctx.DrawLine(pen, new Point(inner.Left, inner.Center.Y), new Point(inner.Center.X - inner.Width / 8, inner.Bottom));
                ctx.DrawLine(pen, new Point(inner.Center.X - inner.Width / 8, inner.Bottom), inner.TopRight);
                break;
        }
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) ? v as IBrush : null;

    /// <summary>The block (first block of the cell) under a point, or -1.</summary>
    public int BlockAt(Point p)
    {
        if (Map is null || _step <= 0)
        {
            return -1;
        }

        int col = (int)(p.X / _step), row = (int)(p.Y / _step);
        if (col < 0 || col >= _columns || row < 0)
        {
            return -1;
        }

        int cell = row * _columns + col;
        return cell < _cells ? cell * _group : -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int block = BlockAt(e.GetPosition(this));
        ToolTip.SetTip(this, block < 0 ? null : Describe(block));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        int block = BlockAt(e.GetPosition(this));
        if (block >= 0)
        {
            SelectedBlock = block;
        }
    }

    /// <summary>Tooltip text: block range, state breakdown, owner file(s) and where it was found.</summary>
    public string Describe(int block)
    {
        var map = Map;
        if (map is null || _states.Length == 0)
        {
            return string.Empty;
        }

        int cell = block / _group;
        int first = cell * _group;
        int last = Math.Min(_states.Length, first + _group) - 1;
        var state = Worst(cell, out var breakdown, wantBreakdown: true);
        var lines = new List<string> { first == last ? $"Block {first}" : $"Blocks {first}–{last}", $"State: {Display(state)}" };
        if (breakdown != null && breakdown.Count > 1)
        {
            lines.AddRange(breakdown.OrderByDescending(kv => Severity(kv.Key)).Select(kv => $"  {Display(kv.Key)}: {kv.Value}"));
        }

        if (map.Ownership.TryGetValue(first, out var owners))
        {
            foreach (var o in owners.Take(3))
            {
                lines.Add($"File: {Path.GetFileName(o.File)} @ {o.Offset:N0} ({o.Length:N0} bytes)");
            }
        }

        if (map.FoundAt.TryGetValue(first, out var found))
        {
            lines.Add($"Found in: {Path.GetFileName(found.File)} @ {found.Offset:N0}");
        }

        var flags = map.GetFlags(first);
        if (flags != BlockFlags.None)
        {
            lines.Add($"Flags: {flags}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string Display(BlockState s) => s switch
    {
        BlockState.Found => "Found in place",
        BlockState.FoundMoved => "Found (shifted)",
        BlockState.FoundElsewhere => "Found in another file",
        BlockState.FoundStored => "Stored in the PAR files",
        BlockState.Recoverable => "Missing, recoverable",
        BlockState.Repairing => "Rebuilding",
        BlockState.Unrecoverable => "Missing, not recoverable",
        BlockState.Processed => "Processed",
        _ => s.ToString(),
    };
}
