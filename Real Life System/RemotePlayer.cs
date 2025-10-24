using GTA;
using GTA.Math;
using System;

namespace Real_Life_System
{
    // ============================================================================
    // ESTRUTURAS DE DADOS (RemotePlayer)
    // ============================================================================
    public class RemotePlayer
    {
        public string Id;
        public string Name;
        public Ped Ped;
        public Vector3 TargetPos;
        public Vector3 TargetVel;
        public float TargetHeading;
        public string Animation;
        public bool IsAlive = true;
        public bool InVehicle;
        public string VehicleId;
        public int Seat;
        public float Health = 100;
        public WeaponHash CurrentWeapon;
        public DateTime LastUpdate = DateTime.UtcNow;
    }
}
