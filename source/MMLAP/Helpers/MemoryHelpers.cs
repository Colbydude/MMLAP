using Archipelago.Core.Util;
using MMLAP.Models;
using static MMLAP.Models.MMLEnums;
using System;
using System.Collections.Generic;
using ImGuiNET;

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
