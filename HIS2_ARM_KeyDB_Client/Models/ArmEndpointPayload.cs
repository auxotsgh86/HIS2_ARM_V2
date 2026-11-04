namespace HIS2_ARM_KeyDB_Client.Models;

public class ArmEndpointPayload
{
    public string ControllerName { get; set; } = string.Empty;
    public List<string> Actions { get; set; } = new();
    // 💡 新增：標示這筆記錄是 Controller (WebAPI/MVC) 還是 Razor Page
    public string EndpointType { get; set; } = "Controller"; // "Controller" | "Page"
}