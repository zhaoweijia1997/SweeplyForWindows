using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SweeplyForWindows.Capture;

/// <summary>
/// The bytes of a packet as Wireshark shows them: an offset, 16 bytes in hex and the same as text on each line.
/// Draws only the lines on screen (a packet can be 64 KB), highlights the bytes of the selected field, and
/// reports a click on a byte so the field it belongs to can be selected.
/// </summary>
public sealed class HexView : Control
{
    public static readonly DependencyProperty BytesProperty = DependencyProperty.Register(nameof(Bytes), typeof(byte[]), typeof(HexView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((HexView)d).OnBytesChanged()));

    public static readonly DependencyProperty HighlightStartProperty = DependencyProperty.Register(nameof(HighlightStart), typeof(int), typeof(HexView),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((HexView)d).ScrollToHighlight()));

    public static readonly DependencyProperty HighlightLengthProperty = DependencyProperty.Register(nameof(HighlightLength), typeof(int), typeof(HexView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ByteClickedCommandProperty = DependencyProperty.Register(nameof(ByteClickedCommand), typeof(ICommand), typeof(HexView));

    private const int PerLine = 16;
    private const int AsciiColumn = 61; // offset (4), gap, 16 hex bytes with a double space in the middle, gap

    /// <summary>Where byte <paramref name="i"/> of a line starts, in characters: Wireshark leaves an extra space after the 8th.</summary>
    private static int HexColumn(int i) => 10 + i * 3 + (i >= 8 ? 1 : 0);
    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Vertical, SmallChange = 1, LargeChange = 10 };
    private readonly Typeface _face = new(new FontFamily("Cascadia Mono, Consolas, Courier New"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private double _charWidth = 7.2, _lineHeight = 17;
    private int _firstLine;

    public HexView()
    {
        Focusable = true;
        ClipToBounds = true;
        AddVisualChild(_scroll);
        _scroll.ValueChanged += (_, e) =>
        {
            _firstLine = (int)Math.Round(e.NewValue);
            InvalidateVisual();
        };
        FontSize = 12.5;
    }

    public byte[]? Bytes { get => (byte[]?)GetValue(BytesProperty); set => SetValue(BytesProperty, value); }
    public int HighlightStart { get => (int)GetValue(HighlightStartProperty); set => SetValue(HighlightStartProperty, value); }
    public int HighlightLength { get => (int)GetValue(HighlightLengthProperty); set => SetValue(HighlightLengthProperty, value); }

    /// <summary>Executed with the offset of a byte that was clicked.</summary>
    public ICommand? ByteClickedCommand { get => (ICommand?)GetValue(ByteClickedCommandProperty); set => SetValue(ByteClickedCommandProperty, value); }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _scroll;

    private int LineCount => Bytes is { Length: > 0 } b ? (b.Length + PerLine - 1) / PerLine : 0;
    private int VisibleLines => Math.Max(1, (int)(ActualHeight / _lineHeight));

    protected override Size MeasureOverride(Size available)
    {
        Measure();
        _scroll.Measure(available);
        double width = (AsciiColumn + PerLine) * _charWidth + 24 + _scroll.DesiredSize.Width;
        return new Size(double.IsInfinity(available.Width) ? width : Math.Min(width, available.Width), double.IsInfinity(available.Height) ? 200 : available.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double barWidth = SystemParameters.VerticalScrollBarWidth;
        _scroll.Arrange(new Rect(final.Width - barWidth, 0, barWidth, final.Height));
        UpdateScroll(final.Height);
        return final;
    }

    private void Measure()
    {
        var sample = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, FontSize, Brushes.Black, 1.0);
        _charWidth = sample.WidthIncludingTrailingWhitespace;
        _lineHeight = Math.Ceiling(sample.Height + 2);
    }

    private void UpdateScroll(double height)
    {
        int visible = Math.Max(1, (int)(height / _lineHeight));
        _scroll.Maximum = Math.Max(0, LineCount - visible);
        _scroll.ViewportSize = visible;
        _scroll.LargeChange = Math.Max(1, visible - 1);
        _scroll.Visibility = LineCount > visible ? Visibility.Visible : Visibility.Hidden;
        if (_firstLine > _scroll.Maximum) _scroll.Value = _scroll.Maximum;
    }

    private void OnBytesChanged()
    {
        _scroll.Value = 0;
        _firstLine = 0;
        UpdateScroll(ActualHeight);
    }

    /// <summary>Brings the highlighted bytes into view when they are off screen.</summary>
    private void ScrollToHighlight()
    {
        if (HighlightStart < 0 || LineCount == 0) return;
        int line = HighlightStart / PerLine;
        if (line < _firstLine || line >= _firstLine + VisibleLines) _scroll.Value = Math.Min(_scroll.Maximum, Math.Max(0, line - 1));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        _scroll.Value = Math.Clamp(_scroll.Value - Math.Sign(e.Delta) * 3, 0, _scroll.Maximum);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (Bytes is not { } bytes) return;
        var p = e.GetPosition(this);
        int line = _firstLine + (int)(p.Y / _lineHeight);
        int column = (int)((p.X - 8) / _charWidth);
        int index = -1;
        for (int i = 0; i < PerLine && index < 0; i++)
            if (column >= HexColumn(i) && column < HexColumn(i) + 2) index = i;
        if (index < 0 && column >= AsciiColumn && column < AsciiColumn + PerLine) index = column - AsciiColumn;
        if (index < 0) return;
        int offset = line * PerLine + index;
        if (offset >= 0 && offset < bytes.Length && ByteClickedCommand?.CanExecute(offset) == true) ByteClickedCommand.Execute(offset);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit-testable everywhere
        if (Bytes is not { Length: > 0 } bytes) return;
        var text = (TryFindResource("TextFillColorPrimaryBrush") as Brush) ?? Foreground ?? Brushes.Black;
        var dim = (TryFindResource("TextFillColorSecondaryBrush") as Brush) ?? Brushes.Gray;
        var mark = (TryFindResource("AccentFillColorDefaultBrush") as SolidColorBrush) is { } accent
            ? new SolidColorBrush(Color.FromArgb(0x55, accent.Color.R, accent.Color.G, accent.Color.B))
            : new SolidColorBrush(Color.FromArgb(0x55, 0x33, 0x99, 0xFF));
        mark.Freeze();
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int start = HighlightStart, end = HighlightStart + Math.Max(0, HighlightLength);
        int last = Math.Min(LineCount, _firstLine + VisibleLines + 1);
        for (int line = _firstLine; line < last; line++)
        {
            double y = (line - _firstLine) * _lineHeight;
            int offset = line * PerLine;
            int count = Math.Min(PerLine, bytes.Length - offset);
            // The highlight behind the bytes of the selected field.
            for (int i = 0; i < count; i++)
            {
                int b = offset + i;
                if (b < start || b >= end) continue;
                double hexX = 8 + HexColumn(i) * _charWidth;
                bool joinNext = i < count - 1 && b + 1 < end;
                double width = joinNext ? (HexColumn(i + 1) - HexColumn(i)) * _charWidth : 2 * _charWidth;
                dc.DrawRectangle(mark, null, new Rect(hexX - _charWidth * 0.25, y, width + (joinNext ? 0 : _charWidth * 0.5), _lineHeight));
                dc.DrawRectangle(mark, null, new Rect(8 + (AsciiColumn + i) * _charWidth, y, _charWidth, _lineHeight));
            }
            var hex = new System.Text.StringBuilder(PerLine * 3);
            var ascii = new System.Text.StringBuilder(PerLine);
            for (int i = 0; i < count; i++)
            {
                byte value = bytes[offset + i];
                hex.Append(value.ToString("x2", CultureInfo.InvariantCulture)).Append(i == 7 ? "  " : " ");
                ascii.Append(value is >= 0x20 and < 0x7F ? (char)value : '·');
            }
            Draw(dc, offset.ToString("x4", CultureInfo.InvariantCulture), 8, y, dim, dpi);
            Draw(dc, hex.ToString(), 8 + 10 * _charWidth, y, text, dpi);
            Draw(dc, ascii.ToString(), 8 + AsciiColumn * _charWidth, y, text, dpi);
        }
    }

    private void Draw(DrawingContext dc, string s, double x, double y, Brush brush, double dpi) =>
        dc.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, FontSize, brush, dpi), new Point(x, y + 1));
}
