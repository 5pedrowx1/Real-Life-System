using GTA;
using GTA.Chrono;
using GTA.Math;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Real_Life_System
{
    public class CoopScript : Script
    {
        FirebaseRelay firebase;
        string myPlayerId;
        string myPlayerName;
        string myRegion = "EU";
        string mySessionId;

        enum ConnectionState
        {
            Disconnected,
            SearchingSessions,
            Connected,
            Hosting
        }
        ConnectionState connectionState = ConnectionState.Disconnected;

        List<SessionInfo> cachedSessions = new List<SessionInfo>();
        ConcurrentDictionary<string, RemotePlayer> remotePlayers = new ConcurrentDictionary<string, RemotePlayer>();
        ConcurrentDictionary<string, RemoteVehicle> remoteVehicles = new ConcurrentDictionary<string, RemoteVehicle>();

        Vector3 lastPos;
        float lastHeading;
        WeaponHash lastWeapon;
        bool lastInVehicle;
        Weather lastWeather = Weather.Clear;
        int lastHour = 12;

        DateTime lastPlayerSync = DateTime.MinValue;
        DateTime lastVehicleSync = DateTime.MinValue;
        DateTime lastEnvironmentSync = DateTime.MinValue;
        DateTime lastDataFetch = DateTime.MinValue;
        DateTime lastStatsDisplay = DateTime.MinValue;

        float playerSpeed = 0f;
        int adaptivePlayerSyncRate = 50;
        int adaptiveVehicleSyncRate = 100;

        int frameCount = 0;
        DateTime lastFpsCheck = DateTime.UtcNow;
        float currentFps = 0;

        public CoopScript()
        {
            try
            {
                myPlayerId = GenerateCompactPlayerId();
                myPlayerName = Game.Player.Name;

                Tick += OnTick;
                KeyDown += OnKeyDown;
                Aborted += OnAborted;

                string firebaseUrl = "https://gta-coop-mod-default-rtdb.europe-west1.firebasedatabase.app/";
                firebase = new FirebaseRelay(firebaseUrl);

                firebase.SetMyPlayerId(myPlayerId);

                firebase.SetSyncRates(
                    playerMs: 50,
                    vehicleMs: 100,
                    environmentMs: 15000
                );

                firebase.SetInterestRadius(300f);

                GTA.UI.Notification.PostTicker("[MP] Mod carregado!", true);

                StartAutoConnect();
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"[MP] ERRO: {ex.Message}", true);
            }
        }

        async void StartAutoConnect()
        {
            try
            {
                connectionState = ConnectionState.SearchingSessions;

                await RefreshSessions();

                GTA.UI.Notification.PostTicker("[MP] Controles:", true);
                GTA.UI.Notification.PostTicker("  F8  = Stats", true);
                GTA.UI.Notification.PostTicker("  F9  = Criar Sessão", true);
                GTA.UI.Notification.PostTicker("  F10 = Listar Sessões", true);
                GTA.UI.Notification.PostTicker("  F11 = Conectar", true);

                await Task.Delay(3000);

                if (connectionState == ConnectionState.SearchingSessions)
                {
                    var bestSession = cachedSessions
                        .Where(s => s.PlayerCount < s.MaxPlayers)
                        .OrderByDescending(s => s.PlayerCount)
                        .FirstOrDefault();

                    if (bestSession != null)
                    {
                        await JoinSession(bestSession.SessionId);
                    }
                    else
                    {
                        await CreateSession();
                    }
                }
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"[MP] AutoConnect: {ex.Message}", true);
            }
        }

        async Task RefreshSessions()
        {
            try
            {
                cachedSessions = await firebase.GetAvailableSessions(myRegion);

                if (cachedSessions.Count == 0)
                {
                    GTA.UI.Notification.PostTicker("[FB] Nenhuma sessão online", true);
                }
                else
                {
                    GTA.UI.Notification.PostTicker($"[FB] {cachedSessions.Count} sessão(ões)", true);
                    foreach (var session in cachedSessions.Take(3))
                    {
                        GTA.UI.Notification.PostTicker($"{session.HostName} ({session.PlayerCount}/{session.MaxPlayers})", true);
                    }
                }
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"Refresh: {ex.Message}", true);
            }
        }

        async Task CreateSession()
        {
            try
            {
                mySessionId = await firebase.CreateSession(myPlayerName, 16, myRegion);

                if (mySessionId != null)
                {
                    connectionState = ConnectionState.Hosting;
                    GTA.UI.Notification.PostTicker("[MP] SESSÃO CRIADA!", true);
                    GTA.UI.Notification.PostTicker($"Host: {myPlayerName}", true);
                    await firebase.JoinSession(mySessionId, myPlayerId, myPlayerName);
                }
                else
                {
                    GTA.UI.Notification.PostTicker("[FB] Erro ao criar sessão", true);
                }
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"Create: {ex.Message}", true);
            }
        }

        async Task JoinSession(string sessionId)
        {
            try
            {
                mySessionId = sessionId;

                var session = cachedSessions.FirstOrDefault(s => s.SessionId == sessionId);
                if (session != null)
                {
                    GTA.UI.Notification.PostTicker($"[MP] Conectando a {session.HostName}...", true);
                }

                bool success = await firebase.JoinSession(sessionId, myPlayerId, myPlayerName);

                if (success)
                {
                    connectionState = ConnectionState.Connected;
                    GTA.UI.Notification.PostTicker("[MP] Conectado!", true);
                }
                else
                {
                    GTA.UI.Notification.PostTicker("[MP] Falha ao conectar", true);
                    connectionState = ConnectionState.Disconnected;
                }
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"Join: {ex.Message}", true);
                connectionState = ConnectionState.Disconnected;
            }
        }

        void OnTick(object sender, EventArgs e)
        {
            try
            {
                if (connectionState != ConnectionState.Connected && connectionState != ConnectionState.Hosting)
                    return;

                var now = DateTime.UtcNow;
                var player = Game.Player.Character;

                if (player == null || !player.Exists()) return;

                frameCount++;
                if ((now - lastFpsCheck).TotalSeconds >= 1)
                {
                    currentFps = frameCount / (float)(now - lastFpsCheck).TotalSeconds;
                    frameCount = 0;
                    lastFpsCheck = now;
                }

                playerSpeed = player.Velocity.Length();

                if (playerSpeed > 50f)
                {
                    adaptivePlayerSyncRate = 33;
                    adaptiveVehicleSyncRate = 33;
                }
                else if (playerSpeed > 20f)
                {
                    adaptivePlayerSyncRate = 50;
                    adaptiveVehicleSyncRate = 50;
                }
                else if (playerSpeed > 5f)
                {
                    adaptivePlayerSyncRate = 100;
                    adaptiveVehicleSyncRate = 100;
                }
                else
                {
                    adaptivePlayerSyncRate = 200;
                    adaptiveVehicleSyncRate = 200;
                }

                if ((now - lastPlayerSync).TotalMilliseconds > adaptivePlayerSyncRate)
                {
                    SyncPlayer();
                    lastPlayerSync = now;
                }

                if (player.IsInVehicle() && player.CurrentVehicle != null && player.CurrentVehicle.Exists())
                {
                    if (player.CurrentVehicle.GetPedOnSeat(VehicleSeat.Driver) == player)
                    {
                        if ((now - lastVehicleSync).TotalMilliseconds > adaptiveVehicleSyncRate)
                        {
                            SyncVehicle(player.CurrentVehicle);
                            lastVehicleSync = now;
                        }
                    }
                }

                if (connectionState == ConnectionState.Hosting && (now - lastEnvironmentSync).TotalSeconds > 15)
                {
                    SyncEnvironment();
                    lastEnvironmentSync = now;
                }

                if ((now - lastDataFetch).TotalMilliseconds > 100)
                {
                    FetchRemoteData();
                    lastDataFetch = now;
                }

                if ((now - lastStatsDisplay).TotalSeconds > 30)
                {
                    ShowStats();
                    lastStatsDisplay = now;
                }

                UpdateRemotePlayers();
                UpdateRemoteVehicles();
                CleanupStaleEntities();
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"Tick Error: {ex.Message}", true);
            }
        }

        async void SyncPlayer()
        {
            try
            {
                var player = Game.Player.Character;
                if (player == null || !player.Exists()) return;

                var pos = player.Position;
                var vel = player.Velocity;
                var heading = player.Heading;
                var weapon = player.Weapons.Current.Hash;
                var inVehicle = player.IsInVehicle();

                string anim = "idle";
                if (player.IsRunning) anim = "running";
                else if (player.IsShooting) anim = "shooting";
                else if (player.IsRagdoll) anim = "ragdoll";

                var data = new PlayerData
                {
                    Name = myPlayerName,
                    PosX = pos.X,
                    PosY = pos.Y,
                    PosZ = pos.Z,
                    VelX = vel.X,
                    VelY = vel.Y,
                    VelZ = vel.Z,
                    Heading = heading,
                    Animation = anim,
                    IsAlive = player.IsAlive,
                    InVehicle = inVehicle,
                    Health = player.Health,
                    Weapon = (int)weapon,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                await firebase.UpdatePlayerData(mySessionId, myPlayerId, data);
            }
            catch
            {
                // Silently fail
            }
        }

        async void SyncVehicle(Vehicle veh)
        {
            try
            {
                if (veh == null || !veh.Exists()) return;

                var data = new VehicleData
                {
                    Model = (int)veh.Model.Hash,
                    PosX = veh.Position.X,
                    PosY = veh.Position.Y,
                    PosZ = veh.Position.Z,
                    VelX = veh.Velocity.X,
                    VelY = veh.Velocity.Y,
                    VelZ = veh.Velocity.Z,
                    Heading = veh.Heading,
                    EngineRunning = veh.IsEngineRunning,
                    Health = veh.Health,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                await firebase.UpdateVehicleData(mySessionId, veh.Handle.ToString(), data);
            }
            catch
            {
                // Silently fail
            }
        }

        async void SyncEnvironment()
        {
            try
            {
                var weather = World.Weather;
                var hour = GTA.Chrono.GameClock.Hour;

                if (weather == lastWeather && hour == lastHour)
                    return;

                lastWeather = weather;
                lastHour = hour;

                await firebase.UpdateEnvironment(mySessionId, (int)weather, hour);
            }
            catch
            {
                // Silently fail
            }
        }

        async void FetchRemoteData()
        {
            if (string.IsNullOrEmpty(mySessionId)) return;

            try
            {
                var myPos = Game.Player.Character.Position;
                var players = await firebase.GetSessionPlayers(mySessionId, myPos);

                foreach (var kvp in players)
                {
                    if (kvp.Key == myPlayerId)
                        continue;

                    var data = kvp.Value;

                    var player = remotePlayers.GetOrAdd(kvp.Key, id => new RemotePlayer { Id = id });
                    player.Name = data.Name;
                    player.TargetPos = new Vector3(data.PosX, data.PosY, data.PosZ);
                    player.TargetVel = new Vector3(data.VelX, data.VelY, data.VelZ);
                    player.TargetHeading = data.Heading;
                    player.Animation = data.Animation;
                    player.IsAlive = data.IsAlive;
                    player.InVehicle = data.InVehicle;
                    player.Health = data.Health;
                    player.CurrentWeapon = (WeaponHash)data.Weapon;
                    player.LastUpdate = DateTime.UtcNow;

                    if (player.Ped == null || !player.Ped.Exists())
                    {
                        player.Ped = World.CreatePed(PedHash.FreemodeMale01, player.TargetPos);
                        if (player.Ped != null && player.Ped.Exists())
                        {
                            player.Ped.IsInvincible = true;
                            player.Ped.BlockPermanentEvents = true;
                            player.Ped.CanRagdoll = false;

                            GTA.UI.Notification.PostTicker($"👤 {player.Name} entrou!", true);
                        }
                    }
                }

                var vehicles = await firebase.GetSessionVehicles(mySessionId, myPos);

                foreach (var kvp in vehicles)
                {
                    var data = kvp.Value;

                    var vehicle = remoteVehicles.GetOrAdd(kvp.Key, id => new RemoteVehicle { Id = id });
                    vehicle.Model = (VehicleHash)data.Model;
                    vehicle.TargetPos = new Vector3(data.PosX, data.PosY, data.PosZ);
                    vehicle.TargetVel = new Vector3(data.VelX, data.VelY, data.VelZ);
                    vehicle.TargetHeading = data.Heading;
                    vehicle.EngineRunning = data.EngineRunning;
                    vehicle.Health = data.Health;
                    vehicle.LastUpdate = DateTime.UtcNow;

                    if (vehicle.Vehicle == null || !vehicle.Vehicle.Exists())
                    {
                        vehicle.Vehicle = World.CreateVehicle(vehicle.Model, vehicle.TargetPos);
                        if (vehicle.Vehicle != null && vehicle.Vehicle.Exists())
                        {
                            vehicle.Vehicle.IsInvincible = true;
                        }
                    }
                }

                if (connectionState != ConnectionState.Hosting)
                {
                    var env = await firebase.GetEnvironment(mySessionId);
                    if (env != null)
                    {
                        World.Weather = (Weather)env.Weather;
                        GTA.Chrono.GameClock.TimeOfDay = GameClockTime.FromHms(env.Hour, 0, 0);
                    }
                }
            }
            catch
            {
                // Silently fail
            }
        }

        void UpdateRemotePlayers()
        {
            try
            {
                foreach (var kv in remotePlayers)
                {
                    var player = kv.Value;
                    if (player.Ped == null || !player.Ped.Exists()) continue;

                    float lerpFactor = playerSpeed > 20f ? 0.5f : 0.3f;

                    player.Ped.Position = Vector3.Lerp(player.Ped.Position, player.TargetPos, lerpFactor);
                    player.Ped.Heading = Lerp(player.Ped.Heading, player.TargetHeading, lerpFactor);
                    player.Ped.Velocity = player.TargetVel;

                    if (player.CurrentWeapon != WeaponHash.Unarmed)
                    {
                        player.Ped.Weapons.Give(player.CurrentWeapon, 9999, false, true);
                    }

                    if (player.Health > 0)
                    {
                        player.Ped.Health = (int)player.Health;
                    }
                }
            }
            catch
            {
                // Silently fail
            }
        }

        void UpdateRemoteVehicles()
        {
            try
            {
                foreach (var kv in remoteVehicles)
                {
                    var vehicle = kv.Value;
                    if (vehicle.Vehicle == null || !vehicle.Vehicle.Exists()) continue;

                    float lerpFactor = playerSpeed > 30f ? 0.6f : 0.4f;

                    vehicle.Vehicle.Position = Vector3.Lerp(vehicle.Vehicle.Position, vehicle.TargetPos, lerpFactor);
                    vehicle.Vehicle.Heading = Lerp(vehicle.Vehicle.Heading, vehicle.TargetHeading, lerpFactor);
                    vehicle.Vehicle.Velocity = vehicle.TargetVel;
                    vehicle.Vehicle.IsEngineRunning = vehicle.EngineRunning;
                    vehicle.Vehicle.Health = (int)vehicle.Health;
                }
            }
            catch
            {
                // Silently fail
            }
        }

        void CleanupStaleEntities()
        {
            try
            {
                var now = DateTime.UtcNow;

                foreach (var kv in remotePlayers.Where(x => (now - x.Value.LastUpdate).TotalSeconds > 10).ToList())
                {
                    if (kv.Value.Ped != null && kv.Value.Ped.Exists())
                        kv.Value.Ped.Delete();
                    remotePlayers.TryRemove(kv.Key, out _);
                    GTA.UI.Notification.PostTicker($"{kv.Value.Name} saiu", true);
                }

                foreach (var kv in remoteVehicles.Where(x => (now - x.Value.LastUpdate).TotalSeconds > 10).ToList())
                {
                    if (kv.Value.Vehicle != null && kv.Value.Vehicle.Exists())
                        kv.Value.Vehicle.Delete();
                    remoteVehicles.TryRemove(kv.Key, out _);
                }
            }
            catch
            {
                // Silently fail
            }
        }

        void ShowStats()
        {
            try
            {
                var stats = firebase.GetStats();
                GTA.UI.Notification.PostTicker($"{stats}", true);
                GTA.UI.Notification.PostTicker($"Players: {remotePlayers.Count} | FPS: {currentFps:F0}", true);
            }
            catch
            {
                // Silently fail
            }
        }

        async void OnKeyDown(object s, System.Windows.Forms.KeyEventArgs e)
        {
            try
            {
                if (e.KeyCode == System.Windows.Forms.Keys.F8)
                {
                    ShowStats();
                }
                else if (e.KeyCode == System.Windows.Forms.Keys.F9)
                {
                    if (connectionState == ConnectionState.Hosting)
                    {
                        GTA.UI.Notification.PostTicker("Já hospedando!", true);
                        return;
                    }
                    await CreateSession();
                }
                else if (e.KeyCode == System.Windows.Forms.Keys.F10)
                {
                    await RefreshSessions();
                }
                else if (e.KeyCode == System.Windows.Forms.Keys.F11)
                {
                    var bestSession = cachedSessions
                        .Where(session => session.PlayerCount < session.MaxPlayers)
                        .OrderByDescending(session => session.PlayerCount)
                        .FirstOrDefault();

                    if (bestSession != null)
                        await JoinSession(bestSession.SessionId);
                    else
                        GTA.UI.Notification.PostTicker("Nenhuma sessão!", true);
                }
                else if (e.KeyCode == System.Windows.Forms.Keys.F12)
                {
                    await RefreshSessions();
                }
                else if (e.KeyCode == System.Windows.Forms.Keys.F7)
                {
                    firebase.ClearCache();
                }
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"KeyDown: {ex.Message}", true);
            }
        }

        void OnAborted(object sender, EventArgs e)
        {
            try
            {
                GTA.UI.Notification.PostTicker("[MP] Desconectando...", true);

                if (!string.IsNullOrEmpty(mySessionId))
                {
                    Task.Run(async () =>
                    {
                        await firebase.LeaveSession(mySessionId, myPlayerId);

                        if (connectionState == ConnectionState.Hosting)
                        {
                            await firebase.DeleteSession(mySessionId);
                        }
                    }).Wait(2000);
                }

                foreach (var player in remotePlayers.Values)
                {
                    if (player.Ped != null && player.Ped.Exists())
                        player.Ped.Delete();
                }

                foreach (var vehicle in remoteVehicles.Values)
                {
                    if (vehicle.Vehicle != null && vehicle.Vehicle.Exists())
                        vehicle.Vehicle.Delete();
                }

                GTA.UI.Notification.PostTicker($"{firebase.GetStats()}", true);
            }
            catch
            {
                // Silently fail
            }
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        static string GenerateCompactPlayerId()
        {
            const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var random = new Random();
            return new string(Enumerable.Repeat(chars, 8)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }
    }
}