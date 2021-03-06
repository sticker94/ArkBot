﻿using QueryMaster.GameServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Caching;
using System.Text;
using System.Threading.Tasks;

namespace ArkBot.Steam
{
    public class SteamManager : IDisposable
    {
        private ServerConfigSection _config;
        private DateTime _sourceServerLastReconnect;
        private DateTime _rconServerLastReconnect;
        private object _sourceServerLock = new object();
        private object _rconServerLock = new object();
        private Task _sourceServerReconnectTask;
        private Task _rconServerReconnectTask;
        private Server _sourceServer;
        private Server _rconServer;

        private DateTime _lastServerInfo;
        private DateTime _lastServerRules;
        private DateTime _lastServerPlayers;

        public SteamManager(ServerConfigSection config)
        {
            _config = config;
        }

        public async Task Initialize()
        {
            await ReconnectSource();
            await ReconnectRcon();
        }

        /// <summary>
        /// Sends an admin command via rcon
        /// </summary>
        /// <returns>Result from server on success, null if failed.</returns>
        public async Task<string> SendRconCommand(string command)
        {
            try
            {
                if (_rconServer?.Rcon == null) await ReconnectRcon();
                if (_rconServer?.Rcon == null)
                {
                    Logging.Log("Exception attempting to send rcon command (could not connect)", typeof(SteamManager), LogLevel.DEBUG);
                    return null;
                }

                var result = await _rconServer.Rcon.SendCommandAsync(command);
                return result;
            }
            catch (Exception ex)
            {
                _rconServer?.Dispose();
                _rconServer = null;
                Logging.LogException("Exception attempting to send rcon command", ex, typeof(SteamManager), LogLevel.DEBUG, ExceptionLevel.Ignored);
                return null;
            }
        }

        private async Task ReconnectSource()
        {
            if (DateTime.Now - _sourceServerLastReconnect <= TimeSpan.FromSeconds(15)) return;

            Task reconnect = null;
            lock (_sourceServerLock)
            {
                if (_sourceServerReconnectTask != null) reconnect = Task.WhenAll(_sourceServerReconnectTask);
                else
                {
                    _sourceServerLastReconnect = DateTime.Now;
                    _sourceServerReconnectTask = reconnect = Task.Run(() =>
                    {
                        try
                        {
                            _sourceServer?.Dispose();
                            _sourceServer = null;

                            _sourceServer = ServerQuery.GetServerInstance(QueryMaster.EngineType.Source, _config.Ip, (ushort)_config.Port, false, 2000, 5000, 1, true);
                        }
                        catch (Exception ex)
                        {
                            _sourceServer?.Dispose();
                            _sourceServer = null;

                            Logging.LogException($"Failed to connect to server steamworks api ({_config.Ip}, {_config.Port})", ex, typeof(SteamManager), LogLevel.WARN, ExceptionLevel.Ignored);
                        }
                    });
                }
            }

            await reconnect;

            lock (_sourceServerLock)
            {
                _sourceServerReconnectTask = null;
            }
        }

        private async Task ReconnectRcon()
        {
            if (DateTime.Now - _rconServerLastReconnect <= TimeSpan.FromSeconds(15)) return;

            Task reconnect = null;
            lock (_rconServerLock)
            {
                if (_rconServerReconnectTask != null) reconnect = Task.WhenAll(_rconServerReconnectTask);
                else
                {
                    _rconServerLastReconnect = DateTime.Now;
                    _rconServerReconnectTask = reconnect = Task.Run(() =>
                    {
                        try
                        {
                            _rconServer?.Dispose();
                            _rconServer = null;

                            _rconServer = ServerQuery.GetServerInstance(QueryMaster.EngineType.Source, _config.Ip, (ushort)_config.RconPort, false, 2000, 5000, 1, true);
                            _rconServer?.GetControl(_config.RconPassword);
                        }
                        catch (SocketException)
                        {
                            _rconServer?.Dispose();
                            _rconServer = null;
                        }
                        catch (Exception ex)
                        {
                            _rconServer?.Dispose();
                            _rconServer = null;

                            Logging.LogException($"Error when connecting to server rcon ({_config.Ip}, {_config.RconPort})", ex, typeof(SteamManager), LogLevel.WARN, ExceptionLevel.Ignored);
                        }
                    });
                }
            }

            await reconnect;

            lock (_rconServerLock)
            {
                _rconServerReconnectTask = null;
            }
        }

        public async Task<long> Ping()
        {
            return await Task.Run(() =>
            {
                return _sourceServer.Ping();
            });
        }

        public ServerInfo GetServerInfoCached()
        {
            var cache = MemoryCache.Default;
            var cacheKey = $"{nameof(GetServerInfo)}_{_config.Ip}_{_config.Port}";
            return cache[cacheKey] as ServerInfo;
        }

        public QueryMaster.QueryMasterCollection<Rule> GetServerRulesCached()
        {
            var cache = MemoryCache.Default;
            var cacheKey = $"{nameof(GetServerRules)}_{_config.Ip}_{_config.Port}";
            return cache[cacheKey] as QueryMaster.QueryMasterCollection<Rule>;
        }

        public QueryMaster.QueryMasterCollection<PlayerInfo> GetServerPlayersCached()
        {
            var cache = MemoryCache.Default;
            var cacheKey = $"{nameof(GetServerPlayers)}_{_config.Ip}_{_config.Port}";
            return cache[cacheKey] as QueryMaster.QueryMasterCollection<PlayerInfo>;
        }

