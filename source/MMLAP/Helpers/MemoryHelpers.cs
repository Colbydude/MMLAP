using Archipelago.Core.Util;

namespace MMLAP.Helpers
{
    public class MemoryHelpers
    {
        public static bool IsInTitleScreen()
        {
            return Memory.ReadByte(Addresses.TitleScreen.Address) == 0x20;
        }

        public static bool IsInGameOrCutscene()
        {
            return Memory.ReadByte(Addresses.TitleScreen.Address) == 0xA4;
        }
    }
}
