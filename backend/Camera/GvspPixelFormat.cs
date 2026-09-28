namespace MTCVision.Backend.Camera;

/// <summary>
/// GVSP pixel format codes as they appear in a data-leader packet (GEV 1.2, Pixel Format Naming
/// Convention). Bits 16-23 of each code are the bit depth per pixel.
/// </summary>
public enum GvspPixelFormat : uint
{
    Unknown = 0,
    Mono8 = 0x01080001,
    Mono10 = 0x01100003,
    Mono10Packed = 0x010C0004,
    Mono12 = 0x01100005,
    Mono12Packed = 0x010C0006,
    Mono14 = 0x01100025,
    Mono16 = 0x01100007,
    BayerGR8 = 0x01080008,
    BayerRG8 = 0x01080009,
    BayerGB8 = 0x0108000A,
    BayerBG8 = 0x0108000B,
    RGB8Packed = 0x02180014,
    BGR8Packed = 0x02180015,
    YUV422Packed = 0x0210001F,
}
