using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace ValheimData_DiscordBot;

public sealed class JotunnCache
{
    public sealed class Options
    {
        public required string ItemsUrl { get; init; }
        public required string RecipesUrl { get; init; }
        public required string PrefabsUrl { get; init; }
        public required string PieceUrl { get; init; }
        public required string CharactersUrl { get; init; }
        public required TimeSpan Expiry { get; init; }
    }

    private readonly Options _opt;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly IBrowsingContext _ctx = BrowsingContext.New(Configuration.Default);
    private readonly object _gate = new();

    private Snapshot _snap = new();
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;
    private string? _etagItems, _etagRecipes, _etagPrefabs, _etagPieces, _etagCharacters;
    private Timer? _timer;

    public JotunnCache(Options opt)
    {
        _opt = opt;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Azumatt.ValDocsBot/1.0 (+discord)");
    }

    /// <summary>Called once from Program: pre-warm now and re-check on a slow cadence (every 30 minutes by default).</summary>
    public void Start(TimeSpan? poll = null)
    {
        _ = EnsureFreshAsync(); // fire & forget warmup
        _timer = new Timer(async _ => await SafeFreshAsync(), null, poll ?? TimeSpan.FromMinutes(30), poll ?? TimeSpan.FromMinutes(30));
    }

    private async Task SafeFreshAsync()
    {
        try
        {
            await EnsureFreshAsync();
        }
        catch
        {
            /* swallow: discord bot must keep running ¯\_(ツ)_/¯ */
        }
    }

