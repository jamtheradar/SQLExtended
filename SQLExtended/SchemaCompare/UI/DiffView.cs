using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using SQLExtended.SchemaCompare.Engine;

namespace SQLExtended.SchemaCompare.UI;

/// <summary>
/// Two read-only editors side by side, one row per <see cref="DiffRow"/>, with filler lines where a side has nothing, so
/// row N is line N on both sides and scrolling one scrolls the other by the same amount. Between them, an overview bar
/// marks every difference across the whole text and the part currently in view.
/// </summary>
internal sealed class DiffView : Grid
{
    // Colours come from the host at paint time (SqlxDiff* and Sqlx* keys), so a theme switch only needs a redraw.
    private readonly TextEditor _left = NewEditor();
    private readonly TextEditor _right = NewEditor();
    private readonly OverviewBar _overview;
    private DiffResult _diff;
    private int _currentHunk = -1;
    private bool _syncing;

    public event EventHandler HunkChanged;

    public DiffView()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _overview = new OverviewBar(this);
        SetColumn(_left, 0);
        SetColumn(_overview, 1);
        SetColumn(_right, 2);
        Children.Add(_left);
        Children.Add(_overview);
        Children.Add(_right);

        Attach(_left, isLeft: true);
        Attach(_right, isLeft: false);

