﻿using Discord;
using Discord.Addons.Hosting;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OriBot.Utility;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace OriBot.Services;

public partial class MessageHandler : DiscordClientService
{
    [GeneratedRegex(@"<@(\d+)>")]
    private static partial Regex MentionRegex();

    [GeneratedRegex(@"hi ori", RegexOptions.IgnoreCase)]
    private static partial Regex GreetingRegex();

    private readonly IServiceProvider _provider;
    private readonly CommandService _commandService;
    private readonly ExceptionReporter _exceptionReporter;
    private readonly VolatileData _volatileData;
    private readonly Globals _globals;
    private readonly MessageUtilities _messageUtilities;
    private readonly IDbContextFactory<SpiritContext> _dbContextFactory;
    private readonly BotOptions _botOptions;

    public MessageHandler(DiscordSocketClient client, ILogger<DiscordClientService> logger, IServiceProvider provider, CommandService commandService,
         ExceptionReporter exceptionReporter, VolatileData volatileData, Globals globals, MessageUtilities messageUtilities,
         IDbContextFactory<SpiritContext> dbContextFactory, IOptions<BotOptions> options)
        : base(client, logger)
    {
        _provider = provider;
        _commandService = commandService;
        _exceptionReporter = exceptionReporter;
        _volatileData = volatileData;
        _globals = globals;
        _messageUtilities = messageUtilities;
        _dbContextFactory = dbContextFactory;
        _botOptions = options.Value;

        commandService.CommandExecuted += OnCommandExecuted;

        Client.MessageDeleted += OnMessageDeleted;
        Client.MessageUpdated += OnMessageUpdated;
        Client.MessageReceived += OnMessageReceived;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _commandService.AddModulesAsync(Assembly.GetEntryAssembly(), _provider);
    }
    private Task OnCommandExecuted(Optional<CommandInfo> info, ICommandContext context, IResult result)
    {
        Task.Run(async () =>
        {
            if (result.IsSuccess)
                return;
            if (result.Error == CommandError.Exception)
            {
                if (result is ExecuteResult executeResult)
                {
                    var exceptionContext = new ExceptionContext(context.Message);
                    await _exceptionReporter.NotifyExceptionAsync(executeResult.Exception, exceptionContext, "Exception while executing a text command", true);
                }
            }
            else
            {
                if (result.ErrorReason == "Unknown command.")
                    return;
                await context.Channel.SendMessageAsync(result.ErrorReason);
            }
        }).ContinueWith(async t =>
        {
            var exceptionContext = new ExceptionContext(context.Message);
            await _exceptionReporter.NotifyExceptionAsync(t.Exception!.InnerException!, exceptionContext, "Exception while executing command executed event", true);
        }, TaskContinuationOptions.OnlyOnFaulted);
        return Task.CompletedTask;
    }
    private Task OnMessageDeleted(Cacheable<IMessage, ulong> deletedMessage, Cacheable<IMessageChannel, ulong> channel)
    {
        Task.Run(async () =>
        {
            if (_volatileData.IgnoredDeletedMessagesIds.TryRemove(deletedMessage.Id)) return;
            if (channel.Value is not SocketGuildChannel) return;

            if (deletedMessage.Value is IUserMessage message)
            {
                EmbedBuilder embedBuilder = Utilities.QuoteUserMessage("Message deleted", message, ColorConstants.SpiritRed,
                    includeOriginChannel: true, includeDirectUserLink: true, includeMessageReference: true);

                await _messageUtilities.SendMessageWithFiles(_globals.NotesChannel, embedBuilder, message);

                if (channel.Id == _botOptions.ArtChannelId)
                {
                    using var db = _dbContextFactory.CreateDbContext();
                    Utilities.RemoveBadgeFromUser(db, message.Author, DbBadges.Creative);
                    db.SaveChanges();
                }
            }
            else if (deletedMessage.Value is not ISystemMessage)
            {
                DateTimeOffset deleteDate = SnowflakeUtils.FromSnowflake(deletedMessage.Id);

                EmbedBuilder embedBuilder = new EmbedBuilder()
                    .WithColor(ColorConstants.SpiritRed)
                    .WithTitle("Message deleted")
                    .AddField("Message", "Message was not in cache")
                    .AddField("Message was created on", Utilities.FullDateTimeStamp(deleteDate), true)
                    .AddField("Channel", $"<#{channel.Id}>", true);

                await _globals.NotesChannel.SendMessageAsync(embed: embedBuilder.Build());
            }

        }).ContinueWith(async t =>
        {
            var exceptionContext = new ExceptionContext(channel.Value);
            await _exceptionReporter.NotifyExceptionAsync(t.Exception!.InnerException!, exceptionContext, "Exception while executing message deleted event", false);
        }, TaskContinuationOptions.OnlyOnFaulted);
        return Task.CompletedTask;
    }
    private Task OnMessageUpdated(Cacheable<IMessage, ulong> oldMessage, SocketMessage newMessage, ISocketMessageChannel channel)
    {
        Task.Run(async () =>
        {
            if (newMessage is not SocketUserMessage message) return;
            if (message.Source != MessageSource.User) return;

            var context = new SocketCommandContext(Client, message);
            if (context.Channel.GetChannelType() == ChannelType.DM)
            {
                // DM stuff
            }
            else //all other channels
            {
                // art channel check
                if (context.Channel.Id == _botOptions.ArtChannelId)
                {
                    await ArtChannelCheckAsync(context);
                }
            }

        }).ContinueWith(async t =>
        {
            var exceptionContext = new ExceptionContext(newMessage);
            await _exceptionReporter.NotifyExceptionAsync(t.Exception!.InnerException!, exceptionContext, "Exception while executing message updated event", false);
        }, TaskContinuationOptions.OnlyOnFaulted);
        return Task.CompletedTask;
    }
    private Task OnMessageReceived(SocketMessage arg)
    {
        Task.Run(async () =>
        {
            if (arg is not SocketUserMessage message) return;
            if (message.Source != MessageSource.User) return;

            var context = new SocketCommandContext(Client, message);
            if (context.Channel.GetChannelType() == ChannelType.DM)
            {
                // handle DMs
            }
            else //all other channels (in this case guild text channels really)
            {
                if (_volatileData.TicketThreads.TryGetValue(message.Channel.Id, out ulong threadUserId))
                {
                    MatchCollection matches = MentionRegex().Matches(message.Content);

                    if (matches.Count > 0)
                    {
                        var thread = (IThreadChannel)context.Channel;
                        foreach (Match match in matches)
                        {
                            IGuildUser user = await thread.Guild.GetUserAsync(ulong.Parse(match.Groups[1].Value));
                            if (!(user.RoleIds.Contains(_globals.ModRole.Id) || user.Id == Client.CurrentUser.Id || user.Id == threadUserId))
                                await thread.RemoveUserAsync(user);
                        }
                    }
                }

                int argPos = 0;
                // art channel check
                if (context.Channel.Id == _botOptions.ArtChannelId)
                {
                    bool valid = await ArtChannelCheckAsync(context);
                    if (valid)
                    {
                        await message.AddReactionAsync(Emotes.Pin);

                        using var db = _dbContextFactory.CreateDbContext();
                        Utilities.AddBadgeToUser(db, message.Author, DbBadges.Creative);
                        db.SaveChanges();
                    }
                }
                // command handling
                else if (message.HasStringPrefix(_botOptions.Prefix, ref argPos))
                {
                    await _commandService.ExecuteAsync(context, argPos, _provider);
                }
                // respond to a predefined message
                else if (message.Content.StartsWith('/'))
                {
                    await context.Message.ReplyAsync("oh! to use slash commands make sure to click on the option!");
                }
                // check for responses and that in the commands channel
                else if (context.Channel.Id == _globals.CommandsChannel.Id)
                {
                    await CommandsChannelMessage(context);
                }
                else if (message.Content.Contains($"<@&{_botOptions.AnyModRoleID}>"))
                {
                    await AnyModPingHandler(context);
                }

            }
        }).ContinueWith(async t =>
        {
            var exceptionContext = new ExceptionContext(arg);
            await _exceptionReporter.NotifyExceptionAsync(t.Exception!.InnerException!, exceptionContext, "Exception while executing message received event", false);
        }, TaskContinuationOptions.OnlyOnFaulted);
        return Task.CompletedTask;
    }
    /********************************************
        MESSAGE OPERATIONS
    ********************************************/
    private async Task<bool> ArtChannelCheckAsync(SocketCommandContext context)
    {
        if (context.Message.Attachments.Count == 0)
        {
            string[] allowedSites = File.ReadAllLines(Utilities.GetLocalFilePath("AllowedSites.txt"));
            if (!Array.Exists(allowedSites, context.Message.Content.Contains))
            {
                string errorMessage = $"Hello there!, the message you sent in <#{context.Channel.Id}> does not contain a valid message that I recognize as art\n" +
                    $"if you think this is an error or want to suggest more sites to accept feel free to send a /ticket in {context.Guild.Name}";
                await MessageDeleterHandlerAsync(context, errorMessage);
                return false;
            }
        }
        else
        {
            var attachment = context.Message.Attachments.First(); // we really only have to check the first because all other situations would be valid anyways
            if (attachment.Width < 40 || attachment.Height < 40)
            {
                string errorMessage = $"Hello there!, the message you sent in <#{context.Channel.Id}> does not contain a valid message that I recognize as art\n" +
                    $"The image sent is too small\n" +
                    $"if you think this is an error feel free to send a /ticket in {context.Guild.Name}";
                await MessageDeleterHandlerAsync(context, errorMessage);
                return false;
            }
        }
        return true;
    }
    private async Task CommandsChannelMessage(SocketCommandContext context)
    {
        if (GreetingRegex().IsMatch(context.Message.Content))
        {
            await context.Message.ReplyAsync("Hello there! I hope you have a great day " + Emotes.OriHeart);
        }
    }

