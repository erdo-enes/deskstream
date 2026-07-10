namespace DeskStreamer.Server.Encode;

/// <summary>
/// Media Foundation / CODECAPI GUIDs used by the encoder. Declared explicitly (rather than
/// relying on Vortice's constant names) so the values are auditable against the Windows SDK
/// headers (mfapi.h, codecapi.h, mftransform.h).
/// </summary>
internal static class MfGuids
{
    // ---- Media type attributes (mfapi.h) ----
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");

    // ---- Major types / subtypes (mfapi.h) ----
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");

    // ---- Transform infrastructure ----
    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee"); // shares GUID with AVLowLatencyMode
    public static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

    // ---- ICodecAPI parameters (codecapi.h) ----
    public static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    // eAVEncCommonRateControlMode_CBR
    public const uint RateControlMode_CBR = 0;

    // eAVEncH264VProfile_Main = 77, High = 100
    public const uint H264Profile_High = 100;

    // MFVideoInterlace_Progressive
    public const uint Interlace_Progressive = 2;

    // ---- ProcessOutput HRESULTs (mferror.h) ----
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

    // ---- MFT output stream info flags (mftransform.h) ----
    public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;
    public const int MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;
}
