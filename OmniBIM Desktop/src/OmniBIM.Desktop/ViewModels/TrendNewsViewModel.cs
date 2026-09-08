using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using OmniBIM.Desktop.Models;

namespace OmniBIM.Desktop.ViewModels;

/// <summary>
/// Backs the Trend News page. The five articles below are real - found via web search in
/// September 2026, titles/sources/URLs copied as published, summaries drawn from what the
/// search actually returned rather than invented. This is a hand-curated SNAPSHOT, not a live
/// feed: nothing here re-fetches or goes stale-checks itself, so it will read exactly the same
/// a year from now. Building a genuine live feed (RSS/API polling, refresh UI, caching) is a
/// real feature this isn't - see the placeholder pattern the other unbuilt sidebar pages use
/// for how this app usually marks "not real" so as not to overstate it.
/// </summary>
public sealed class TrendNewsViewModel
{
    public ObservableCollection<NewsArticle> Articles { get; } = [];

    public ICommand OpenCommand { get; }

    public TrendNewsViewModel()
    {
        OpenCommand = new RelayCommand(param =>
        {
            if (param is not string url || string.IsNullOrWhiteSpace(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // Opening the user's default browser failed (none registered, malformed URL,
                // etc.) - not worth a dialog over a "read more" link.
            }
        });

        Articles.Add(new NewsArticle
        {
            Title = "Autodesk Folds Construction Cloud Into Forma, Will Release Geometry-Based AI Assistants",
            Source = "Engineering News-Record",
            Category = "Industry News",
            Summary = "Autodesk is consolidating its construction and design tools into the Forma cloud platform, with AI assistants that can interrogate design and iterate on BIM/CAD geometry directly.",
            Url = "https://www.enr.com/articles/61362-autodesk-folds-construction-cloud-into-forma-will-release-geometry-based-ai-assistants",
        });

        Articles.Add(new NewsArticle
        {
            Title = "Autodesk Targets BIM With Forma Building Design",
            Source = "AEC Magazine",
            Category = "Technology",
            Summary = "Forma Building Design adds BIM-level LOD 200/300 detail alongside AI-powered automated design tools and integrated analysis, positioned to complement Revit.",
            Url = "https://aecmag.com/bim/autodesk-targets-bim-with-forma-building-design/",
        });

        Articles.Add(new NewsArticle
        {
            Title = "5 BIM Trends in 2026 That Will Shape the Future of AEC",
            Source = "United-BIM",
            Category = "Industry News",
            Summary = "Digital twin adoption is climbing toward two-thirds of owners and facility managers, as BIM shifts from a modelling exercise to a baseline client expectation across projects.",
            Url = "https://www.united-bim.com/5-innovative-trends-shaping-the-future-of-bim-technology/",
        });

        Articles.Add(new NewsArticle
        {
            Title = "BIM Trends 2026: Evolving Design, Construction, and Skills",
            Source = "BibLus (ACCA Software)",
            Category = "Best Practice",
            Summary = "ISO 19650 has become the globally accepted standard for information management, and firms are being pushed to build a strategic digital mindset across technology, data and process.",
            Url = "https://biblus.accasoftware.com/en/bim-trends-2026-evolving-design-construction-and-skills/",
        });

        Articles.Add(new NewsArticle
        {
            Title = "AEC Trends 2026: How Digital Twins, AI, & BIM 6.0 Are Reshaping Construction",
            Source = "Tesla Outsourcing Services",
            Category = "Technology",
            Summary = "\"BIM 6.0\" describes AI, digital twins, IoT, robotics, geospatial systems and automated project delivery converging into one connected workflow rather than separate tools.",
            Url = "https://www.teslaoutsourcingservices.com/blog/the-2026-aec-technology-bim-ai-digital-twins/",
        });
    }
}
