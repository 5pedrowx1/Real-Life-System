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
    public class FirebaseRelay
    {
        private readonly FirebaseClient firebase;
        readonly ChatSystem chatSystem;
        private Timer heartbeatTimer;
        private string currentSessionId;
        private string myPlayerId;
        private readonly ConcurrentDictionary<string, PlayerData> playersCache = new ConcurrentDictionary<string, PlayerData>();
        private readonly ConcurrentDictionary<string, VehicleData> vehiclesCache = new ConcurrentDictionary<string, VehicleData>();
        private EnvironmentData environmentCache = null;
        private List<ChatMessage> chatCache = new List<ChatMessage>();
        private DateTime lastPlayerFetch = DateTime.MinValue;
        private DateTime lastVehicleFetch = DateTime.MinValue;
        private DateTime lastEnvironmentFetch = DateTime.MinValue;
        private DateTime lastChatFetch = DateTime.MinValue;
        private int playerFetchInterval = 50;
        private int vehicleFetchInterval = 100;
        private int environmentFetchInterval = 10000;
        private readonly int chatFetchInterval = 500;
        private float interestRadius = 300f;
        private readonly Dictionary<string, PlayerData> lastSentPlayerData = new Dictionary<string, PlayerData>();
        private readonly Dictionary<string, VehicleData> lastSentVehicleData = new Dictionary<string, VehicleData>();
        public int TotalFirebaseCalls = 0;
        public int CachedResponses = 0;
        public int SelfSyncBlocked = 0;
        public long TotalBytesSent = 0;
        private readonly Queue<PendingUpdate> pendingUpdates = new Queue<PendingUpdate>();
        private readonly Timer batchTimer;

        public FirebaseRelay(string firebaseUrl)
        {
            firebase = new FirebaseClient(firebaseUrl);
            batchTimer = new Timer(_ => ProcessBatchUpdates(), null, 33, 33);
        }

        public void SetMyPlayerId(string playerId)
        {
            myPlayerId = playerId;
        }

        public async Task<string> CreateSession(string hostName, int maxPlayers, string region)
        {
            currentSessionId = GenerateCompactId();

            try
            {
                var timestamp = GetTimestamp();

                await firebase
                    .Child("s")
                    .Child(currentSessionId)
                    .Child("info")
                    .PutAsync(new Dictionary<string, object>
                    {
                        { "h", hostName },
                        { "p", 1 },
                        { "m", maxPlayers },
                        { "r", region },
                        { "c", timestamp },
                        { "hb", timestamp }
                    });

                TotalFirebaseCalls++;
                heartbeatTimer = new Timer(async _ => await SendHeartbeat(), null, 5000, 10000);

                chatSystem.AddSystemMessage($"[FB] Sessão criada: {currentSessionId.Substring(0, 6)}");
                return currentSessionId;
            }
            catch (Exception ex)
            {
                chatSystem.AddSystemMessage($"[FB] Erro ao criar: {ex.Message}");
                return null;
            }
        }

        private async Task<SessionInfo> ProcessSession(string sessionId, string region, long currentTime)
        {
            try
            {
                var infoData = await firebase
                    .Child("s")
                    .Child(sessionId)
                    .Child("info")
                    .OnceSingleAsync<Dictionary<string, object>>();

                TotalFirebaseCalls++;

                if (infoData == null)
                {
                    chatSystem.AddSystemMessage($"{sessionId}: info null");
                    return null;
                }

                if (infoData.ContainsKey("hb"))
                {
                    var heartbeat = Convert.ToInt64(infoData["hb"]);
                    var age = currentTime - heartbeat;

                    if (age > 60)
                    {
                        chatSystem.AddSystemMessage($"{sessionId}: heartbeat expirado ({age}s)");
                        return null;
                    }
                }

                if (region != null && infoData.ContainsKey("r"))
                {
                    var sessionRegion = infoData["r"].ToString();
                    if (sessionRegion != region)
                    {
                        chatSystem.AddSystemMessage($"{sessionId}: região diferente ({sessionRegion})");
                        return null;
                    }
                }

                var sessionInfo = new SessionInfo
                {
                    SessionId = sessionId,
                    HostName = infoData.ContainsKey("h") ? infoData["h"].ToString() : "Unknown",
                    PlayerCount = infoData.ContainsKey("p") ? Convert.ToInt32(infoData["p"]) : 0,
                    MaxPlayers = infoData.ContainsKey("m") ? Convert.ToInt32(infoData["m"]) : 8,
                    Region = infoData.ContainsKey("r") ? infoData["r"].ToString() : "?"
                };

                chatSystem.AddSystemMessage($"✓ {sessionInfo.HostName} ({sessionInfo.PlayerCount}/{sessionInfo.MaxPlayers})");
                return sessionInfo;
            }
            catch (Exception ex)
            {
                chatSystem.AddSystemMessage($"Erro em {sessionId}: {ex.Message}");
                return null;
            }
        }

        public async Task<List<SessionInfo>> GetAvailableSessions(string region = null)
        {
            try
            {
                GTA.UI.Notification.PostTicker("Buscando sessões...", true);

                var sessions = await firebase
                    .Child("s")
                    .OnceAsync<object>();

                TotalFirebaseCalls++;

                chatSystem.AddSystemMessage($"{sessions.Count} sessões no Firebase");

                if (sessions.Count == 0)
                {
                    GTA.UI.Notification.PostTicker("Nenhuma sessão encontrada!", true);
                    return new List<SessionInfo>();
                }

                var currentTime = GetTimestamp();
                var result = new List<SessionInfo>();
                var tasks = new List<Task<SessionInfo>>();

                foreach (var session in sessions)
                {
                    tasks.Add(ProcessSession(session.Key, region, currentTime));
                }

                var sessionInfos = await Task.WhenAll(tasks);

                foreach (var info in sessionInfos)
                {
                    if (info != null)
                    {
                        result.Add(info);
                    }
                }

                chatSystem.AddSystemMessage($"{result.Count} sessões válidas");
                return result;
            }
            catch (Exception ex)
            {
                chatSystem.AddSystemMessage($"[FB] GetSessions erro: {ex.Message}");
                return new List<SessionInfo>();
            }
        }

        public async Task<bool> JoinSession(string sessionId, string playerId, string playerName)
        {
            try
            {
                currentSessionId = sessionId;
                myPlayerId = playerId;

                chatSystem.AddSystemMessage($"[FB] Entrando na sessão {sessionId.Substring(0, 6)}...");

                await firebase
                    .Child("s")
                    .Child(sessionId)
                    .Child("pl")
                    .Child(playerId)
                    .PutAsync(new { n = playerName, j = GetTimestamp() });

                TotalFirebaseCalls++;

                var players = await firebase
                    .Child("s")
                    .Child(sessionId)
                    .Child("pl")
                    .OnceAsync<object>();

                TotalFirebaseCalls++;

                await firebase
                    .Child("s")
                    .Child(sessionId)
                    .Child("info")
                    .Child("p")
                    .PutAsync(players.Count);

                TotalFirebaseCalls++;

                heartbeatTimer = new Timer(async _ => await SendHeartbeat(), null, 5000, 10000);

                chatSystem.AddSystemMessage($"[FB] ✓ Conectado! {players.Count} jogadores");
                return true;
            }
            catch (Exception ex)
            {
                chatSystem.AddSystemMessage($"[FB] Erro ao entrar: {ex.Message}");
                return false;
            }
        }

        public async Task LeaveSession(string sessionId, string playerId)
        {
            try
            {
                heartbeatTimer?.Dispose();
                batchTimer?.Dispose();

                await firebase
                    .Child("s")
                    .Child(sessionId)
                    .Child("pl")
                    .Child(playerId)
                    .DeleteAsync();

                await firebase
                    .Child("s")
                    .Child(sessionId)
                    .Child("p")
                    .Child(playerId)
                    .DeleteAsync();

                TotalFirebaseCalls += 2;

                var players = await firebase
                    .Child("s")
                    .Child(sessionId)
                    .Child("pl")
                    .OnceAsync<object>();

                TotalFirebaseCalls++;

                if (players.Count > 0)
                {
                    await firebase
                        .Child("s")
                        .Child(sessionId)
                        .Child("info")
                        .Child("p")
                        .PutAsync(players.Count);

                    TotalFirebaseCalls++;
                }
                else
                {
                    await DeleteSession(sessionId);
                }
            }
            catch { }
        }

        public async Task DeleteSession(string sessionId)
        {
            try
            {
                heartbeatTimer?.Dispose();
                batchTimer?.Dispose();

                await firebase
                    .Child("s")
                    .Child(sessionId)
                    .DeleteAsync();

                TotalFirebaseCalls++;
                GTA.UI.Notification.PostTicker("[FB] Sessão deletada", true);
            }
            catch { }
        }

        private async Task SendHeartbeat()
        {
            if (string.IsNullOrEmpty(currentSessionId)) return;

            try
            {
                await firebase
                    .Child("s")
                    .Child(currentSessionId)
                    .Child("info")
                    .Child("hb")
                    .PutAsync(GetTimestamp());

                TotalFirebaseCalls++;
            }
            catch { }
        }

        public async Task UpdatePlayerData(string sessionId, string playerId, PlayerData data)
        {
            if (playerId == myPlayerId)
            {
                SelfSyncBlocked++;
                return;
            }

            try
            {
                if (lastSentPlayerData.ContainsKey(playerId))
                {
                    var lastData = lastSentPlayerData[playerId];

                    float deltaPos = (float)Math.Sqrt(
                        Math.Pow(data.PosX - lastData.PosX, 2) +
                        Math.Pow(data.PosY - lastData.PosY, 2) +
                        Math.Pow(data.PosZ - lastData.PosZ, 2)
                    );

                    float deltaHeading = Math.Abs(data.Heading - lastData.Heading);

                    if (deltaPos < 0.2f && deltaHeading < 3f &&
                        data.Animation == lastData.Animation &&
                        data.InVehicle == lastData.InVehicle)
                    {
                        CachedResponses++;
                        return;
                    }
                }

                var compressedData = new Dictionary<string, object>
                {
                    { "n", data.Name.Substring(0, Math.Min(12, data.Name.Length)) },
                    { "x", (short)(data.PosX * 10) },
                    { "y", (short)(data.PosY * 10) },
                    { "z", (short)(data.PosZ * 10) },
                    { "vx", (sbyte)(data.VelX * 10) },
                    { "vy", (sbyte)(data.VelY * 10) },
                    { "vz", (sbyte)(data.VelZ * 10) },
                    { "h", (byte)(data.Heading / 1.41f) },
                    { "a", GetAnimByte(data.Animation) },
                    { "f", PackFlags(data) },
                    { "hp", (byte)(data.Health / 4) },
                    { "w", (short)data.Weapon },
                    { "t", GetTimestamp() }
                };

                if (data.InVehicle && !string.IsNullOrEmpty(data.VehicleId))
                {
                    compressedData["vid"] = data.VehicleId;
                    compressedData["seat"] = (sbyte)data.VehicleSeat;
                }

                pendingUpdates.Enqueue(new PendingUpdate
                {
                    Path = $"s/{sessionId}/p/{playerId}",
                    Data = compressedData
                });

                lastSentPlayerData[playerId] = data;
                playersCache[playerId] = data;
            }
            catch { }
        }

        public async Task<Dictionary<string, PlayerData>> GetSessionPlayers(string sessionId, GTA.Math.Vector3? myPosition = null)
        {
            var now = DateTime.UtcNow;

            if ((now - lastPlayerFetch).TotalMilliseconds < playerFetchInterval)
            {
                CachedResponses++;
                return new Dictionary<string, PlayerData>(playersCache);
            }

            try
            {
                var players = await firebase
                    .Child("s")
                    .Child(sessionId)
                    .Child("p")
                    .OnceAsync<Dictionary<string, object>>();

                TotalFirebaseCalls++;
                lastPlayerFetch = now;

                var result = new Dictionary<string, PlayerData>();

                foreach (var player in players)
                {
                    if (player.Key == myPlayerId)
                    {
                        SelfSyncBlocked++;
                        continue;
                    }

                    var data = player.Object;
                    if (data == null) continue;

                    var playerData = new PlayerData
                    {
                        Name = data.ContainsKey("n") ? data["n"].ToString() : "",
                        PosX = data.ContainsKey("x") ? Convert.ToInt16(data["x"]) / 10f : 0,
                        PosY = data.ContainsKey("y") ? Convert.ToInt16(data["y"]) / 10f : 0,
                        PosZ = data.ContainsKey("z") ? Convert.ToInt16(data["z"]) / 10f : 0,
                        VelX = data.ContainsKey("vx") ? Convert.ToSByte(data["vx"]) / 10f : 0,
                        VelY = data.ContainsKey("vy") ? Convert.ToSByte(data["vy"]) / 10f : 0,
                        VelZ = data.ContainsKey("vz") ? Convert.ToSByte(data["vz"]) / 10f : 0,
                        Heading = data.ContainsKey("h") ? Convert.ToByte(data["h"]) * 1.41f : 0,
                        Animation = data.ContainsKey("a") ? GetAnimString(Convert.ToByte(data["a"])) : "idle",
                        Health = data.ContainsKey("hp") ? Convert.ToByte(data["hp"]) * 4 : 100,
                        Weapon = data.ContainsKey("w") ? Convert.ToInt32(data["w"]) : 0,
                        VehicleId = data.ContainsKey("vid") ? data["vid"].ToString() : null,
                        VehicleSeat = data.ContainsKey("seat") ? Convert.ToInt32(data["seat"]) : -1,
                        Timestamp = data.ContainsKey("t") ? Convert.ToInt64(data["t"]) : 0
                    };

                    if (data.ContainsKey("f"))
                    {
                        UnpackFlags(Convert.ToByte(data["f"]), playerData);
                    }

                    if (myPosition.HasValue)
                    {
                        float distance = (float)Math.Sqrt(
                            Math.Pow(playerData.PosX - myPosition.Value.X, 2) +
                            Math.Pow(playerData.PosY - myPosition.Value.Y, 2) +
                            Math.Pow(playerData.PosZ - myPosition.Value.Z, 2)
                        );

                        if (distance > interestRadius)
                            continue;
                    }

                    result[player.Key] = playerData;
                    playersCache[player.Key] = playerData;
                }

                var existingIds = result.Keys.ToHashSet();
                var toRemove = playersCache.Keys.Where(k => !existingIds.Contains(k) && k != myPlayerId).ToList();
                foreach (var id in toRemove)
                {
                    playersCache.TryRemove(id, out _);
                }

                return result;
            }
            catch
            {
                return new Dictionary<string, PlayerData>(playersCache);
            }
        }

        public async Task UpdateVehicleData(string sessionId, string vehicleId, VehicleData data)
        {
            string ownedVehicleId = $"{myPlayerId}_{vehicleId}";

            try
            {
                if (lastSentVehicleData.ContainsKey(ownedVehicleId))
                {
                    var lastData = lastSentVehicleData[ownedVehicleId];

                    float deltaPos = (float)Math.Sqrt(
                        Math.Pow(data.PosX - lastData.PosX, 2) +
                        Math.Pow(data.PosY - lastData.PosY, 2) +
                        Math.Pow(data.PosZ - lastData.PosZ, 2)
                    );

                    if (deltaPos < 0.3f && data.EngineRunning == lastData.EngineRunning)
                    {
                        CachedResponses++;
                        return;
                    }
                }

                var compressedData = new Dictionary<string, object>
                {
                    { "m", data.Model },
                    { "x", (short)(data.PosX * 10) },
                    { "y", (short)(data.PosY * 10) },
                    { "z", (short)(data.PosZ * 10) },
                    { "vx", (sbyte)(data.VelX * 10) },
                    { "vy", (sbyte)(data.VelY * 10) },
                    { "vz", (sbyte)(data.VelZ * 10) },
                    { "h", (byte)(data.Heading / 1.41f) },
                    { "e", data.EngineRunning ? 1 : 0 },
                    { "hp", (byte)(data.Health / 10) },
                    { "o", myPlayerId },
                    { "t", GetTimestamp() }
                };

                pendingUpdates.Enqueue(new PendingUpdate
                {
                    Path = $"s/{sessionId}/v/{ownedVehicleId}",
                    Data = compressedData
                });

                lastSentVehicleData[ownedVehicleId] = data;
                vehiclesCache[ownedVehicleId] = data;
            }
            catch { }
        }

        public async Task<Dictionary<string, VehicleData>> GetSessionVehicles(string sessionId, GTA.Math.Vector3? myPosition = null)
        {
            var now = DateTime.UtcNow;

            if ((now - lastVehicleFetch).TotalMilliseconds < vehicleFetchInterval)
            {
                CachedResponses++;
                return new Dictionary<string, VehicleData>(vehiclesCache);
            }

            try
            {
                var vehicles = await firebase
                    .Child("s")
                    .Child(sessionId)
                    .Child("v")
                    .OnceAsync<Dictionary<string, object>>();

                TotalFirebaseCalls++;
                lastVehicleFetch = now;

                var result = new Dictionary<string, VehicleData>();

                foreach (var vehicle in vehicles)
                {
                    var data = vehicle.Object;
                    if (data == null) continue;

                    string ownerId = data.ContainsKey("o") ? data["o"].ToString() : "";
                    if (ownerId == myPlayerId)
                    {
                        SelfSyncBlocked++;
                        continue;
                    }

                    var vehicleData = new VehicleData
                    {
                        Model = data.ContainsKey("m") ? Convert.ToInt32(data["m"]) : 0,
                        PosX = data.ContainsKey("x") ? Convert.ToInt16(data["x"]) / 10f : 0,
                        PosY = data.ContainsKey("y") ? Convert.ToInt16(data["y"]) / 10f : 0,
                        PosZ = data.ContainsKey("z") ? Convert.ToInt16(data["z"]) / 10f : 0,
                        VelX = data.ContainsKey("vx") ? Convert.ToSByte(data["vx"]) / 10f : 0,
                        VelY = data.ContainsKey("vy") ? Convert.ToSByte(data["vy"]) / 10f : 0,
                        VelZ = data.ContainsKey("vz") ? Convert.ToSByte(data["vz"]) / 10f : 0,
                        Heading = data.ContainsKey("h") ? Convert.ToByte(data["h"]) * 1.41f : 0,
                        EngineRunning = data.ContainsKey("e") && Convert.ToInt32(data["e"]) == 1,
                        Health = data.ContainsKey("hp") ? Convert.ToByte(data["hp"]) * 10 : 1000,
                        Timestamp = data.ContainsKey("t") ? Convert.ToInt64(data["t"]) : 0
                    };

                    if (myPosition.HasValue)
                    {
                        float distance = (float)Math.Sqrt(
                            Math.Pow(vehicleData.PosX - myPosition.Value.X, 2) +
                            Math.Pow(vehicleData.PosY - myPosition.Value.Y, 2) +
                            Math.Pow(vehicleData.PosZ - myPosition.Value.Z, 2)
                        );

                        if (distance > interestRadius * 1.5f)
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

        public async Task UpdateEnvironment(string sessionId, int weather, int hour)
        {
            try
            {
                if (environmentCache != null &&
                    environmentCache.Weather == weather &&
                    environmentCache.Hour == hour)
                {
                    CachedResponses++;
                    return;
                }

                var envDict = new Dictionary<string, object>
                {
                    { "w", (byte)weather },
                    { "h", (byte)hour },
                    { "t", GetTimestamp() }
                };

                await firebase
                    .Child("s")
                    .Child(sessionId)
                    .Child("e")
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

            if ((now - lastEnvironmentFetch).TotalMilliseconds < environmentFetchInterval && environmentCache != null)
            {
                CachedResponses++;
                return environmentCache;
            }

            try
            {
                var env = await firebase
                    .Child("s")
                    .Child(sessionId)
                    .Child("e")
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

        public async Task<string> SendChatMessage(string sessionId, string playerId, string playerName, string message)
        {
            try
            {
                var messageId = GenerateCompactId();
                var messageData = new Dictionary<string, object>
                {
                    { "pid", playerId },
                    { "pn", playerName.Substring(0, Math.Min(12, playerName.Length)) },
                    { "msg", message.Substring(0, Math.Min(100, message.Length)) },
                    { "t", GetTimestamp() }
                };

                await firebase
                    .Child("s")
                    .Child(sessionId)
                    .Child("chat")
                    .Child(messageId)
                    .PutAsync(messageData);

                TotalFirebaseCalls++;
                return messageId;
            }
            catch (Exception ex)
            {
                chatSystem.AddSystemMessage($"[CHAT ERROR] {ex.Message}");
                return null;
            }
        }

        public async Task<List<ChatMessage>> GetChatMessages(string sessionId)
        {
            var now = DateTime.UtcNow;

            if ((now - lastChatFetch).TotalMilliseconds < chatFetchInterval)
            {
                CachedResponses++;
                return new List<ChatMessage>(chatCache);
            }

            try
            {
                var messages = await firebase
                    .Child("s")
                    .Child(sessionId)
                    .Child("chat")
                    .OrderByKey()
                    .LimitToLast(20)
                    .OnceAsync<Dictionary<string, object>>();

                TotalFirebaseCalls++;
                lastChatFetch = now;

                var result = new List<ChatMessage>();

                foreach (var msg in messages)
                {
                    var data = msg.Object;
                    if (data == null) continue;

                    var chatMessage = new ChatMessage
                    {
                        Id = msg.Key,
                        PlayerId = data.ContainsKey("pid") ? data["pid"].ToString() : "",
                        PlayerName = data.ContainsKey("pn") ? data["pn"].ToString() : "Unknown",
                        Message = data.ContainsKey("msg") ? data["msg"].ToString() : "",
                        Timestamp = data.ContainsKey("t") ? Convert.ToInt64(data["t"]) : 0
                    };

                    result.Add(chatMessage);
                }

                chatCache = result;
                return result;
            }
            catch
            {
                return new List<ChatMessage>(chatCache);
            }
        }

        public async Task SendDamageEvent(string sessionId, string targetPlayerId, string attackerId, float newHealth)
        {
            try
            {
                var damageData = new Dictionary<string, object>
                {
                    { "aid", attackerId },
                    { "hp", (byte)(newHealth / 4) },
                    { "t", GetTimestamp() }
                };

                await firebase
                    .Child("s")
                    .Child(sessionId)
                    .Child("p")
                    .Child(targetPlayerId)
                    .Child("dmg")
                    .PutAsync(damageData);

                TotalFirebaseCalls++;
            }
            catch { }
        }

        private async void ProcessBatchUpdates()
        {
            if (pendingUpdates.Count == 0) return;

            try
            {
                var batch = new List<PendingUpdate>();
                while (pendingUpdates.Count > 0 && batch.Count < 10)
                {
                    batch.Add(pendingUpdates.Dequeue());
                }

                var tasks = batch.Select(update =>
                    firebase.Child(update.Path).PutAsync(update.Data)
                );

                await Task.WhenAll(tasks);

                TotalFirebaseCalls += batch.Count;
                TotalBytesSent += batch.Sum(u => EstimateDataSize(u.Data));
            }
            catch { }
        }

        private long EstimateDataSize(Dictionary<string, object> data)
        {
            long size = 0;
            foreach (var kvp in data)
            {
                size += kvp.Key.Length;

                if (kvp.Value is string s)
                    size += s.Length;
                else if (kvp.Value is int || kvp.Value is float)
                    size += 4;
                else if (kvp.Value is short || kvp.Value is ushort)
                    size += 2;
                else if (kvp.Value is byte || kvp.Value is sbyte)
                    size += 1;
                else if (kvp.Value is long)
                    size += 8;
            }
            return size;
        }

        private byte PackFlags(PlayerData data)
        {
            byte flags = 0;
            if (data.IsAlive) flags |= 1;
            if (data.InVehicle) flags |= 2;
            return flags;
        }

        private void UnpackFlags(byte flags, PlayerData data)
        {
            data.IsAlive = (flags & 1) != 0;
            data.InVehicle = (flags & 2) != 0;
        }

        private byte GetAnimByte(string anim)
        {
            switch (anim)
            {
                case "idle": return 0;
                case "running": return 1;
                case "shooting": return 2;
                case "ragdoll": return 3;
                default: return 0;
            }
        }

        private string GetAnimString(byte anim)
        {
            switch (anim)
            {
                case 0: return "idle";
                case 1: return "running";
                case 2: return "shooting";
                case 3: return "ragdoll";
                default: return "idle";
            }
        }

        private string GenerateCompactId()
        {
            const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            var random = new Random();
            return new string(Enumerable.Repeat(chars, 8)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        private long GetTimestamp()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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

            return $"FB: {TotalFirebaseCalls} | Cache: {CachedResponses} ({hitRate:F0}%) | SelfBlock: {SelfSyncBlocked} | Data: {TotalBytesSent / 1024}KB";
        }

        public void ClearCache()
        {
            playersCache.Clear();
            vehiclesCache.Clear();
            environmentCache = null;
            chatCache.Clear();
            lastSentPlayerData.Clear();
            lastSentVehicleData.Clear();
        }

        public async Task CleanupOldSessions()
        {
            try
            {
                var sessions = await firebase
                    .Child("s")
                    .OnceAsync<Dictionary<string, object>>();

                TotalFirebaseCalls++;

                var currentTime = GetTimestamp();

                foreach (var session in sessions)
                {
                    var data = session.Object;
                    if (data == null) continue;

                    if (data.ContainsKey("hb"))
                    {
                        var heartbeat = Convert.ToInt64(data["hb"]);
                        if (currentTime - heartbeat > 60)
                        {
                            await firebase
                                .Child("s")
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