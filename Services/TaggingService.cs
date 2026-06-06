using System.Text;
using PocketReader.Helpers;
using PocketReader.Models;

namespace PocketReader.Services;

public class TagSuggestion
{
    public string Tag { get; set; }
    public double Confidence { get; set; }
    public bool Auto { get; set; }
    public string Source { get; set; } // "learned" | "domain" | "phrase"
}

/// <summary>
/// Fully-local tag suggester. Learns word→tag associations from the user's own
/// already-tagged titles, plus domain rules and light key-phrase extraction.
/// No external API, no model files.
/// </summary>
public class TaggingService
{
    // Auto-apply only when strongly predicted; suggest in the softer band.
    private const double AutoThreshold = 0.75;
    private const double SuggestThreshold = 0.30;
    private const int MinUnigramSupport = 8; // a single word must appear in >= this many docs
    private const int MinBigramSupport = 4;  // a 2-word phrase is rarer but more specific
    private const int MinTagFreq = 5;        // a tag must have >= this many articles
    private const double MinFeatureProb = 0.12; // ignore weak word→tag signals

    private readonly Dictionary<string, int> _tagFreq = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _wordCount = new();                          // word doc-frequency (all docs)
    private readonly Dictionary<string, Dictionary<string, int>> _wordTag = new();         // word -> (tag -> co-occurrence)

    public bool IsTrained { get; private set; }

