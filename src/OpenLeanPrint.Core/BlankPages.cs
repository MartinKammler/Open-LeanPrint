namespace OpenLeanPrint.Core;

/// <summary>
/// Puts blank pages the user asked for into a page sequence.
/// <para>
/// A blank is anchored to a page of its document: anchor <c>n</c> means "after
/// page n", so <c>0</c> is before the first page. Anchoring to the document's
/// own page numbers rather than to a position in the output is what keeps a
/// blank where it was put when the layout, the page selection or the other jobs
/// change. A blank after a page that is not being printed stays: it was asked
/// for between two pages, and the page that is left out does not change that.
/// </para>
/// </summary>
public static class BlankPages
{
    /// <summary>
    /// Returns <paramref name="pages"/> with the blanks inserted. Each blank is a
    /// <see cref="SourcePage"/> with <see cref="SourcePage.IsBlank"/> set, its
    /// document's index, its anchor as <see cref="SourcePage.PageIndex"/>, and
    /// the size of the page it stands next to, so it takes up a cell exactly as
    /// that page would.
    /// </summary>
    /// <param name="pages">The pages to print, in pool order: grouped by document, pages ascending.</param>
    /// <param name="blanks">Per document index: anchor → how many blanks.</param>
    public static IReadOnlyList<SourcePage> Insert(
        IReadOnlyList<SourcePage> pages,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> blanks)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(blanks);
        if (blanks.Count == 0 || blanks.Values.All(perDoc => perDoc.Values.All(count => count <= 0)))
            return pages;

        var result = new List<SourcePage>(pages.Count + 8);
        int i = 0;
        while (i < pages.Count)
        {
            int document = pages[i].DocumentIndex;
            int end = i;
            while (end < pages.Count && pages[end].DocumentIndex == document) end++;

            var anchors = blanks.TryGetValue(document, out var perDoc)
                ? perDoc.Where(entry => entry.Value > 0).OrderBy(entry => entry.Key).ToList()
                : new List<KeyValuePair<int, int>>();
            int next = 0;

            for (int p = i; p < end; p++)
            {
                var page = pages[p];
                int number = page.PageIndex + 1;
                // Everything anchored before this page - including after pages
                // that are not printed - comes first.
                while (next < anchors.Count && anchors[next].Key < number)
                    AddBlanks(result, document, anchors[next++], page.Size);
                result.Add(page);
            }

            var last = pages[end - 1].Size;
            while (next < anchors.Count) AddBlanks(result, document, anchors[next++], last);

            i = end;
        }
        return result;
    }

    private static void AddBlanks(List<SourcePage> result, int document, KeyValuePair<int, int> anchor, PtSize size)
    {
        for (int n = 0; n < anchor.Value; n++)
            result.Add(new SourcePage(document, anchor.Key, size) { IsBlank = true });
    }
}
