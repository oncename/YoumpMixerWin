// --- START OF FILE YoumpDsp.h ---
#pragma once

#ifdef _WIN32
#ifdef YOUMP_DSP_STATIC
#define YOUMP_DSP_API
#elif defined(YOUMP_DSP_EXPORTS)
#define YOUMP_DSP_API __declspec(dllexport)
#else
#define YOUMP_DSP_API __declspec(dllimport)
#endif
#else
#define YOUMP_DSP_API
#endif

#define YOUMP_DSP_MAX_INSTANCES 64

#ifdef __cplusplus
extern "C" {
#endif

#pragma pack(push, 8)
    typedef struct YoumpMixerParams
    {
        float trimGain;       // 0.0 .. 2.0 (1.0 = 0 dB; 2.0 = +9 dB)
        float eqLowDb;        // -24.0 .. +12.0 dB (<= -11.8 dB = Total Kill; boost clamped to +6 dB)
        float eqMidDb;        // -24.0 .. +12.0 dB (<= -11.8 dB = Total Kill; boost clamped to +6 dB)
        float eqHighDb;       // -24.0 .. +12.0 dB (<= -11.8 dB = Total Kill; boost clamped to +6 dB)
        float colorKnob;      // 0.0 (LPF) .. 0.5 (Bypass) .. 1.0 (HPF)
        float channelFader;   // 0.0 .. 1.0 (Pioneer DJM Curve: value^2)
        float masterVolume;   // 0.0 .. 1.0 (Pioneer DJM Curve: value^2)
        float balance;        // 0.0 (L) .. 0.5 (Center) .. 1.0 (R); < -0.5 = mic auto-mono (reserved)
        int   limiterEnabled; // 1 = Soft-Knee Limiter [-1.0, 1.0], 0 = bypass
    } YoumpMixerParams;

    typedef struct YoumpMixerMeters
    {
        float channelPeakPfl;
        float masterPeakL;
        float masterPeakR;
    } YoumpMixerMeters;
#pragma pack(pop)

    YOUMP_DSP_API int YoumpDsp_GetVersion();

    // Legacy Gain API
    YOUMP_DSP_API void YoumpDsp_SetGainDb(float gainDb);
    YOUMP_DSP_API int YoumpDsp_Process(
        float* samples,
        int frameCount,
        int channels,
        float gainDb);
    YOUMP_DSP_API int YoumpDsp_ProcessCurrentGain(
        float* samples,
        int frameCount,
        int channels);

    // Full Pioneer DJM-2000 Nexus / Serato DJ Pro 32-bit Float Mixer & Isolator API
    YOUMP_DSP_API void YoumpDsp_InitMixer(int sampleRate);
    YOUMP_DSP_API int  YoumpDsp_AcquireSlot(int sampleRate);
    YOUMP_DSP_API void YoumpDsp_ReleaseSlot(int slotId);
    YOUMP_DSP_API void YoumpDsp_InitMixerInstance(int instanceId, int sampleRate);
    YOUMP_DSP_API void YoumpDsp_SetMixerParams(const YoumpMixerParams* params);
    YOUMP_DSP_API void YoumpDsp_GetMixerMeters(YoumpMixerMeters* meters);
    YOUMP_DSP_API int  YoumpDsp_ProcessMixer(
        float* samples,
        int frameCount,
        int channels);
    YOUMP_DSP_API int  YoumpDsp_ProcessMixerInstance(
        int instanceId,
        float* samples,
        int frameCount,
        int channels);
    YOUMP_DSP_API int  YoumpDsp_ProcessChannel(
        int channelIndex,
        float* samples,
        int frameCount,
        int channels,
        const YoumpMixerParams* params,
        YoumpMixerMeters* outMeters);
    YOUMP_DSP_API unsigned int YoumpDsp_GetApoHeartbeat();
    YOUMP_DSP_API int YoumpDsp_InstanceHasActiveOutput(int instanceId);

#ifdef __cplusplus
}
#endif
// --- END OF FILE YoumpDsp.h ---