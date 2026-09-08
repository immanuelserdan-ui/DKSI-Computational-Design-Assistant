namespace OmniBIM.Desktop.Models;

/// <summary>
/// One Trend News card. A hand-curated snapshot (see TrendNewsViewModel) of real articles -
/// title, source and URL are all genuine, found via web search, never invented. No per-article
/// publish date: search results didn't reliably surface a verifiable one for each piece, and
/// showing a made-up date would be worse than showing none.
/// </summary>
public sealed class NewsArticle
{
    public required string Title { get; init; }
    public required string Source { get; init; }
    public required string Category { get; init; }
    public required string Summary { get; init; }
    public required string Url { get; init; }
}
