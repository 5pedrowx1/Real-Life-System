using System.Collections.Generic;

namespace Real_Life_System
{
    internal class PendingUpdate
    {
        public string Path { get; set; }
        public Dictionary<string, object> Data { get; set; }
    }
}
