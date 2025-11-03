using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;

namespace ValheimData_DiscordBot;

static class AutoUtil
{
    public static string Needle(IAutocompleteInteraction i) => i?.Data?.Current?.Value?.ToString() ?? string.Empty;

    public static string Opt(IAutocompleteInteraction i, string name) =>
        i?.Data?.Options?.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Value?.ToString() ?? string.Empty;
}

public sealed class ItemAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext ctx, IAutocompleteInteraction i, IParameterInfo p, IServiceProvider services)
    {
        try
        {
            JotunnCache cache = services.GetRequiredService<JotunnCache>();
            string needle = AutoUtil.Needle(i);
            IEnumerable<AutocompleteResult> results = cache.SuggestItems(needle, 25).Select(s => new AutocompleteResult(s, s));
            return Task.FromResult(AutocompletionResult.FromSuccess(results));
        }
        catch
        {
            return Task.FromResult(AutocompletionResult.FromSuccess([]));
        }
    }
}

public sealed class PrefabAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext ctx, IAutocompleteInteraction i, IParameterInfo p, IServiceProvider services)
    {
        try
        {
            JotunnCache cache = services.GetRequiredService<JotunnCache>();
            string needle = AutoUtil.Needle(i);
            IEnumerable<AutocompleteResult> results = cache.SuggestPrefabs(needle, 25).Select(s => new AutocompleteResult(s, s));
            return Task.FromResult(AutocompletionResult.FromSuccess(results));
        }
        catch
        {
            return Task.FromResult(AutocompletionResult.FromSuccess([]));
        }
    }
}

public sealed class PieceAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext ctx, IAutocompleteInteraction i, IParameterInfo p, IServiceProvider services)
    {
        try
        {
            JotunnCache cache = services.GetRequiredService<JotunnCache>();
            string needle = AutoUtil.Needle(i);
            IEnumerable<AutocompleteResult> results = cache.SuggestPieces(needle, 25).Select(s => new AutocompleteResult(s, s));
            return Task.FromResult(AutocompletionResult.FromSuccess(results));
        }
        catch
        {
            return Task.FromResult(AutocompletionResult.FromSuccess([]));
        }
    }
}

public sealed class CharacterAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext ctx, IAutocompleteInteraction i, IParameterInfo p, IServiceProvider services)
    {
        try
        {
            JotunnCache cache = services.GetRequiredService<JotunnCache>();
            string needle = AutoUtil.Needle(i);
            IEnumerable<AutocompleteResult> results = cache.SuggestCharacters(needle, 25).Select(s => new AutocompleteResult(s, s));
            return Task.FromResult(AutocompletionResult.FromSuccess(results));
        }
        catch
        {
            return Task.FromResult(AutocompletionResult.FromSuccess([]));
        }
    }
}

public sealed class RecipeQueryAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext ctx, IAutocompleteInteraction i, IParameterInfo p, IServiceProvider services)
    {
        try
        {
            JotunnCache cache = services.GetRequiredService<JotunnCache>();
            string needle = AutoUtil.Needle(i);
            bool reverse = bool.TryParse(AutoUtil.Opt(i, "reverse"), out bool b) && b;

            IEnumerable<string> src = reverse
                ? cache.SuggestRecipeIngredients(needle, 25)
                : cache.SuggestRecipeResults(needle, 25);

            IEnumerable<AutocompleteResult> results = src.Select(s => new AutocompleteResult(s, s));
            return Task.FromResult(AutocompletionResult.FromSuccess(results));
        }
        catch
        {
            return Task.FromResult(AutocompletionResult.FromSuccess([]));
        }
    }
}