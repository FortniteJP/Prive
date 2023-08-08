using Discord;
using Discord.Rest;

namespace Prive.Server.Http;

public class DiscordBot {
    public const ulong GuildId = 1008378981109731388;
    public const ulong ChannelId = 1138395634974588938;

    public RestUserMessage EmbedMessage { get; set; } = default!;
    public readonly string TokenLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Server/discordtoken.txt");

    private DiscordRestClient Bot { get; }

    public DiscordBot() {
        Bot = new();
        Bot.LoggedIn += OnLogin;
    }

    public async Task StartAsync(string? token = null) {
        await Bot.LoginAsync(TokenType.Bot, Environment.GetEnvironmentVariable("DISCORD_TOKEN") ?? token ?? await File.ReadAllTextAsync(TokenLocation) ?? throw new NullReferenceException("DISCORD_TOKEN is not set"));
        await Initialize();
    }

    public async Task StopAsync() {
        await Bot.LogoutAsync();
    }

    public async Task UpdateEmbedAsync(MatchMakingManager manager) {
        await EmbedMessage.ModifyAsync(x => {
            var isDefaultSolo = manager.PlaylistId == "Playlist_DefaultSolo";
            x.Embeds.Value[isDefaultSolo ? 0 : 1] = new EmbedBuilder() {
                Color = Color.Purple,
                Author = new() {
                    Name = isDefaultSolo ? "Default Solo" : "LateGame Solo"
                },
                Title = isDefaultSolo ? "ソロ" : "レイトゲーム ソロ",
                Fields = new() {
                    new() {
                        Name = "ステータス",
                        Value = manager.IsListening ? $"マッチ中" : "マッチメイキング中",
                        IsInline = true
                    },
                    new() {
                        Name = "プレイ人数",
                        Value = manager.Communicator.GetPlayersLeft().ToString(),
                        IsInline = true
                    },
                    new() {
                        Name = "待機人数",
                        Value = manager.Clients.Count.ToString(),
                        IsInline = true
                    },
                }
            }.Build();
        });
    }

    private Task OnLogin() {
        Console.WriteLine("Discord bot is ready");
        return Task.CompletedTask;
    }

    private async Task Initialize() {
        var guild = await Bot.GetGuildAsync(GuildId);
        var channel = await guild.GetTextChannelAsync(ChannelId);
        var messages = (await channel.GetMessagesAsync().ToListAsync()).First();

        if (messages.Count <= 0) {
            Console.WriteLine("No messages found, creating new one");
            EmbedMessage = await channel.SendMessageAsync(embeds: new[] {
                new EmbedBuilder() {
                    Color = Color.Purple,
                    Author = new() {
                        Name = "PlaylistNameEN"
                    },
                    Title = "PlaylistNameJP",
                    Fields = new() {
                        new() {
                            Name = "ステータス",
                            Value = "N/A",
                            IsInline = true
                        },
                        new() {
                            Name = "プレイ人数",
                            Value = "N/A",
                            IsInline = true
                        },
                        new() {
                            Name = "待機人数",
                            Value = "N/A",
                            IsInline = true
                        },
                    }
                }.Build(),
                new EmbedBuilder() {
                    Color = Color.Purple,
                    Author = new() {
                        Name = "PlaylistNameEN"
                    },
                    Title = "PlaylistNameJP",
                    Fields = new() {
                        new() {
                            Name = "ステータス",
                            Value = "N/A",
                            IsInline = true
                        },
                        new() {
                            Name = "プレイ人数",
                            Value = "N/A",
                            IsInline = true
                        },
                        new() {
                            Name = "待機人数",
                            Value = "N/A",
                            IsInline = true
                        },
                    }
                }.Build()
            });
        }
        EmbedMessage ??= (RestUserMessage)messages.First(x => x.Author.Id == Bot.CurrentUser.Id);
        Console.WriteLine($"Message Id: {EmbedMessage.Id}");
    }
}