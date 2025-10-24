using GTA;
using GTA.Math;
using System;

namespace Real_Life_System
{
    // ============================================================================
    // ESTRUTURAS DE DADOS (RemoteVehicle)
    // ============================================================================

    public class RemoteVehicle
    {
        public string Id;
        public Vehicle Vehicle;
        public Vector3 TargetPos;
        public Vector3 TargetVel;
        public float TargetHeading;
        public VehicleHash Model;
        public bool EngineRunning;
        public float Health;
        public DateTime LastUpdate = DateTime.UtcNow;
    }
}
