using LiveSlides.Core;
using Xunit;

public class ShowSequenceTests
{
    private static string Describe(List<ShowStep> steps)
        => string.Join(" ", steps.Select(s => s.Kind == StepKind.Tappo ? "T" : $"{s.DeckIndex}.{s.PageIndex}"));

    [Fact]
    public void DecksFollowEachOtherWithoutGaps()
        => Assert.Equal("0.0 0.1 1.0 1.1 1.2", Describe(ShowSequence.Build(new[] { 2, 3 }, false, true, true, true)));

    [Fact]
    public void TappoGoesAtStartBetweenAndEnd()
        => Assert.Equal("T 0.0 T 1.0 1.1 T", Describe(ShowSequence.Build(new[] { 1, 2 }, true, true, true, true)));

    [Fact]
    public void TappoOnlyBetween()
        => Assert.Equal("0.0 T 1.0", Describe(ShowSequence.Build(new[] { 1, 1 }, true, false, true, false)));

    [Fact]
    public void EmptyDecksAreSkippedAndDoNotGetATappo()
        => Assert.Equal("1.0 T 3.0", Describe(ShowSequence.Build(new[] { 0, 1, 0, 1, 0 }, true, false, true, false)));

    [Fact]
    public void NothingReadyMeansNoSteps()
        => Assert.Empty(ShowSequence.Build(new[] { 0, 0 }, true, true, true, true));

    [Fact]
    public void BetweenTappoPointsToTheDeckThatFollows()
    {
        var steps = ShowSequence.Build(new[] { 1, 1 }, true, false, true, false);
        Assert.Equal(1, ShowSequence.Find(steps, StepKind.Tappo, 1, -1));
        Assert.Equal(2, ShowSequence.Find(steps, StepKind.Slide, 1, 0));
        Assert.Equal(-1, ShowSequence.Find(steps, StepKind.Slide, 5, 0));
    }

    [Fact]
    public void SavedOrderWinsAndNewFilesGoLast()
        => Assert.Equal(new[] { "c.pdf", "a.pptx", "b.pdf", "d.pdf" },
            ShowFile.ApplyOrder(new[] { "a.pptx", "b.pdf", "c.pdf", "d.pdf" }, new[] { "c.pdf", "gone.pdf", "a.pptx" }));
}
