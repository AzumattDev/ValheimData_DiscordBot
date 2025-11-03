using Discord;

namespace ValheimData_DiscordBot;

public static class DiscordLimits
{
    public const int MaxMessage = 2000;
    public const int MaxEmbedTotal = 6000;
    public const int MaxEmbedTitle = 256;
    public const int MaxEmbedDescription = 4096;
    public const int MaxEmbedFields = 25;
    public const int MaxFieldName = 256;
    public const int MaxFieldValue = 1024;
}

public class Chunking
{
    public IReadOnlyList<string> ChunkText(string text, int limit = DiscordLimits.MaxMessage)
    {
        if (string.IsNullOrEmpty(text)) return new List<string> { "" };
        List<string> parts = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            int take = Math.Min(limit, text.Length - i);
            int lastNl = text.LastIndexOf('\n', i + take - 1, take);
            if (lastNl > i && (i + take - lastNl) < 100) take = (lastNl - i) + 1;
            parts.Add(text.Substring(i, take));
            i += take;
        }

        return parts;
    }

    private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…");

    /// <summary>Truncate a field value to be safely &lt;= 1024 (default budget a bit under, to allow markup).</summary>
    public static string TruncField(string text, int budget = 1000)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Length <= budget) return text;

        const string fence = "```";
        string trimmed = text[..Math.Max(0, budget - 5)];
        // If opened a code fence in the excerpt, try to close it for neatness.
        bool hasFence = trimmed.Contains(fence);
        return hasFence ? trimmed + "\n```…```" : trimmed + " …";
    }

    /// <summary>
    /// Builds paged embeds while enforcing: total ≤ 6000, ≤ 25 fields, field name/value limits, title/desc limits.
    /// Starts a new embed whenever adding the next field would exceed any limit.
    /// </summary>
    public IEnumerable<Embed> BuildPagedEmbeds(string title, string header, IEnumerable<(string Name, string Value, bool Inline)> fields, Color color)
    {
        title = Trunc(title, DiscordLimits.MaxEmbedTitle);
        header = Trunc(header, DiscordLimits.MaxEmbedDescription);

        EmbedBuilder current = NewPage(title, header, color);
        int currentLen = (current.Title?.Length ?? 0) + (current.Description?.Length ?? 0);
        int fieldCount = 0;

        foreach ((var Name, var Value, bool Inline) in fields)
        {
            string safeName = Trunc(Name ?? "", DiscordLimits.MaxFieldName);
            string safeValue = Trunc(Value ?? "", DiscordLimits.MaxFieldValue);

            // Predict new totals
            int predictedLen = currentLen + safeName.Length + safeValue.Length;
            bool tooManyFields = fieldCount >= DiscordLimits.MaxEmbedFields;
            bool tooLargeTotal = predictedLen >= DiscordLimits.MaxEmbedTotal;

            if (tooManyFields || tooLargeTotal)
            {
                // yield current and start a new page
                yield return current.Build();
                current = NewPage(title, header, color);
                currentLen = (current.Title?.Length ?? 0) + (current.Description?.Length ?? 0);
                fieldCount = 0;
            }

            current.AddField(safeName, safeValue, Inline);
            currentLen += safeName.Length + safeValue.Length;
            fieldCount++;
        }

        yield return current.Build();
        yield break;

        static EmbedBuilder NewPage(string t, string d, Color c) => new EmbedBuilder().WithTitle(t).WithDescription(d).WithColor(c);
    }

    public IEnumerable<Embed> BuildDescriptionEmbeds(string title, string longText, Color color)
    {
        title = Trunc(title, DiscordLimits.MaxEmbedTitle);
        foreach (string chunk in ChunkText(longText, DiscordLimits.MaxEmbedDescription))
        {
            yield return new EmbedBuilder().WithTitle(title).WithDescription(chunk).WithColor(color).Build();
        }
    }

    public IEnumerable<Embed> BuildDescriptionEmbedsWithHeader(string title, string header, string body, Color color)
    {
        title = Trunc(title, DiscordLimits.MaxEmbedTitle);
        header = Trunc(header, DiscordLimits.MaxEmbedDescription);

        string glue = string.IsNullOrWhiteSpace(header) ? body : header + "\n\n" + body;
        foreach (string chunk in ChunkText(glue, DiscordLimits.MaxEmbedDescription))
        {
            yield return new EmbedBuilder().WithTitle(title).WithDescription(chunk).WithColor(color).Build();
        }
    }
}