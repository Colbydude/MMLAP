using Archipelago.Core.Util;
using System.Linq;

namespace MMLAP.Helpers
{
    public class MemoryHelpers
    {
        public static bool IsInTitleScreen()
        {
            return Memory.ReadByte(Addresses.TitleScreen.Address) == 0x20;
        }

        public static bool IsOutOfTitleScreen()
        {
            bool[] conditions = [
                Memory.ReadByte(Addresses.TitleScreen.Address) == 0xA4,
                new short [] {
                    0x0000,  // Voiceover cutscene
                    0x0700,  // Gesselschaft cutscene
                    0x0701,  // Gesselschaft cutscene
                    0x0702,  // Gesselschaft cutscene
                    0x0703,  // Gesselschaft cutscene
                    0x0704,  // Gesselschaft cutscene
                    0x0705,  // Gesselschaft cutscene
                    0x0706,  // Gesselschaft cutscene
                    0x0707   // Gesselschaft cutscene
                }.Contains(Memory.ReadShort(Addresses.CurrentLevel.Address, Enums.Endianness.Big))
            ];
            return conditions.All(value => value);
        }
    }
}
