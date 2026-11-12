namespace LiveSlides.Core;

public enum StepKind { Slide, Tappo }

/// <summary>
/// One thing the output can show. For a slide, DeckIndex/PageIndex say which one. For a tappo,
/// DeckIndex is the deck that FOLLOWS it (-1 for the closing tappo) and PageIndex is -1.
/// </summary>
public readonly record struct ShowStep(StepKind Kind, int DeckIndex, int PageIndex);

/// <summary>
/// Flattens the playlist into the single linear sequence the operator steps through: every
/// deck's slides one after another, as if they were one presentation, with the optional
/// "tappo" image before the first deck, between decks and after the last one.
/// </summary>
public static class ShowSequence
{
    /// <param name="pageCounts">Slides per deck, in playlist order. 0 = skip (disabled, not ready or failed).</param>
    public static List<ShowStep> Build(IReadOnlyList<int> pageCounts, bool hasTappo,
        bool tappoAtStart, bool tappoBetween, bool tappoAtEnd)
    {
        var steps = new List<ShowStep>();
        var any = false;
        for (var deck = 0; deck < pageCounts.Count; deck++)
        {
            if (pageCounts[deck] <= 0) continue;
            if (hasTappo && (any ? tappoBetween : tappoAtStart))
                steps.Add(new ShowStep(StepKind.Tappo, deck, -1));
            for (var page = 0; page < pageCounts[deck]; page++)
                steps.Add(new ShowStep(StepKind.Slide, deck, page));
            any = true;
        }
        if (any && hasTappo && tappoAtEnd)
            steps.Add(new ShowStep(StepKind.Tappo, -1, -1));
        return steps;
    }

    /// <summary>Index of the given step, or -1.</summary>
    public static int Find(IReadOnlyList<ShowStep> steps, StepKind kind, int deckIndex, int pageIndex)
    {
        for (var i = 0; i < steps.Count; i++)
            if (steps[i].Kind == kind && steps[i].DeckIndex == deckIndex && steps[i].PageIndex == pageIndex)
                return i;
        return -1;
    }
}