    private async Task AnyModPingHandler(SocketCommandContext context)
    {
        var guild = context.Guild;
        var channel = context.Channel;
        var moderators = guild.Users
                .Where(x =>
                x.GuildPermissions.BanMembers &&
                x.Status != Discord.UserStatus.Offline &&
                !x.IsBot
                )
                .ToList();
        if (!moderators.Any())
        {
            await channel.SendMessageAsync("<@&" + _botOptions.ModRoleId + ">, I have pinged all moderators for you.");
        }
        else
        {
            var moderator = moderators[(int)Math.Round(Random.Shared.NextDouble() * (moderators.Count - 1))];
            await channel.SendMessageAsync("<@" + moderator.Id + ">, Will be here to assist you.");
        }
    }
    /********************************************
        HELPER METHODS
    ********************************************/
    private async Task MessageDeleterHandlerAsync(SocketCommandContext context, string dmMessage)
    {
        var embedBuilder = Utilities.QuoteUserMessage("Message autodeleted", context.Message, ColorConstants.SpiritRed,
            includeOriginChannel: true, includeDirectUserLink: true, includeMessageReference: true);

        _volatileData.IgnoredDeletedMessagesIds.Add(context.Message.Id);
        await context.Message.DeleteAsync();

        await _messageUtilities.TrySendDmAsync(context.User, dmMessage, embedBuilder);

        await _messageUtilities.SendMessageWithFiles(_globals.AutosChannel, embedBuilder, context.Message);
    }
}
