namespace Real_Life_System
{

    public class Message
    {
        public string Type { get; set; }
        public string EntityId { get; set; }
        public string Payload { get; set; }

        public Message(string t, string id, string p) { Type = t; EntityId = id; Payload = p; }
        public string ToRaw() => $"{Type}|{EntityId}|{Payload}";

        public static Message Parse(string raw)
        {
            try
            {
                var parts = raw.Split(new char[] { '|' }, 3);
                if (parts.Length < 3) return null;
                return new Message(parts[0], parts[1], parts[2]);
            }
            catch { return null; }
        }
    }
}