    private static readonly (string Fragment, string Tag)[] DomainRules =
    {
        ("github.com", "github"), ("gitlab.com", "gitlab"),
        ("youtube.com", "video"), ("youtu.be", "video"), ("vimeo.com", "video"),
        ("medium.com", "medium"), ("reddit.com", "reddit"), ("news.ycombinator.com", "hackernews"),
        ("stackoverflow.com", "stackoverflow"), ("arxiv.org", "research"),
        ("twitter.com", "twitter"), ("x.com", "twitter"), ("wikipedia.org", "wikipedia")
    };

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","and","or","but","of","to","in","on","for","with","at","by","from","up","about",
        "into","over","after","is","are","was","were","be","been","being","it","its","this","that","these",
        "those","as","if","then","than","so","not","no","you","your","i","we","they","he","she","my","our",
        "their","how","why","what","when","where","who","which","can","will","just","new","get","using","use",
        "vs","via","best","top","guide","tutorial","review","com","www","https","http"
    };

    /// <summary>Train from all (active) articles — tagged ones supply word→tag signal.</summary>
    public void Train(IEnumerable<Article> articles)
    {
        _tagFreq.Clear(); _wordCount.Clear(); _wordTag.Clear();

        foreach (var a in articles)
        {
            var words = Features(a.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var w in words)
                _wordCount[w] = _wordCount.GetValueOrDefault(w) + 1;

            var tags = a.GetTagsList();
            foreach (var tag in tags)
            {
                _tagFreq[tag] = _tagFreq.GetValueOrDefault(tag) + 1;
                foreach (var w in words)
                {
                    if (!_wordTag.TryGetValue(w, out var m))
                    {
                        m = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        _wordTag[w] = m;
                    }
                    m[tag] = m.GetValueOrDefault(tag) + 1;
                }
            }
        }

        IsTrained = true;
    }

    public List<TagSuggestion> Suggest(string title, string url, IEnumerable<string> existing)
    {
        var have = new HashSet<string>(existing ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var feats = Features(title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<TagSuggestion>();

        // 1) Learned: gather P(tag | feature) evidence over unigrams + bigrams.
        var evidence = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in feats)
        {
            if (!_wordCount.TryGetValue(f, out var fc)) continue;
            var minSup = f.Contains(' ') ? MinBigramSupport : MinUnigramSupport;
            if (fc < minSup) continue;
            if (!_wordTag.TryGetValue(f, out var tagMap)) continue;

            foreach (var kv in tagMap)
            {
                if (_tagFreq.GetValueOrDefault(kv.Key) < MinTagFreq) continue;
                var p = (double)kv.Value / fc;
                if (p < MinFeatureProb) continue;
                if (!evidence.TryGetValue(kv.Key, out var list)) { list = new List<double>(); evidence[kv.Key] = list; }
                list.Add(p);
            }
        }

        // Combine evidence per tag with noisy-OR: independent signals reinforce.
        foreach (var kv in evidence)
        {
            if (have.Contains(kv.Key)) continue;
            double notAny = 1.0;
            foreach (var p in kv.Value.OrderByDescending(x => x).Take(4)) notAny *= (1 - p);
            var conf = 1 - notAny;
            if (conf < SuggestThreshold) continue;
            result.Add(new TagSuggestion { Tag = kv.Key, Confidence = conf, Auto = conf >= AutoThreshold, Source = "learned" });
        }

        // 2) Domain rule (auto).
        var domainTag = DomainTag(url);
        if (domainTag != null && !have.Contains(domainTag) && !result.Any(r => r.Tag.Equals(domainTag, StringComparison.OrdinalIgnoreCase)))
            result.Add(new TagSuggestion { Tag = domainTag, Confidence = 1.0, Auto = true, Source = "domain" });

        // 3) Key phrases from the title (suggest only).
        foreach (var ph in KeyPhrases(title).Take(2))
            if (!have.Contains(ph) && !result.Any(r => r.Tag.Equals(ph, StringComparison.OrdinalIgnoreCase)))
                result.Add(new TagSuggestion { Tag = ph, Confidence = 0.3, Auto = false, Source = "phrase" });

        return result.OrderByDescending(r => r.Auto).ThenByDescending(r => r.Confidence).ToList();
    }

    private string DomainTag(string url)
    {
        try
        {
            var host = new Uri(UrlHelper.Unwrap(url)).Host.ToLowerInvariant();
            foreach (var rule in DomainRules)
                if (host == rule.Fragment || host.EndsWith("." + rule.Fragment)) return rule.Tag;

            // If the site's main label is already a tag the user uses, adopt it.
            var label = host.Replace("www.", "").Split('.').FirstOrDefault();
            if (!string.IsNullOrEmpty(label) && label.Length >= 3 && _tagFreq.ContainsKey(label)) return label;
        }
        catch { }
        return null;
    }

    // Consecutive non-stopword runs from the title (1–3 words), longest first.
    private static IEnumerable<string> KeyPhrases(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) yield break;
        var tokens = title.Split(new[] { ' ', '\t', '\n', '-', '–', '—', '|', ':', ',', '.', '/', '(', ')', '"', '\'' },
            StringSplitOptions.RemoveEmptyEntries);
        var run = new List<string>();
        var phrases = new List<string>();
        void Flush()
        {
            if (run.Count >= 1 && run.Count <= 3)
                phrases.Add(string.Join(" ", run).ToLowerInvariant());
            run.Clear();
        }
        foreach (var raw in tokens)
        {
            var t = new string(raw.Where(char.IsLetterOrDigit).ToArray());
            if (t.Length >= 3 && !Stop.Contains(t)) run.Add(t);
            else Flush();
        }
        Flush();
        foreach (var p in phrases.Where(p => p.Contains(' ')).OrderByDescending(p => p.Length))
            yield return p;
    }

    // Unigrams + adjacent bigrams (bigrams are more specific → higher-precision tags).
    private static IEnumerable<string> Features(string text)
    {
        var words = Tokenize(text).ToList();
        foreach (var w in words) yield return w;
        for (var i = 0; i + 1 < words.Count; i++) yield return words[i] + " " + words[i + 1];
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0)
            {
                var w = sb.ToString(); sb.Clear();
                if (w.Length >= 2 && !Stop.Contains(w)) yield return w;
            }
        }
        if (sb.Length > 0)
        {
            var w = sb.ToString();
            if (w.Length >= 2 && !Stop.Contains(w)) yield return w;
        }
    }
}
