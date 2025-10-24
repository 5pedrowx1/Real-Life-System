namespace Real_Life_System
{
    public class SessionInfo
    {
        public string SessionId;
        public string HostName;
        public int PlayerCount;
        public int MaxPlayers;
        public string Region;

        public override string ToString()
        {
            return $"[{Region}] {HostName} ({PlayerCount}/{MaxPlayers})";
        }
    }
}