    public async Task EnsureFreshAsync()
    {
        if (DateTimeOffset.UtcNow - _lastFetch < _opt.Expiry && _snap.HasData) return;
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _lastFetch < _opt.Expiry && _snap.HasData) return;
        }

        (IDocument? itemsDoc, string? tagItems) = await GetDocWithEtagAsync(_opt.ItemsUrl, _etagItems);
        (IDocument? recipesDoc, string? tagRecipes) = await GetDocWithEtagAsync(_opt.RecipesUrl, _etagRecipes);
        (IDocument? prefabsDoc, string? tagPrefabs) = await GetDocWithEtagAsync(_opt.PrefabsUrl, _etagPrefabs);
        (IDocument? pieceDoc, string? tagPiece) = await GetDocWithEtagAsync(_opt.PieceUrl, _etagPieces);
        (IDocument? charactersDoc, string? tagCharacters) = await GetDocWithEtagAsync(_opt.CharactersUrl, _etagCharacters);

        Snapshot next = new Snapshot
        {
            Items = itemsDoc is null ? _snap.Items : ParseItems(itemsDoc),
            Recipes = recipesDoc is null ? _snap.Recipes : ParseRecipes(recipesDoc),
            Prefabs = prefabsDoc is null ? _snap.Prefabs : ParsePrefabs(prefabsDoc),
            Pieces = pieceDoc is null ? _snap.Pieces : ParsePieces(pieceDoc),
            Characters = charactersDoc is null ? _snap.Characters : ParseCharacters(charactersDoc),
        };

        lock (_gate)
        {
            _snap = next;
            _lastFetch = DateTimeOffset.UtcNow;
            if (tagItems is not null) _etagItems = tagItems;
            if (tagRecipes is not null) _etagRecipes = tagRecipes;
            if (tagPrefabs is not null) _etagPrefabs = tagPrefabs;
            if (tagPiece is not null) _etagPieces = tagPiece;
            if (tagCharacters is not null) _etagCharacters = tagCharacters;
        }
    }

    private static string? ResolveUrl(string? pageUrl, string? relative)
    {
        if (string.IsNullOrWhiteSpace(pageUrl) || string.IsNullOrWhiteSpace(relative))
            return null;

        return Uri.TryCreate(new Uri(pageUrl), relative, out Uri? absolute) ? absolute.ToString() : null;
    }


    public IEnumerable<JItem> FindItems(string query, int limit = 10)
    {
        string q = query.ToLowerInvariant();
        return _snap.Items.Where(it =>
                (it.EnglishName ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase) ||
                (it.DisplayCell ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase) ||
                (it.Token ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase) ||
                (it.AssetId ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase) ||
                (it.Type ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase))
            .Take(Math.Clamp(limit, 1, 50));
    }

    public IEnumerable<JRecipe> FindRecipesFor(string resultName, int limit = 10)
        => _snap.Recipes.Where(r => (r.ResultName ?? "").Contains(resultName, StringComparison.OrdinalIgnoreCase)).Take(Math.Clamp(limit, 1, 50));

    public IEnumerable<JRecipe> FindRecipesByIngredient(string ingredientName, int limit = 10)
        => _snap.Recipes.Where(r => r.RequirementsText.Contains(ingredientName, StringComparison.OrdinalIgnoreCase)).Take(Math.Clamp(limit, 1, 50));

    public IEnumerable<JPrefab> FindPrefabs(string query, int limit = 10)
    {
        string q = query.ToLowerInvariant();
        return _snap.Prefabs.Where(p =>
                (p.Name ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase) ||
                (p.EnglishName ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase) ||
                (p.Token ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase) ||
                (p.AssetId ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase))
            .Take(Math.Clamp(limit, 1, 50));
    }

    public IEnumerable<JPiece> FindPieces(string query, int limit = 10)
    {
        string q = query.ToLowerInvariant();
        return _snap.Pieces.Where(p =>
                (p.Name ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase) ||
                (p.EnglishName ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase) ||
                (p.Token ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase) ||
                (p.AssetId ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase))
            .Take(Math.Clamp(limit, 1, 50));
    }

    public IEnumerable<JCharacter> FindCharacters(string query, int limit = 10)
    {
        string q = query.ToLowerInvariant();
        return _snap.Characters
            .Where(c =>
                (c.Name ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase) ||
                (c.AssetId ?? "").Contains(q, StringComparison.InvariantCultureIgnoreCase) ||
                c.Components.Any(x => x.Contains(q, StringComparison.InvariantCultureIgnoreCase)))
            .Take(Math.Clamp(limit, 1, 50));
    }

    public IEnumerable<string> SuggestItems(string needle, int max = 25)
    {
        string q = (needle ?? string.Empty).Trim().ToLowerInvariant();
        IEnumerable<string> names = _snap.Items.Select(i => i.EnglishName).Where(s => !string.IsNullOrWhiteSpace(s))!;
        return Rank(names, q).Take(max);
    }

    public IEnumerable<string> SuggestPrefabs(string needle, int max = 25)
    {
        string q = (needle ?? string.Empty).Trim().ToLowerInvariant();
        IEnumerable<string> names = _snap.Prefabs.Select(p => p.Name).Where(s => !string.IsNullOrWhiteSpace(s))!;
        return Rank(names, q).Take(max);
    }

    public IEnumerable<string> SuggestPieces(string needle, int max = 25)
    {
        string q = (needle ?? string.Empty).Trim().ToLowerInvariant();
        IEnumerable<string> names = _snap.Pieces.Select(p => p.Name).Where(s => !string.IsNullOrWhiteSpace(s))!;
        return Rank(names, q).Take(max);
    }

    public IEnumerable<string> SuggestCharacters(string needle, int max = 25)
    {
        string q = (needle ?? string.Empty).Trim().ToLowerInvariant();
        IEnumerable<string> names = _snap.Characters.Select(c => c.Name).Where(s => !string.IsNullOrWhiteSpace(s))!;
        return Rank(names, q).Take(max);
    }

    public IEnumerable<string> SuggestRecipeResults(string needle, int max = 25)
    {
        string q = (needle ?? string.Empty).Trim().ToLowerInvariant();
        IEnumerable<string> names = _snap.Recipes.Select(r => r.ResultName).Where(s => !string.IsNullOrWhiteSpace(s))!;
        return Rank(names, q).Take(max);
    }

    public IEnumerable<string> SuggestRecipeIngredients(string needle, int max = 25)
    {
        string q = (needle ?? string.Empty).Trim().ToLowerInvariant();
        // Split requirements blob into tokens; basic, but good enough for suggestions.
        HashSet<string> pool = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JRecipe r in _snap.Recipes)
        {
            foreach (string line in r.RequirementsText.Split('\n'))
            {
                string s = line.Trim();
                if (s.StartsWith("- ")) s = s[2..].Trim();
                if (s.Length > 0) pool.Add(s);
            }
        }

        return Rank(pool, q).Take(max);
    }

    private static IEnumerable<string> Rank(IEnumerable<string> src, string q)
    {
        if (string.IsNullOrEmpty(q))
            return src.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

        return src
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => (s, lower: s.ToLowerInvariant()))
            .OrderBy(t => t.lower.StartsWith(q) ? 0 : 1)
            .ThenBy(t =>
            {
                int i = t.lower.IndexOf(q, StringComparison.Ordinal);
                return i < 0 ? int.MaxValue : i;
            })
            .ThenBy(t => t.s, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.s);
    }


    private async Task<(IDocument? doc, string? etag)> GetDocWithEtagAsync(string url, string? oldEtag)
    {
        using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(oldEtag))
            req.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(oldEtag));

        using HttpResponseMessage res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (res.StatusCode == System.Net.HttpStatusCode.NotModified)
            return (null, oldEtag);

        res.EnsureSuccessStatusCode();
        string html = await res.Content.ReadAsStringAsync();

        IDocument doc = await _ctx.OpenAsync(r =>
        {
            r.Address(url);
            r.Content(html);
        });

        string? tag = res.Headers.ETag?.Tag;
        return (doc, tag);
    }


    private static List<JItem> ParseItems(IDocument doc)
    {
        List<JItem> list = new List<JItem>();
        foreach (IElement tr in doc.QuerySelectorAll("table tbody tr"))
        {
            IHtmlCollection<IElement> td = tr.QuerySelectorAll("td");
            if (td.Length < 6) continue;
            string T(IElement e) => e.Text().Trim().Replace('\u00A0', ' ');

            string? relSrc = td[0].QuerySelector("img")?.GetAttribute("src");
            string? imgAbs = ResolveUrl(doc.Url, relSrc);

            list.Add(new JItem(
                DisplayCell: T(td[0]),
                AssetId: T(td[1]),
                Token: T(td[2]),
                EnglishName: string.IsNullOrWhiteSpace(T(td[3])) ? T(td[2]) : T(td[3]),
                Type: T(td[4]),
                Description: T(td[5]),
                ImageUrl: imgAbs
            ));
        }

        return list;
    }

    private static List<JRecipe> ParseRecipes(IDocument doc)
    {
        List<JRecipe> list = new List<JRecipe>();
        foreach (IElement tr in doc.QuerySelectorAll("table tbody tr"))
        {
            IHtmlCollection<IElement> td = tr.QuerySelectorAll("td");
            if (td.Length < 5) continue;
            string T(IElement e) => e.Text().Trim().Replace('\u00A0', ' ');
            string name = T(td[0]);
            string asset = T(td[1]);
            string result = T(td[2]);
            int amount = int.TryParse(T(td[3]), out int a) ? a : 1;
            string reqTxt = NormalizeRequirements(td[4].InnerHtml.Trim());
            list.Add(new JRecipe(name, asset, result, amount, reqTxt));
        }

        return list;
    }

    private static List<JPrefab> ParsePrefabs(IDocument doc)
    {
        List<JPrefab> list = new List<JPrefab>();
        foreach (IElement tr in doc.QuerySelectorAll("table tbody tr"))
        {
            IHtmlCollection<IElement> td = tr.QuerySelectorAll("td");
            if (td.Length < 6) continue;
            string T(IElement e) => e.Text().Trim().Replace('\u00A0', ' ');

            static List<string> Li(IElement e) =>
                e.QuerySelectorAll("li").Select(li => li.Text().Trim()).Where(s => s.Length > 0).ToList();

            list.Add(new JPrefab(
                Name: T(td[0]),
                AssetId: T(td[1]),
                Token: T(td[2]),
                EnglishName: T(td[3]),
                Components: Li(td[4]),
                ChildComponents: Li(td[5])
            ));
        }

        return list;
    }

    private static List<JPiece> ParsePieces(IDocument doc)
    {
        List<JPiece> list = new List<JPiece>();
        foreach (IElement tr in doc.QuerySelectorAll("table tbody tr"))
        {
            IHtmlCollection<IElement> td = tr.QuerySelectorAll("td");
            if (td.Length < 6) continue;
            string T(IElement e) => e.Text().Trim().Replace('\u00A0', ' ');

            string? relSrc = td[0].QuerySelector("img")?.GetAttribute("src");
            string? imgAbs = ResolveUrl(doc.Url, relSrc);

            static List<string> Li(IElement e) =>
                e.QuerySelectorAll("li").Select(li => li.Text().Trim()).Where(s => s.Length > 0).ToList();

            list.Add(new JPiece(
                Name: T(td[0]),
                AssetId: T(td[1]),
                Token: T(td[2]),
                EnglishName: T(td[3]),
                Description: T(td[4]),
                ResourcesRequired: Li(td[5]),
                MaterialType: T(td[6]),
                ImageUrl: imgAbs
            ));
        }

        return list;
    }

    private static List<JCharacter> ParseCharacters(IDocument doc)
    {
        List<JCharacter> list = new List<JCharacter>();

        static string T(IElement e) => e.Text().Trim().Replace('\u00A0', ' ');

        static List<string> Li(IElement e) =>
            e.QuerySelectorAll("li").Select(li => li.Text().Trim()).Where(s => s.Length > 0).ToList();

        foreach (IElement tr in doc.QuerySelectorAll("table tbody tr"))
        {
            IHtmlCollection<IElement> td = tr.QuerySelectorAll("td");
            if (td.Length < 6) continue; // Name, AssetID, Components, DamageModifiers, Items, Drops

            // Optional image in the first cell (Jötunn may add later)
            string? relSrc = td[0].QuerySelector("img")?.GetAttribute("src");
            string? imgAbs = ResolveUrl(doc.Url, relSrc);

            string name = T(td[0]);
            string asset = T(td[1]);

            // Components / DamageMods / Items / Drops are usually <ul><li>…</li></ul>.
            // If not, fallback to line-splitting.
            List<string> Components = Li(td[2]);
            if (Components.Count == 0)
                Components = T(td[2]).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            List<string> DamageMods = Li(td[3]);
            if (DamageMods.Count == 0)
                DamageMods = T(td[3]).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            List<string> Items = Li(td[4]);
            if (Items.Count == 0)
                Items = T(td[4]).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            List<string> Drops = Li(td[5]);
            if (Drops.Count == 0)
                Drops = T(td[5]).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            list.Add(new JCharacter(name, asset, Components, DamageMods, Items, Drops, imgAbs));
        }

        return list;
    }


    private static string NormalizeRequirements(string html)
    {
        IBrowsingContext ctx = BrowsingContext.New(Configuration.Default);
        IDocument doc = ctx.OpenAsync(r => r.Content(html)).Result;

        List<(string Level, List<string> Items)> groups = new List<(string Level, List<string> Items)>();
        (string Level, List<string> Items) current = (Level: "Level 1", Items: new List<string>());
        string? lastHeader = null;

        void StartLevel(string header)
        {
            header = header.Trim().Replace('\u00A0', ' ');
            if (!header.EndsWith(":")) header += ":";

            // avoid double “Level N” headers seen twice (e.g., <strong> + surrounding text)
            string bare = header[..^1];
            if (string.Equals(bare, lastHeader, StringComparison.OrdinalIgnoreCase))
                return;

            CommitIfAny();
            current = (Level: bare, Items: new List<string>());
            lastHeader = bare;
        }

        Walk(doc.Body ?? doc.DocumentElement);

        // Finish the last group
        if (current.Items.Count > 0 || groups.Count == 0)
            groups.Add(current);

        // Build Markdown
        StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < groups.Count; i++)
        {
            (string lvl, List<string> items) = groups[i];

            sb.Append("**").Append(lvl).Append(":**\n");
            foreach (string it in items)
                sb.Append("• ").Append(it).Append('\n');

            if (i < groups.Count - 1) sb.Append('\n');
        }

        string result = sb.ToString().TrimEnd();
        return string.IsNullOrEmpty(result) ? "(no data)" : result;

        void CommitIfAny()
        {
            if (current.Items.Count > 0) groups.Add(current);
        }

        void Walk(INode n)
        {
            switch (n)
            {
                case IHtmlListItemElement li:
                {
                    string t = li.Text().Trim().Replace('\u00A0', ' ');
                    if (!string.IsNullOrWhiteSpace(t))
                        current.Items.Add(t);
                    return;
                }

                case IText txt:
                {
                    IElement? parentEl = txt.ParentElement;
                    if (parentEl != null && string.Equals(parentEl.TagName, "LI", StringComparison.OrdinalIgnoreCase))
                        return;

                    string t = txt.Text.Trim().Replace('\u00A0', ' ');
                    if (string.IsNullOrWhiteSpace(t)) break;

                    if (t.StartsWith("Level ", StringComparison.OrdinalIgnoreCase))
                    {
                        // Normalize header like "Level 1"
                        string firstLine = t.Split('\n')[0].Trim().TrimEnd(':');
                        StartLevel(firstLine);
                    }
                    else
                    {
                        // Sometimes requirements are plain lines outside lists
                        current.Items.Add(t);
                    }

                    break;
                }
            }

            foreach (INode c in n.ChildNodes)
                Walk(c);
        }
    }


    private sealed class Snapshot
    {
        public List<JItem> Items { get; set; } = new();
        public List<JRecipe> Recipes { get; set; } = new();
        public List<JPrefab> Prefabs { get; set; } = new();
        public List<JPiece> Pieces { get; set; } = new();
        public List<JCharacter> Characters { get; set; } = new();
        public bool HasData => Items.Count + Recipes.Count + Prefabs.Count > 0;
    }
}

public record JItem(string DisplayCell, string AssetId, string Token, string EnglishName, string Type, string Description, string? ImageUrl);

public record JRecipe(string RecipeName, string AssetId, string ResultName, int Amount, string RequirementsText);

public record JPrefab(string Name, string AssetId, string Token, string EnglishName, List<string> Components, List<string> ChildComponents);

public record JPiece(string Name, string AssetId, string Token, string EnglishName, string? Description, List<string> ResourcesRequired, string? MaterialType, string? ImageUrl);

public record JCharacter(string Name, string AssetId, List<string> Components, List<string> DamageModifiers, List<string> Items, List<string> Drops, string? ImageUrl);