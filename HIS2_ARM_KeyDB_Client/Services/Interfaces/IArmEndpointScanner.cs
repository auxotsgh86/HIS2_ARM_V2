using HIS2_ARM_KeyDB_Client.Models;

namespace HIS2_ARM_KeyDB_Client.Services.Interfaces;

public interface IArmEndpointScanner
{
    IEnumerable<ArmEndpointPayload> ScanEndpoints();
}