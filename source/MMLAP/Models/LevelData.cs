namespace MMLAP.Models
{
    public class LevelData(
        string areaName,
        string roomName,
        byte areaCode,
        byte roomCode
        // , List<LevelData>? connectedLevels = null
    )
    {
        public string AreaName { get; set; } = areaName;
        public string RoomName { get; set; } = roomName;
        public byte AreaCode { get; set; } = areaCode;
        public byte RoomCode { get; set; } = roomCode;
    }
}
