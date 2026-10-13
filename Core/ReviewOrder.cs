namespace CleanTag.Core;

public static class ReviewOrder
{
    public static void Defer(List<FileEntry> cards, int position)
    {
        var file = cards[position];
        // Earlier cards are navigation history; only future repeats are coalesced.
        for (int i = cards.Count - 1; i > position; i--)
            if (cards[i].Identity == file.Identity) cards.RemoveAt(i);
        cards.Add(file);
    }
    public static int NextPending(IReadOnlyList<FileEntry> cards, int position, Func<FileEntry, bool> completed)
    {
        int next = position + 1;
        while (next < cards.Count && completed(cards[next])) next++;
        return next;
    }
}
