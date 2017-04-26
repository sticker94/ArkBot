﻿using ArkBot.Database;
using ArkBot.Extensions;
using ArkBot.Helpers;
using ArkBot.ViewModel;
using ArkBot.WebApi.Model;
using ArkSavegameToolkitNet.Domain;
using QueryMaster.GameServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Http;

namespace ArkBot.WebApi.Controllers
{
    public class ServersController : ApiController
    {
        private IConfig _config;
        private EfDatabaseContextFactory _databaseContextFactory;

        public ServersController(IConfig config, EfDatabaseContextFactory databaseContextFactory)
        {
            _config = config;
            _databaseContextFactory = databaseContextFactory;
        }

        public async Task<ServersViewModel> Get()
        {
            var result = new ServersViewModel();
            foreach (var context in Workspace.Instance.ServerContexts.Values)
            {
                var status = await CommandHelper.GetServerStatus(context.Config.Ip, (ushort)context.Config.Port);
                if (status == null || status.Item1 == null || status.Item2 == null)
                {
                    //Server status is currently unavailable
                }
                else
                {
                    var serverInfo = status.Item1;
                    var serverRules = status.Item2;
                    var playerInfo = status.Item3;

                    var m = new Regex(@"^(?<name>.+?)\s+-\s+\(v(?<version>\d+\.\d+)\)$", RegexOptions.IgnoreCase | RegexOptions.Singleline).Match(serverInfo.Name);
                    var name = m.Success ? m.Groups["name"].Value : serverInfo.Name;
                    var version = m.Success ? m.Groups["version"] : null;
                    var currentTime = serverRules.FirstOrDefault(x => x.Name == "DayTime_s")?.Value;
                    var tamedDinosCount = context.TamedCreatures?.Count();
                    //var uploadedDinosCount = _context.Cluster?.Creatures?.Count();
                    var wildDinosCount = context.WildCreatures?.Count();
                    //var tamedDinosMax = 6000; //todo: remove hardcoded value
                    var structuresCount = context.Structures?.Count();
                    var totalPlayers = context.Players?.Count();
                    var totalTribes = context.Tribes?.Count();

                    //server uptime
                    //DateTime? serverStarted = null;
                    //try
                    //{
                    //    serverStarted = Process.GetProcessesByName(_constants.ArkServerProcessName)?.FirstOrDefault()?.StartTime;

                    //}
                    //catch { /* ignore exceptions */ }

                    var nextUpdate = context.ApproxTimeUntilNextUpdate;
                    var nextUpdateTmp = nextUpdate?.ToStringCustom();
                    var nextUpdateString = (nextUpdate.HasValue ? (!string.IsNullOrWhiteSpace(nextUpdateTmp) ? $"~{nextUpdateTmp}" : "waiting for new update ...") : null);
                    var lastUpdate = context.LastUpdate;
                    var lastUpdateString = lastUpdate.ToStringWithRelativeDay();

                    var sr = new ServerViewModel
                    {
                        Key = context.Config.Key,
                        Name = name,
                        Address = serverInfo.Address,
                        Version = version.ToString(),
                        OnlinePlayerCount = serverInfo.Players,
                        OnlinePlayerMax = serverInfo.MaxPlayers,
                        MapName = serverInfo.Map,
                        InGameTime = currentTime,
                        TamedCreatureCount = tamedDinosCount ?? 0,
                        WildCreatureCount = wildDinosCount ?? 0,
                        StructureCount = structuresCount ?? 0,
                        PlayerCount = totalPlayers ?? 0,
                        TribeCount = totalTribes ?? 0,
                        LastUpdate = lastUpdateString,
                        NextUpdate = nextUpdateString
                    };

                    var players = playerInfo?.Where(x => !string.IsNullOrEmpty(x.Name)).ToArray() ?? new PlayerInfo[] { };
                    using (var db = _databaseContextFactory.Create())
                    {
                        Dictionary<string, Tuple<ArkPlayer, Database.Model.User, Discord.User, long?, TimeSpan, ArkTribe>> d = null;
                        if (context.Players != null)
                        {
                            var playerNames = players.Select(x => x.Name).ToArray();
                            d = context.Players.Where(x => playerNames.Contains(x.Name, StringComparer.Ordinal)).Select(x =>
                            {
                                long steamId;
                                return new Tuple<ArkPlayer, Database.Model.User, Discord.User, long?, TimeSpan, ArkTribe>(
                                    x,
                                    null,
                                    null,
                                    long.TryParse(x.SteamId, out steamId) ? steamId : (long?)null,
                                    TimeSpan.Zero, null);
                            }).ToDictionary(x => x.Item1.Name, StringComparer.OrdinalIgnoreCase);

                            var ids = new List<ulong>();
                            var steamIds = d.Values.Select(x => x.Item4).Where(x => x != null).ToArray();
                            foreach (var user in db.Users.Where(y => steamIds.Contains(y.SteamId)))
                            {
                                var item = d.Values.FirstOrDefault(x => x.Item4 == user.SteamId);
                                if (item == null) continue;

                                ids.Add(item.Item1.Id);

                                var discordUser = (Discord.User)null; //e.User?.Client?.Servers?.Select(x => x.GetUser((ulong)user.DiscordId)).FirstOrDefault();
                                var playedLastSevenDays = TimeSpan.MinValue; //TimeSpan.FromSeconds(user?.Played?.OrderByDescending(x => x.Date).Take(7).Sum(x => x.TimeInSeconds) ?? 0);

                                d[item.Item1.Name] = Tuple.Create(item.Item1, user, discordUser, item.Item4, playedLastSevenDays, item.Item1.TribeId.HasValue ? context.Tribes?.FirstOrDefault(x => x.Id == item.Item1.TribeId.Value) : null);
                            }

                            var remaining = d.Values.Where(x => !ids.Contains(x.Item1.Id)).Where(x => x.Item4 != null).Select(x => x.Item4.Value).ToArray();
                            foreach (var user in db.Played.Where(x => x.SteamId.HasValue && remaining.Contains(x.SteamId.Value))
                                .GroupBy(x => x.SteamId)
                                .Select(x => new { key = x.Key, items = x.OrderByDescending(y => y.Date).Take(7).ToList() })
                                .ToArray())
                            {
                                var item = d.Values.FirstOrDefault(x => x.Item4 == user.key);
                                if (item == null) continue;

                                var playedLastSevenDays = TimeSpan.FromSeconds(user?.items?.Sum(x => x.TimeInSeconds) ?? 0);
                                d[item.Item1.Name] = Tuple.Create(item.Item1, item.Item2, item.Item3, item.Item4, playedLastSevenDays, item.Item1.TribeId.HasValue ? context.Tribes?.FirstOrDefault(x => x.Id == item.Item1.TribeId.Value) : null);
                            }
                        }

                        //var playerslist = players.Select(x => {
                        //    var extra = d.ContainsKey(x.Name) ? d[x.Name] : null;
                        //    return new
                        //    {
                        //        Steam = x.Name,
                        //        Name = extra?.Item1?.Name,
                        //        Tribe = extra?.Item1?.TribeName,
                        //        Discord = extra != null && extra.Item3 != null ? $"{extra.Item3.Name}#{extra.Item3.Discriminator}" : null,
                        //        TimeOnline = x.Time.ToStringCustom(),
                        //        PlayedLastSevenDays = extra != null && extra.Item5.TotalMinutes > 1 ? extra?.Item5.ToStringCustom() : null
                        //    };
                        //}).ToArray();

                        foreach (var player in players)
                        {
                            var extra = d?.ContainsKey(player.Name) == true ? d[player.Name] : null;
                            sr.OnlinePlayers.Add(new OnlinePlayerViewModel
                            {
                                SteamName = player.Name,
                                CharacterName = extra?.Item1?.Name,
                                TribeName = extra?.Item6?.Name,
                                DiscordName = extra != null && extra.Item3 != null ? $"{extra.Item3.Name}#{extra.Item3.Discriminator}" : null,
                                TimeOnline = player.Time.ToStringCustom(),
                                TimeOnlineSeconds = (int)Math.Round(player.Time.TotalSeconds)
                            });
                        }
                    }

                    result.Servers.Add(sr);
                }
            }

            foreach (var context in Workspace.Instance.ClusterContexts.Values)
            {
                var cc = new ClusterViewModel
                {
                    Key = context.Config.Key,
                    ServerKeys = Workspace.Instance.ServerContexts.Values
                        .Where(x => x.Config.Cluster.Equals(context.Config.Key, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Config.Key).ToArray()
                };
                result.Clusters.Add(cc);
            }

            return result;
        }
    }
}
