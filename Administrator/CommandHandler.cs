﻿using Administrator.Common;
using Administrator.Extensions;
using Administrator.Services;
using Administrator.Services.Database;
using Administrator.Services.Database.Models;
using Discord.Commands;
using Discord.WebSocket;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Administrator
{
    public class CommandHandler
    {
        private static readonly Config Config = BotConfig.New();
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private readonly DiscordSocketClient _client;
        private readonly CommandService _commands;
        private readonly DbService _db;
        private readonly StatsService _stats;
        private readonly CrosstalkService _crosstalk;
        private readonly IServiceProvider _services;
        private readonly Dictionary<ValueTuple<SocketTextChannel, SocketTextChannel>, DateTimeOffset> _callMessages;

        public CommandHandler(IServiceProvider services)
        {
            _client = services.GetService(typeof(DiscordSocketClient)) as DiscordSocketClient;
            _commands = services.GetService(typeof(CommandService)) as CommandService;
            _db = services.GetService(typeof(DbService)) as DbService;
            _stats = services.GetService(typeof(StatsService)) as StatsService;
            _crosstalk = services.GetService(typeof(CrosstalkService)) as CrosstalkService;
            _services = services;
            _callMessages = new Dictionary<(SocketTextChannel, SocketTextChannel), DateTimeOffset>();
            if (_client != null) _client.MessageReceived += HandleAsync;
        }

        private async Task HandleAsync(SocketMessage message)
        {
            var watch = Stopwatch.StartNew();
            _stats.MessagesReceived++;
            const string pattern =
                @"discord(?:\.com|\.gg)[\/invite\/]?(?:(?!.*[Ii10OolL]).[a-zA-Z0-9]{5,6}|[a-zA-Z0-9\-]{2,32})";

            if (!(message is SocketUserMessage msg)
                || msg.Author.IsBot
                || msg.Author.Equals(_client.CurrentUser)) return;

            var blws = await _db.GetAsync<BlacklistedWord>().ConfigureAwait(false);

            if (msg.MentionedUsers.Count == 0
                && msg.Channel is SocketGuildChannel c
                && blws.Any(x => x.GuildId == (long) c.Guild.Id && msg.Content.ToLower().Contains(x.Word.ToLower())))
            {
                var gc = await _db.GetOrCreateGuildConfigAsync(c.Guild).ConfigureAwait(false);
                if (msg.Author is SocketGuildUser u && u.Roles.All(x => x.Id != (ulong) gc.PermRole))
                {
                    try
                    {
                        await msg.DeleteAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignored
                    }
                    return;
                }
            }

            var argPos = 0;
            if (!msg.HasStringPrefix(Config.BotPrefix, ref argPos))
            {
                _ = Task.Run(async () =>
                {
                    if (!(msg.Channel is SocketGuildChannel channel)) return;
                    var gc = await _db.GetOrCreateGuildConfigAsync(channel.Guild).ConfigureAwait(false);

                    // check for invites to filter
                    try
                    {
                        var invites = await channel.Guild.GetInvitesAsync().ConfigureAwait(false);
                        if (gc.InviteFiltering && Regex.IsMatch(msg.Content, pattern) &&
                            !invites.Any(i => msg.Content.Contains(i.Code)))
                        {
                            await msg.DeleteAsync().ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // ignored
                    }

                    // check for active calls
                    if (!string.IsNullOrWhiteSpace(msg.Content)
                        && msg.Channel is SocketTextChannel ch &&
                        _crosstalk.Calls.FirstOrDefault(x => x.Channel1.Id == ch.Id || x.Channel2.Id == ch.Id && x.IsConnected) is
                            CrosstalkCall call)
                    {
                        if (call.Channel1.Id == channel.Id)
                        {
                            /*
                            var chnl2Messages = await (call.Channel2 as IMessageChannel)
                                .GetMessagesAsync(20, CacheMode.CacheOnly).FlattenAsync().ConfigureAwait(false);
                            var msgs2 = chnl2Messages.Where(x => x.Content.Equals(
                                    $"{Emote.Parse("<a:typing:447092767428968458>")} **{msg.Author.Username.SanitizeMentions()}** is typing...")
                                    && x.Author.Id == _client.CurrentUser.Id)
                                    .ToList();

                            if (msgs2.Any())
                            {
                                foreach (var m in msgs2)
                                {
                                    try
                                    {
                                        await m.DeleteAsync();
                                    }
                                    catch
                                    {
                                        // ignored
                                    }
                                }
                            }
                            */
                            await call.Channel2
                                .SendMessageAsync($"**{msg.Author.Username.SanitizeMentions()}**: {msg.Content.SanitizeMentions()}")
                                .ConfigureAwait(false);
                            return;
                        }

                        /*
                        var chnl1Messages = await (call.Channel1 as IMessageChannel)
                            .GetMessagesAsync(20, CacheMode.CacheOnly).FlattenAsync().ConfigureAwait(false);
                        var msgs1 = chnl1Messages.Where(x => x.Content.Equals(
                                $"{Emote.Parse("<a:typing:447092767428968458>")} **{msg.Author.Username.SanitizeMentions()}** is typing...")
                                && x.Author.Id == _client.CurrentUser.Id)
                                .ToList();

                        if (msgs1.Any())
                        {
                            foreach (var m in msgs1)
                            {
                                try
                                {
                                    await m.DeleteAsync();
                                }
                                catch
                                {
                                    // ignored
                                }
                            }
                        }
                        */

                        await call.Channel1
                            .SendMessageAsync($"**{msg.Author.Username.SanitizeMentions()}**: {msg.Content.SanitizeMentions()}")
                            .ConfigureAwait(false);
                    }

                    // check and increment phrases
                    var userPhrases = await _db.GetAsync<UserPhrase>(x => x.GuildId == (long) channel.Guild.Id)
                        .ConfigureAwait(false);
                    userPhrases = userPhrases.Where(x => msg.Content.ContainsWord(x.Phrase)).ToList();
                    var phrases = await _db
                        .GetAsync<Phrase>(x => userPhrases.Select(y => y.Id).Contains(x.UserPhraseId))
                        .ConfigureAwait(false);

                    if (userPhrases.Any())
                    {
                        var toInsert = new List<Phrase>();

                        foreach (var up in userPhrases)
                        {
                            if (phrases.All(x => DateTimeOffset.UtcNow - x.Timestamp >= TimeSpan.FromSeconds(5)))
                            {
                                toInsert.Add(new Phrase
                                {
                                    GuildId = up.GuildId,
                                    ChannelId = (long) msg.Channel.Id,
                                    UserId = (long) msg.Author.Id,
                                    UserPhraseId = up.Id
                                });
                            }
                        }

                        if (toInsert.Any()) await _db.InsertAllAsync(toInsert).ConfigureAwait(false);
                    }

                    if (!gc.EnableRespects || msg.Content != "F") return;

                    var respects = await _db.GetAsync<Respects>(x =>
                            x.GuildId == (long) channel.Guild.Id)
                        .ConfigureAwait(false);
                    if (respects.Any(x => x.UserId == (long) msg.Author.Id && x.Timestamp.Day == DateTimeOffset.UtcNow.Day)) return;

                    var r = new Respects
                    {
                        GuildId = (long) channel.Guild.Id,
                        UserId = (long) msg.Author.Id
                    };
                    await _db.InsertAsync(r).ConfigureAwait(false);
                    await msg.Channel
                        .SendConfirmAsync(
                            $"**{msg.Author}** has paid their respects today ({respects.Count(x => x.Timestamp.Day == DateTimeOffset.UtcNow.Day) + 1} total today).")
                        .ConfigureAwait(false);
                });
                return;
            }

            var context = new SocketCommandContext(_client, msg);
            var result = await _commands.ExecuteAsync(context, argPos, _services).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                if (result.Error != CommandError.UnknownCommand)
                {
                    Log.CommandError(watch.ElapsedMilliseconds / 1000.0, context, result);
                    if (context.Guild is SocketGuild g)
                    {
                        var gc = await _db.GetOrCreateGuildConfigAsync(g).ConfigureAwait(false);
                        if (gc.VerboseErrors)
                        {
                            await context.Channel.SendErrorAsync(result.ErrorReason).ConfigureAwait(false);
                        }
                    }
                }
            }
            else
            {
                _stats.CommandsRun++;
                Log.CommandSuccess(watch.ElapsedMilliseconds / 1000.0, context);
            }
            watch.Stop();
        }
    }
}