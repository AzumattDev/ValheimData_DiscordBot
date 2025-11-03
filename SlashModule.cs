using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace ValheimData_DiscordBot;

public abstract class AppModuleBase(Chunking chunk) : InteractionModuleBase<SocketInteractionContext>
{
    protected Chunking Chunk { get; } = chunk;

    protected async Task DeferIfNeeded(bool ephemeral = false)
    {
        if (!Context.Interaction.HasResponded)
            await DeferAsync(ephemeral: ephemeral);
    }

    protected async Task SendEmbedsPagedAsync(IEnumerable<Embed> embeds)
    {
        // First page as original response, rest as followups
        using IEnumerator<Embed> e = embeds.GetEnumerator();
        if (!e.MoveNext())
        {
            await FollowupAsync("No results.");
            return;
        }

        if (Context.Interaction.HasResponded)
            await FollowupAsync(embed: e.Current);
        else
            await RespondAsync(embed: e.Current);

        while (e.MoveNext())
            await FollowupAsync(embed: e.Current);
    }
}

public sealed class ValDocsSlash(Chunking chunk, JotunnCache cache) : AppModuleBase(chunk)
{
    [SlashCommand("diag_here", "Diagnose why slash commands may be hidden or failing in this channel.")]
    [EnabledInDm(false)]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    public async Task DiagHere(
        [Summary("user", "User to simulate (optional)")]
        IUser? who = null)
    {
        await DeferAsync(ephemeral: true);

        if (Context.Guild is null || Context.Channel is not SocketGuildChannel ch)
        {
            await FollowupAsync("Run this in a server text channel (not DM).", ephemeral: true);
            return;
        }

        var guild = Context.Guild;
        var me = await ((IGuild)guild).GetCurrentUserAsync();

        // Pick target = provided user or the invoker
        SocketGuildUser? target =
            who != null
                ? guild.GetUser(who.Id)
                : Context.User as SocketGuildUser;

        if (target is null)
        {
            await FollowupAsync("That user isn’t in this server.", ephemeral: true);
            return;
        }

        // Effective perms in this channel
        var userPerms = target.GetPermissions(ch);
        var botPerms = me.GetPermissions(ch);

        var lines = new List<string>();
        void Line(bool ok, string text) => lines.Add($"{(ok ? "✅" : "❌")} {text}");

        // Member-side gates (visibility/ability to invoke)
        Line(userPerms.UseApplicationCommands, $"**{target.DisplayName}** has **Use Application Commands** in this channel.");

        // Bot-side gates (ability to respond)
        Line(botPerms.ViewChannel, "Bot can **View Channel**.");
        Line(botPerms.SendMessages, "Bot can **Send Messages**.");
        Line(botPerms.EmbedLinks, "Bot can **Embed Links**.");
        Line(botPerms.AttachFiles, "Bot can **Attach Files**.");

        // Admin bypass note (for the simulated target)
        if (target.GuildPermissions.Administrator)
            lines.Add("ℹ️ Target is **Administrator** and bypasses channel denies. Regular members won’t.");

        // Surface channel overwrites explicitly denying Use Application Commands
        var denyTargets = new List<string>();
        try
        {
            foreach (var ow in ch.PermissionOverwrites)
            {
                var p = ow.Permissions;
                if (p.UseApplicationCommands == PermValue.Deny)
                {
                    bool applies = ow.TargetType switch
                    {
                        PermissionTarget.User => ow.TargetId == target.Id,
                        PermissionTarget.Role => target.Roles.Any(r => r.Id == ow.TargetId),
                        _ => false
                    };
                    if (!applies) continue;

                    string name = ow.TargetType == PermissionTarget.Role
                        ? (guild.GetRole(ow.TargetId)?.Name ?? $"Role {ow.TargetId}")
                        : (guild.GetUser(ow.TargetId)?.Username ?? $"User {ow.TargetId}");
                    denyTargets.Add(name);
                }
            }
        }
        catch
        {
            /* older libs may not expose UseApplicationCommands on overwrites */
        }

        if (denyTargets.Count > 0)
            lines.Add("⚠️ **Explicit denies for target:** " + string.Join(", ", denyTargets.Select(n => $"`{n}`")));

        // Integration permissions hint (server-wide command restrictions)
        lines.Add("ℹ️ Also check **Server Settings → Integrations → Your App → Command Permissions**. If restricted, only allowed roles/users can run slash commands.");

        var eb = new EmbedBuilder()
            .WithTitle($"Diagnostics for #{ch.Name} — {(who != null ? target.DisplayName : "you")}")
            .WithDescription(string.Join("\n", lines))
            .WithColor(lines.Any(l => l.StartsWith("❌")) || denyTargets.Count > 0 ? Color.DarkRed : Color.DarkGreen);

        await FollowupAsync(embed: eb.Build(), ephemeral: true);
    }


