namespace Real_Life_System
{
    // ============================================================================
    // INFORMAÇÃO DE LOBBY
    // ============================================================================
    public class LobbyInfo
    {
        public string LobbyId;
        public string HostName;
        public string HostIP;
        public int Port;
        public int PlayerCount;
        public int MaxPlayers;
        public string Region;

        public override string ToString()
        {
            return $"[{Region}] {HostName} ({PlayerCount}/{MaxPlayers})";
        }
    }
}
