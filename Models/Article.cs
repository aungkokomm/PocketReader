namespace PocketReader.Models;

public class Article
{
    public int Id { get; set; }
    public string Url { get; set; }
    public string Title { get; set; }
    public string Content { get; set; }
    public string Tags { get; set; } // Comma-separated
    public int Rating { get; set; } // 0-5 stars
    public DateTime DateSaved { get; set; }
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
    public string RaindropId { get; set; } // External ID from Raindrop
    public bool ContentCached { get; set; } // Is full article content downloaded?
    public DateTime? ContentCachedAt { get; set; } // When was content cached?
    public string FaviconPath { get; set; } // Local path to favicon (null = use URL)
    public string Cover { get; set; } // Raindrop cover/thumbnail image URL
    public string ResolvedUrl { get; set; } // Real URL after following short-link redirects (local; survives re-sync)
    public DateTime LastUpdate { get; set; } // Raindrop lastUpdate — transient, used for incremental sync (not persisted)
    public string Note { get; set; } // User's personal note (local)
    public bool IsFavorite { get; set; } // From Raindrop "important" flag
    public bool IsRead { get; set; } // Set locally when opened in the reader
    public bool IsArchived { get; set; } // Local "archive" (hidden from main lists)
    public bool IsDeleted { get; set; } // Local soft-delete (in the Recycle Bin)
    public int WordCount { get; set; } // Captured when opened — for reading statistics
    public int ScrollPercent { get; set; } // Last reading position (0-100) — for resume
    public List<Highlight> Highlights { get; set; } // Transient — loaded by the reader

    public List<string> GetTagsList() => string.IsNullOrWhiteSpace(Tags)
        ? new List<string>()
        : Tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .ToList();

    public void SetTags(IEnumerable<string> tags) => Tags = string.Join(',', tags);
}