        _left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_left, _right);
        _right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_right, _left);

        // AvalonEdit's highlighting colours are values, not resource references: swap the definition with the theme.
        EventHandler themeChanged = (_, _) => ApplyTheme();
        Loaded += (_, _) =>
        {
            CompareHost.Current.ThemeChanged -= themeChanged;
            CompareHost.Current.ThemeChanged += themeChanged;
            ApplyTheme();
        };
        Unloaded += (_, _) => CompareHost.Current.ThemeChanged -= themeChanged;
    }

    private void ApplyTheme()
    {
        var definition = SqlHighlighting.For(CompareHost.Current.IsDark);
        _left.SyntaxHighlighting = definition;
        _right.SyntaxHighlighting = definition;
        Redraw();
    }

    public DiffResult Diff => _diff;

    public int CurrentHunk => _currentHunk;

    public int HunkCount => _diff?.HunkStarts.Count ?? 0;

    private static TextEditor NewEditor()
    {
        var editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = false,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = 13,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            SyntaxHighlighting = SqlHighlighting.For(CompareHost.Current.IsDark),
            Padding = new Thickness(6, 0, 0, 0),
        };
        editor.SetResourceReference(Control.BackgroundProperty, "SqlxSurface");
        editor.SetResourceReference(Control.ForegroundProperty, "SqlxText");
        editor.TextArea.SetResourceReference(TextArea.SelectionBrushProperty, "SqlxSelection");
        editor.TextArea.SelectionBorder = null;
        editor.TextArea.SetResourceReference(TextArea.SelectionForegroundProperty, "SqlxSelectionText");
        editor.TextArea.Caret.CaretBrush = null;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.HighlightCurrentLine = false;
        return editor;
    }

    private void Attach(TextEditor editor, bool isLeft)
    {
        editor.TextArea.TextView.BackgroundRenderers.Add(new DiffBackground(this, isLeft));
        editor.TextArea.LeftMargins.Insert(0, new RowNumberMargin(this, isLeft));
    }

    private void Sync(TextEditor from, TextEditor to)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            to.ScrollToVerticalOffset(from.VerticalOffset);
            to.ScrollToHorizontalOffset(from.HorizontalOffset);
        }
        finally
        {
            _syncing = false;
        }

        _overview.InvalidateVisual();
    }

    public void Show(string left, string right, bool ignoreCase, bool ignoreWhitespace)
    {
        _diff = LineDiff.Compute(left ?? "", right ?? "", ignoreCase, ignoreWhitespace);

        var l = new StringBuilder();
        var r = new StringBuilder();
        for (int i = 0; i < _diff.Rows.Count; i++)
        {
            var row = _diff.Rows[i];
            if (i > 0)
            {
                l.Append('\n');
                r.Append('\n');
            }

            if (row.Left >= 0) l.Append(_diff.LeftLines[row.Left]);
            if (row.Right >= 0) r.Append(_diff.RightLines[row.Right]);
        }

        _left.Text = l.ToString();
        _right.Text = r.ToString();
        _left.ScrollToHome();
        _right.ScrollToHome();
        _currentHunk = -1;

        if (_diff.HunkStarts.Count > 0) GoToHunk(0);
        else HunkChanged?.Invoke(this, EventArgs.Empty);

        Redraw();
    }

    public void Clear()
    {
        _diff = null;
        _left.Text = "";
        _right.Text = "";
        _currentHunk = -1;
        HunkChanged?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    private void Redraw()
    {
        _left.TextArea.TextView.Redraw();
        _right.TextArea.TextView.Redraw();
        foreach (var m in _left.TextArea.LeftMargins.Concat(_right.TextArea.LeftMargins)) (m as UIElement)?.InvalidateVisual();
        _overview.InvalidateVisual();
    }

    public void NextHunk()
    {
        if (HunkCount == 0) return;
        GoToHunk(Math.Min(_currentHunk + 1, HunkCount - 1));
    }

    public void PreviousHunk()
    {
        if (HunkCount == 0) return;
        GoToHunk(Math.Max(_currentHunk - 1, 0));
    }

    public void GoToHunk(int index)
    {
        if (_diff == null || index < 0 || index >= _diff.HunkStarts.Count) return;
        _currentHunk = index;
        ScrollToRow(_diff.HunkStarts[index]);
        HunkChanged?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    /// <summary>Puts <paramref name="row"/> a few lines below the top, so the context above a change stays in view.</summary>
    internal void ScrollToRow(int row)
    {
        var view = _left.TextArea.TextView;
        double lineHeight = view.DefaultLineHeight;
        double y = Math.Max(0, (row - 3) * lineHeight);
        _left.ScrollToVerticalOffset(y);
        _left.TextArea.Caret.Line = Math.Min(row + 1, _left.Document.LineCount);
        _right.TextArea.Caret.Line = Math.Min(row + 1, _right.Document.LineCount);
    }

    /// <summary>Selects the hunk that contains or follows <paramref name="row"/> (an overview click).</summary>
    internal void JumpToRow(int row)
    {
        if (_diff == null) return;
        int hunk = _diff.HunkStarts.FindIndex(h => h >= row);
        if (hunk > 0 && row < _diff.HunkStarts[hunk] && HunkEnd(hunk - 1) >= row) hunk--;
        if (hunk < 0) hunk = _diff.HunkStarts.Count - 1;
        if (hunk >= 0) GoToHunk(hunk);
        else ScrollToRow(row);
    }

    internal int HunkEnd(int hunk)
    {
        int row = _diff.HunkStarts[hunk];
        while (row + 1 < _diff.Rows.Count && _diff.Rows[row + 1].Kind != DiffRowKind.Equal) row++;
        return row;
    }

    internal (double Top, double Height, double Total) Viewport()
    {
        var view = _left.TextArea.TextView;
        return (_left.VerticalOffset, view.ActualHeight, Math.Max(view.DocumentHeight, 1));
    }

    private static Brush B(string key) => CompareHost.Current.GetBrush(key);

    // ---- painting -------------------------------------------------------------------------------------------

    /// <summary>Line backgrounds by row kind, the changed words on top, and a rule around the current difference.</summary>
    private sealed class DiffBackground : IBackgroundRenderer
    {
        private readonly DiffView _owner;
        private readonly bool _isLeft;
        public DiffBackground(DiffView owner, bool isLeft)
        {
            _owner = owner;
            _isLeft = isLeft;
        }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext dc)
        {
            var diff = _owner._diff;
            if (diff == null || !textView.VisualLinesValid) return;

            int currentStart = -1, currentEnd = -1;
            if (_owner._currentHunk >= 0)
            {
                currentStart = diff.HunkStarts[_owner._currentHunk];
                currentEnd = _owner.HunkEnd(_owner._currentHunk);
            }

            Brush changedLine = B("SqlxDiffChangedLine"), changedWords = B("SqlxDiffChangedWords"), leftLine = B("SqlxDiffLeftLine"), rightLine = B("SqlxDiffRightLine"), filler = B("SqlxDiffFiller");
            var currentPen = new Pen(B("SqlxDiffCurrent"), 1);
            double width = Math.Max(textView.ActualWidth, ((System.Windows.Controls.Primitives.IScrollInfo)textView).ExtentWidth) + textView.HorizontalOffset;
            foreach (var visual in textView.VisualLines)
            {
                int row = visual.FirstDocumentLine.LineNumber - 1;
                if (row >= diff.Rows.Count) continue;
                var d = diff.Rows[row];
                double top = visual.VisualTop - textView.VerticalOffset;
                var rect = new Rect(0, top, width, visual.Height);

                Brush fill = d.Kind switch
                {
                    DiffRowKind.Changed => changedLine,
                    DiffRowKind.LeftOnly => _isLeft ? leftLine : filler,
                    DiffRowKind.RightOnly => _isLeft ? filler : rightLine,
                    _ => null,
                };
                if (fill != null) dc.DrawRectangle(fill, null, rect);

                if (d.Kind == DiffRowKind.Changed)
                {
                    var line = visual.FirstDocumentLine;
                    foreach (var span in _isLeft ? d.LeftSpans : d.RightSpans)
                    {
                        var segment = new ICSharpCode.AvalonEdit.Document.TextSegment { StartOffset = line.Offset + span.Start, Length = Math.Min(span.Length, Math.Max(0, line.Length - span.Start)) };
                        foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment)) dc.DrawRectangle(changedWords, null, r);
                    }
                }

                if (row == currentStart) dc.DrawLine(currentPen, new Point(0, top + 0.5), new Point(width, top + 0.5));
                if (row == currentEnd) dc.DrawLine(currentPen, new Point(0, top + visual.Height - 0.5), new Point(width, top + visual.Height - 0.5));
            }
        }
    }

    /// <summary>The side's own line numbers; a filler row has none.</summary>
    private sealed class RowNumberMargin : AbstractMargin
    {
        private readonly DiffView _owner;
        private readonly bool _isLeft;
        private static readonly Typeface Face = new("Cascadia Mono, Consolas");

        public RowNumberMargin(DiffView owner, bool isLeft)
        {
            _owner = owner;
            _isLeft = isLeft;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            int lines = _owner._diff == null ? 1 : Math.Max(_owner._diff.LeftLines.Length, _owner._diff.RightLines.Length);
            var text = Format(new string('9', Math.Max(3, lines.ToString(CultureInfo.InvariantCulture).Length)));
            return new Size(text.Width + 16, 0);
        }

        protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
        {
            if (oldTextView != null) oldTextView.VisualLinesChanged -= Changed;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView != null) newTextView.VisualLinesChanged += Changed;
        }

        private void Changed(object sender, EventArgs e)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }

        private FormattedText Format(string s)
            => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12, B("SqlxLineNumber"), VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override void OnRender(DrawingContext dc)
        {
            var view = TextView;
            var diff = _owner._diff;
            dc.DrawRectangle(B("SqlxSurface"), null, new Rect(RenderSize));
            dc.DrawRectangle(B("SqlxDivider"), null, new Rect(RenderSize.Width - 1, 0, 1, RenderSize.Height));
            if (view == null || diff == null || !view.VisualLinesValid) return;

            foreach (var visual in view.VisualLines)
            {
                int row = visual.FirstDocumentLine.LineNumber - 1;
                if (row >= diff.Rows.Count) continue;
                int line = _isLeft ? diff.Rows[row].Left : diff.Rows[row].Right;
                if (line < 0) continue;
                var text = Format((line + 1).ToString(CultureInfo.InvariantCulture));
                dc.DrawText(text, new Point(RenderSize.Width - text.Width - 8, visual.VisualTop - view.VerticalOffset));
            }
        }
    }

    /// <summary>The whole text in miniature: a mark for every difference and a frame round what is in view. Click to jump.</summary>
    private sealed class OverviewBar : FrameworkElement
    {
        private readonly DiffView _owner;

        public OverviewBar(DiffView owner)
        {
            _owner = owner;
            Cursor = Cursors.Hand;
            ToolTip = "Differences in the whole text. Click to jump.";
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(B("SqlxSurfaceAlt"), null, new Rect(RenderSize));
            var diff = _owner._diff;
            if (diff == null || diff.Rows.Count == 0) return;

            Brush changed = B("SqlxDiffMarkChanged"), left = B("SqlxDiffMarkLeft"), right = B("SqlxDiffMarkRight");
            double h = ActualHeight, w = ActualWidth;
            double perRow = h / diff.Rows.Count;
            for (int i = 0; i < diff.Rows.Count; i++)
            {
                var kind = diff.Rows[i].Kind;
                if (kind == DiffRowKind.Equal) continue;
                Brush b = kind == DiffRowKind.Changed ? changed : kind == DiffRowKind.LeftOnly ? left : right;
                dc.DrawRoundedRectangle(b, null, new Rect(4, i * perRow, w - 8, Math.Max(perRow, 3)), 1.5, 1.5);
            }

            var (top, height, total) = _owner.Viewport();
            double y = top / total * h, vh = Math.Max(height / total * h, 6);
            dc.DrawRoundedRectangle(null, new Pen(B("SqlxDiffViewport"), 1), new Rect(1.5, y + 0.5, w - 3, Math.Max(Math.Min(vh, h - y) - 1, 1)), 3, 3);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var diff = _owner._diff;
            if (diff == null || diff.Rows.Count == 0) return;
            int row = (int)(e.GetPosition(this).Y / ActualHeight * diff.Rows.Count);
            _owner.JumpToRow(Math.Max(0, Math.Min(row, diff.Rows.Count - 1)));
            e.Handled = true;
        }
    }
}