        public Tuple<ServerInfo, QueryMaster.QueryMasterCollection<Rule>, QueryMaster.QueryMasterCollection<PlayerInfo>> GetServerStatusCached()
        {
            var info = GetServerInfoCached();
            var rules = GetServerRulesCached();
            var players = GetServerPlayersCached();

            return Tuple.Create(info, rules, players);
        }

        public async Task<ServerInfo> GetServerInfo()
        {
            var cache = MemoryCache.Default;
            var cacheKey = $"{nameof(GetServerInfo)}_{_config.Ip}_{_config.Port}";
            var info = cache[cacheKey] as ServerInfo;

            if ((DateTime.Now - _lastServerInfo) > TimeSpan.FromMinutes(1))
            {
                await Task.Run(async () =>
                {
                    try
                    {
                        if (_sourceServer == null) await ReconnectSource();
                        if (_sourceServer == null)
                        {
                            Logging.Log("Exception attempting to get server info (could not connect)", typeof(SteamManager), LogLevel.DEBUG);
                            return;
                        }

                        info = _sourceServer.GetInfo();
                        if (info != null)
                        {
                            _lastServerInfo = DateTime.Now;
                            cache.Set(cacheKey, info, new CacheItemPolicy { }); //AbsoluteExpiration = DateTime.Now.AddMinutes(1)
                        }
                    }
                    catch (System.Net.Sockets.SocketException ex)
                    when (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.HostUnreachable)
                    {
                        _sourceServer?.Dispose();
                        _sourceServer = null;
                        return;
                    }
                    catch (Exception ex)
                    {
                        _sourceServer?.Dispose();
                        _sourceServer = null;
                        Logging.LogException("Exception attempting to get server info", ex, typeof(SteamManager), LogLevel.DEBUG, ExceptionLevel.Ignored);
                        return;
                    }
                });
            }

            return info;
        }

        public async Task<QueryMaster.QueryMasterCollection<Rule>> GetServerRules()
        {
            var cache = MemoryCache.Default;
            var cacheKey = $"{nameof(GetServerRules)}_{_config.Ip}_{_config.Port}";
            var rules = cache[cacheKey] as QueryMaster.QueryMasterCollection<Rule>;

            if ((DateTime.Now - _lastServerRules) > TimeSpan.FromMinutes(1))
            {
                await Task.Run(async () =>
                {
                    try
                    {
                        if (_sourceServer == null) await ReconnectSource();
                        if (_sourceServer == null)
                        {
                            Logging.Log("Exception attempting to get server rules (could not connect)", typeof(SteamManager), LogLevel.DEBUG);
                            return;
                        }

                        rules = _sourceServer.GetRules();
                        if (rules != null)
                        {
                            _lastServerRules = DateTime.Now;
                            cache.Set(cacheKey, rules, new CacheItemPolicy { }); //AbsoluteExpiration = DateTime.Now.AddMinutes(1)
                        }
                    }
                    catch (System.Net.Sockets.SocketException ex)
                    when (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.HostUnreachable)
                    {
                        _sourceServer?.Dispose();
                        _sourceServer = null;
                        return;
                    }
                    catch (Exception ex)
                    {
                        _sourceServer?.Dispose();
                        _sourceServer = null;
                        Logging.LogException("Exception attempting to get server rules", ex, typeof(SteamManager), LogLevel.DEBUG, ExceptionLevel.Ignored);
                        return;
                    }
                });
            }

            return rules;
        }

        public async Task<QueryMaster.QueryMasterCollection<PlayerInfo>> GetServerPlayers()
        {
            var cache = MemoryCache.Default;
            var cacheKey = $"{nameof(GetServerPlayers)}_{_config.Ip}_{_config.Port}";
            var players = cache[cacheKey] as QueryMaster.QueryMasterCollection<PlayerInfo>;

            if ((DateTime.Now - _lastServerPlayers) > TimeSpan.FromMinutes(1))
            {
                await Task.Run(async () =>
                {
                    try
                    {
                        if (_sourceServer == null) await ReconnectSource();
                        if (_sourceServer == null)
                        {
                            Logging.Log("Exception attempting to get server players (could not connect)", typeof(SteamManager), LogLevel.DEBUG);
                            return;
                        }

                        players = _sourceServer.GetPlayers();
                        if (players != null)
                        {
                            _lastServerPlayers = DateTime.Now;
                            cache.Set(cacheKey, players, new CacheItemPolicy { }); //AbsoluteExpiration = DateTime.Now.AddMinutes(1)
                        }
                    }
                    catch (System.Net.Sockets.SocketException ex)
                    when (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.HostUnreachable)
                    {
                        _sourceServer?.Dispose();
                        _sourceServer = null;
                        return;
                    }
                    catch (Exception ex)
                    {
                        _sourceServer?.Dispose();
                        _sourceServer = null;
                        Logging.LogException("Exception attempting to get server players", ex, typeof(SteamManager), LogLevel.DEBUG, ExceptionLevel.Ignored);
                        return;
                    }
                });
            }

            return players;
        }

        public async Task<Tuple<ServerInfo, QueryMaster.QueryMasterCollection<Rule>, QueryMaster.QueryMasterCollection<PlayerInfo>>> GetServerStatus()
        {
            var info = await GetServerInfo();
            var rules = await GetServerRules();
            var players = await GetServerPlayers();

            return Tuple.Create(info, rules, players);
        }

        #region IDisposable Support
        protected virtual void Dispose(bool disposing)
        {
            if (disposedValue) return;

            if (disposing)
            {
                _sourceServer?.Dispose();
                _sourceServer = null;
                _rconServer?.Dispose();
                _rconServer = null;
            }

            disposedValue = true;
        }
        public void Dispose() { Dispose(true); }
        private bool disposedValue = false;
        #endregion
    }
}
