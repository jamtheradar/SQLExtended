using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using SQLExtended.Cache;

namespace SQLExtended.Search;

/// <summary>
/// AvalonEdit line transformer that highlights all occurrences of a search term
/// with a background color in the preview editor, by the same rule the search matched with
/// (<see cref="SearchTextMatcher"/>) — under whole word, <c>personal</c> stays unmarked when <c>person</c> is searched.
/// </summary>
internal sealed class SearchTermHighlighter : DocumentColorizingTransformer
{
    private string _searchTerm;

    private static readonly SolidColorBrush HighlightBrush =
        new(Color.FromArgb(0x60, 0xFF, 0xD7, 0x00)); // semi-transparent gold

    static SearchTermHighlighter()
    {
        HighlightBrush.Freeze();
    }

    public string SearchTerm
    {
        get => _searchTerm;
        set => _searchTerm = value;
    }

    public bool WholeWord { get; set; }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (string.IsNullOrEmpty(_searchTerm))
            return;

        string lineText = CurrentContext.Document.GetText(line);
        int startOffset = line.Offset;
        int searchLen = _searchTerm.Length;
        int index = 0;

        while ((index = SearchTextMatcher.IndexOf(lineText, _searchTerm, index, WholeWord)) >= 0)
        {
            base.ChangeLinePart(
                startOffset + index,
                startOffset + index + searchLen,
                element => element.TextRunProperties.SetBackgroundBrush(HighlightBrush));

            index += searchLen;
        }
    }
}
