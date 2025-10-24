using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Real_Life_System
{
    public class NetLayer
    {
        readonly UdpClient udp;
        readonly TcpListener tcpListener;
        readonly ConcurrentDictionary<IPEndPoint, TcpClient> tcpPeers = new ConcurrentDictionary<IPEndPoint, TcpClient>();
        readonly Thread recvThread;
        readonly Thread tcpThread;
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        readonly ConcurrentDictionary<IPEndPoint, bool> peers = new ConcurrentDictionary<IPEndPoint, bool>();

        public bool IsHost { get; private set; }
        public int Port { get; private set; }
        public event Action<IPEndPoint, Message> OnMessage;
        public event Action<bool> OnPeerConnectionChanged;

        public int ConnectedPeers => peers.Count;

        public NetLayer(int localPort)
        {
            Port = localPort;

            try
            {
                // Create UDP socket with address reuse enabled
                var udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udpSocket.Bind(new IPEndPoint(IPAddress.Any, localPort));
                udp = new UdpClient();
                udp.Client = udpSocket;

                // Create TCP listener with address reuse
                tcpListener = new TcpListener(IPAddress.Any, localPort + 1);
                tcpListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                recvThread = new Thread(ReceiveLoop) { IsBackground = true };
                tcpThread = new Thread(TcpLoop) { IsBackground = true };

                GTA.UI.Notification.PostTicker($"[Net] NetLayer inicializado na porta {localPort}", true);
            }
            catch (SocketException ex)
            {
                GTA.UI.Notification.PostTicker($"[Net] ERRO: Porta {localPort} em uso. Tente fechar o GTA e abrir novamente.", true);
                throw new Exception($"Porta {localPort} já está em uso. Feche outras instâncias do mod.", ex);
            }
        }

        public void StartHost()
        {
            IsHost = true;
            try
            {
                tcpListener.Start();
                recvThread.Start();
                tcpThread.Start();
                GTA.UI.Notification.PostTicker($"[Net] Host iniciado em UDP:{Port}, TCP:{Port + 1}", true);
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"[Net] Erro ao iniciar host: {ex.Message}", true);
                throw;
            }
        }

        public void StartClient(string hostIp, int hostPort)
        {
            IsHost = false;
            var hostEp = new IPEndPoint(IPAddress.Parse(hostIp), hostPort);
            peers.TryAdd(hostEp, true);
            recvThread.Start();

            try
            {
                var tcp = new TcpClient();
                tcp.Connect(hostIp, hostPort + 1);
                tcpPeers.TryAdd(hostEp, tcp);
                new Thread(() => TcpRead(tcp, hostEp)) { IsBackground = true }.Start();

                SendReliable(new Message("JOIN", "0", "connected"));
                OnPeerConnectionChanged?.Invoke(true);
                GTA.UI.Notification.PostTicker($"[Net] Conectado a {hostIp}:{hostPort}", true);
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"[Net] Erro ao conectar: {ex.Message}", true);
            }
        }

        public void Stop()
        {
            GTA.UI.Notification.PostTicker("[Net] Fechando conexões...", true);
            cts.Cancel();

            try
            {
                // Close TCP connections first
                foreach (var peer in tcpPeers.Values)
                {
                    try { peer?.Close(); } catch { }
                }
                tcpPeers.Clear();

                // Stop TCP listener
                try { tcpListener?.Stop(); } catch { }

                // Close UDP last
                try { udp?.Close(); } catch { }

                peers.Clear();
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker($"[Net] Erro ao fechar: {ex.Message}", true);
            }
        }

        void TcpLoop()
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var client = tcpListener.AcceptTcpClient();
                    var ep = (IPEndPoint)client.Client.RemoteEndPoint;
                    tcpPeers.TryAdd(ep, client);
                    peers.TryAdd(ep, true);
                    OnPeerConnectionChanged?.Invoke(true);
                    new Thread(() => TcpRead(client, ep)) { IsBackground = true }.Start();
                }
                catch { break; }
            }
        }

        void TcpRead(TcpClient client, IPEndPoint ep)
        {
            try
            {
                using (var reader = new System.IO.StreamReader(client.GetStream()))
                {
                    while (!cts.IsCancellationRequested && client.Connected)
                    {
                        var line = reader.ReadLine();
                        if (string.IsNullOrEmpty(line)) continue;
                        var msg = Message.Parse(line);
                        if (msg != null)
                        {
                            OnMessage?.Invoke(ep, msg);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                tcpPeers.TryRemove(ep, out _);
                peers.TryRemove(ep, out _);
                OnPeerConnectionChanged?.Invoke(false);
            }
        }

        void ReceiveLoop()
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    var data = udp.Receive(ref remote);
                    var text = Encoding.UTF8.GetString(data);
                    var msg = Message.Parse(text);
                    if (msg != null)
                    {
                        OnMessage?.Invoke(remote, msg);
                    }
                }
                catch { break; }
            }
        }

        public void Broadcast(Message m)
        {
            var raw = Encoding.UTF8.GetBytes(m.ToRaw());
            foreach (var p in peers.Keys)
            {
                try
                {
                    udp.Send(raw, raw.Length, p);
                }
                catch { }
            }
        }

        public void SendReliable(Message m)
        {
            var raw = m.ToRaw() + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(raw);
            foreach (var kv in tcpPeers)
            {
                try
                {
                    kv.Value.GetStream().Write(bytes, 0, bytes.Length);
                    kv.Value.GetStream().Flush();
                }
                catch { }
            }
        }
    }
}