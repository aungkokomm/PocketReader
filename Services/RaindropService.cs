using System.Text.Json;
using PocketReader.Data;
using PocketReader.Models;

namespace PocketReader.Services;

public class RaindropService
{
    private readonly DatabaseService _db;
    private string _accessToken;
    private const string ApiBaseUrl = "https://api.raindrop.io/rest/v1";

    public RaindropService(DatabaseService db)
    {
        _db = db;
        LoadAccessToken();
    }

    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);

    public void SetAccessToken(string token)
    {
        _accessToken = token;
        SaveAccessToken(token);
    }

    private void SaveAccessToken(string token)
    {
        var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataFolder);
        var tokenPath = Path.Combine(dataFolder, "token.txt");
        File.WriteAllText(tokenPath, token);
    }

    /// <summary>Check a pasted token works by calling the user endpoint.</summary>
    public async Task<bool> ValidateTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/user");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
            using var resp = await Helpers.HttpFetcher.Client.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public void Logout()
    {
        _accessToken = null;
        try
        {
            var tokenPath = Path.Combine(AppContext.BaseDirectory, "data", "token.txt");
            if (File.Exists(tokenPath)) File.Delete(tokenPath);
        }
        catch { }
    }

    private void LoadAccessToken()
    {
        var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
        var tokenPath = Path.Combine(dataFolder, "token.txt");

        if (File.Exists(tokenPath))
        {
            _accessToken = File.ReadAllText(tokenPath).Trim();
        }
    }

    // Parse one API page into Article objects only (no DB work).
    private async Task<List<Article>> ParsePageAsync(int page)
    {
        if (string.IsNullOrEmpty(_accessToken))
            throw new InvalidOperationException("Not authenticated. Call SetAccessToken first.");

        // Collection 0 = "all bookmarks". Sort by -lastUpdate so incremental sync can
        // stop as soon as it reaches already-synced items.
        var url = $"{ApiBaseUrl}/raindrops/0?sort=-lastUpdate&page={page}&perpage=50";
        string json = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
            using var response = await Helpers.HttpFetcher.Client.SendAsync(req);

            if ((int)response.StatusCode == 429)
            {
                // Rate limited — honor Retry-After (default ~5s) and try again.
                var wait = GetRetryAfterSeconds(response) ?? 5;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 1, 60)));
                continue;
            }

            json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Raindrop API {(int)response.StatusCode}: {json}");
            break;
        }

        if (json == null)
            throw new Exception("Raindrop rate limit — please wait a moment and sync again.");

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var articles = new List<Article>();
        if (!root.TryGetProperty("items", out var items)) return articles;

        foreach (var item in items.EnumerateArray())
        {
            // _id is a number in Raindrop's API — GetString() would throw.
            var idEl = item.GetProperty("_id");
            var raindropId = idEl.ValueKind == JsonValueKind.Number ? idEl.GetRawText() : idEl.GetString();

            var title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : "Untitled";
            if (string.IsNullOrWhiteSpace(title)) title = "Untitled";

            var link = item.TryGetProperty("link", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString() : "";
            if (string.IsNullOrEmpty(link)) link = $"raindrop://{raindropId}";

            var tagsArray = item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? string.Join(",", tags.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()))
                : "";

            var dateSaved = DateTime.UtcNow;
            if (item.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.String
                && DateTime.TryParse(created.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                dateSaved = parsed;

            // lastUpdate drives incremental sync (falls back to created).
            var lastUpdate = dateSaved;
            if (item.TryGetProperty("lastUpdate", out var lu) && lu.ValueKind == JsonValueKind.String
                && DateTime.TryParse(lu.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var luParsed))
                lastUpdate = luParsed.ToUniversalTime();

            var isFavorite = item.TryGetProperty("important", out var imp) && imp.ValueKind == JsonValueKind.True;

            var cover = item.TryGetProperty("cover", out var cov) && cov.ValueKind == JsonValueKind.String
                ? cov.GetString() : null;

            // Fall back to the first media image if there's no explicit cover.
            if (string.IsNullOrWhiteSpace(cover) && item.TryGetProperty("media", out var media)
                && media.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in media.EnumerateArray())
                {
                    if (m.TryGetProperty("link", out var ml) && ml.ValueKind == JsonValueKind.String)
                    {
                        var mlink = ml.GetString();
                        if (!string.IsNullOrWhiteSpace(mlink)) { cover = mlink; break; }
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(cover)) cover = null;

            articles.Add(new Article
            {
                RaindropId = raindropId,
                Title = title,
                Url = link,
                Tags = tagsArray,
                DateSaved = dateSaved,
                Rating = 0,
                Content = "",
                IsFavorite = isFavorite,
                Cover = cover,
                LastUpdate = lastUpdate
            });
        }

        return articles;
    }

    private static int? GetRetryAfterSeconds(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra == null) return null;
        if (ra.Delta.HasValue) return (int)Math.Ceiling(ra.Delta.Value.TotalSeconds);
        if (ra.Date.HasValue)
        {
            var s = (ra.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
            return s > 0 ? (int)Math.Ceiling(s) : 1;
        }
        return null;
    }

    /// <summary>
    /// Sync bookmarks. When <paramref name="since"/> is set, stops paging once it reaches
    /// items not newer than the watermark (incremental). Null = full sync.
    /// Returns how many were processed and the newest lastUpdate seen (new watermark).
    /// </summary>
    public async Task<(int Processed, DateTime NewWatermark)> SyncAsync(
        DateTime? since, IProgress<(int Current, int Total)> progress = null)
    {
        // Preload existing keys once (Id/RaindropId/Url) — avoids per-item DB lookups.
        var keys = await _db.GetKeysAsync();
        var idByRaindrop = new Dictionary<string, int>();
        var idByUrl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
        {
            if (!string.IsNullOrEmpty(k.RaindropId)) idByRaindrop[k.RaindropId] = k.Id;
            if (!string.IsNullOrEmpty(k.Url)) idByUrl[k.Url] = k.Id;
        }

        var toInsert = new List<Article>();
        var toUpdate = new List<Article>();
        var pendingUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newWatermark = since ?? DateTime.MinValue;

        int page = 0, total = 0;
        var reachedKnown = false;

        while (!reachedKnown)
        {
            var batch = await ParsePageAsync(page);
            if (batch.Count == 0) break;

            foreach (var art in batch)
            {
                // Incremental: items are sorted newest-changed first, so once we hit one
                // at/older than the watermark, everything after is already synced.
                if (since.HasValue && art.LastUpdate <= since.Value)
                {
                    reachedKnown = true;
                    break;
                }

                if (art.LastUpdate > newWatermark) newWatermark = art.LastUpdate;

                if (idByRaindrop.TryGetValue(art.RaindropId, out var id1)) { art.Id = id1; toUpdate.Add(art); }
                else if (idByUrl.TryGetValue(art.Url, out var id2)) { art.Id = id2; toUpdate.Add(art); }
                else if (pendingUrls.Contains(art.Url)) { /* duplicate within this sync — skip */ }
                else { art.DateAdded = DateTime.UtcNow; toInsert.Add(art); pendingUrls.Add(art.Url); }

                total++;
            }

            progress?.Report((total, total));

            page++;
            if (page > 10000) break; // anti-runaway guard (~500k)

            // Pace requests to stay under Raindrop's ~120 req/min limit (full sync only
            // pages a lot; incremental stops early so this barely matters).
            if (!reachedKnown) await Task.Delay(550);
        }

        await _db.BulkInsertAsync(toInsert);
        await _db.BulkSyncUpdateAsync(toUpdate);
        return (total, newWatermark);
    }
}
