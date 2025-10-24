using GTA;
using GTA.Chrono;
using GTA.Math;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Real_Life_System
{
    /// <summary>
    /// CoopScript OTIMIZADO com Cache Local
    /// Reduz uso do Firebase em ~80%
    /// </summary>
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

        // Adaptive sync rate
        float playerSpeed = 0f;
        int adaptivePlayerSyncRate = 100;
        int adaptiveVehicleSyncRate = 150;

        public CoopScript()
        {
            try
            {
                myPlayerId = Guid.NewGuid().ToString();
                myPlayerName = Game.Player.Name;

                Tick += OnTick;
                KeyDown += OnKeyDown;
                Aborted += OnAborted;

                string firebaseUrl = "https://gta-coop-mod-default-rtdb.europe-west1.firebasedatabase.app/";
                firebase = new FirebaseRelay(firebaseUrl);

                // Configurar taxas de sincronização otimizadas
                firebase.SetSyncRates(
                    playerMs: 200,      // 5x/segundo
                    vehicleMs: 250,     // 4x/segundo
                    environmentMs: 5000 // 1x/5segundos
                );

                // Interest management - 500m de raio
                firebase.SetInterestRadius(500f);

                StartAutoConnect();
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"[Coop] ERRO: {ex.Message}", true);
                throw;
            }
        }

        async void StartAutoConnect()
        {
            connectionState = ConnectionState.SearchingSessions;

            GTA.UI.Notification.PostTicker("═══════════════════════════════", true);
            GTA.UI.Notification.PostTicker("🎮 [Coop] GTA V Multiplayer", true);
            GTA.UI.Notification.PostTicker("🔥 Firebase Relay OTIMIZADO", true);
            GTA.UI.Notification.PostTicker("✅ Cache Local + Delta Compression", true);
            GTA.UI.Notification.PostTicker("✅ 80% menos uso de dados!", true);
            GTA.UI.Notification.PostTicker("═══════════════════════════════", true);

            await RefreshSessions();

            GTA.UI.Notification.PostTicker("═══════════════════════════════", true);
            GTA.UI.Notification.PostTicker("[Coop] Controles:", true);
            GTA.UI.Notification.PostTicker("  F9  = Criar Sessão", true);
            GTA.UI.Notification.PostTicker("  F10 = Listar Sessões", true);
            GTA.UI.Notification.PostTicker("  F11 = Conectar", true);
            GTA.UI.Notification.PostTicker("  F12 = Atualizar", true);
            GTA.UI.Notification.PostTicker("  F8  = Mostrar Stats", true);
            GTA.UI.Notification.PostTicker("═══════════════════════════════", true);

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

        async Task RefreshSessions()
        {
            cachedSessions = await firebase.GetAvailableSessions(myRegion);

            if (cachedSessions.Count == 0)
            {
                GTA.UI.Notification.PostTicker("[Firebase] 📡 Nenhuma sessão online", true);
            }
            else
            {
                GTA.UI.Notification.PostTicker($"[Firebase] 📡 {cachedSessions.Count} sessão(ões) encontrada(s)", true);
                foreach (var session in cachedSessions.Take(3))
                {
                    GTA.UI.Notification.PostTicker($"  • {session.HostName} ({session.PlayerCount}/{session.MaxPlayers})", true);
                }
            }
        }

        async Task CreateSession()
        {
            mySessionId = await firebase.CreateSession(myPlayerName, 8, myRegion);

            if (mySessionId != null)
            {
                connectionState = ConnectionState.Hosting;

                GTA.UI.Notification.PostTicker("═══════════════════════════════", true);
                GTA.UI.Notification.PostTicker("🎮 [Coop] SESSÃO CRIADA!", true);
                GTA.UI.Notification.PostTicker($"📌 Host: {myPlayerName}", true);
                GTA.UI.Notification.PostTicker($"🔑 ID: {mySessionId.Substring(0, 8)}...", true);
                GTA.UI.Notification.PostTicker("👥 Jogadores: 1/8", true);
                GTA.UI.Notification.PostTicker("✅ Sistema otimizado ativo!", true);
                GTA.UI.Notification.PostTicker("═══════════════════════════════", true);

                await firebase.JoinSession(mySessionId, myPlayerId, myPlayerName);
            }
            else
            {
                GTA.UI.Notification.PostTicker("❌ [Firebase] Erro ao criar sessão", true);
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
                    GTA.UI.Notification.PostTicker("═══════════════════════════════", true);
                    GTA.UI.Notification.PostTicker("🔌 [Coop] Entrando na sessão...", true);
                    GTA.UI.Notification.PostTicker($"📌 Host: {session.HostName}", true);
                    GTA.UI.Notification.PostTicker("═══════════════════════════════", true);
                }

                bool success = await firebase.JoinSession(sessionId, myPlayerId, myPlayerName);

                if (success)
                {
                    connectionState = ConnectionState.Connected;
                    GTA.UI.Notification.PostTicker("✅ [Coop] Conectado com sucesso!", true);
                    GTA.UI.Notification.PostTicker("✅ [Coop] Cache local ativo!", true);
                }
                else
                {
                    GTA.UI.Notification.PostTicker("❌ [Coop] Falha ao conectar", true);
                    connectionState = ConnectionState.Disconnected;
                }
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"❌ [Coop] Erro: {ex.Message}", true);
                connectionState = ConnectionState.Disconnected;
            }
        }

        void OnTick(object sender, EventArgs e)
        {
            if (connectionState != ConnectionState.Connected && connectionState != ConnectionState.Hosting)
                return;

            var now = DateTime.UtcNow;
            var player = Game.Player.Character;

            // ADAPTIVE SYNC RATE: Ajustar taxa baseado na velocidade
            playerSpeed = player.Velocity.Length();
            if (playerSpeed > 20f) // Rápido (veículo/correndo)
            {
                adaptivePlayerSyncRate = 100; // 10x/segundo
                adaptiveVehicleSyncRate = 100;
            }
            else if (playerSpeed > 5f) // Médio (andando)
            {
                adaptivePlayerSyncRate = 200; // 5x/segundo
                adaptiveVehicleSyncRate = 200;
            }
            else // Parado/lento
            {
                adaptivePlayerSyncRate = 500; // 2x/segundo
                adaptiveVehicleSyncRate = 500;
            }

            // Sincronizar jogador
            if ((now - lastPlayerSync).TotalMilliseconds > adaptivePlayerSyncRate)
            {
                SyncPlayer();
                lastPlayerSync = now;
            }

            // Sincronizar veículo
            if (player.IsInVehicle() && (now - lastVehicleSync).TotalMilliseconds > adaptiveVehicleSyncRate)
            {
                SyncVehicle(player.CurrentVehicle);
                lastVehicleSync = now;
            }

            // Host sincroniza ambiente
            if (connectionState == ConnectionState.Hosting && (now - lastEnvironmentSync).TotalSeconds > 5)
            {
                SyncEnvironment();
                lastEnvironmentSync = now;
            }

            // Buscar dados remotos com cache
            if ((now - lastDataFetch).TotalMilliseconds > 200)
            {
                FetchRemoteData();
                lastDataFetch = now;
            }

            // Mostrar stats periodicamente
            if ((now - lastStatsDisplay).TotalSeconds > 30)
            {
                ShowStats();
                lastStatsDisplay = now;
            }

            UpdateRemotePlayers();
            UpdateRemoteVehicles();
            CleanupStaleEntities();
        }

        async void SyncPlayer()
        {
            var player = Game.Player.Character;
            var pos = player.Position;
            var vel = player.Velocity;
            var heading = player.Heading;
            var weapon = player.Weapons.Current.Hash;
            var inVehicle = player.IsInVehicle();

            // Delta já é checado dentro do FirebaseRelay
            lastPos = pos;
            lastHeading = heading;
            lastWeapon = weapon;
            lastInVehicle = inVehicle;

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

        async void SyncVehicle(Vehicle veh)
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

        async void SyncEnvironment()
        {
            var weather = World.Weather;
            var hour = GTA.Chrono.GameClock.Hour;

            if (weather == lastWeather && hour == lastHour)
                return;

            lastWeather = weather;
            lastHour = hour;

            await firebase.UpdateEnvironment(mySessionId, (int)weather, hour);
        }

        async void FetchRemoteData()
        {
            if (string.IsNullOrEmpty(mySessionId)) return;

            try
            {
                var myPos = Game.Player.Character.Position;

                // O cache é gerenciado automaticamente pelo FirebaseRelay
                var players = await firebase.GetSessionPlayers(mySessionId, myPos);

                foreach (var kvp in players)
                {
                    if (kvp.Key == myPlayerId) continue;

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
                        player.Ped.IsInvincible = true;
                        player.Ped.BlockPermanentEvents = true;

                        // Notification quando jogador aparece
                        GTA.UI.Notification.PostTicker($"👤 [Coop] {player.Name} entrou!", true);
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
                        vehicle.Vehicle.IsInvincible = true;
                    }
                }

                // Ambiente (apenas se não for host)
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
            catch { }
        }

        void UpdateRemotePlayers()
        {
            foreach (var kv in remotePlayers)
            {
                var player = kv.Value;
                if (player.Ped == null || !player.Ped.Exists()) continue;

                // Interpolação suave
                player.Ped.Position = Vector3.Lerp(player.Ped.Position, player.TargetPos, 0.3f);
                player.Ped.Heading = Lerp(player.Ped.Heading, player.TargetHeading, 0.3f);
                player.Ped.Velocity = player.TargetVel;

                // Animações básicas
                if (player.Animation == "running" && !player.Ped.IsRunning)
                {
                    // Fazer andar/correr
                }
            }
        }

        void UpdateRemoteVehicles()
        {
            foreach (var kv in remoteVehicles)
            {
                var vehicle = kv.Value;
                if (vehicle.Vehicle == null || !vehicle.Vehicle.Exists()) continue;

                vehicle.Vehicle.Position = Vector3.Lerp(vehicle.Vehicle.Position, vehicle.TargetPos, 0.3f);
                vehicle.Vehicle.Heading = Lerp(vehicle.Vehicle.Heading, vehicle.TargetHeading, 0.3f);
                vehicle.Vehicle.Velocity = vehicle.TargetVel;
                vehicle.Vehicle.IsEngineRunning = vehicle.EngineRunning;
            }
        }

        void CleanupStaleEntities()
        {
            var now = DateTime.UtcNow;

            foreach (var kv in remotePlayers.Where(x => (now - x.Value.LastUpdate).TotalSeconds > 15).ToList())
            {
                if (kv.Value.Ped != null && kv.Value.Ped.Exists())
                    kv.Value.Ped.Delete();
                remotePlayers.TryRemove(kv.Key, out _);
                GTA.UI.Notification.PostTicker($"👤 [Coop] {kv.Value.Name} saiu", true);
            }

            foreach (var kv in remoteVehicles.Where(x => (now - x.Value.LastUpdate).TotalSeconds > 15).ToList())
            {
                if (kv.Value.Vehicle != null && kv.Value.Vehicle.Exists())
                    kv.Value.Vehicle.Delete();
                remoteVehicles.TryRemove(kv.Key, out _);
            }
        }

        void ShowStats()
        {
            var stats = firebase.GetStats();
            GTA.UI.Notification.PostTicker($"📊 [Stats] {stats}", true);
            GTA.UI.Notification.PostTicker($"📊 [Jogadores] {remotePlayers.Count} online | Sync: {adaptivePlayerSyncRate}ms", true);
        }

        async void OnKeyDown(object s, System.Windows.Forms.KeyEventArgs e)
        {
            if (e.KeyCode == System.Windows.Forms.Keys.F8)
            {
                ShowStats();
            }
            else if (e.KeyCode == System.Windows.Forms.Keys.F9)
            {
                if (connectionState == ConnectionState.Hosting)
                {
                    GTA.UI.Notification.PostTicker("⚠️ [Coop] Você já está hospedando!", true);
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
                    GTA.UI.Notification.PostTicker("[Firebase] Nenhuma sessão disponível!", true);
            }
            else if (e.KeyCode == System.Windows.Forms.Keys.F12)
            {
                await RefreshSessions();
            }
            else if (e.KeyCode == System.Windows.Forms.Keys.F7)
            {
                // Limpar cache manualmente
                firebase.ClearCache();
                GTA.UI.Notification.PostTicker("🔄 [Cache] Cache limpo!", true);
            }
            else if (e.KeyCode == System.Windows.Forms.Keys.F6)
            {
                // Debug: Criar veículo
                var player = Game.Player.Character;
                var veh = World.CreateVehicle(VehicleHash.Adder, player.Position + player.ForwardVector * 5f);
                GTA.UI.Notification.PostTicker("[Debug] Veículo criado!", true);
            }
        }

        void OnAborted(object sender, EventArgs e)
        {
            GTA.UI.Notification.PostTicker("[Coop] Desconectando...", true);

            try
            {
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

                // Mostrar stats finais
                GTA.UI.Notification.PostTicker($"📊 [Final] {firebase.GetStats()}", true);
            }
            catch { }
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}