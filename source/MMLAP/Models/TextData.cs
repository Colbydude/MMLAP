namespace MMLAP.Models
{
    public class TextData(
        ulong startAddress,
        byte[] textByteArr
    )
    {
        public ulong StartAddress { get; set; } = startAddress;
        public byte[] TextByteArr { get; set; } = textByteArr;
    }
}
