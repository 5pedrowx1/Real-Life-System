using GTA;
using GTA.Chrono;
using GTA.Math;
using GTA.Native;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Real_Life_System
{
    public class CoopScript : Script
    {
        readonly FirebaseRelay firebase;
        readonly ChatSystem chatSystem;
        readonly string myPlayerId;
        readonly string myPlayerName;
        readonly string myRegion = "EU";
        string mySessionId;
        ConnectionState connectionState = ConnectionState.Disconnected;
        List<SessionInfo> cachedSessions = new List<SessionInfo>();
        readonly ConcurrentDictionary<string, RemotePlayer> remotePlayers = new ConcurrentDictionary<string, RemotePlayer>();
        readonly ConcurrentDictionary<string, RemoteVehicle> remoteVehicles = new ConcurrentDictionary<string, RemoteVehicle>();
        Weather lastWeather = Weather.Clear;
        int lastHour = 12;
        DateTime lastPlayerSync = DateTime.MinValue;
        DateTime lastVehicleSync = DateTime.MinValue;
        DateTime lastEnvironmentSync = DateTime.MinValue;
        DateTime lastDataFetch = DateTime.MinValue;
        DateTime lastChatFetch = DateTime.MinValue;
        DateTime lastDamageCheck = DateTime.MinValue;
        DateTime lastSessionHealthCheck = DateTime.MinValue;
        DateTime lastFpsCheck = DateTime.UtcNow;
        float playerSpeed = 0f;
        int adaptivePlayerSyncRate = 50;
        int adaptiveVehicleSyncRate = 100;
        int frameCount = 0;
        float currentFps = 0;
        bool isCurrentHost = false;

        public CoopScript()
        {
            try
            {
                myPlayerId = GenerateCompactPlayerId();
                myPlayerName = Game.Player.Name;

                Tick += OnTick;
                KeyDown += OnKeyDown;
                KeyUp += OnKeyUp;
                Aborted += OnAborted;

                string firebaseUrl = "https://gta-coop-mod-default-rtdb.europe-west1.firebasedatabase.app/";
                chatSystem = new ChatSystem();
                firebase = new FirebaseRelay(firebaseUrl, chatSystem);

                firebase.SetMyPlayerId(myPlayerId);

                firebase.SetSyncRates(
                    playerMs: 50,
                    vehicleMs: 100,
                    environmentMs: 15000
                );

                firebase.SetInterestRadius(300f);
                chatSystem.AddSystemMessage("Pressione T para abrir o chat");

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

                chatSystem.AddSystemMessage("Controles:");
                chatSystem.AddSystemMessage("F8=Stats | F9=Criar | F10=Listar | F11=Conectar");

                await Task.Delay(2000);

                if (connectionState == ConnectionState.SearchingSessions)
                {
                    var bestSession = cachedSessions
                        .Where(s => s.PlayerCount < s.MaxPlayers && s.PlayerCount > 0)
                        .OrderByDescending(s => s.PlayerCount)
                        .ThenBy(s => s.SessionId)
                        .FirstOrDefault();

                    if (bestSession != null)
                    {
                        chatSystem.AddSystemMessage($"✓ Entrando: {bestSession.HostName}");
                        await JoinSession(bestSession.SessionId);
                        return;
                    }

                    var emptySession = cachedSessions
                        .Where(s => s.PlayerCount == 0 && s.PlayerCount < s.MaxPlayers)
                        .FirstOrDefault();

                    if (emptySession != null)
                    {
                        chatSystem.AddSystemMessage($"✓ Entrando: {emptySession.HostName}");
                        await JoinSession(emptySession.SessionId);
                        return;
                    }

                    chatSystem.AddSystemMessage("✓ Criando nova sessão...");
                    await CreateSession();
                }
            }
            catch (Exception ex)
            {
                chatSystem.AddErrorMessage($"AutoConnect: {ex.Message}");
                connectionState = ConnectionState.Disconnected;
            }
        }

        async Task RefreshSessions()
        {
            try
            {
                chatSystem.AddSystemMessage("Buscando sessões...");

                cachedSessions = await firebase.GetAvailableSessions(myRegion);

                if (cachedSessions.Count == 0)
                {
                    chatSystem.AddSystemMessage("Nenhuma sessão encontrada");
                }
                else
                {
                    chatSystem.AddSystemMessage($"✓ {cachedSessions.Count} sessão(ões)");
                    foreach (var session in cachedSessions.Take(3))
                    {
                        chatSystem.AddSystemMessage($"  → {session.HostName} ({session.PlayerCount}/{session.MaxPlayers})");
                    }
                }
            }
            catch (Exception ex)
            {
                chatSystem.AddErrorMessage($"Refresh: {ex.Message}");
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
                    chatSystem.AddSystemMessage("SESSÃO CRIADA!");
                    chatSystem.AddSystemMessage($"Host: {myPlayerName}");
                    chatSystem.AddSystemMessage($"ID: {mySessionId.Substring(0, 6)}");
                    await firebase.JoinSession(mySessionId, myPlayerId, myPlayerName);
                }
                else
                {
                    chatSystem.AddErrorMessage("Erro ao criar sessão");
                }
            }
            catch (Exception ex)
            {
                chatSystem.AddErrorMessage($"Create: {ex.Message}");
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
                    if (session.PlayerCount >= session.MaxPlayers)
                    {
                        chatSystem.AddErrorMessage($"Sessão de {session.HostName} está cheia!");
                        connectionState = ConnectionState.Disconnected;
                        return;
                    }

                    chatSystem.AddSystemMessage($"Conectando a {session.HostName}...");
                }

                bool success = await firebase.JoinSession(sessionId, myPlayerId, myPlayerName);

                if (success)
                {
                    connectionState = ConnectionState.Connected;
                    chatSystem.AddSystemMessage("✓ CONECTADO!");
                    chatSystem.AddSystemMessage("Bem-vindo ao servidor");
                    chatSystem.AddSystemMessage("Digite /help para comandos");
                }
                else
                {
                    chatSystem.AddErrorMessage("Falha ao conectar");
                    connectionState = ConnectionState.Disconnected;
                }
            }
            catch (Exception ex)
            {
                chatSystem.AddErrorMessage($"Join: {ex.Message}");
                connectionState = ConnectionState.Disconnected;
            }
        }

        void OnTick(object sender, EventArgs e)
        {
            try
            {
                chatSystem.Draw();

                if (connectionState != ConnectionState.Connected && connectionState != ConnectionState.Hosting)
                    return;

                var now = DateTime.UtcNow;
                var player = Game.Player.Character;

                if (player == null || !player.Exists()) return;

                if ((now - lastSessionHealthCheck).TotalSeconds > 5)
                {
                    Task.Run(async () =>
                    {
                        await firebase.MonitorSessionHealth();
                        isCurrentHost = await firebase.IsHost(mySessionId, myPlayerId);
                    });
                    lastSessionHealthCheck = now;
                }

                if (isCurrentHost && connectionState != ConnectionState.Hosting)
                {
                    connectionState = ConnectionState.Hosting;
                }

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

                if ((now - lastChatFetch).TotalMilliseconds > 500)
                {
                    FetchChatMessages();
                    lastChatFetch = now;
                }

                if ((now - lastDamageCheck).TotalMilliseconds > 100)
                {
                    CheckDamageDealt();
                    lastDamageCheck = now;
                }

                UpdateRemotePlayers();
                UpdateRemoteVehicles();
                CleanupStaleEntities();
                ApplyCollisionToRemotePlayers();
            }
            catch (Exception ex)
            {
                chatSystem.AddErrorMessage($"Tick: {ex.Message}");
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

                string vehicleId = null;
                int vehicleSeat = -1;

                if (inVehicle && player.CurrentVehicle != null && player.CurrentVehicle.Exists())
                {
                    vehicleId = player.CurrentVehicle.Handle.ToString();

                    if (player.CurrentVehicle.GetPedOnSeat(VehicleSeat.Driver) == player)
                        vehicleSeat = -1;
                    else if (player.CurrentVehicle.GetPedOnSeat(VehicleSeat.Passenger) == player)
                        vehicleSeat = 0;
                    else if (player.CurrentVehicle.GetPedOnSeat(VehicleSeat.LeftRear) == player)
                        vehicleSeat = 1;
                    else if (player.CurrentVehicle.GetPedOnSeat(VehicleSeat.RightRear) == player)
                        vehicleSeat = 2;
                }

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
                    VehicleId = vehicleId,
                    VehicleSeat = vehicleSeat,
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
                    player.VehicleId = data.VehicleId;
                    player.Seat = data.VehicleSeat;
                    player.Health = data.Health;
                    player.CurrentWeapon = (WeaponHash)data.Weapon;
                    player.LastUpdate = DateTime.UtcNow;

                    if (player.Ped == null || !player.Ped.Exists())
                    {
                        player.Ped = World.CreatePed(PedHash.FreemodeMale01, player.TargetPos);
                        if (player.Ped != null && player.Ped.Exists())
                        {
                            player.Ped.IsInvincible = false;
                            player.Ped.BlockPermanentEvents = true;
                            player.Ped.CanRagdoll = true;

                            chatSystem.AddSystemMessage($"✓ {player.Name} entrou");
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
                            vehicle.Vehicle.IsInvincible = false;
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

                    if (player.InVehicle && !string.IsNullOrEmpty(player.VehicleId))
                    {
                        var vehicleKvp = remoteVehicles.FirstOrDefault(v => v.Key.Contains(player.VehicleId));
                        if (vehicleKvp.Value != null && vehicleKvp.Value.Vehicle != null && vehicleKvp.Value.Vehicle.Exists())
                        {
                            var veh = vehicleKvp.Value.Vehicle;

                            VehicleSeat seat = VehicleSeat.Driver;
                            if (player.Seat == -1) seat = VehicleSeat.Driver;
                            else if (player.Seat == 0) seat = VehicleSeat.Passenger;
                            else if (player.Seat == 1) seat = VehicleSeat.LeftRear;
                            else if (player.Seat == 2) seat = VehicleSeat.RightRear;

                            if (!player.Ped.IsInVehicle() || player.Ped.CurrentVehicle != veh)
                            {
                                player.Ped.SetIntoVehicle(veh, seat);
                            }
                        }
                    }
                    else
                    {
                        if (player.Ped.IsInVehicle())
                        {
                            player.Ped.Task.LeaveVehicle();
                        }

                        player.Ped.Position = Vector3.Lerp(player.Ped.Position, player.TargetPos, lerpFactor);
                        player.Ped.Heading = Lerp(player.Ped.Heading, player.TargetHeading, lerpFactor);
                        player.Ped.Velocity = player.TargetVel;
                    }

                    if (player.CurrentWeapon != WeaponHash.Unarmed)
                    {
                        player.Ped.Weapons.Give(player.CurrentWeapon, 9999, false, true);
                    }

                    if (player.Health > 0)
                    {
                        player.Ped.Health = (int)player.Health;
                    }
                    else if (player.Ped.IsAlive)
                    {
                        player.Ped.Kill();
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

        void ApplyCollisionToRemotePlayers()
        {
            try
            {
                foreach (var kv in remotePlayers)
                {
                    var player = kv.Value;
                    if (player.Ped == null || !player.Ped.Exists()) continue;

                    Function.Call(Hash.SET_ENTITY_COLLISION, player.Ped.Handle, true, true);
                }
            }
            catch
            {
                // Silently fail
            }
        }

        void CheckDamageDealt()
        {
            try
            {
                var player = Game.Player.Character;
                if (player == null || !player.Exists()) return;

                foreach (var kv in remotePlayers)
                {
                    var remotePlayer = kv.Value;
                    if (remotePlayer.Ped == null || !remotePlayer.Ped.Exists()) continue;

                    if (remotePlayer.Ped.HasBeenDamagedBy(player))
                    {
                        float currentHealth = remotePlayer.Ped.Health;

                        Task.Run(async () =>
                        {
                            await firebase.SendDamageEvent(mySessionId, kv.Key, myPlayerId, currentHealth);
                        });

                        remotePlayer.Ped.ClearLastWeaponDamage();
                    }
                }
            }
            catch
            {
                // Silently fail
            }
        }

        async void FetchChatMessages()
        {
            if (string.IsNullOrEmpty(mySessionId)) return;

            try
            {
                var messages = await firebase.GetChatMessages(mySessionId);

                foreach (var msg in messages)
                {
                    if (msg.PlayerId == myPlayerId)
                    {
                        chatSystem.MarkMessageAsDisplayed(msg.Id);
                        continue;
                    }

                    if (chatSystem.IsMessageDisplayed(msg.Id))
                        continue;

                    chatSystem.AddMessage(msg.PlayerName, msg.Message, ChatMessageType.Normal);
                    chatSystem.MarkMessageAsDisplayed(msg.Id);
                }
            }
            catch
            {
                // Silently fail
            }
        }

        async void SendChatMessage(string message)
        {
            if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(mySessionId)) return;

            try
            {
                var cmd = chatSystem.ProcessCommand(message, myPlayerName);

                if (cmd == null) return;

                string messageId = $"{myPlayerId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

                chatSystem.MarkMessageAsDisplayed(messageId);
                chatSystem.AddMessage(myPlayerName, cmd.Message, cmd.Type);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await firebase.SendChatMessage(mySessionId, myPlayerId, myPlayerName, cmd.Message);
                    }
                    catch
                    {
                        // Silently fail
                    }
                });
            }
            catch (Exception ex)
            {
                chatSystem.AddErrorMessage($"Erro: {ex.Message}");
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
                    chatSystem.AddSystemMessage($"✗ {kv.Value.Name} saiu");
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
                chatSystem.AddSystemMessage(stats);
                chatSystem.AddSystemMessage($"Players: {remotePlayers.Count} | FPS: {currentFps:F0}");

                string hostStatus = isCurrentHost ? "HOST" : "Cliente";
                chatSystem.AddSystemMessage($"Status: {hostStatus} | Speed: {playerSpeed:F1} m/s");
            }
            catch
            {
                // Silently fail
            }
        }

        void OnKeyDown(object s, KeyEventArgs e)
        {
            try
            {
                if (chatSystem.IsActive)
                {
                    if (e.KeyCode == Keys.Up)
                    {
                        chatSystem.ScrollUp();
                        e.SuppressKeyPress = true;
                        return;
                    }
                    else if (e.KeyCode == Keys.Down)
                    {
                        chatSystem.ScrollDown();
                        e.SuppressKeyPress = true;
                        return;
                    }

                    if (e.KeyCode == Keys.Enter)
                    {
                        string input = chatSystem.GetInputAndClear();
                        if (!string.IsNullOrEmpty(input))
                        {
                            SendChatMessage(input);
                        }
                        chatSystem.Deactivate();
                        e.SuppressKeyPress = true;
                    }
                    else if (e.KeyCode == Keys.Escape)
                    {
                        chatSystem.Deactivate();
                        e.SuppressKeyPress = true;
                    }
                    else if (e.KeyCode == Keys.Back)
                    {
                        chatSystem.RemoveCharacter();
                        e.SuppressKeyPress = true;
                    }
                    return;
                }

                if (e.KeyCode == Keys.T)
                {
                    chatSystem.Activate();
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.F8)
                {
                    ShowStats();
                }
                else if (e.KeyCode == Keys.F9)
                {
                    if (connectionState == ConnectionState.Hosting)
                    {
                        chatSystem.AddSystemMessage("Já está hospedando!");
                        return;
                    }
                    if (connectionState == ConnectionState.Connected)
                    {
                        chatSystem.AddSystemMessage("Já está conectado!");
                        return;
                    }
                    Task.Run(async () => await CreateSession());
                }
                else if (e.KeyCode == Keys.F10)
                {
                    Task.Run(async () => await RefreshSessions());
                }
                else if (e.KeyCode == Keys.F11)
                {
                    if (connectionState == ConnectionState.Connected || connectionState == ConnectionState.Hosting)
                    {
                        chatSystem.AddSystemMessage("Já está conectado!");
                        return;
                    }

                    var bestSession = cachedSessions
                        .Where(session => session.PlayerCount < session.MaxPlayers)
                        .OrderByDescending(session => session.PlayerCount)
                        .FirstOrDefault();

                    if (bestSession != null)
                    {
                        Task.Run(async () => await JoinSession(bestSession.SessionId));
                    }
                    else
                    {
                        chatSystem.AddSystemMessage("Nenhuma sessão disponível!");
                    }
                }
                else if (e.KeyCode == Keys.F7)
                {
                    firebase.ClearCache();
                    Task.Run(async () => await firebase.CleanupExpiredSessions());
                    chatSystem.AddSystemMessage("✓ Limpeza completa iniciada");
                }
            }
            catch (Exception ex)
            {
                chatSystem.AddErrorMessage($"KeyDown: {ex.Message}");
            }
        }

        void OnKeyUp(object s, KeyEventArgs e)
        {
            try
            {
                if (chatSystem.IsActive)
                {
                    if ((e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z) ||
                        (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9))
                    {
                        char c = e.KeyCode.ToString()[0];
                        if (!e.Shift)
                            c = char.ToLower(c);
                        chatSystem.AddCharacter(c);
                    }
                    else if (e.KeyCode == Keys.Space)
                    {
                        chatSystem.AddCharacter(' ');
                    }
                    else if (e.KeyCode == Keys.OemPeriod)
                        chatSystem.AddCharacter('.');
                    else if (e.KeyCode == Keys.Oemcomma)
                        chatSystem.AddCharacter(',');
                    else if (e.KeyCode == Keys.OemQuestion)
                        chatSystem.AddCharacter(e.Shift ? '?' : '/');
                    else if (e.KeyCode == Keys.OemSemicolon)
                        chatSystem.AddCharacter(e.Shift ? ':' : ';');
                    else if (e.KeyCode == Keys.OemQuotes)
                        chatSystem.AddCharacter(e.Shift ? '"' : '\'');
                    else if (e.KeyCode == Keys.D1 && e.Shift)
                        chatSystem.AddCharacter('!');
                    else if (e.KeyCode == Keys.D8 && e.Shift)
                        chatSystem.AddCharacter('*');
                }
            }
            catch
            {
                // Silently fail
            }
        }

        void OnAborted(object sender, EventArgs e)
        {
            try
            {
                chatSystem.AddSystemMessage("Desconectando...");

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

                chatSystem.AddSystemMessage(firebase.GetStats());
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