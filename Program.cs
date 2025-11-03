using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ValheimData_DiscordBot;

internal class Program
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IConfiguration _config;
    private readonly IServiceProvider _services;

    static Task Main(string[] args) => new Program().MainAsync();

    public Program()
    {
        DiscordSocketConfig socketCfg = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages,
            AlwaysDownloadUsers = false,
            LogGatewayIntentWarnings = false
        };

        _client = new DiscordSocketClient(socketCfg);
        _config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: false).Build();

        _services = new ServiceCollection()
            .AddSingleton(_client)
            .AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>()))
            .AddSingleton<Chunking>()
            .AddSingleton(new JotunnCache(new JotunnCache.Options
            {
                // I don't plan on making dynamic shit for this or using a lot of pages, so this works for now.
                ItemsUrl = "https://valheim-modding.github.io/Jotunn/data/objects/item-list.html",
                RecipesUrl = "https://valheim-modding.github.io/Jotunn/data/objects/recipe-list.html",
                PrefabsUrl = "https://valheim-modding.github.io/Jotunn/data/prefabs/prefab-list.html",
                PieceUrl = "https://valheim-modding.github.io/Jotunn/data/pieces/piece-list.html",
                CharactersUrl = "https://valheim-modding.github.io/Jotunn/data/prefabs/character-list.html",
                Expiry = TimeSpan.FromDays(1)
            }))
            .BuildServiceProvider();

        _interactions = _services.GetRequiredService<InteractionService>();

        _services.GetRequiredService<JotunnCache>().Start();
    }

    public async Task MainAsync()
    {
        _client.Log += msg =>
        {
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        };
        _interactions.Log += msg =>
        {
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        };

        _client.Ready += OnReadyAsync;
        _client.InteractionCreated += HandleInteractionAsync;

        string? token = _config["BotToken"];
        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        // Add modules containing slash commands
        await _interactions.AddModulesAsync(Assembly.GetEntryAssembly(), _services);

        await Task.Delay(-1);
    }

    private async Task OnReadyAsync()
    {
        Console.WriteLine($"{_client.CurrentUser} is connected!");

        // Register commands: prefer testing in a guild (fast) then switch to global.
        if (ulong.TryParse(_config["DevGuildId"], out ulong guildId) && guildId != 0)
        {
            await _interactions.RegisterCommandsToGuildAsync(guildId, true);
            Console.WriteLine($"Registered slash commands to guild {guildId}");
        }
        else
        {
            await _interactions.RegisterCommandsGloballyAsync(true);
            Console.WriteLine("Registered slash commands globally (may take up to an hour to appear).");
        }
    }

    private async Task HandleInteractionAsync(SocketInteraction raw)
    {
        try
        {
            SocketInteractionContext ctx = new SocketInteractionContext(_client, raw);
            await _interactions.ExecuteCommandAsync(ctx, _services);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            if (raw.Type == InteractionType.ApplicationCommand)
            {
                try
                {
                    await raw.GetOriginalResponseAsync();
                }
                catch
                {
                    /* ignore */
                }
            }
        }
    }
}