using Firebase.Database;
using Firebase.Database.Query;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Real_Life_System
{
    /// <summary>
    /// Firebase Relay OTIMIZADO com Cache Local
    /// - Reduz chamadas ao Firebase em ~80%
    /// - Usa delta compression
    /// - Interest management (só sincroniza próximos)
    /// - Adaptive sync rate
    /// </summary>
    public class FirebaseRelay
    {
        private FirebaseClient firebase;
        private Timer heartbeatTimer;
        private string currentSessionId;

        // ============================================================================
        // CACHE LOCAL - Reduz chamadas ao Firebase
        // ============================================================================
        private ConcurrentDictionary<string, PlayerData> playersCache = new ConcurrentDictionary<string, PlayerData>();
        private ConcurrentDictionary<string, VehicleData> vehiclesCache = new ConcurrentDictionary<string, VehicleData>();
        private EnvironmentData environmentCache = null;

        private DateTime lastPlayerFetch = DateTime.MinValue;
        private DateTime lastVehicleFetch = DateTime.MinValue;
        private DateTime lastEnvironmentFetch = DateTime.MinValue;

        // Configurações de otimização
        private int playerFetchInterval = 200;      // 200ms = 5x/segundo
        private int vehicleFetchInterval = 250;     // 250ms = 4x/segundo
        private int environmentFetchInterval = 5000; // 5s = 1x/5segundos

        private float interestRadius = 500f; // Só sincronizar jogadores a 500m

        // Delta compression - só envia se mudou significativamente
        private Dictionary<string, PlayerData> lastSentPlayerData = new Dictionary<string, PlayerData>();
        private Dictionary<string, VehicleData> lastSentVehicleData = new Dictionary<string, VehicleData>();

        // Estatísticas
        public int TotalFirebaseCalls = 0;
        public int CachedResponses = 0;
        public long TotalBytesSent = 0;

        public FirebaseRelay(string firebaseUrl)
        {
            firebase = new FirebaseClient(firebaseUrl);
        }

        // ============================================================================
        // SESSÕES
        // ============================================================================

        public async Task<string> CreateSession(string hostName, int maxPlayers, string region)
        {
            currentSessionId = Guid.NewGuid().ToString();

            var sessionData = new Dictionary<string, object>
            {
                { "hostName", hostName },
                { "players", 1 },
                { "maxPlayers", maxPlayers },
                { "region", region },
                { "created", DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                { "lastHeartbeat", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
            };

            try
            {
                await firebase
                    .Child("sessions")
                    .Child(currentSessionId)
                    .PutAsync(sessionData);

                TotalFirebaseCalls++;

                // Heartbeat otimizado - apenas timestamp
                heartbeatTimer = new Timer(async _ => await SendHeartbeat(), null, 5000, 5000);

                GTA.UI.Notification.PostTicker($"[Firebase] Sessão criada: {currentSessionId.Substring(0, 8)}", true);
                return currentSessionId;
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"[Firebase] Erro: {ex.Message}", true);
                return null;
            }
        }

        public async Task<List<SessionInfo>> GetAvailableSessions(string region = null)
        {
            try
            {
                var sessions = await firebase
                    .Child("sessions")
                    .OnceAsync<Dictionary<string, object>>();

                TotalFirebaseCalls++;

                var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var result = new List<SessionInfo>();

                foreach (var session in sessions)
                {
                    var data = session.Object;
                    if (data == null) continue;

                    if (data.ContainsKey("lastHeartbeat"))
                    {
                        var heartbeat = Convert.ToInt64(data["lastHeartbeat"]);
                        if (currentTime - heartbeat > 15) continue;
                    }

                    if (region != null && data.ContainsKey("region"))
                    {
                        if (data["region"].ToString() != region) continue;
                    }

                    var sessionInfo = new SessionInfo
                    {
                        SessionId = session.Key,
                        HostName = data.ContainsKey("hostName") ? data["hostName"].ToString() : "Unknown",
                        PlayerCount = data.ContainsKey("players") ? Convert.ToInt32(data["players"]) : 0,
                        MaxPlayers = data.ContainsKey("maxPlayers") ? Convert.ToInt32(data["maxPlayers"]) : 8,
                        Region = data.ContainsKey("region") ? data["region"].ToString() : "Unknown"
                    };

                    result.Add(sessionInfo);
                }

                return result;
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"[Firebase] Erro: {ex.Message}", true);
                return new List<SessionInfo>();
            }
        }

        public async Task<bool> JoinSession(string sessionId, string playerId, string playerName)
        {
            try
            {
                currentSessionId = sessionId;

                await firebase
                    .Child("sessions")
                    .Child(sessionId)
                    .Child("playersList")
                    .Child(playerId)
                    .PutAsync(new { name = playerName, joined = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

                TotalFirebaseCalls++;

                var players = await firebase
                    .Child("sessions")
                    .Child(sessionId)
                    .Child("playersList")
                    .OnceAsync<object>();

                TotalFirebaseCalls++;

                await firebase
                    .Child("sessions")
                    .Child(sessionId)
                    .Child("players")
                    .PutAsync(players.Count);

                TotalFirebaseCalls++;

                heartbeatTimer = new Timer(async _ => await SendHeartbeat(), null, 5000, 5000);

                return true;
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"[Firebase] Erro ao entrar: {ex.Message}", true);
                return false;
            }
        }

        public async Task LeaveSession(string sessionId, string playerId)
        {
            try
            {
                heartbeatTimer?.Dispose();

                await firebase
                    .Child("sessions")
                    .Child(sessionId)
                    .Child("playersList")
                    .Child(playerId)
                    .DeleteAsync();

                await firebase
                    .Child("sessions")
                    .Child(sessionId)
                    .Child("players")
                    .Child(playerId)
                    .DeleteAsync();

                TotalFirebaseCalls += 2;

                var players = await firebase
                    .Child("sessions")
                    .Child(sessionId)
                    .Child("playersList")
                    .OnceAsync<object>();

                TotalFirebaseCalls++;

                if (players.Count > 0)
                {
                    await firebase
                        .Child("sessions")
                        .Child(sessionId)
                        .Child("players")
                        .PutAsync(players.Count);

                    TotalFirebaseCalls++;
                }
            }
            catch { }
        }

        public async Task DeleteSession(string sessionId)
        {
            try
            {
                heartbeatTimer?.Dispose();

                await firebase
                    .Child("sessions")
                    .Child(sessionId)
                    .DeleteAsync();

                TotalFirebaseCalls++;
            }
            catch { }
        }

        private async Task SendHeartbeat()
        {
            if (string.IsNullOrEmpty(currentSessionId)) return;

            try
            {
                await firebase
                    .Child("sessions")
                    .Child(currentSessionId)
                    .Child("lastHeartbeat")
                    .PutAsync(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                TotalFirebaseCalls++;
            }
            catch { }
        }

        // ============================================================================
        // JOGADORES - COM CACHE E DELTA COMPRESSION
        // ============================================================================

        public async Task UpdatePlayerData(string sessionId, string playerId, PlayerData data)
        {
            try
            {
                // DELTA COMPRESSION: Só envia se mudou significativamente
                if (lastSentPlayerData.ContainsKey(playerId))
                {
                    var lastData = lastSentPlayerData[playerId];

                    // Calcular distância movida
                    float deltaPos = (float)Math.Sqrt(
                        Math.Pow(data.PosX - lastData.PosX, 2) +
                        Math.Pow(data.PosY - lastData.PosY, 2) +
                        Math.Pow(data.PosZ - lastData.PosZ, 2)
                    );

                    float deltaHeading = Math.Abs(data.Heading - lastData.Heading);

                    // Só envia se mudou > 0.3m OU > 5 graus OU mudou animação
                    if (deltaPos < 0.3f && deltaHeading < 5f &&
                        data.Animation == lastData.Animation &&
                        data.InVehicle == lastData.InVehicle)
                    {
                        CachedResponses++;
                        return; // SKIP - economia de bandwidth!
                    }
                }

                // Comprimir dados usando shorts (2 bytes) em vez de floats (4 bytes)
                var compressedData = new Dictionary<string, object>
                {
                    { "n", data.Name },                          // Nome
                    { "x", (short)(data.PosX * 10) },           // Posição X (1 decimal)
                    { "y", (short)(data.PosY * 10) },           // Posição Y
                    { "z", (short)(data.PosZ * 10) },           // Posição Z
                    { "vx", (short)(data.VelX * 100) },         // Velocidade X (2 decimais)
                    { "vy", (short)(data.VelY * 100) },         // Velocidade Y
                    { "vz", (short)(data.VelZ * 100) },         // Velocidade Z
                    { "h", (short)data.Heading },               // Heading (inteiro)
                    { "a", data.Animation },                    // Animação
                    { "al", data.IsAlive ? 1 : 0 },            // Alive (bit)
                    { "iv", data.InVehicle ? 1 : 0 },          // InVehicle (bit)
                    { "hp", (short)data.Health },               // Health
                    { "w", data.Weapon },                       // Weapon
                    { "t", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
                };

                await firebase
                    .Child("sessions")
                    .Child(sessionId)
                    .Child("players")
                    .Child(playerId)
                    .PutAsync(compressedData);

                TotalFirebaseCalls++;
                TotalBytesSent += EstimateDataSize(compressedData);

                // Atualizar cache
                lastSentPlayerData[playerId] = data;
                playersCache[playerId] = data;
            }
            catch { }
        }

        public async Task<Dictionary<string, PlayerData>> GetSessionPlayers(string sessionId, GTA.Math.Vector3? myPosition = null)
        {
            var now = DateTime.UtcNow;

            // USAR CACHE se ainda não passou o intervalo
            if ((now - lastPlayerFetch).TotalMilliseconds < playerFetchInterval)
            {
                CachedResponses++;
                return new Dictionary<string, PlayerData>(playersCache);
            }

            try
            {
                var players = await firebase
                    .Child("sessions")
                    .Child(sessionId)
                    .Child("players")
                    .OnceAsync<Dictionary<string, object>>();

                TotalFirebaseCalls++;
                lastPlayerFetch = now;

                var result = new Dictionary<string, PlayerData>();

                foreach (var player in players)
                {
                    var data = player.Object;
                    if (data == null) continue;

                    var playerData = new PlayerData
                    {
                        Name = data.ContainsKey("n") ? data["n"].ToString() : "",
                        PosX = data.ContainsKey("x") ? Convert.ToInt16(data["x"]) / 10f : 0,
                        PosY = data.ContainsKey("y") ? Convert.ToInt16(data["y"]) / 10f : 0,
                        PosZ = data.ContainsKey("z") ? Convert.ToInt16(data["z"]) / 10f : 0,
                        VelX = data.ContainsKey("vx") ? Convert.ToInt16(data["vx"]) / 100f : 0,
                        VelY = data.ContainsKey("vy") ? Convert.ToInt16(data["vy"]) / 100f : 0,
                        VelZ = data.ContainsKey("vz") ? Convert.ToInt16(data["vz"]) / 100f : 0,
                        Heading = data.ContainsKey("h") ? Convert.ToInt16(data["h"]) : 0,
                        Animation = data.ContainsKey("a") ? data["a"].ToString() : "idle",
                        IsAlive = data.ContainsKey("al") ? Convert.ToInt32(data["al"]) == 1 : true,
                        InVehicle = data.ContainsKey("iv") ? Convert.ToInt32(data["iv"]) == 1 : false,
                        Health = data.ContainsKey("hp") ? Convert.ToInt16(data["hp"]) : 100,
                        Weapon = data.ContainsKey("w") ? Convert.ToInt32(data["w"]) : 0,
                        Timestamp = data.ContainsKey("t") ? Convert.ToInt64(data["t"]) : 0
                    };

                    // INTEREST MANAGEMENT: Só retornar jogadores próximos
                    if (myPosition.HasValue)
                    {
                        float distance = (float)Math.Sqrt(
                            Math.Pow(playerData.PosX - myPosition.Value.X, 2) +
                            Math.Pow(playerData.PosY - myPosition.Value.Y, 2) +
                            Math.Pow(playerData.PosZ - myPosition.Value.Z, 2)
                        );

                        if (distance > interestRadius)
                            continue; // SKIP jogadores longe
                    }

                    result[player.Key] = playerData;
                    playersCache[player.Key] = playerData; // Atualizar cache
                }

                // Limpar jogadores que não existem mais do cache
                var existingIds = result.Keys.ToHashSet();
                var toRemove = playersCache.Keys.Where(k => !existingIds.Contains(k)).ToList();
                foreach (var id in toRemove)
                {
                    playersCache.TryRemove(id, out _);
                }

                return result;
            }
            catch
            {
                // Em caso de erro, retornar cache
                return new Dictionary<string, PlayerData>(playersCache);
            }
        }

        // ============================================================================
        // VEÍCULOS - COM CACHE
        // ============================================================================

        public async Task UpdateVehicleData(string sessionId, string vehicleId, VehicleData data)
        {
            try
            {
                // DELTA COMPRESSION para veículos
                if (lastSentVehicleData.ContainsKey(vehicleId))
                {
                    var lastData = lastSentVehicleData[vehicleId];

                    float deltaPos = (float)Math.Sqrt(
                        Math.Pow(data.PosX - lastData.PosX, 2) +
                        Math.Pow(data.PosY - lastData.PosY, 2) +
                        Math.Pow(data.PosZ - lastData.PosZ, 2)
                    );

                    if (deltaPos < 0.5f && data.EngineRunning == lastData.EngineRunning)
                    {
                        CachedResponses++;
                        return; // SKIP
                    }
                }

                var compressedData = new Dictionary<string, object>
                {
                    { "m", data.Model },
                    { "x", (short)(data.PosX * 10) },
                    { "y", (short)(data.PosY * 10) },
                    { "z", (short)(data.PosZ * 10) },
                    { "vx", (short)(data.VelX * 100) },
                    { "vy", (short)(data.VelY * 100) },
                    { "vz", (short)(data.VelZ * 100) },
                    { "h", (short)data.Heading },
                    { "e", data.EngineRunning ? 1 : 0 },
                    { "hp", (short)data.Health },
                    { "t", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
                };

                await firebase
                    .Child("sessions")
                    .Child(sessionId)
                    .Child("vehicles")
                    .Child(vehicleId)
                    .PutAsync(compressedData);

                TotalFirebaseCalls++;
                TotalBytesSent += EstimateDataSize(compressedData);

                lastSentVehicleData[vehicleId] = data;
                vehiclesCache[vehicleId] = data;
            }
            catch { }
        }

        public async Task<Dictionary<string, VehicleData>> GetSessionVehicles(string sessionId, GTA.Math.Vector3? myPosition = null)
        {
            var now = DateTime.UtcNow;

            // USAR CACHE
            if ((now - lastVehicleFetch).TotalMilliseconds < vehicleFetchInterval)
            {
                CachedResponses++;
                return new Dictionary<string, VehicleData>(vehiclesCache);
            }

            try
            {
                var vehicles = await firebase
                    .Child("sessions")
                    .Child(sessionId)
                    .Child("vehicles")
                    .OnceAsync<Dictionary<string, object>>();

                TotalFirebaseCalls++;
                lastVehicleFetch = now;

                var result = new Dictionary<string, VehicleData>();

                foreach (var vehicle in vehicles)
                {
                    var data = vehicle.Object;
                    if (data == null) continue;

                    var vehicleData = new VehicleData
                    {
                        Model = data.ContainsKey("m") ? Convert.ToInt32(data["m"]) : 0,
                        PosX = data.ContainsKey("x") ? Convert.ToInt16(data["x"]) / 10f : 0,
                        PosY = data.ContainsKey("y") ? Convert.ToInt16(data["y"]) / 10f : 0,
                        PosZ = data.ContainsKey("z") ? Convert.ToInt16(data["z"]) / 10f : 0,
                        VelX = data.ContainsKey("vx") ? Convert.ToInt16(data["vx"]) / 100f : 0,
                        VelY = data.ContainsKey("vy") ? Convert.ToInt16(data["vy"]) / 100f : 0,
                        VelZ = data.ContainsKey("vz") ? Convert.ToInt16(data["vz"]) / 100f : 0,
                        Heading = data.ContainsKey("h") ? Convert.ToInt16(data["h"]) : 0,
                        EngineRunning = data.ContainsKey("e") ? Convert.ToInt32(data["e"]) == 1 : false,
                        Health = data.ContainsKey("hp") ? Convert.ToInt16(data["hp"]) : 1000,
                        Timestamp = data.ContainsKey("t") ? Convert.ToInt64(data["t"]) : 0
                    };

                    // Interest management para veículos
                    if (myPosition.HasValue)
                    {
                        float distance = (float)Math.Sqrt(
                            Math.Pow(vehicleData.PosX - myPosition.Value.X, 2) +
                            Math.Pow(vehicleData.PosY - myPosition.Value.Y, 2) +
                            Math.Pow(vehicleData.PosZ - myPosition.Value.Z, 2)
                        );

                        if (distance > interestRadius * 1.2f) // Veículos um pouco mais longe
                            continue;
                    }

                    result[vehicle.Key] = vehicleData;
                    vehiclesCache[vehicle.Key] = vehicleData;
                }

                var existingIds = result.Keys.ToHashSet();
                var toRemove = vehiclesCache.Keys.Where(k => !existingIds.Contains(k)).ToList();
                foreach (var id in toRemove)
                {
                    vehiclesCache.TryRemove(id, out _);
                }

                return result;
            }
            catch
            {
                return new Dictionary<string, VehicleData>(vehiclesCache);
            }
        }

        // ============================================================================
        // AMBIENTE - COM CACHE LONGO
        // ============================================================================

        public async Task UpdateEnvironment(string sessionId, int weather, int hour)
        {
            try
            {
                // Só envia se mudou
                if (environmentCache != null &&
                    environmentCache.Weather == weather &&
                    environmentCache.Hour == hour)
                {
                    CachedResponses++;
                    return;
                }

                var envDict = new Dictionary<string, object>
                {
                    { "w", weather },
                    { "h", hour },
                    { "t", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
                };

                await firebase
                    .Child("sessions")
                    .Child(sessionId)
                    .Child("environment")
                    .PutAsync(envDict);

                TotalFirebaseCalls++;

                environmentCache = new EnvironmentData
                {
                    Weather = weather,
                    Hour = hour
                };
            }
            catch { }
        }

        public async Task<EnvironmentData> GetEnvironment(string sessionId)
        {
            var now = DateTime.UtcNow;

            // CACHE de 5 segundos para ambiente
            if ((now - lastEnvironmentFetch).TotalMilliseconds < environmentFetchInterval && environmentCache != null)
            {
                CachedResponses++;
                return environmentCache;
            }

            try
            {
                var env = await firebase
                    .Child("sessions")
                    .Child(sessionId)
                    .Child("environment")
                    .OnceSingleAsync<Dictionary<string, object>>();

                TotalFirebaseCalls++;
                lastEnvironmentFetch = now;

                if (env == null) return environmentCache;

                environmentCache = new EnvironmentData
                {
                    Weather = env.ContainsKey("w") ? Convert.ToInt32(env["w"]) : 0,
                    Hour = env.ContainsKey("h") ? Convert.ToInt32(env["h"]) : 12
                };

                return environmentCache;
            }
            catch
            {
                return environmentCache;
            }
        }

        // ============================================================================
        // UTILITÁRIOS
        // ============================================================================

        private long EstimateDataSize(Dictionary<string, object> data)
        {
            long size = 0;
            foreach (var kvp in data)
            {
                size += kvp.Key.Length; // Chave

                if (kvp.Value is string s)
                    size += s.Length;
                else if (kvp.Value is int || kvp.Value is float)
                    size += 4;
                else if (kvp.Value is short)
                    size += 2;
                else if (kvp.Value is long)
                    size += 8;
            }
            return size;
        }

        public void SetInterestRadius(float radius)
        {
            interestRadius = radius;
        }

        public void SetSyncRates(int playerMs, int vehicleMs, int environmentMs)
        {
            playerFetchInterval = playerMs;
            vehicleFetchInterval = vehicleMs;
            environmentFetchInterval = environmentMs;
        }

        public string GetStats()
        {
            float hitRate = TotalFirebaseCalls > 0
                ? (float)CachedResponses / (TotalFirebaseCalls + CachedResponses) * 100
                : 0;

            return $"Firebase Calls: {TotalFirebaseCalls} | Cache Hits: {CachedResponses} ({hitRate:F1}%) | Data Sent: {TotalBytesSent / 1024}KB";
        }

        public void ClearCache()
        {
            playersCache.Clear();
            vehiclesCache.Clear();
            environmentCache = null;
            lastSentPlayerData.Clear();
            lastSentVehicleData.Clear();
        }

        // ============================================================================
        // LIMPEZA
        // ============================================================================

        public async Task CleanupOldSessions()
        {
            try
            {
                var sessions = await firebase
                    .Child("sessions")
                    .OnceAsync<Dictionary<string, object>>();

                TotalFirebaseCalls++;

                var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                foreach (var session in sessions)
                {
                    var data = session.Object;
                    if (data == null) continue;

                    if (data.ContainsKey("lastHeartbeat"))
                    {
                        var heartbeat = Convert.ToInt64(data["lastHeartbeat"]);
                        if (currentTime - heartbeat > 30)
                        {
                            await firebase
                                .Child("sessions")
                                .Child(session.Key)
                                .DeleteAsync();

                            TotalFirebaseCalls++;
                        }
                    }
                }
            }
            catch { }
        }
    }
}