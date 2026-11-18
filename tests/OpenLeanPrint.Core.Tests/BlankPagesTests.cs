using OpenLeanPrint.Core;
using OpenLeanPrint.Core.Imposition;
using Xunit;

namespace OpenLeanPrint.Core.Tests;

public class BlankPagesTests
{
    private static readonly PtSize Portrait = new(595, 842);
    private static readonly PtSize Landscape = new(842, 595);

    private static List<SourcePage> Pages(int document, params int[] numbers) =>
        numbers.Select(n => new SourcePage(document, n - 1, Portrait)).ToList();

    private static Dictionary<int, IReadOnlyDictionary<int, int>> Blanks(int document, params (int Anchor, int Count)[] entries) =>
        new() { [document] = entries.ToDictionary(e => e.Anchor, e => e.Count) };

    /// <summary>"1 _ 2" style: page numbers, and _ for a blank.</summary>
    private static string Describe(IEnumerable<SourcePage> pages) =>
        string.Join(" ", pages.Select(p => p.IsBlank ? "_" : (p.PageIndex + 1).ToString()));

    [Fact]
    public void NoBlanks_ReturnsTheSameSequence()
    {
        var pages = Pages(0, 1, 2, 3);
        Assert.Same(pages, BlankPages.Insert(pages, new Dictionary<int, IReadOnlyDictionary<int, int>>()));
    }

    [Theory]
    [InlineData(0, "_ 1 2 3")]
    [InlineData(1, "1 _ 2 3")]
    [InlineData(3, "1 2 3 _")]
    public void ABlank_LandsAfterItsAnchorPage(int anchor, string expected)
    {
        var result = BlankPages.Insert(Pages(0, 1, 2, 3), Blanks(0, (anchor, 1)));
        Assert.Equal(expected, Describe(result));
    }

    [Fact]
    public void SeveralBlanks_AtOneAnchor_AllAppear()
    {
        var result = BlankPages.Insert(Pages(0, 1, 2), Blanks(0, (1, 2)));
        Assert.Equal("1 _ _ 2", Describe(result));
    }

    [Fact]
    public void ABlankAfterALeftOutPage_StaysBetweenItsNeighbours()
    {
        // Page 2 is not printed; the blank was put after it, so it still sits
        // between 1 and 3 rather than vanishing with the page.
        var result = BlankPages.Insert(Pages(0, 1, 3), Blanks(0, (2, 1)));
        Assert.Equal("1 _ 3", Describe(result));
    }

    [Fact]
    public void Blanks_StayWithTheirOwnDocument()
    {
        var pages = Pages(0, 1, 2).Concat(Pages(1, 1, 2)).ToList();
        var blanks = new Dictionary<int, IReadOnlyDictionary<int, int>>
        {
            [0] = new Dictionary<int, int> { [2] = 1 },
            [1] = new Dictionary<int, int> { [0] = 1 },
        };

        var result = BlankPages.Insert(pages, blanks);

        Assert.Equal("1 2 _ _ 1 2", Describe(result));
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 1 }, result.Select(p => p.DocumentIndex));
    }

    [Fact]
    public void ABlank_TakesTheSizeOfThePageItStandsBefore()
    {
        var pages = new List<SourcePage> { new(0, 0, Portrait), new(0, 1, Landscape) };
        var result = BlankPages.Insert(pages, Blanks(0, (1, 1)));
        Assert.Equal(Landscape, result[1].Size);
    }

    [Fact]
    public void ABlank_CarriesItsAnchorAsPageIndex()
    {
        var result = BlankPages.Insert(Pages(0, 1, 2, 3), Blanks(0, (2, 1)));
        var blank = Assert.Single(result, p => p.IsBlank);
        Assert.Equal(2, blank.PageIndex);
    }

    [Fact]
    public void ABlank_TakesACellOfItsOwn_InTheNUpLayout()
    {
        // Three pages and one blank fill a 2x2 sheet exactly: the blank is a
        // page to the imposer, so it is placed and can be found under a click.
        var sequence = BlankPages.Insert(Pages(0, 1, 2, 3), Blanks(0, (1, 1)));
        var result = new NUpImposer().Impose(sequence.Cast<SourcePage?>().ToList(),
                                             ImpositionSettings.NUp(2, 2) with { SheetSize = PaperSizes.A4 });

        var sheet = Assert.Single(result.Sheets);
        Assert.Equal(4, sheet.Pages.Count);
        Assert.True(sheet.Pages[1].Source.IsBlank);
    }
}