    [SlashCommand("val_item", "Search Valheim items (Jötunn data).")]
    public async Task ValItem(
        [Autocomplete(typeof(ItemAutocomplete))] [Summary("query", description: "Text to search (matches name, token, asset ID, or type).")]
        string query,
        [Summary("limit", description: "Maximum number of results to return (1–20).")]
        int limit = 10,
        [Summary("private", description: "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 20);
        await DeferIfNeeded(ephemeral);
        await cache.EnsureFreshAsync();

        List<JItem> list = cache.FindItems(query, limit).ToList();
        if (list.Count == 0)
        {
            await FollowupAsync($"No items matched “{query}”.");
            return;
        }

        bool anyImages = list.Any(x => !string.IsNullOrWhiteSpace(x.ImageUrl));

        if (anyImages)
        {
            List<Embed> embeds = new List<Embed>(list.Count);
            foreach (JItem it in list)
            {
                string desc =
                    $"**Prefab Name:** {it.DisplayCell}\n" +
                    $"**Type:** {it.Type}\n" +
                    $"**Token:** `{it.Token}`\n" +
                    $"**AssetID:** `{it.AssetId}`\n" +
                    $"{Trunc(it.Description, 900)}";

                EmbedBuilder? eb = new EmbedBuilder()
                    .WithTitle(it.EnglishName)
                    .WithDescription(desc)
                    .WithColor(Color.DarkGreen);

                if (!string.IsNullOrWhiteSpace(it.ImageUrl))
                    eb.WithThumbnailUrl(it.ImageUrl);

                eb.WithFooter("Source: Jötunn item list");

                embeds.Add(eb.Build());
            }

            await SendEmbedsPagedAsync(embeds);
            return;
        }

        // Fallback (no images found)
        IEnumerable<(string Name, string Value, bool Inline)> rows = list.Select(it => (
            Name: it.EnglishName,
            Value:
            $"**Type:** {it.Type}\n" +
            $"**Token:** `{it.Token}`\n" +
            $"**AssetID:** `{it.AssetId}`\n" +
            $"{Trunc(it.Description, 900)}",
            Inline: false
        ));

        string header = $"Matches for **{query}**  •  Source: Jötunn item list";
        IEnumerable<Embed> paged = Chunk.BuildPagedEmbeds("Valheim Items", header, rows, Color.DarkGreen);
        await SendEmbedsPagedAsync(paged);
    }

    [SlashCommand("val_prefab", "Search Valheim prefabs (name/token/asset).")]
    public async Task ValPrefab(
        [Autocomplete(typeof(PrefabAutocomplete))] [Summary("query", description: "Text to search (matches name, English name, token, or asset ID).")]
        string query,
        [Summary("limit", description: "Maximum number of results to return (1–20).")]
        int limit = 10,
        [Summary("private", description: "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 20);
        await DeferIfNeeded(ephemeral);
        await cache.EnsureFreshAsync();

        List<JPrefab> list = cache.FindPrefabs(query, limit).ToList();
        if (list.Count == 0)
        {
            await FollowupAsync($"No prefabs matched “{query}”.");
            return;
        }

        IEnumerable<(string Name, string Value, bool Inline)> rows = list.Select(p => (
            Name: p.Name,
            Value:
            $"**English:** {p.EnglishName}\n" +
            $"**Token:** `{p.Token}`  •  **AssetID:** `{p.AssetId}`\n" +
            (p.Components.Count == 0 ? "" : $"**Components:** {Trunc(Join(p.Components, 12), 900)}\n") +
            (p.ChildComponents.Count == 0 ? "" : $"**Children:** {Trunc(Join(p.ChildComponents, 12), 900)}"),
            Inline: false
        ));

        string header = $"Matches for **{query}**  •  Source: Jötunn prefab list";
        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds("Valheim Prefabs", header, rows, Color.DarkBlue);
        await SendEmbedsPagedAsync(embeds);
        return;

        string Join(IEnumerable<string> xs, int take = 10) => string.Join(", ", xs.Take(take)) + (xs.Count() > take ? " …" : "");
    }

    [SlashCommand("val_piece", "Search Valheim pieces (name/token/asset).")]
    public async Task ValPiece(
        [Autocomplete(typeof(PieceAutocomplete))] [Summary("query", description: "Text to search (matches name, English name, token, or asset ID).")]
        string query,
        [Summary("limit", description: "Maximum number of results to return (1–20).")]
        int limit = 10,
        [Summary("private", description: "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 20);
        await DeferIfNeeded(ephemeral);
        await cache.EnsureFreshAsync();

        List<JPiece> list = cache.FindPieces(query, limit).ToList();
        if (list.Count == 0)
        {
            await FollowupAsync($"No pieces matched “{query}”.");
            return;
        }

        List<Embed> embeds = new List<Embed>(list.Count);
        foreach (JPiece p in list)
        {
            string desc =
                $"**English:** {p.EnglishName}\n" +
                $"**Token:** `{p.Token}`  •  **AssetID:** `{p.AssetId}`\n" +
                (string.IsNullOrWhiteSpace(p.Description) ? "" : $"**Description:** {p.Description}\n") +
                (string.IsNullOrWhiteSpace(p.MaterialType) ? "" : $"**Material Type:** {p.MaterialType}\n") +
                (p.ResourcesRequired is { Count: > 0 }
                    ? $"**Resources Required:** {string.Join(", ", p.ResourcesRequired.Take(12))}{(p.ResourcesRequired.Count > 12 ? " …" : "")}"
                    : "");

            EmbedBuilder? eb = new EmbedBuilder()
                .WithTitle(p.Name)
                .WithDescription(desc.TrimEnd())
                .WithColor(Color.DarkBlue)
                .WithFooter("Source: Jötunn piece list");

            if (!string.IsNullOrWhiteSpace(p.ImageUrl))
                eb.WithThumbnailUrl(p.ImageUrl);

            embeds.Add(eb.Build());
        }

        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("val_character", "Search Valheim characters (name/asset/components).")]
    public async Task ValCharacter(
        [Autocomplete(typeof(CharacterAutocomplete))] [Summary("query", description: "Text to search (matches name, English name, token, or asset ID).\"")]
        string query,
        [Summary("limit", description: "Maximum number of results to return (1–20).")]
        int limit = 10,
        [Summary("private", description: "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 20);
        await DeferIfNeeded(ephemeral);
        await cache.EnsureFreshAsync();

        List<JCharacter> list = cache.FindCharacters(query, limit).ToList();
        if (list.Count == 0)
        {
            await FollowupAsync($"No characters matched “{query}”.");
            return;
        }

        List<Embed> embeds = new List<Embed>(list.Count);
        foreach (JCharacter c in list)
        {
            EmbedBuilder? eb = new EmbedBuilder()
                .WithTitle(c.Name)
                .WithColor(Color.DarkGrey)
                .WithFooter("Source: Jötunn character list")
                .WithDescription($"**AssetID:** `{c.AssetId}`");

            if (c.Components?.Count > 0)
                eb.AddField("Components", Join(c.Components), false);

            if (c.DamageModifiers?.Count > 0)
                eb.AddField("Damage Modifiers", string.Join(" • ", c.DamageModifiers.Take(12)) + (c.DamageModifiers.Count > 12 ? " …" : ""), false);

            if (c.Items?.Count > 0)
                eb.AddField("Items", string.Join("\n", c.Items.Take(12)) + (c.Items.Count > 12 ? "\n…" : ""), false);

            if (c.Drops?.Count > 0)
                eb.AddField("Drops", string.Join("\n", c.Drops.Take(12)) + (c.Drops.Count > 12 ? "\n…" : ""), false);

            if (!string.IsNullOrWhiteSpace(c.ImageUrl))
                eb.WithThumbnailUrl(c.ImageUrl);

            embeds.Add(eb.Build());
        }

        await SendEmbedsPagedAsync(embeds);
        return;

        string Join(IEnumerable<string> xs, int take = 12) => string.Join(", ", xs.Take(take)) + (xs.Count() > take ? " …" : "");
    }


    [SlashCommand("val_recipe", "Show how to craft an item or where an ingredient is used.")]
    public async Task ValRecipe(
        [Autocomplete(typeof(RecipeQueryAutocomplete))] [Summary("query", description: "Result name (or ingredient name when reverse = true).")]
        string query,
        [Summary("reverse", description: "If true, find recipes that use this as an ingredient.")]
        bool reverse = false,
        [Summary("Max_Results", description: "Max results (1-15)")]
        int limit = 10,
        [Summary("private", description: "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 15);
        await DeferIfNeeded(ephemeral);
        await cache.EnsureFreshAsync();

        IEnumerable<(string Name, string Value, bool Inline)> rows = (!reverse
                ? cache.FindRecipesFor(query, limit)
                : cache.FindRecipesByIngredient(query, limit))
            .Select(r => (
                Name: $"{r.ResultName} ×{r.Amount}  `({r.RecipeName})`",
                Value: Trunc(r.RequirementsText, 1024),
                Inline: false
            ));

        string title = reverse ? $"Recipes that use {query}" : $"Recipe(s) for {query}";
        const string header = "Source: Jötunn recipe list";
        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds(title, header, rows, Color.Teal);
        await SendEmbedsPagedAsync(embeds);
    }

    // Admin convenience: refresh right now (bypasses the 24h window)
    [SlashCommand("val_refresh", "Force-refresh the cached Jötunn data (admin).")]
    public async Task ValRefresh([Summary("private", description: "Only you can see the response")] bool ephemeral = true)
    {
        await DeferIfNeeded(ephemeral);
        // Force refresh by temporarily pretending we're expired
        await cache.EnsureFreshAsync(); // first ensure (in case empty)
        await cache.EnsureFreshAsync();
        await FollowupAsync("Valheim docs cache refreshed.");
    }

    private static string Trunc(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}