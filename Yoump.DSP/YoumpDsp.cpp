// --- START OF FILE YoumpDsp.cpp ---
#include "YoumpDsp.h"
#include "PioneerDjmFormulas.h"

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <new>

namespace
{
    constexpr float PI_F = 3.14159265358979323846f;
    constexpr float SQRT2_INV = 0.7071067811865475f;
    constexpr uint32_t SHM_MAGIC = 0x594D5034U;
    constexpr int MAX_DSP_INSTANCES = YOUMP_DSP_MAX_INSTANCES;
    constexpr int MAX_CH_INSTANCES = 16;
    constexpr size_t SHM_VIEW_SIZE = 4096;
    constexpr int ECHO_BUF_LEN = 65536;
    constexpr int MISMATCH_FRAMES_THRESHOLD = 12;
    constexpr int SHM_CUSTOM_IR_MAX_TAPS = 512;
    constexpr uint64_t APP_HEARTBEAT_TIMEOUT_MS = 3500ULL;

    constexpr inline int ClampIntValue(int val, int minVal, int maxVal) noexcept
    {
        return (val < minVal) ? minVal : ((val > maxVal) ? maxVal : val);
    }

    constexpr inline int ClampSlotIndex(int instanceId) noexcept
    {
        if (instanceId < 0) return 0;
        const int raw = instanceId & 0xFF;
        return (raw >= MAX_DSP_INSTANCES) ? (MAX_DSP_INSTANCES - 1) : raw;
    }

    struct ScopedSpinLock
    {
        std::atomic_flag& flag;
        explicit ScopedSpinLock(std::atomic_flag& f) noexcept : flag(f)
        {
            while (flag.test_and_set(std::memory_order_acquire))
            {
#ifdef _WIN32
                YieldProcessor();
#endif
            }
        }
        ~ScopedSpinLock() noexcept
        {
            flag.clear(std::memory_order_release);
        }
    };

    struct ScopedTrySpinLock
    {
        std::atomic_flag& flag;
        bool acquired;
        explicit ScopedTrySpinLock(std::atomic_flag& f) noexcept
            : flag(f), acquired(!f.test_and_set(std::memory_order_acquire))
        {}
        ~ScopedTrySpinLock() noexcept
        {
            if (acquired)
            {
                flag.clear(std::memory_order_release);
            }
        }
    };

#pragma pack(push, 8)
    struct YoumpSharedDspPage
    {
        uint32_t magic;                                    // 0
        volatile long apoHeartbeat;                        // 4
        YoumpMixerParams params;                           // 8..43
        YoumpMixerMeters meters;                           // 44..55
        float gainDb;                                      // 56
        float gainLinear;                                  // 60
        volatile long activeApoInstanceId;                 // 64
        uint64_t lastApoProcessTick;                       // 72..79
        uint8_t legacyGainMode;                            // 80
        uint8_t padding[47];                               // 81..127

        int32_t channelCount;                              // 128
        int32_t reserved0;                                 // 132
        float channelRawPeak[16];                          // 136..199
        float channelAttenPeak[16];                        // 200..263
        YoumpMixerParams channelParams[16];                // 264..839

        int32_t micIrPreset;                               // 840
        float   micIrMix;                                  // 844
        uint32_t customIrVersion;                          // 848
        int32_t customIrLength;                            // 852
        float   customIrCoeffs[SHM_CUSTOM_IR_MAX_TAPS];    // 856..2903
        float   micGateThreshold;                          // 2904
        int32_t micExtraFlags;                             // 2908
    };
#pragma pack(pop)

#ifdef _WIN32
#pragma data_seg(".ympshm")
#endif
    std::atomic<float> g_paramTrim{ 1.0f };
    std::atomic<float> g_paramEqLow{ 0.0f };
    std::atomic<float> g_paramEqMid{ 0.0f };
    std::atomic<float> g_paramEqHigh{ 0.0f };
    std::atomic<float> g_paramColorKnob{ 0.5f };
    std::atomic<float> g_paramChannelFader{ 1.0f };
    std::atomic<float> g_paramMasterVolume{ 1.0f };
    std::atomic<float> g_paramBalance{ 0.5f };
    std::atomic<int>   g_paramLimiter{ 1 };

    std::atomic<float> g_meterPfl{ 0.0f };
    std::atomic<float> g_meterMasterL{ 0.0f };
    std::atomic<float> g_meterMasterR{ 0.0f };
    std::atomic<uint32_t> g_apoHeartbeat{ 0U };
#ifdef _WIN32
#pragma data_seg()
#pragma comment(linker, "/SECTION:.ympshm,RWS")
#endif

    std::atomic<float> g_gainDb{ 0.0f };
    std::atomic<float> g_gainLinear{ 1.0f };
    std::atomic<bool>  g_paramsExplicitlySet{ false };
    std::atomic<bool>  g_legacyGainMode{ false };

    std::atomic<YoumpSharedDspPage*> g_shmPage{ nullptr };
    std::atomic<bool> g_shmIsWritable{ false };

    inline YoumpMixerParams MakeFlatMixerParams() noexcept
    {
        YoumpMixerParams flat = {};
        flat.trimGain = 1.0f;
        flat.eqLowDb = 0.0f;
        flat.eqMidDb = 0.0f;
        flat.eqHighDb = 0.0f;
        flat.colorKnob = 0.5f;
        flat.channelFader = 1.0f;
        flat.masterVolume = 1.0f;
        flat.balance = 0.5f;
        flat.limiterEnabled = 1;
        return flat;
    }

    inline YoumpMixerParams SanitizeMixerParams(const YoumpMixerParams& raw) noexcept
    {
        YoumpMixerParams s = raw;
        if (!std::isfinite(s.trimGain))      s.trimGain = 1.0f;
        if (!std::isfinite(s.eqLowDb))       s.eqLowDb = 0.0f;
        if (!std::isfinite(s.eqMidDb))       s.eqMidDb = 0.0f;
        if (!std::isfinite(s.eqHighDb))      s.eqHighDb = 0.0f;
        if (!std::isfinite(s.colorKnob))     s.colorKnob = 0.5f;
        if (!std::isfinite(s.channelFader))  s.channelFader = 1.0f;
        if (!std::isfinite(s.masterVolume))  s.masterVolume = 1.0f;
        if (!std::isfinite(s.balance))       s.balance = 0.5f;
        return s;
    }

    inline YoumpMixerParams ReadConsistentParams(const YoumpMixerParams& src) noexcept
    {
        const volatile auto* vSrc = &src;
        YoumpMixerParams a = {};
        YoumpMixerParams b = {};

        for (int attempt = 0; attempt < 4; ++attempt)
        {
            a.trimGain = vSrc->trimGain;
            a.eqLowDb = vSrc->eqLowDb;
            a.eqMidDb = vSrc->eqMidDb;
            a.eqHighDb = vSrc->eqHighDb;
            a.colorKnob = vSrc->colorKnob;
            a.channelFader = vSrc->channelFader;
            a.masterVolume = vSrc->masterVolume;
            a.balance = vSrc->balance;
            a.limiterEnabled = vSrc->limiterEnabled;

            std::atomic_thread_fence(std::memory_order_acquire);

            b.trimGain = vSrc->trimGain;
            b.eqLowDb = vSrc->eqLowDb;
            b.eqMidDb = vSrc->eqMidDb;
            b.eqHighDb = vSrc->eqHighDb;
            b.colorKnob = vSrc->colorKnob;
            b.channelFader = vSrc->channelFader;
            b.masterVolume = vSrc->masterVolume;
            b.balance = vSrc->balance;
            b.limiterEnabled = vSrc->limiterEnabled;

            if (std::memcmp(&a, &b, sizeof(YoumpMixerParams)) == 0)
            {
                return SanitizeMixerParams(a);
            }
        }
        return SanitizeMixerParams(b);
    }

    inline float EstimateChannelPostEqGain(const YoumpMixerParams& cp) noexcept
    {
        if ((cp.limiterEnabled & 0x10) != 0 || cp.channelFader <= 0.001f || cp.masterVolume <= 0.001f)
        {
            return 0.0f;
        }

        const float trimLin = yoump::CalculatePioneerTrimLinear(std::clamp(cp.trimGain, 0.0f, 2.0f));
        if (trimLin <= 0.0001f)
        {
            return 0.0f;
        }

        const float lowLin = yoump::DbToIsolatorLinear(std::clamp(cp.eqLowDb, -24.0f, 12.0f));
        const float midLin = yoump::DbToIsolatorLinear(std::clamp(cp.eqMidDb, -24.0f, 12.0f));
        const float hiLin = yoump::DbToIsolatorLinear(std::clamp(cp.eqHighDb, -24.0f, 12.0f));
        const float eqGain = (lowLin * 0.45f) + (midLin * 0.35f) + (hiLin * 0.20f);

        const int colorMode = (cp.limiterEnabled >> 1) & 0x03;
        const float c = std::clamp(cp.colorKnob, 0.0f, 1.0f);
        float colorGain = 1.0f;

        if (colorMode == 0)
        {
            if (c < 0.025f)      colorGain = std::max(0.0f, c / 0.025f) * 0.2f;
            else if (c > 0.975f) colorGain = std::max(0.0f, (1.0f - c) / 0.025f) * 0.2f;
            else if (c < 0.48f)  colorGain = 0.25f + 0.75f * (c / 0.48f);
            else if (c > 0.52f)  colorGain = 0.25f + 0.75f * ((1.0f - c) / 0.48f);
        }
        else if (colorMode == 3 && c > 0.52f)
        {
            colorGain = 1.0f + ((c - 0.5f) * 0.45f);
        }

        return std::clamp(trimLin * eqGain * colorGain, 0.0f, 4.0f);
    }

#ifdef _WIN32
    SRWLOCK g_shmLock = SRWLOCK_INIT;
    std::atomic<bool> g_shmAsyncMapPending{ false };
    std::atomic<uint32_t> g_shmWriteUpgradeAttempts{ 0U };

    void InitializeDefaultShmPage(YoumpSharedDspPage* view) noexcept
    {
        if (view == nullptr) return;
        std::memset(view, 0, sizeof(YoumpSharedDspPage));
        view->apoHeartbeat = 0;
        view->params = MakeFlatMixerParams();
        view->gainDb = 0.0f;
        view->gainLinear = 1.0f;
        view->legacyGainMode = 0;
        view->channelCount = 0;
        view->reserved0 = 0;
        view->micIrPreset = 0;
        view->micIrMix = 1.0f;
        view->customIrVersion = 0;
        view->customIrLength = 0;
        view->micGateThreshold = 0.0045f;
        view->micExtraFlags = 3;
        MemoryBarrier();
        view->magic = SHM_MAGIC;
        MemoryBarrier();
    }
#endif

    YoumpSharedDspPage* EnsureSharedDspPageMapped(bool needWrite = false)
    {
#ifdef _WIN32
        YoumpSharedDspPage* current = g_shmPage.load(std::memory_order_acquire);
        if (current != nullptr && (!needWrite || g_shmIsWritable.load(std::memory_order_acquire)))
        {
            return current;
        }

        AcquireSRWLockExclusive(&g_shmLock);

        current = g_shmPage.load(std::memory_order_relaxed);
        if (current != nullptr && (!needWrite || g_shmIsWritable.load(std::memory_order_relaxed)))
        {
            ReleaseSRWLockExclusive(&g_shmLock);
            return current;
        }

        HANDLE hMap = nullptr;
        bool writable = false;

        if (needWrite)
        {
            CreateDirectoryW(L"C:\\ProgramData\\Yoump", nullptr);
        }

        const DWORD creationDisposition = needWrite ? OPEN_ALWAYS : OPEN_EXISTING;

        HANDLE hFile = CreateFileW(
            L"C:\\ProgramData\\Yoump\\yoump_dsp_shm_v4.bin",
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            creationDisposition,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);

        if (hFile != INVALID_HANDLE_VALUE)
        {
            LARGE_INTEGER fileSize = {};
            if (GetFileSizeEx(hFile, &fileSize) && fileSize.QuadPart < static_cast<LONGLONG>(SHM_VIEW_SIZE))
            {
                LARGE_INTEGER newPos = {};
                newPos.QuadPart = static_cast<LONGLONG>(SHM_VIEW_SIZE);
                if (SetFilePointerEx(hFile, newPos, nullptr, FILE_BEGIN))
                {
                    SetEndOfFile(hFile);
                }
            }

            hMap = CreateFileMappingW(hFile, nullptr, PAGE_READWRITE, 0, static_cast<DWORD>(SHM_VIEW_SIZE), nullptr);
            CloseHandle(hFile);
            writable = (hMap != nullptr);
        }
        else if (current == nullptr)
        {
            hFile = CreateFileW(
                L"C:\\ProgramData\\Yoump\\yoump_dsp_shm_v4.bin",
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                nullptr,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                nullptr);

            if (hFile != INVALID_HANDLE_VALUE)
            {
                hMap = CreateFileMappingW(hFile, nullptr, PAGE_READONLY, 0, static_cast<DWORD>(SHM_VIEW_SIZE), nullptr);
                CloseHandle(hFile);
                writable = false;
            }
        }

        if (hMap == nullptr)
        {
            const wchar_t* mapNames[] = { L"Global\\YoumpDspSharedMemV4", L"Local\\YoumpDspSharedMemV4" };
            for (const wchar_t* name : mapNames)
            {
                hMap = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
                if (hMap != nullptr)
                {
                    writable = true;
                    break;
                }
                if (current == nullptr)
                {
                    hMap = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
                    if (hMap != nullptr)
                    {
                        writable = false;
                        break;
                    }
                }
            }
        }

        if (hMap == nullptr && needWrite && current == nullptr)
        {
            hMap = CreateFileMappingW(
                INVALID_HANDLE_VALUE,
                nullptr,
                PAGE_READWRITE,
                0,
                static_cast<DWORD>(SHM_VIEW_SIZE),
                L"Local\\YoumpDspSharedMemV4");
            writable = (hMap != nullptr);
        }

        if (hMap != nullptr)
        {
            DWORD access = writable ? FILE_MAP_ALL_ACCESS : FILE_MAP_READ;
            auto* view = static_cast<YoumpSharedDspPage*>(MapViewOfFile(hMap, access, 0, 0, sizeof(YoumpSharedDspPage)));
            CloseHandle(hMap);

            if (view != nullptr)
            {
                if (writable && view->magic != SHM_MAGIC)
                {
                    InitializeDefaultShmPage(view);
                }

                if (view->magic == SHM_MAGIC)
                {
                    static YoumpSharedDspPage* s_prevReadOnlyView = nullptr;
                    if (current != nullptr && current != view)
                    {
                        if (s_prevReadOnlyView != nullptr && s_prevReadOnlyView != current)
                        {
                            UnmapViewOfFile(s_prevReadOnlyView);
                        }
                        s_prevReadOnlyView = current;
                    }
                    g_shmIsWritable.store(writable, std::memory_order_release);
                    g_shmPage.store(view, std::memory_order_release);
                    current = view;
                }
                else
                {
                    UnmapViewOfFile(view);
                }
            }
        }

        ReleaseSRWLockExclusive(&g_shmLock);
        return (needWrite && !g_shmIsWritable.load(std::memory_order_acquire)) ? nullptr : current;
#else
        (void)needWrite;
        return nullptr;
#endif
    }

#ifdef _WIN32
    VOID NTAPI AsyncMapShmCallback(PTP_CALLBACK_INSTANCE, PVOID)
    {
        if (g_shmPage.load(std::memory_order_acquire) == nullptr)
        {
            EnsureSharedDspPageMapped(false);
        }
        else if (!g_shmIsWritable.load(std::memory_order_acquire))
        {
            g_shmWriteUpgradeAttempts.fetch_add(1U, std::memory_order_relaxed);
            EnsureSharedDspPageMapped(true);
        }
        g_shmAsyncMapPending.store(false, std::memory_order_release);
    }

    void RequestAsyncSharedDspPageMap() noexcept
    {
        if (g_shmPage.load(std::memory_order_relaxed) != nullptr &&
            g_shmIsWritable.load(std::memory_order_relaxed))
        {
            return;
        }

        bool expected = false;
        if (g_shmAsyncMapPending.compare_exchange_strong(expected, true, std::memory_order_acq_rel))
        {
            if (!TrySubmitThreadpoolCallback(AsyncMapShmCallback, nullptr, nullptr))
            {
                g_shmAsyncMapPending.store(false, std::memory_order_release);
            }
        }
    }
#endif

    float ClampGainDb(float value) noexcept
    {
        if (!std::isfinite(value)) return 0.0f;
        return std::clamp(value, -60.0f, 12.0f);
    }

    int ProcessWithMultiplier(float* samples, int frameCount, int channels, float multiplier) noexcept
    {
        if (frameCount < 0 || samples == nullptr) return 1;
        if (channels < 1 || channels > 8) return 2;
        if (frameCount == 0) return 0;
        if (frameCount > (std::numeric_limits<int>::max)() / channels) return 3;

        const int sampleCount = frameCount * channels;
        for (int i = 0; i < sampleCount; ++i)
        {
            float sample = samples[i];
            if (!std::isfinite(sample)) sample = 0.0f;
            samples[i] = sample * multiplier;
        }
        return 0;
    }

    class BiQuad
    {
    public:
        float b0 = 1.0f, b1 = 0.0f, b2 = 0.0f;
        float a1 = 0.0f, a2 = 0.0f;
        float x1 = 0.0f, x2 = 0.0f;
        float y1 = 0.0f, y2 = 0.0f;

        void Reset() noexcept { x1 = 0.0f; x2 = 0.0f; y1 = 0.0f; y2 = 0.0f; }

        void SetLowPass(float sampleRate, float cutoff, float q) noexcept
        {
            if (sampleRate <= 0.0f || cutoff <= 0.0f) return;
            cutoff = std::clamp(cutoff, 10.0f, sampleRate * 0.49f);
            const float w0 = 2.0f * PI_F * cutoff / sampleRate;
            const float cosW0 = std::cos(w0);
            const float alpha = std::sin(w0) / (2.0f * std::max(0.1f, q));
            const float invA0 = 1.0f / (1.0f + alpha);
            b0 = ((1.0f - cosW0) * 0.5f) * invA0;
            b1 = (1.0f - cosW0) * invA0;
            b2 = b0;
            a1 = (-2.0f * cosW0) * invA0;
            a2 = (1.0f - alpha) * invA0;
        }

        void SetHighPass(float sampleRate, float cutoff, float q) noexcept
        {
            if (sampleRate <= 0.0f || cutoff <= 0.0f) return;
            cutoff = std::clamp(cutoff, 10.0f, sampleRate * 0.49f);
            const float w0 = 2.0f * PI_F * cutoff / sampleRate;
            const float cosW0 = std::cos(w0);
            const float alpha = std::sin(w0) / (2.0f * std::max(0.1f, q));
            const float invA0 = 1.0f / (1.0f + alpha);
            b0 = ((1.0f + cosW0) * 0.5f) * invA0;
            b1 = (-(1.0f + cosW0)) * invA0;
            b2 = b0;
            a1 = (-2.0f * cosW0) * invA0;
            a2 = (1.0f - alpha) * invA0;
        }

        void SetAllPass(float sampleRate, float cutoff, float q) noexcept
        {
            if (sampleRate <= 0.0f || cutoff <= 0.0f) return;
            cutoff = std::clamp(cutoff, 10.0f, sampleRate * 0.49f);
            const float w0 = 2.0f * PI_F * cutoff / sampleRate;
            const float cosW0 = std::cos(w0);
            const float alpha = std::sin(w0) / (2.0f * std::max(0.1f, q));
            const float invA0 = 1.0f / (1.0f + alpha);
            b0 = (1.0f - alpha) * invA0;
            b1 = (-2.0f * cosW0) * invA0;
            b2 = 1.0f;
            a1 = b1;
            a2 = b0;
        }

        void SetPeakingEq(float sampleRate, float centreFreq, float q, float dbGain) noexcept
        {
            if (sampleRate <= 0.0f || centreFreq <= 0.0f) return;
            centreFreq = std::clamp(centreFreq, 15.0f, sampleRate * 0.48f);
            const float w0 = 2.0f * PI_F * centreFreq / sampleRate;
            const float cosW0 = std::cos(w0);
            const float alpha = std::sin(w0) / (2.0f * std::max(0.1f, q));
            const float A = std::pow(10.0f, dbGain / 40.0f);
            const float invA0 = 1.0f / (1.0f + alpha / A);
            b0 = (1.0f + alpha * A) * invA0;
            b1 = (-2.0f * cosW0) * invA0;
            b2 = (1.0f - alpha * A) * invA0;
            a1 = b1;
            a2 = (1.0f - alpha / A) * invA0;
        }

        inline float Transform(float in) noexcept
        {
            float out = b0 * in + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            if (!std::isfinite(out)) { Reset(); return 0.0f; }
            if (std::abs(out) < 1.0e-20f) out = 0.0f;
            x2 = x1; x1 = in; y2 = y1; y1 = out;
            return out;
        }
    };

    class CytomicSvf
    {
    public:
        float ic1eq = 0.0f, ic2eq = 0.0f;
        float a1 = 0.0f, a2 = 0.0f, a3 = 0.0f, k = 1.0f;

        void Reset() noexcept { ic1eq = 0.0f; ic2eq = 0.0f; }

        void SetCoeffs(float sampleRate, float cutoff, float q) noexcept
        {
            if (sampleRate <= 0.0f) return;
            cutoff = std::clamp(cutoff, 15.0f, sampleRate * 0.47f);
            const float g = std::tan(PI_F * cutoff / sampleRate);
            k = 1.0f / std::max(0.1f, q);
            a1 = 1.0f / (1.0f + g * (g + k));
            a2 = g * a1;
            a3 = g * a2;
        }

        inline float ProcessLowPass(float v0) noexcept
        {
            const float v3 = v0 - ic2eq;
            const float v1 = a1 * ic1eq + a2 * v3;
            const float v2 = ic2eq + a2 * ic1eq + a3 * v3;
            ic1eq = 2.0f * v1 - ic1eq;
            ic2eq = 2.0f * v2 - ic2eq;
            if (!std::isfinite(v2)) { Reset(); return 0.0f; }
            if (std::abs(ic1eq) < 1.0e-20f) ic1eq = 0.0f;
            if (std::abs(ic2eq) < 1.0e-20f) ic2eq = 0.0f;
            return v2;
        }

        inline float ProcessHighPass(float v0) noexcept
        {
            const float v3 = v0 - ic2eq;
            const float v1 = a1 * ic1eq + a2 * v3;
            const float v2 = ic2eq + a2 * ic1eq + a3 * v3;
            ic1eq = 2.0f * v1 - ic1eq;
            ic2eq = 2.0f * v2 - ic2eq;
            const float res = v0 - k * v1 - v2;
            if (!std::isfinite(res)) { Reset(); return 0.0f; }
            if (std::abs(ic1eq) < 1.0e-20f) ic1eq = 0.0f;
            if (std::abs(ic2eq) < 1.0e-20f) ic2eq = 0.0f;
            return res;
        }
    };

    struct PrecomputedIrSpectrum
    {
        static constexpr int BLOCK_SIZE = 64;
        static constexpr int FFT_SIZE = 128;
        static constexpr int NUM_PARTS = 15;

        int   numActiveParts = 0;
        float headCoeffs[BLOCK_SIZE] = {};
        float irSpecRe[NUM_PARTS][FFT_SIZE] = {};
        float irSpecIm[NUM_PARTS][FFT_SIZE] = {};
    };

    class GardnerZeroLatencyConvolver
    {
    public:
        static constexpr int BLOCK_SIZE = PrecomputedIrSpectrum::BLOCK_SIZE;
        static constexpr int FFT_SIZE = PrecomputedIrSpectrum::FFT_SIZE;
        static constexpr int NUM_PARTS = PrecomputedIrSpectrum::NUM_PARTS;
        static constexpr int MAX_IR_TAPS = BLOCK_SIZE * (NUM_PARTS + 1);

        int activePreset = -1;
        int currentSampleRate = 48000;
        uint32_t loadedCustomVersion = 0;
        int numActiveParts = 0;

        float headCoeffs[BLOCK_SIZE] = {};
        float headHistory[BLOCK_SIZE] = {};
        int headWritePos = 0;

        float irSpecRe[NUM_PARTS][FFT_SIZE] = {};
        float irSpecIm[NUM_PARTS][FFT_SIZE] = {};

        float fdlRe[NUM_PARTS][FFT_SIZE] = {};
        float fdlIm[NUM_PARTS][FFT_SIZE] = {};
        int fdlPos = 0;

        float inBlock[BLOCK_SIZE] = {};
        float olaBuf[FFT_SIZE] = {};
        int blockPos = 0;

        float fftAccRe[FFT_SIZE] = {};
        float fftAccIm[FFT_SIZE] = {};
        float scratchIr[MAX_IR_TAPS] = {};

        static void Fft128(float* re, float* im, bool inverse) noexcept
        {
            constexpr int N = FFT_SIZE;
            int j = 0;
            for (int i = 0; i < N; ++i)
            {
                if (i < j)
                {
                    std::swap(re[i], re[j]);
                    std::swap(im[i], im[j]);
                }
                int bit = N >> 1;
                while (bit >= 1 && j >= bit)
                {
                    j -= bit;
                    bit >>= 1;
                }
                j += bit;
            }

            const float sign = inverse ? 1.0f : -1.0f;
            for (int len = 2; len <= N; len <<= 1)
            {
                const int halfLen = len >> 1;
                const float angle = sign * (2.0f * PI_F / static_cast<float>(len));
                const float wStepRe = std::cos(angle);
                const float wStepIm = std::sin(angle);

                for (int i = 0; i < N; i += len)
                {
                    float wRe = 1.0f;
                    float wIm = 0.0f;
                    for (int k = 0; k < halfLen; ++k)
                    {
                        const int evenIdx = i + k;
                        const int oddIdx = evenIdx + halfLen;

                        const float tRe = wRe * re[oddIdx] - wIm * im[oddIdx];
                        const float tIm = wRe * im[oddIdx] + wIm * re[oddIdx];

                        re[oddIdx] = re[evenIdx] - tRe;
                        im[oddIdx] = im[evenIdx] - tIm;
                        re[evenIdx] += tRe;
                        im[evenIdx] += tIm;

                        const float nextWRe = wRe * wStepRe - wIm * wStepIm;
                        wIm = wRe * wStepIm + wIm * wStepRe;
                        wRe = nextWRe;
                    }
                }
            }

            if (inverse)
            {
                constexpr float invN = 1.0f / static_cast<float>(N);
                for (int i = 0; i < N; ++i)
                {
                    re[i] *= invN;
                    im[i] *= invN;
                }
            }
        }

        static void CompileImpulseToArrays(
            const float* irData,
            int tapCount,
            int& outNumParts,
            float(&outHead)[BLOCK_SIZE],
            float(&outRe)[NUM_PARTS][FFT_SIZE],
            float(&outIm)[NUM_PARTS][FFT_SIZE]) noexcept
        {
            std::memset(outHead, 0, sizeof(outHead));
            std::memset(outRe, 0, sizeof(outRe));
            std::memset(outIm, 0, sizeof(outIm));

            if (irData == nullptr || tapCount <= 0)
            {
                outHead[0] = 1.0f;
                outNumParts = 0;
                return;
            }

            const int safeLen = ClampIntValue(tapCount, 1, MAX_IR_TAPS);
            const int headCount = (safeLen < BLOCK_SIZE) ? safeLen : BLOCK_SIZE;
            for (int i = 0; i < headCount; ++i)
            {
                outHead[i] = std::isfinite(irData[i]) ? irData[i] : 0.0f;
            }

            if (safeLen <= BLOCK_SIZE)
            {
                outNumParts = 0;
                return;
            }

            const int reqParts = (safeLen - BLOCK_SIZE + BLOCK_SIZE - 1) / BLOCK_SIZE;
            outNumParts = (reqParts < NUM_PARTS) ? reqParts : NUM_PARTS;
            for (int p = 0; p < outNumParts; ++p)
            {
                const int baseOffset = (p + 1) * BLOCK_SIZE;
                for (int i = 0; i < BLOCK_SIZE; ++i)
                {
                    const int srcIdx = baseOffset + i;
                    const float v = (srcIdx < safeLen && std::isfinite(irData[srcIdx])) ? irData[srcIdx] : 0.0f;
                    outRe[p][i] = v;
                    outIm[p][i] = 0.0f;
                    outRe[p][i + BLOCK_SIZE] = 0.0f;
                    outIm[p][i + BLOCK_SIZE] = 0.0f;
                }
                Fft128(outRe[p], outIm[p], false);
            }
        }

        static void CompileImpulseToSpectrum(
            const float* irData,
            int tapCount,
            PrecomputedIrSpectrum& outSpec) noexcept
        {
            CompileImpulseToArrays(
                irData,
                tapCount,
                outSpec.numActiveParts,
                outSpec.headCoeffs,
                outSpec.irSpecRe,
                outSpec.irSpecIm);
        }

        static int SynthesizeFactoryImpulseTaps(
            int presetId,
            int sampleRate,
            float(&ir)[MAX_IR_TAPS]) noexcept
        {
            const int sr = (sampleRate > 8000) ? sampleRate : 48000;
            const float srF = static_cast<float>(sr);

            std::memset(ir, 0, sizeof(ir));
            ir[0] = 1.0f;

            if (presetId <= 0)
            {
                return 1;
            }

            BiQuad eq1, eq2, eq3, eq4, ap1, ap2;

            if (presetId == 1) // 1: Shure SM7B (Dynamic Broadcast)
            {
                eq1.SetPeakingEq(srF, 210.0f, 0.85f, 3.2f);
                eq2.SetPeakingEq(srF, 480.0f, 1.40f, -2.4f);
                eq3.SetPeakingEq(srF, 4100.0f, 1.10f, 2.8f);
                eq4.SetLowPass(srF, 13800.0f, 0.707f);
                ap1.SetAllPass(srF, 180.0f, 0.7f);
                ap2.SetAllPass(srF, 2400.0f, 0.7f);

                for (int n = 0; n < 256; ++n)
                {
                    float s = (n == 0) ? 1.0f : 0.0f;
                    s = eq1.Transform(s);
                    s = eq2.Transform(s);
                    s = eq3.Transform(s);
                    s = eq4.Transform(s);
                    s = ap1.Transform(s);
                    s = ap2.Transform(s);
                    ir[n] = s;
                }
                return 256;
            }
            if (presetId == 2) // 2: Neumann U87 Ai (Studio Condenser)
            {
                eq1.SetPeakingEq(srF, 240.0f, 0.90f, 1.8f);
                eq2.SetPeakingEq(srF, 3200.0f, 1.25f, 1.6f);
                eq3.SetPeakingEq(srF, 9800.0f, 0.75f, 4.2f);
                eq4.SetHighPass(srF, 65.0f, 0.707f);
                ap1.SetAllPass(srF, 4800.0f, 0.8f);

                for (int n = 0; n < 384; ++n)
                {
                    float s = (n == 0) ? 1.0f : 0.0f;
                    s = eq1.Transform(s);
                    s = eq2.Transform(s);
                    s = eq3.Transform(s);
                    s = eq4.Transform(s);
                    s = ap1.Transform(s);
                    ir[n] = s;
                }
                const int er1 = std::min(380, static_cast<int>(0.0028f * srF));
                const int er2 = std::min(380, static_cast<int>(0.0051f * srF));
                ir[er1] += 0.045f;
                ir[er2] -= 0.028f;
                return 384;
            }
            if (presetId == 3) // 3: Electro-Voice RE20 (Radio Voice)
            {
                eq1.SetPeakingEq(srF, 145.0f, 1.10f, 2.5f);
                eq2.SetPeakingEq(srF, 340.0f, 1.50f, -2.8f);
                eq3.SetPeakingEq(srF, 2900.0f, 1.00f, 3.1f);
                eq4.SetPeakingEq(srF, 7500.0f, 0.90f, 2.0f);
                ap1.SetAllPass(srF, 950.0f, 0.707f);

                for (int n = 0; n < 256; ++n)
                {
                    float s = (n == 0) ? 1.0f : 0.0f;
                    s = eq1.Transform(s);
                    s = eq2.Transform(s);
                    s = eq3.Transform(s);
                    s = eq4.Transform(s);
                    s = ap1.Transform(s);
                    ir[n] = s;
                }
                return 256;
            }
            if (presetId == 4) // 4: Sony C800G (Pop Vocal Tube)
            {
                eq1.SetPeakingEq(srF, 190.0f, 0.95f, 2.2f);
                eq2.SetPeakingEq(srF, 550.0f, 1.30f, -1.8f);
                eq3.SetPeakingEq(srF, 5200.0f, 1.10f, 2.4f);
                eq4.SetPeakingEq(srF, 11500.0f, 0.68f, 5.2f);
                ap1.SetAllPass(srF, 320.0f, 0.75f);

                for (int n = 0; n < 256; ++n)
                {
                    float s = (n == 0) ? 1.0f : 0.0f;
                    s = eq1.Transform(s);
                    s = eq2.Transform(s);
                    s = eq3.Transform(s);
                    s = eq4.Transform(s);
                    s = ap1.Transform(s);
                    ir[n] = s;
                }
                return 256;
            }
            if (presetId == 5 || presetId == 6 || presetId == 8) // 5: Lexicon 480L / 6: Club Room L / 8: Club Room R
            {
                const bool isLexicon = (presetId == 5);
                const bool isRightStereo = (presetId == 8);
                eq1.SetLowPass(srF, isLexicon ? 7800.0f : 5200.0f, 0.707f);
                eq2.SetHighPass(srF, isLexicon ? 140.0f : 110.0f, 0.707f);
                ap1.SetAllPass(srF, isRightStereo ? 760.0f : 720.0f, 0.7f);
                ap2.SetAllPass(srF, isRightStereo ? 1920.0f : 1850.0f, 0.7f);

                uint32_t rng = isLexicon ? 0x13579BDFU : (isRightStereo ? 0x369CF258U : 0x2468ACE0U);
                const float decayRate = isLexicon ? 4.2f : 3.4f;
                const float tailScale = isLexicon ? 0.085f : 0.105f;

                ir[0] = 0.82f;
                for (int n = 1; n < MAX_IR_TAPS; ++n)
                {
                    rng = rng * 1664525U + 1013904223U;
                    const float white = (static_cast<float>(static_cast<int32_t>(rng)) / 2147483648.0f);
                    const float tNorm = static_cast<float>(n) / static_cast<float>(MAX_IR_TAPS);
                    const float env = std::exp(-decayRate * tNorm);

                    float sample = white * env * tailScale;
                    if (!isRightStereo && (n == 19 || n == 43 || n == 79 || n == 131 || n == 211))
                    {
                        sample += ((n & 2) ? -0.14f : 0.16f) * env;
                    }
                    else if (isRightStereo && (n == 23 || n == 47 || n == 83 || n == 137 || n == 219))
                    {
                        sample += ((n & 2) ? 0.15f : -0.14f) * env;
                    }
                    sample = eq1.Transform(eq2.Transform(sample));
                    sample = ap2.Transform(ap1.Transform(sample));
                    ir[n] = sample;
                }
                return MAX_IR_TAPS;
            }

            return 1;
        }

        static void BuildFactoryPresetSpectrum(
            int presetId,
            int sampleRate,
            PrecomputedIrSpectrum& outSpec,
            float(&irScratchBuf)[MAX_IR_TAPS]) noexcept
        {
            const int taps = SynthesizeFactoryImpulseTaps(presetId, sampleRate, irScratchBuf);
            CompileImpulseToSpectrum(irScratchBuf, taps, outSpec);
        }

        void ResetState() noexcept
        {
            std::memset(headHistory, 0, sizeof(headHistory));
            std::memset(fdlRe, 0, sizeof(fdlRe));
            std::memset(fdlIm, 0, sizeof(fdlIm));
            std::memset(inBlock, 0, sizeof(inBlock));
            std::memset(olaBuf, 0, sizeof(olaBuf));
            headWritePos = 0;
            fdlPos = 0;
            blockPos = 0;
        }

        void ApplyPrecomputedSpectrum(const PrecomputedIrSpectrum& spec, bool clearHistory) noexcept
        {
            if (clearHistory)
            {
                ResetState();
            }
            numActiveParts = spec.numActiveParts;
            std::memcpy(headCoeffs, spec.headCoeffs, sizeof(headCoeffs));
            std::memcpy(irSpecRe, spec.irSpecRe, sizeof(irSpecRe));
            std::memcpy(irSpecIm, spec.irSpecIm, sizeof(irSpecIm));
        }

        void LoadImpulse(const float* irData, int tapCount, bool clearHistory = true) noexcept
        {
            if (clearHistory)
            {
                ResetState();
            }
            CompileImpulseToArrays(
                irData,
                tapCount,
                numActiveParts,
                headCoeffs,
                irSpecRe,
                irSpecIm);
        }

        void ConfigurePresetFast(
            int presetId,
            int sampleRate,
            const YoumpSharedDspPage* shm,
            const PrecomputedIrSpectrum* precomputedBank) noexcept
        {
            const int sr = (sampleRate > 8000) ? sampleRate : 48000;
            const uint32_t shmVer = (shm != nullptr)
                ? *reinterpret_cast<const volatile uint32_t*>(&shm->customIrVersion)
                : 0U;

            if (presetId == 7)
            {
                if (shm == nullptr)
                {
                    return;
                }

                const int32_t curLen = *reinterpret_cast<const volatile int32_t*>(&shm->customIrLength);
                if (curLen <= 0 || shmVer == 0U)
                {
                    return;
                }

                if (activePreset == 7 && sr == currentSampleRate && shmVer == loadedCustomVersion)
                {
                    return;
                }

                const bool needHistoryReset = (activePreset <= 0 || sr != currentSampleRate);
                int validLen = 0;
                uint32_t consistentVer = 0U;
                bool snapshotOk = false;

                for (int attempt = 0; attempt < 4; ++attempt)
                {
                    const uint32_t verBefore = *reinterpret_cast<const volatile uint32_t*>(&shm->customIrVersion);
                    if (verBefore == 0U || (verBefore & 1U) != 0U)
                    {
                        break;
                    }
                    std::atomic_thread_fence(std::memory_order_acquire);

                    const int32_t rawLen = *reinterpret_cast<const volatile int32_t*>(&shm->customIrLength);
                    if (rawLen <= 0)
                    {
                        break;
                    }

                    const int len = ClampIntValue(static_cast<int>(rawLen), 1, SHM_CUSTOM_IR_MAX_TAPS);
                    const volatile float* vCoeffs = shm->customIrCoeffs;
                    for (int i = 0; i < len; ++i)
                    {
                        const float v = vCoeffs[i];
                        scratchIr[i] = std::isfinite(v) ? v : 0.0f;
                    }

                    std::atomic_thread_fence(std::memory_order_acquire);
                    const int32_t lenAfter = *reinterpret_cast<const volatile int32_t*>(&shm->customIrLength);
                    const uint32_t verAfter = *reinterpret_cast<const volatile uint32_t*>(&shm->customIrVersion);

                    if (verBefore == verAfter && rawLen == lenAfter && lenAfter > 0)
                    {
                        validLen = len;
                        consistentVer = verAfter;
                        snapshotOk = true;
                        break;
                    }
                }

                if (!snapshotOk || validLen <= 0)
                {
                    return;
                }

                activePreset = 7;
                currentSampleRate = sr;
                loadedCustomVersion = consistentVer;
                LoadImpulse(scratchIr, validLen, needHistoryReset);
                return;
            }

            if (presetId == activePreset && sr == currentSampleRate)
            {
                return;
            }

            const bool needHistoryReset = (activePreset <= 0 || sr != currentSampleRate);

            activePreset = presetId;
            currentSampleRate = sr;
            loadedCustomVersion = shmVer;

            if (presetId <= 0)
            {
                ResetState();
                std::memset(headCoeffs, 0, sizeof(headCoeffs));
                headCoeffs[0] = 1.0f;
                numActiveParts = 0;
                return;
            }

            if (precomputedBank != nullptr && presetId >= 1 && presetId <= 8 && presetId != 7)
            {
                ApplyPrecomputedSpectrum(precomputedBank[presetId], needHistoryReset);
                return;
            }

            const int taps = SynthesizeFactoryImpulseTaps(presetId, sr, scratchIr);
            LoadImpulse(scratchIr, taps, needHistoryReset);
        }

        void ProcessFftPartitionBlock() noexcept
        {
            fdlPos = (fdlPos == 0) ? (NUM_PARTS - 1) : (fdlPos - 1);
            float* curFdlRe = fdlRe[fdlPos];
            float* curFdlIm = fdlIm[fdlPos];

            for (int i = 0; i < BLOCK_SIZE; ++i)
            {
                curFdlRe[i] = inBlock[i];
                curFdlIm[i] = 0.0f;
                curFdlRe[i + BLOCK_SIZE] = 0.0f;
                curFdlIm[i + BLOCK_SIZE] = 0.0f;
            }
            Fft128(curFdlRe, curFdlIm, false);

            std::memset(fftAccRe, 0, sizeof(fftAccRe));
            std::memset(fftAccIm, 0, sizeof(fftAccIm));

            for (int p = 0; p < numActiveParts; ++p)
            {
                int slot = fdlPos + p;
                if (slot >= NUM_PARTS) slot -= NUM_PARTS;

                const float* xRe = fdlRe[slot];
                const float* xIm = fdlIm[slot];
                const float* hRe = irSpecRe[p];
                const float* hIm = irSpecIm[p];

                for (int k = 0; k < FFT_SIZE; ++k)
                {
                    fftAccRe[k] += (xRe[k] * hRe[k] - xIm[k] * hIm[k]);
                    fftAccIm[k] += (xRe[k] * hIm[k] + xIm[k] * hRe[k]);
                }
            }

            Fft128(fftAccRe, fftAccIm, true);

            for (int i = 0; i < BLOCK_SIZE; ++i)
            {
                olaBuf[i] = olaBuf[i + BLOCK_SIZE] + fftAccRe[i];
                olaBuf[i + BLOCK_SIZE] = fftAccRe[i + BLOCK_SIZE];
            }
        }

        inline float ProcessSample(float x) noexcept
        {
            if (!std::isfinite(x)) x = 0.0f;

            headHistory[headWritePos] = x;
            float yHead = 0.0f;
            int idx = headWritePos;
            for (int k = 0; k < BLOCK_SIZE; ++k)
            {
                yHead += headCoeffs[k] * headHistory[idx];
                idx = (idx - 1) & (BLOCK_SIZE - 1);
            }
            headWritePos = (headWritePos + 1) & (BLOCK_SIZE - 1);

            float yTail = 0.0f;
            if (numActiveParts > 0)
            {
                inBlock[blockPos] = x;
                yTail = olaBuf[blockPos];
                blockPos++;
                if (blockPos >= BLOCK_SIZE)
                {
                    ProcessFftPartitionBlock();
                    blockPos = 0;
                }
            }

            const float out = yHead + yTail;
            return std::isfinite(out) ? out : 0.0f;
        }
    };

    inline float PioneerSoftKneeLimiter(float x) noexcept
    {
        if (!std::isfinite(x)) return 0.0f;
        const float absX = std::abs(x);
        if (absX <= 0.80f) return x;
        const float sign = (x >= 0.0f) ? 1.0f : -1.0f;
        const float excess = (absX - 0.80f) / 0.20f;
        return sign * (0.80f + 0.20f * std::tanh(excess));
    }

    struct SharedFactoryIrCache
    {
        std::atomic_flag lock = ATOMIC_FLAG_INIT;
        int cachedSampleRate = 0;
        PrecomputedIrSpectrum bank[9] = {};
        float scratch[GardnerZeroLatencyConvolver::MAX_IR_TAPS] = {};

        const PrecomputedIrSpectrum* GetBank(int sampleRate) noexcept
        {
            const int sr = (sampleRate > 8000) ? sampleRate : 48000;
            ScopedSpinLock guard(lock);
            if (cachedSampleRate != sr)
            {
                for (int id = 0; id <= 8; ++id)
                {
                    if (id == 7) continue;
                    GardnerZeroLatencyConvolver::BuildFactoryPresetSpectrum(id, sr, bank[id], scratch);
                }
                cachedSampleRate = sr;
            }
            return bank;
        }
    };

    SharedFactoryIrCache g_sharedFactoryIrCache;

    struct DspHeavyBuffers
    {
        const PrecomputedIrSpectrum* factoryIrBank = nullptr;
        GardnerZeroLatencyConvolver micConvolver;
        GardnerZeroLatencyConvolver spaceReverbConvolverL;
        GardnerZeroLatencyConvolver spaceReverbConvolverR;
        float echoBuf[2][ECHO_BUF_LEN] = {};

        void EnsureFactoryBankBuilt(int sampleRate) noexcept
        {
            factoryIrBank = g_sharedFactoryIrCache.GetBank(sampleRate);
        }
    };

    struct PioneerMixerDsp
    {
        PioneerMixerDsp() = default;

        std::atomic_flag instanceLock = ATOMIC_FLAG_INIT;
        bool initialized = false;
        int sampleRate = 48000;
        BiQuad lpLow250_1[8], lpLow250_2[8], hpLow250_1[8], hpLow250_2[8];
        BiQuad lpMid3500_1[8], lpMid3500_2[8], hpHigh3500_1[8], hpHigh3500_2[8];
        BiQuad apLow3500[8];
        BiQuad micHpf80[8];
        BiQuad micHpf80_2[8];
        BiQuad deEssBp[8];
        BiQuad feedbackNotch1[8];
        BiQuad feedbackNotch2[8];
        CytomicSvf svf1[8], svf2[8];
        std::unique_ptr<DspHeavyBuffers> heavy;
        bool svfWasBypassed = true;
        bool wasLowPassMode = true;
        int lastColorMode = 0;
        int lastMicFlags = -1;
        int autoMonoRouting = 1;

        float xlr1Env = 0.0f;
        float xlr2Env = 0.0f;
        float xlr1Gain = 1.0f;
        float xlr2Gain = 0.0f;

        float micGateEnv = 0.0f;
        float micGateGain = 1.0f;
        bool  micGateOpen = false;
        float micCompEnv = 0.0f;
        float deEssEnv = 0.0f;

        int   echoWritePos = 0;
        int   echoValidFrames = 0;
        float curEchoDelaySamples = 11520.0f;
        float lastEchoFeedback = 0.35f;
        float lastEchoWet = 0.0f;
        float echoTailEnergy = 0.0f;
        float crushHoldSample[8] = {};
        int   crushPhase = 0;

        float curTrim = 1.0f, curLowLin = 1.0f, curMidLin = 1.0f, curHighLin = 1.0f;
        float curGainL = 1.0f, curGainR = 1.0f, curColorKnob = 0.5f;
        std::atomic<float> meterPfl{ 0.0f };
        std::atomic<float> meterL{ 0.0f };
        std::atomic<float> meterR{ 0.0f };
        std::atomic<float> lastFramePeakOut{ 0.0f };

        static inline float ReadEchoInterpolated(const float* buf, int writePos, float delaySamples) noexcept
        {
            const float clampedDelay = std::clamp(delaySamples, 16.0f, static_cast<float>(ECHO_BUF_LEN - 4));
            const int intDelay = static_cast<int>(clampedDelay);
            const float frac = clampedDelay - static_cast<float>(intDelay);

            const int pos0 = (writePos - intDelay + ECHO_BUF_LEN) & (ECHO_BUF_LEN - 1);
            const int pos1 = (pos0 - 1 + ECHO_BUF_LEN) & (ECHO_BUF_LEN - 1);
            return buf[pos0] * (1.0f - frac) + buf[pos1] * frac;
        }

        void InitUnlocked(int rate) noexcept
        {
            sampleRate = (rate > 0) ? rate : 48000;
            const float sr = static_cast<float>(sampleRate);
            for (int ch = 0; ch < 8; ++ch)
            {
                lpLow250_1[ch].Reset(); lpLow250_2[ch].Reset();
                hpLow250_1[ch].Reset(); hpLow250_2[ch].Reset();
                lpMid3500_1[ch].Reset(); lpMid3500_2[ch].Reset();
                hpHigh3500_1[ch].Reset(); hpHigh3500_2[ch].Reset();
                apLow3500[ch].Reset();
                micHpf80[ch].Reset();
                micHpf80_2[ch].Reset();
                deEssBp[ch].Reset();
                feedbackNotch1[ch].Reset();
                feedbackNotch2[ch].Reset();
                svf1[ch].Reset(); svf2[ch].Reset();
                crushHoldSample[ch] = 0.0f;

                lpLow250_1[ch].SetLowPass(sr, yoump::kLowMidCrossoverHz, SQRT2_INV);
                lpLow250_2[ch].SetLowPass(sr, yoump::kLowMidCrossoverHz, SQRT2_INV);
                hpLow250_1[ch].SetHighPass(sr, yoump::kLowMidCrossoverHz, SQRT2_INV);
                hpLow250_2[ch].SetHighPass(sr, yoump::kLowMidCrossoverHz, SQRT2_INV);

                lpMid3500_1[ch].SetLowPass(sr, yoump::kMidHighCrossoverHz, SQRT2_INV);
                lpMid3500_2[ch].SetLowPass(sr, yoump::kMidHighCrossoverHz, SQRT2_INV);
                hpHigh3500_1[ch].SetHighPass(sr, yoump::kMidHighCrossoverHz, SQRT2_INV);
                hpHigh3500_2[ch].SetHighPass(sr, yoump::kMidHighCrossoverHz, SQRT2_INV);

                apLow3500[ch].SetAllPass(sr, yoump::kMidHighCrossoverHz, SQRT2_INV);
                micHpf80[ch].SetHighPass(sr, 80.0f, SQRT2_INV);
                micHpf80_2[ch].SetHighPass(sr, 80.0f, SQRT2_INV);
                deEssBp[ch].SetPeakingEq(sr, 6500.0f, 1.85f, 12.0f);
                feedbackNotch1[ch].SetPeakingEq(sr, 2150.0f, 6.5f, -9.5f);
                feedbackNotch2[ch].SetPeakingEq(sr, 3850.0f, 6.5f, -9.5f);

                svf1[ch].SetCoeffs(sr, 20000.0f, 1.45f);
                svf2[ch].SetCoeffs(sr, 20000.0f, 0.85f);
            }
            if (!heavy)
            {
                heavy.reset(new (std::nothrow) DspHeavyBuffers());
            }
            if (heavy)
            {
                heavy->EnsureFactoryBankBuilt(sampleRate);
                heavy->micConvolver.activePreset = -1;
                heavy->micConvolver.ConfigurePresetFast(0, sampleRate, nullptr, heavy->factoryIrBank);
                heavy->spaceReverbConvolverL.activePreset = -1;
                heavy->spaceReverbConvolverL.ConfigurePresetFast(6, sampleRate, nullptr, heavy->factoryIrBank);
                heavy->spaceReverbConvolverR.activePreset = -1;
                heavy->spaceReverbConvolverR.ConfigurePresetFast(8, sampleRate, nullptr, heavy->factoryIrBank);
                std::memset(heavy->echoBuf, 0, sizeof(heavy->echoBuf));
            }
            echoWritePos = 0;
            echoValidFrames = 0;
            curEchoDelaySamples = std::clamp(sr * 0.240f, 256.0f, static_cast<float>(ECHO_BUF_LEN - 4));
            lastEchoFeedback = 0.35f;
            lastEchoWet = 0.0f;
            echoTailEnergy = 0.0f;
            crushPhase = 0;
            micGateEnv = 0.0f;
            micGateGain = 1.0f;
            micGateOpen = false;
            micCompEnv = 0.0f;
            deEssEnv = 0.0f;
            svfWasBypassed = true;
            wasLowPassMode = true;
            lastColorMode = 0;
            lastMicFlags = -1;
            autoMonoRouting = 1;
            xlr1Env = 0.0f;
            xlr2Env = 0.0f;
            xlr1Gain = 1.0f;
            xlr2Gain = 0.0f;
            curTrim = 1.0f;
            curLowLin = 1.0f;
            curMidLin = 1.0f;
            curHighLin = 1.0f;
            curGainL = 1.0f;
            curGainR = 1.0f;
            curColorKnob = 0.5f;
            meterPfl.store(0.0f, std::memory_order_relaxed);
            meterL.store(0.0f, std::memory_order_relaxed);
            meterR.store(0.0f, std::memory_order_relaxed);
            lastFramePeakOut.store(0.0f, std::memory_order_relaxed);
            initialized = true;
        }

        void Init(int rate) noexcept
        {
            ScopedSpinLock guard(instanceLock);
            InitUnlocked(rate);
        }

        void InitIfSampleRateChanged(int rate, bool activeSlot) noexcept
        {
            ScopedSpinLock guard(instanceLock);
            const int safeSr = (rate > 0) ? rate : 48000;
            if (!initialized || sampleRate != safeSr)
            {
                if (activeSlot || initialized)
                {
                    InitUnlocked(safeSr);
                }
                else
                {
                    sampleRate = safeSr;
                }
            }
        }

        void ResetStreamFilterStateNonBlocking() noexcept
        {
            ScopedTrySpinLock guard(instanceLock);
            if (!guard.acquired || !initialized) return;

            for (int ch = 0; ch < 8; ++ch)
            {
                lpLow250_1[ch].Reset(); lpLow250_2[ch].Reset();
                hpLow250_1[ch].Reset(); hpLow250_2[ch].Reset();
                lpMid3500_1[ch].Reset(); lpMid3500_2[ch].Reset();
                hpHigh3500_1[ch].Reset(); hpHigh3500_2[ch].Reset();
                apLow3500[ch].Reset();
                micHpf80[ch].Reset();
                micHpf80_2[ch].Reset();
                deEssBp[ch].Reset();
                feedbackNotch1[ch].Reset();
                feedbackNotch2[ch].Reset();
                svf1[ch].Reset(); svf2[ch].Reset();
                crushHoldSample[ch] = 0.0f;
            }
            micGateEnv = 0.0f;
            micCompEnv = 0.0f;
            deEssEnv = 0.0f;
        }

        int ProcessBuffer(float* samples, int frameCount, int channels, const YoumpMixerParams& p, YoumpMixerMeters* outMeters) noexcept
        {
            if (frameCount < 0 || samples == nullptr) return 1;
            if (channels < 1 || channels > 8) return 2;
            if (frameCount == 0) return 0;
            if (frameCount > (std::numeric_limits<int>::max)() / channels) return 3;

            ScopedTrySpinLock guard(instanceLock);
            if (!guard.acquired)
            {
                if (((p.limiterEnabled & 0x10) != 0) || p.channelFader <= 0.001f || p.masterVolume <= 0.001f)
                {
                    std::memset(samples, 0, static_cast<size_t>(frameCount) * static_cast<size_t>(channels) * sizeof(float));
                    lastFramePeakOut.store(0.0f, std::memory_order_relaxed);
                }
                if (outMeters != nullptr)
                {
                    const float curPfl = meterPfl.load(std::memory_order_relaxed);
                    const float curL = meterL.load(std::memory_order_relaxed);
                    const float curR = meterR.load(std::memory_order_relaxed);
                    outMeters->channelPeakPfl = std::min(1.0f, curPfl * 1.35f);
                    outMeters->masterPeakL = std::min(1.0f, curL * 1.35f);
                    outMeters->masterPeakR = std::min(1.0f, (channels >= 2 ? curR : curL) * 1.35f);
                }
                return 0;
            }

            if (!initialized || !heavy) InitUnlocked(sampleRate > 0 ? sampleRate : 48000);
            DspHeavyBuffers* hb = heavy.get();
            if (hb == nullptr) return 0;

            const int packedFlags = p.limiterEnabled;
            const bool limiter = ((packedFlags & 0x01) != 0);
            const int colorMode = (packedFlags >> 1) & 0x03;
            const bool micStudioEnabled = ((packedFlags & 0x08) == 0);
            const bool isChannelMuted = ((packedFlags & 0x10) != 0);
            const int packedIrPreset = (packedFlags >> 5) & 0x07;

            const YoumpSharedDspPage* shm = g_shmPage.load(std::memory_order_acquire);
            int activeMicIrPreset = packedIrPreset;
            float activeMicIrMix = 1.0f;
            float configuredGateThreshold = 0.0045f;
            int micExtraFlags = 3;
            if (shm != nullptr)
            {
                const uint64_t appTick = *reinterpret_cast<const volatile uint64_t*>(&shm->lastApoProcessTick);
#ifdef _WIN32
                const uint64_t curTick = GetTickCount64();
#else
                const uint64_t curTick = appTick;
#endif
                const bool appAlive = (appTick > 0ULL && (curTick <= appTick || (curTick - appTick) <= APP_HEARTBEAT_TIMEOUT_MS));
                if (appAlive)
                {
                    if (activeMicIrPreset == 0 && shm->micIrPreset > 0 && shm->micIrPreset <= 7)
                    {
                        activeMicIrPreset = shm->micIrPreset;
                    }
                    if (std::isfinite(shm->micIrMix) && shm->micIrMix >= 0.0f && shm->micIrMix <= 1.0f)
                    {
                        activeMicIrMix = shm->micIrMix;
                    }
                    if (std::isfinite(shm->micGateThreshold) && shm->micGateThreshold >= 0.0f && shm->micGateThreshold <= 0.25f)
                    {
                        configuredGateThreshold = shm->micGateThreshold;
                    }
                    micExtraFlags = shm->micExtraFlags;
                }
            }

            const bool deEsserEnabled = ((micExtraFlags & 0x01) != 0);
            const bool plosive24dB = ((micExtraFlags & 0x02) != 0);
            const bool antiFeedbackEnabled = ((micExtraFlags & 0x04) != 0);
            const int xlrInputMode = (micExtraFlags >> 3) & 0x03;

            const float rawTrim = std::clamp(p.trimGain, 0.0f, 2.0f);
            const float eqLow = std::clamp(p.eqLowDb, -24.0f, 12.0f);
            const float eqMid = std::clamp(p.eqMidDb, -24.0f, 12.0f);
            const float eqHigh = std::clamp(p.eqHighDb, -24.0f, 12.0f);
            const float targetColor = std::clamp(p.colorKnob, 0.0f, 1.0f);
            const float faderVal = isChannelMuted ? 0.0f : std::clamp(p.channelFader, 0.0f, 1.0f);
            const float masterVal = std::clamp(p.masterVolume, 0.0f, 1.0f);
            const bool isMicAutoMono = (p.balance < -0.5f);
            const float bal = isMicAutoMono ? 0.5f : std::clamp(p.balance, 0.0f, 1.0f);

            if (isMicAutoMono)
            {
                const int curMicFlagsMask = (micStudioEnabled ? 0x100 : 0) | (micExtraFlags & 0x1F);
                if (lastMicFlags != -1 && lastMicFlags != curMicFlagsMask)
                {
                    const int diffMask = lastMicFlags ^ curMicFlagsMask;
                    if ((diffMask & 0x100) != 0)
                    {
                        micHpf80[0].Reset();
                        micHpf80_2[0].Reset();
                        deEssBp[0].Reset();
                        micGateEnv = 0.0f;
                        micGateGain = 1.0f;
                        micGateOpen = false;
                        micCompEnv = 0.0f;
                        deEssEnv = 0.0f;
                    }
                    if ((diffMask & 0x01) != 0)
                    {
                        deEssBp[0].Reset();
                        deEssEnv = 0.0f;
                    }
                    if ((diffMask & 0x02) != 0)
                    {
                        micHpf80_2[0].Reset();
                    }
                    if ((diffMask & 0x04) != 0)
                    {
                        feedbackNotch1[0].Reset();
                        feedbackNotch2[0].Reset();
                    }
                }
                lastMicFlags = curMicFlagsMask;
                hb->micConvolver.ConfigurePresetFast(activeMicIrPreset, sampleRate, shm, hb->factoryIrBank);
            }

            if (isMicAutoMono && channels >= 2 && frameCount > 0)
            {
                const float srLocal = static_cast<float>(sampleRate > 0 ? sampleRate : 48000);
                const float envAtt = 1.0f - std::exp(-1.0f / (0.0015f * srLocal));
                const float envRel = 1.0f - std::exp(-1.0f / (0.0850f * srLocal));
                const float gainSmooth = 1.0f - std::exp(-1.0f / (0.0040f * srLocal));
                const float openTh = std::clamp(configuredGateThreshold * 0.5f, 0.0020f, 0.0080f);

                for (int i = 0; i < frameCount; ++i)
                {
                    const int baseFrameIdx = i * channels;
                    float in1 = samples[baseFrameIdx];
                    float in2 = samples[baseFrameIdx + 1];
                    if (!std::isfinite(in1)) in1 = 0.0f;
                    if (!std::isfinite(in2)) in2 = 0.0f;

                    float monoCenter = in1;
                    if (xlrInputMode == 1)
                    {
                        monoCenter = in1;
                    }
                    else if (xlrInputMode == 2)
                    {
                        monoCenter = in2;
                    }
                    else if (xlrInputMode == 3)
                    {
                        monoCenter = (in1 + in2) * SQRT2_INV;
                    }
                    else
                    {
                        const float a1 = std::abs(in1);
                        const float a2 = std::abs(in2);

                        xlr1Env += ((a1 > xlr1Env) ? envAtt : envRel) * (a1 - xlr1Env);
                        xlr2Env += ((a2 > xlr2Env) ? envAtt : envRel) * (a2 - xlr2Env);

                        if (xlr1Env > openTh && xlr2Env < xlr1Env * 0.25f)
                            autoMonoRouting = 1;
                        else if (xlr2Env > openTh && xlr1Env < xlr2Env * 0.25f)
                            autoMonoRouting = 2;
                        else if (xlr1Env > openTh && xlr2Env > openTh)
                            autoMonoRouting = 3;
                        else if (autoMonoRouting < 1 || autoMonoRouting > 3)
                            autoMonoRouting = 1;

                        float targetG1 = 1.0f;
                        float targetG2 = 0.0f;

                        if (autoMonoRouting == 2)
                        {
                            targetG1 = 0.0f;
                            targetG2 = 1.0f;
                        }
                        else if (autoMonoRouting == 3)
                        {
                            targetG1 = SQRT2_INV;
                            targetG2 = SQRT2_INV;
                        }

                        xlr1Gain += gainSmooth * (targetG1 - xlr1Gain);
                        xlr2Gain += gainSmooth * (targetG2 - xlr2Gain);

                        monoCenter = (in1 * xlr1Gain) + (in2 * xlr2Gain);
                    }

                    for (int ch = 0; ch < channels; ++ch)
                    {
                        samples[baseFrameIdx + ch] = monoCenter;
                    }
                }
            }

            const float sr = static_cast<float>(sampleRate);

            const float gateAttackCoeff = std::exp(-1.0f / (0.0015f * sr));
            const float gateReleaseCoeff = std::exp(-1.0f / (0.0600f * sr));
            const float gateGainAttack = std::exp(-1.0f / (0.0020f * sr));
            const float gateGainRelease = std::exp(-1.0f / (0.0850f * sr));
            const float compAttackCoeff = std::exp(-1.0f / (0.0050f * sr));
            const float compReleaseCoeff = std::exp(-1.0f / (0.0900f * sr));
            const float deEssAttCoeff = std::exp(-1.0f / (0.0010f * sr));
            const float deEssRelCoeff = std::exp(-1.0f / (0.0400f * sr));
            const float delaySlewCoeff = 1.0f - std::exp(-1.0f / (0.0120f * sr));

            const float targetTrim = yoump::CalculatePioneerTrimLinear(rawTrim);
            const float targetLowLin = yoump::DbToIsolatorLinear(eqLow);
            const float targetMidLin = yoump::DbToIsolatorLinear(eqMid);
            const float targetHighLin = yoump::DbToIsolatorLinear(eqHigh);

            const float targetFader = faderVal * faderVal;
            const float targetMaster = masterVal * masterVal;
            float targetGainL = targetFader * targetMaster;
            float targetGainR = targetFader * targetMaster;
            if (bal < 0.5f) targetGainR *= (bal * 2.0f);
            else            targetGainL *= ((1.0f - bal) * 2.0f);

            const float invFrames = 1.0f / static_cast<float>(frameCount);
            const float stepTrim = (targetTrim - curTrim) * invFrames;
            const float stepLow = (targetLowLin - curLowLin) * invFrames;
            const float stepMid = (targetMidLin - curMidLin) * invFrames;
            const float stepHigh = (targetHighLin - curHighLin) * invFrames;
            const float stepGL = (targetGainL - curGainL) * invFrames;
            const float stepGR = (targetGainR - curGainR) * invFrames;
            const float stepCol = (targetColor - curColorKnob) * invFrames;

            if (colorMode != lastColorMode)
            {
                for (int c = 0; c < channels; ++c) { svf1[c].Reset(); svf2[c].Reset(); crushHoldSample[c] = 0.0f; }
                hb->spaceReverbConvolverL.ResetState();
                hb->spaceReverbConvolverR.ResetState();
                std::memset(hb->echoBuf, 0, sizeof(hb->echoBuf));
                echoWritePos = 0;
                echoValidFrames = 0;
                curEchoDelaySamples = std::clamp(sr * ((targetColor < 0.5f) ? 0.240f : 0.165f), 256.0f, static_cast<float>(ECHO_BUF_LEN - 4));
                lastEchoFeedback = 0.35f;
                lastEchoWet = 0.0f;
                echoTailEnergy = 0.0f;
                if (colorMode == 1)
                {
                    const float initEchoCutoff = (targetColor < 0.5f) ? 1600.0f : 450.0f;
                    for (int c = 0; c < channels; ++c) svf1[c].SetCoeffs(sr, initEchoCutoff, 0.8f);
                }
                lastColorMode = colorMode;
            }

            const bool bypassColor = (std::abs(targetColor - 0.5f) < 0.008f && std::abs(curColorKnob - 0.5f) < 0.008f);
            if (bypassColor)
            {
                curColorKnob = targetColor;
                if (!svfWasBypassed)
                {
                    if (colorMode != 1)
                    {
                        for (int c = 0; c < channels; ++c) { svf1[c].Reset(); svf2[c].Reset(); }
                    }
                    svfWasBypassed = true;
                }
            }
            else
            {
                if (svfWasBypassed)
                {
                    wasLowPassMode = ((curColorKnob + stepCol) < 0.5f);
                    if (colorMode == 1)
                    {
                        const float initEchoCutoff = wasLowPassMode ? 1600.0f : 450.0f;
                        if (echoTailEnergy <= 1.0e-5f)
                        {
                            curEchoDelaySamples = std::clamp(sr * (wasLowPassMode ? 0.240f : 0.165f), 256.0f, static_cast<float>(ECHO_BUF_LEN - 4));
                        }
                        for (int c = 0; c < channels; ++c) svf1[c].SetCoeffs(sr, initEchoCutoff, 0.8f);
                    }
                }
                svfWasBypassed = false;
            }

            float maxPfl = 0.0f, maxOutL = 0.0f, maxOutR = 0.0f;

            for (int i = 0; i < frameCount; ++i)
            {
                curTrim += stepTrim;
                curLowLin += stepLow;
                curMidLin += stepMid;
                curHighLin += stepHigh;
                curGainL += stepGL;
                curGainR += stepGR;
                if (!bypassColor) curColorKnob += stepCol;

                const float k = curColorKnob;
                bool modeSwitched = false;

                if (!bypassColor && (colorMode == 0 || colorMode == 1))
                {
                    const bool isLowPass = (k < 0.5f);
                    if (isLowPass != wasLowPassMode)
                    {
                        for (int c = 0; c < channels; ++c)
                        {
                            svf1[c].Reset();
                            svf2[c].Reset();
                        }
                        if (colorMode == 1)
                        {
                            echoTailEnergy *= 0.25f;
                            lastEchoWet *= 0.25f;
                        }
                        wasLowPassMode = isLowPass;
                        modeSwitched = true;
                    }

                    if (colorMode == 0 && (modeSwitched || (i & 7) == 0 || i == frameCount - 1))
                    {
                        if (isLowPass)
                        {
                            const float t = std::clamp(k / 0.48f, 0.0f, 1.0f);
                            const float freq = 30.0f * std::pow(18000.0f / 30.0f, t);
                            for (int c = 0; c < channels; ++c) { svf1[c].SetCoeffs(sr, freq, 1.45f); svf2[c].SetCoeffs(sr, freq, 0.85f); }
                        }
                        else
                        {
                            const float t = std::clamp((k - 0.52f) / 0.48f, 0.0f, 1.0f);
                            const float freq = 25.0f * std::pow(15000.0f / 25.0f, t);
                            for (int c = 0; c < channels; ++c) { svf1[c].SetCoeffs(sr, freq, 1.40f); svf2[c].SetCoeffs(sr, freq, 0.85f); }
                        }
                    }
                    else if (colorMode == 1 && (modeSwitched || (i & 15) == 0))
                    {
                        const float echoCutoff = isLowPass ? 1600.0f : 450.0f;
                        for (int c = 0; c < channels; ++c)
                        {
                            svf1[c].SetCoeffs(sr, echoCutoff, 0.8f);
                        }
                    }
                }

                float colorWet = 0.0f, edgeAtten = 1.0f;
                const float distFromCenter = std::abs(k - 0.5f);
                if (!bypassColor)
                {
                    colorWet = 1.0f;
                    if (distFromCenter <= 0.01f)      colorWet = 0.0f;
                    else if (distFromCenter < 0.03f)  colorWet = (distFromCenter - 0.01f) / 0.02f;

                    if (colorMode == 0)
                    {
                        if (k < 0.025f)      edgeAtten = std::max(0.0f, k / 0.025f);
                        else if (k > 0.975f) edgeAtten = std::max(0.0f, (1.0f - k) / 0.025f);
                    }
                }

                if (isMicAutoMono)
                {
                    float micMono = samples[i * channels];
                    if (!std::isfinite(micMono)) micMono = 0.0f;

                    if (micStudioEnabled)
                    {
                        micMono = micHpf80[0].Transform(micMono);
                        if (plosive24dB)
                        {
                            micMono = micHpf80_2[0].Transform(micMono);
                        }
                    }

                    micMono *= curTrim;

                    if (activeMicIrPreset > 0 && activeMicIrMix > 0.001f)
                    {
                        const float wetIr = hb->micConvolver.ProcessSample(micMono);
                        micMono = micMono * (1.0f - activeMicIrMix) + wetIr * activeMicIrMix;
                    }

                    if (antiFeedbackEnabled)
                    {
                        micMono = -micMono;
                        micMono = feedbackNotch2[0].Transform(feedbackNotch1[0].Transform(micMono));
                    }

                    if (micStudioEnabled)
                    {
                        const float framePeak = std::abs(micMono);
                        if (framePeak > micGateEnv)
                            micGateEnv = gateAttackCoeff * micGateEnv + (1.0f - gateAttackCoeff) * framePeak;
                        else
                            micGateEnv = gateReleaseCoeff * micGateEnv + (1.0f - gateReleaseCoeff) * framePeak;

                        const float openGateTh = std::max(0.0f, configuredGateThreshold);
                        const float closeGateTh = openGateTh * 0.65f;

                        if (openGateTh <= 0.0001f)
                        {
                            micGateOpen = true;
                        }
                        else
                        {
                            if (!micGateOpen && micGateEnv > openGateTh) micGateOpen = true;
                            else if (micGateOpen && micGateEnv < closeGateTh) micGateOpen = false;
                        }

                        const float closedFloor = (closeGateTh > 0.0001f)
                            ? std::clamp(micGateEnv / closeGateTh, 0.08f, 1.0f)
                            : 1.0f;
                        const float targetGate = micGateOpen ? 1.0f : closedFloor;
                        if (targetGate > micGateGain)
                            micGateGain = gateGainAttack * micGateGain + (1.0f - gateGainAttack) * targetGate;
                        else
                            micGateGain = gateGainRelease * micGateGain + (1.0f - gateGainRelease) * targetGate;

                        micMono *= micGateGain;

                        if (deEsserEnabled)
                        {
                            const float sibiliant = (deEssBp[0].Transform(micMono) - micMono) * 0.3354f;
                            const float absSib = std::abs(sibiliant);
                            deEssEnv = (absSib > deEssEnv)
                                ? (deEssAttCoeff * deEssEnv + (1.0f - deEssAttCoeff) * absSib)
                                : (deEssRelCoeff * deEssEnv + (1.0f - deEssRelCoeff) * absSib);
                            if (deEssEnv > 0.045f)
                            {
                                const float deEssAtten = std::min(0.65f, (deEssEnv - 0.045f) * 2.2f);
                                micMono -= sibiliant * deEssAtten;
                            }
                        }

                        const float gatedPeak = std::abs(micMono);
                        if (gatedPeak > micCompEnv)
                            micCompEnv = compAttackCoeff * micCompEnv + (1.0f - compAttackCoeff) * gatedPeak;
                        else
                            micCompEnv = compReleaseCoeff * micCompEnv + (1.0f - compReleaseCoeff) * gatedPeak;

                        float compGain = 1.32f;
                        if (micCompEnv > 0.18f)
                        {
                            const float overRatio = micCompEnv / 0.18f;
                            compGain = 1.32f * std::pow(overRatio, -0.666f);
                        }
                        micMono *= compGain;
                    }

                    for (int ch = 0; ch < channels; ++ch)
                    {
                        samples[i * channels + ch] = micMono;
                    }
                }

                const float normDist = std::clamp(distFromCenter * 2.0f, 0.0f, 1.0f);
                const int crushDecim = (colorMode == 2 && k > 0.5f)
                    ? std::max(1, static_cast<int>(1.0f + normDist * 18.0f))
                    : 1;
                const bool updateCrushHold = ((crushPhase % crushDecim) == 0);
                crushPhase++;

                const bool hasEchoTailThisFrame = (echoTailEnergy > 1.0e-5f);
                const float targetDelaySamples = std::clamp(sr * ((k < 0.5f) ? 0.240f : 0.165f), 256.0f, static_cast<float>(ECHO_BUF_LEN - 4));
                if (colorWet <= 0.001f && !hasEchoTailThisFrame)
                {
                    curEchoDelaySamples = targetDelaySamples;
                }
                else
                {
                    curEchoDelaySamples += delaySlewCoeff * (targetDelaySamples - curEchoDelaySamples);
                }

                const bool echoReadValid = (echoValidFrames >= static_cast<int>(curEchoDelaySamples) + 2);
                const float frameEchoWet = lastEchoWet;
                float frameMaxTailEnergy = 0.0f;
                float spaceIrOut[2] = { 0.0f, 0.0f };

                if (colorMode == 1)
                {
                    if (!bypassColor)
                    {
                        const float feedback = std::min(0.78f, 0.25f + 0.52f * normDist);
                        const float wetLevel = 0.65f * normDist * colorWet;
                        lastEchoFeedback = feedback;
                        lastEchoWet = std::max(lastEchoWet * 0.9995f, wetLevel);
                    }
                    else if (hasEchoTailThisFrame)
                    {
                        lastEchoWet *= 0.9995f;
                    }
                    else
                    {
                        if (lastEchoWet > 0.0f)
                        {
                            hb->spaceReverbConvolverL.ResetState();
                            hb->spaceReverbConvolverR.ResetState();
                        }
                        echoTailEnergy = 0.0f;
                        lastEchoWet = 0.0f;
                    }
                }

                for (int ch = 0; ch < channels; ++ch)
                {
                    const int idx = i * channels + ch;
                    float x = samples[idx];
                    if (!std::isfinite(x)) x = 0.0f;

                    if (!isMicAutoMono)
                    {
                        x *= curTrim;
                    }

                    const float lowBranch = lpLow250_2[ch].Transform(lpLow250_1[ch].Transform(x));
                    const float highBranch = hpLow250_2[ch].Transform(hpLow250_1[ch].Transform(x));

                    const float lowBand = apLow3500[ch].Transform(lowBranch);
                    const float midBand = lpMid3500_2[ch].Transform(lpMid3500_1[ch].Transform(highBranch));
                    const float highBand = hpHigh3500_2[ch].Transform(hpHigh3500_1[ch].Transform(highBranch));

                    float postEq = (lowBand * curLowLin) + (midBand * curMidLin) + (highBand * curHighLin);

                    if (colorMode == 1)
                    {
                        const int echoCh = (ch < 2) ? ch : (ch & 1);
                        const int crossCh = (channels >= 2) ? (1 - echoCh) : 0;
                        const float delayed = echoReadValid
                            ? ReadEchoInterpolated(hb->echoBuf[echoCh], echoWritePos, curEchoDelaySamples)
                            : 0.0f;
                        const float crossDelayed = echoReadValid
                            ? ReadEchoInterpolated(hb->echoBuf[crossCh], echoWritePos, curEchoDelaySamples)
                            : 0.0f;

                        if (!bypassColor)
                        {
                            if (ch == 0)
                            {
                                spaceIrOut[0] = hb->spaceReverbConvolverL.ProcessSample(postEq + 0.25f * crossDelayed);
                            }
                            else if (ch == 1)
                            {
                                spaceIrOut[1] = hb->spaceReverbConvolverR.ProcessSample(postEq + 0.25f * crossDelayed);
                            }
                            const float spaceIrSample = spaceIrOut[echoCh];
                            const float fbFiltered = (k < 0.5f)
                                ? svf1[ch].ProcessLowPass(delayed + spaceIrSample * 0.35f)
                                : svf1[ch].ProcessHighPass(delayed + spaceIrSample * 0.42f);
                            const float feedback = lastEchoFeedback;
                            const float wetLevel = 0.65f * normDist * colorWet;
                            const float effectiveEchoWet = std::max(wetLevel, lastEchoWet);

                            if (ch < 2)
                            {
                                float echoWriteVal = postEq * colorWet + fbFiltered * feedback;
                                if (!std::isfinite(echoWriteVal)) echoWriteVal = 0.0f;
                                hb->echoBuf[ch][echoWritePos] = std::clamp(echoWriteVal, -2.0f, 2.0f);
                                frameMaxTailEnergy = std::max(frameMaxTailEnergy, std::abs(echoWriteVal));
                            }
                            postEq = postEq + (fbFiltered * 0.72f + spaceIrSample * 0.28f) * effectiveEchoWet;
                        }
                        else
                        {
                            if (hasEchoTailThisFrame)
                            {
                                if (ch == 0)
                                {
                                    spaceIrOut[0] = hb->spaceReverbConvolverL.ProcessSample(0.25f * crossDelayed);
                                }
                                else if (ch == 1)
                                {
                                    spaceIrOut[1] = hb->spaceReverbConvolverR.ProcessSample(0.25f * crossDelayed);
                                }
                                const float spaceIrSample = spaceIrOut[echoCh];
                                const float fbFiltered = wasLowPassMode
                                    ? svf1[ch].ProcessLowPass(delayed + spaceIrSample * 0.35f)
                                    : svf1[ch].ProcessHighPass(delayed + spaceIrSample * 0.42f);
                                const float tailFeedback = lastEchoFeedback * 0.92f;

                                if (ch < 2)
                                {
                                    float echoWriteVal = postEq + fbFiltered * tailFeedback;
                                    if (!std::isfinite(echoWriteVal)) echoWriteVal = 0.0f;
                                    hb->echoBuf[ch][echoWritePos] = std::clamp(echoWriteVal, -2.0f, 2.0f);
                                    frameMaxTailEnergy = std::max(frameMaxTailEnergy, std::abs(fbFiltered * tailFeedback));
                                }
                                postEq = postEq + (fbFiltered * 0.72f + spaceIrSample * 0.28f) * frameEchoWet;
                            }
                            else
                            {
                                if (ch < 2)
                                {
                                    hb->echoBuf[ch][echoWritePos] = std::clamp(postEq, -2.0f, 2.0f);
                                }
                            }
                        }
                    }
                    else if (!bypassColor)
                    {
                        if (colorMode == 0)
                        {
                            const float filtered = (k < 0.5f)
                                ? svf2[ch].ProcessLowPass(svf1[ch].ProcessLowPass(postEq)) * edgeAtten
                                : svf2[ch].ProcessHighPass(svf1[ch].ProcessHighPass(postEq)) * edgeAtten;
                            postEq = postEq * (1.0f - colorWet) + filtered * colorWet;
                        }
                        else if (colorMode == 2)
                        {
                            float crushed = postEq;
                            if (k < 0.5f)
                            {
                                const float drive = 1.0f + normDist * 4.5f;
                                crushed = std::tanh(postEq * drive) / std::tanh(drive);
                            }
                            else
                            {
                                if (updateCrushHold)
                                {
                                    const float steps = std::max(4.0f, 64.0f * (1.0f - normDist * 0.88f));
                                    crushHoldSample[ch] = std::round(postEq * steps) / steps;
                                }
                                crushed = crushHoldSample[ch];
                            }
                            postEq = postEq * (1.0f - colorWet * normDist) + crushed * (colorWet * normDist);
                        }
                        else if (colorMode == 3)
                        {
                            if (k > 0.5f)
                            {
                                const float drive = 1.0f + normDist * 1.8f;
                                const float normTanh = std::max(0.001f, std::tanh(drive));
                                const float subBoost = (std::tanh(lowBand * drive) / normTanh) * (1.0f + 0.55f * normDist);
                                const float airBoost = highBand * (1.0f + normDist * 1.2f);
                                const float loudSig = (subBoost * curLowLin) + (midBand * curMidLin) + (airBoost * curHighLin);
                                postEq = postEq * (1.0f - colorWet) + loudSig * colorWet;
                            }
                            else
                            {
                                const float vocalSig = (lowBand * curLowLin * (1.0f - 0.65f * normDist))
                                    + (midBand * curMidLin * (1.0f + 0.65f * normDist))
                                    + (highBand * curHighLin * (1.0f - 0.35f * normDist));
                                postEq = postEq * (1.0f - colorWet) + vocalSig * colorWet;
                            }
                        }
                    }

                    const float absPfl = std::abs(postEq);
                    if (absPfl > maxPfl) maxPfl = absPfl;

                    const float chGain = (ch == 0) ? curGainL : ((ch == 1) ? curGainR : 0.5f * (curGainL + curGainR));
                    float outSample = postEq * chGain;
                    if (limiter) outSample = PioneerSoftKneeLimiter(outSample);
                    if (!std::isfinite(outSample)) outSample = 0.0f;
                    samples[idx] = outSample;

                    const float absOut = std::abs(outSample);
                    if (ch == 0) { if (absOut > maxOutL) maxOutL = absOut; }
                    else { if (absOut > maxOutR) maxOutR = absOut; }
                }

                if (colorMode == 1)
                {
                    if (channels == 1)
                    {
                        hb->echoBuf[1][echoWritePos] = hb->echoBuf[0][echoWritePos];
                    }
                    if (!bypassColor || hasEchoTailThisFrame)
                    {
                        echoTailEnergy = std::max(echoTailEnergy * 0.99985f, frameMaxTailEnergy);
                    }
                    echoWritePos = (echoWritePos + 1) & (ECHO_BUF_LEN - 1);
                    if (echoValidFrames < ECHO_BUF_LEN)
                    {
                        ++echoValidFrames;
                    }
                }
            }

            curTrim = targetTrim; curLowLin = targetLowLin; curMidLin = targetMidLin; curHighLin = targetHighLin;
            curGainL = targetGainL; curGainR = targetGainR; curColorKnob = targetColor;

            lastFramePeakOut.store(std::max(maxOutL, maxOutR), std::memory_order_relaxed);
            const float decayRate = std::exp(-static_cast<float>(frameCount) / (0.30f * sr));
            const float prevPfl = meterPfl.load(std::memory_order_relaxed);
            const float prevL = meterL.load(std::memory_order_relaxed);
            const float prevR = meterR.load(std::memory_order_relaxed);
            const float nextPfl = (maxPfl >= prevPfl) ? maxPfl : (prevPfl * decayRate);
            const float nextL = (maxOutL >= prevL) ? maxOutL : (prevL * decayRate);
            const float nextR = (maxOutR >= prevR) ? maxOutR : (prevR * decayRate);

            meterPfl.store(nextPfl, std::memory_order_relaxed);
            meterL.store(nextL, std::memory_order_relaxed);
            meterR.store(nextR, std::memory_order_relaxed);

            if (outMeters != nullptr)
            {
                outMeters->channelPeakPfl = std::min(1.0f, nextPfl * 1.35f);
                outMeters->masterPeakL = std::min(1.0f, nextL * 1.35f);
                outMeters->masterPeakR = std::min(1.0f, (channels >= 2 ? nextR : nextL) * 1.35f);
            }
            return 0;
        }
    };

    PioneerMixerDsp g_apoInstances[MAX_DSP_INSTANCES];
    PioneerMixerDsp g_channelInstances[MAX_CH_INSTANCES];

    std::atomic<bool>     g_slotInUse[MAX_DSP_INSTANCES] = {};
    std::atomic<bool>     g_slotHasActiveOutput[MAX_DSP_INSTANCES] = {};
    std::atomic<int>      g_slotLockedChan[MAX_DSP_INSTANCES] = {};
    std::atomic<int>      g_slotMatchFrames[MAX_DSP_INSTANCES] = {};
    std::atomic<int>      g_slotMismatchFrames[MAX_DSP_INSTANCES] = {};
    std::atomic<uint64_t> g_slotLastTick[MAX_DSP_INSTANCES] = {};
    std::atomic<float>    g_slotEnvPeak[MAX_DSP_INSTANCES] = {};
    std::atomic<float>    g_slotChanErr[MAX_DSP_INSTANCES][16] = {};

    inline void ResetSlotChannelLock(int slotId) noexcept
    {
        if (slotId < 0 || slotId >= MAX_DSP_INSTANCES) return;
        g_slotLockedChan[slotId].store(-1, std::memory_order_relaxed);
        g_slotMatchFrames[slotId].store(0, std::memory_order_relaxed);
        g_slotMismatchFrames[slotId].store(0, std::memory_order_relaxed);
        for (int c = 0; c < 16; ++c)
        {
            g_slotChanErr[slotId][c].store(0.0f, std::memory_order_relaxed);
        }
    }

    inline void ResetSlotTrackingState(int slotId) noexcept
    {
        if (slotId < 0 || slotId >= MAX_DSP_INSTANCES) return;
        ResetSlotChannelLock(slotId);
        g_slotHasActiveOutput[slotId].store(false, std::memory_order_relaxed);
    }
}

int YoumpDsp_GetVersion()
{
    EnsureSharedDspPageMapped(false);
    return 4;
}

void YoumpDsp_SetGainDb(float gainDb)
{
    const float safeGain = ClampGainDb(gainDb);
    const float linear = yoump::DbToLinear(safeGain);
    g_gainDb.store(safeGain, std::memory_order_relaxed);
    g_gainLinear.store(linear, std::memory_order_relaxed);
    g_legacyGainMode.store(true, std::memory_order_release);

    if (auto* shm = EnsureSharedDspPageMapped(true))
    {
        shm->gainDb = safeGain;
        shm->gainLinear = linear;
        shm->legacyGainMode = 1;
        shm->channelCount = 0;
    }
}

int YoumpDsp_Process(float* samples, int frameCount, int channels, float gainDb)
{
    return ProcessWithMultiplier(samples, frameCount, channels, yoump::DbToLinear(ClampGainDb(gainDb)));
}

int YoumpDsp_ProcessCurrentGain(float* samples, int frameCount, int channels)
{
    float multiplier = g_gainLinear.load(std::memory_order_relaxed);
    if (auto* shm = g_shmPage.load(std::memory_order_acquire))
    {
        multiplier = shm->gainLinear;
    }
    else if (auto* mapped = EnsureSharedDspPageMapped(false))
    {
        multiplier = mapped->gainLinear;
    }
    return ProcessWithMultiplier(samples, frameCount, channels, multiplier);
}

void YoumpDsp_InitMixer(int sampleRate)
{
    g_legacyGainMode.store(false, std::memory_order_release);
    g_paramsExplicitlySet.store(false, std::memory_order_release);
    const int safeSr = (sampleRate > 0) ? sampleRate : 48000;
    for (int i = 0; i < MAX_DSP_INSTANCES; ++i)
    {
        const bool isActiveSlot = (i == 0 || g_slotInUse[i].load(std::memory_order_relaxed));
        g_apoInstances[i].InitIfSampleRateChanged(safeSr, isActiveSlot);
    }
    for (int i = 0; i < MAX_CH_INSTANCES; ++i)
    {
        g_channelInstances[i].InitIfSampleRateChanged(safeSr, true);
    }
    if (auto* shm = EnsureSharedDspPageMapped(true))
    {
        shm->legacyGainMode = 0;
    }
    else
    {
        EnsureSharedDspPageMapped(false);
    }
}

int YoumpDsp_AcquireSlot(int sampleRate)
{
    g_paramsExplicitlySet.store(false, std::memory_order_release);
    EnsureSharedDspPageMapped(false);
    for (int i = 0; i < MAX_DSP_INSTANCES; ++i)
    {
        bool expected = false;
        if (g_slotInUse[i].compare_exchange_strong(expected, true, std::memory_order_acq_rel))
        {
            ResetSlotTrackingState(i);
            g_slotLastTick[i].store(0ULL, std::memory_order_relaxed);
            g_slotEnvPeak[i].store(0.0f, std::memory_order_relaxed);
            g_apoInstances[i].Init(sampleRate);
            return i;
        }
    }
    return -1;
}

void YoumpDsp_ReleaseSlot(int slotId)
{
    if (slotId >= 0 && slotId < MAX_DSP_INSTANCES)
    {
        ResetSlotTrackingState(slotId);
        g_slotLastTick[slotId].store(0ULL, std::memory_order_relaxed);
        g_slotEnvPeak[slotId].store(0.0f, std::memory_order_relaxed);
        g_slotInUse[slotId].store(false, std::memory_order_release);
    }
}

void YoumpDsp_InitMixerInstance(int instanceId, int sampleRate)
{
    g_paramsExplicitlySet.store(false, std::memory_order_release);
    const int safeInst = ClampSlotIndex(instanceId);
    if (safeInst < 0 || safeInst >= MAX_DSP_INSTANCES) return;
    const int safeSr = (sampleRate > 0) ? sampleRate : 48000;
    if (g_apoInstances[safeInst].initialized && g_apoInstances[safeInst].sampleRate == safeSr)
    {
        g_apoInstances[safeInst].ResetStreamFilterStateNonBlocking();
    }
    else
    {
        g_apoInstances[safeInst].Init(safeSr);
        EnsureSharedDspPageMapped(false);
    }
}

void YoumpDsp_SetMixerParams(const YoumpMixerParams* p)
{
    if (!p) return;
    const YoumpMixerParams clean = SanitizeMixerParams(*p);
    const float trim = std::clamp(clean.trimGain, 0.0f, 2.0f);
    const float low = std::clamp(clean.eqLowDb, -24.0f, 12.0f);
    const float mid = std::clamp(clean.eqMidDb, -24.0f, 12.0f);
    const float high = std::clamp(clean.eqHighDb, -24.0f, 12.0f);
    const float color = std::clamp(clean.colorKnob, 0.0f, 1.0f);
    const float fader = std::clamp(clean.channelFader, 0.0f, 1.0f);
    const float master = std::clamp(clean.masterVolume, 0.0f, 1.0f);
    const float bal = (clean.balance < -0.5f) ? -1.0f : std::clamp(clean.balance, 0.0f, 1.0f);
    const int lim = clean.limiterEnabled;

    g_paramTrim.store(trim, std::memory_order_relaxed);
    g_paramEqLow.store(low, std::memory_order_relaxed);
    g_paramEqMid.store(mid, std::memory_order_relaxed);
    g_paramEqHigh.store(high, std::memory_order_relaxed);
    g_paramColorKnob.store(color, std::memory_order_relaxed);
    g_paramChannelFader.store(fader, std::memory_order_relaxed);
    g_paramMasterVolume.store(master, std::memory_order_relaxed);
    g_paramBalance.store(bal, std::memory_order_relaxed);
    g_paramLimiter.store(lim, std::memory_order_relaxed);
    g_paramsExplicitlySet.store(true, std::memory_order_relaxed);
    g_legacyGainMode.store(false, std::memory_order_release);

    if (auto* shm = EnsureSharedDspPageMapped(true))
    {
        shm->legacyGainMode = 0;
        shm->params.trimGain = trim;
        shm->params.eqLowDb = low;
        shm->params.eqMidDb = mid;
        shm->params.eqHighDb = high;
        shm->params.colorKnob = color;
        shm->params.channelFader = 1.0f;
        shm->params.masterVolume = 1.0f;
        shm->params.balance = (bal < -0.5f) ? -1.0f : 0.5f;
        shm->params.limiterEnabled = lim;
    }
}

void YoumpDsp_GetMixerMeters(YoumpMixerMeters* meters)
{
    if (!meters) return;
    if (auto* shm = EnsureSharedDspPageMapped(false))
    {
        *meters = shm->meters;
        return;
    }
    meters->channelPeakPfl = g_meterPfl.load(std::memory_order_relaxed);
    meters->masterPeakL = g_meterMasterL.load(std::memory_order_relaxed);
    meters->masterPeakR = g_meterMasterR.load(std::memory_order_relaxed);
}

unsigned int YoumpDsp_GetApoHeartbeat()
{
    if (auto* shm = EnsureSharedDspPageMapped(false))
    {
#ifdef _WIN32
        return static_cast<unsigned int>(InterlockedCompareExchange(&shm->apoHeartbeat, 0, 0));
#else
        return static_cast<unsigned int>(shm->apoHeartbeat);
#endif
    }
    return g_apoHeartbeat.load(std::memory_order_relaxed);
}

int YoumpDsp_InstanceHasActiveOutput(int instanceId)
{
    const int safeInst = ClampSlotIndex(instanceId);
    if (safeInst < 0 || safeInst >= MAX_DSP_INSTANCES) return 0;
    return g_slotHasActiveOutput[safeInst].load(std::memory_order_relaxed) ? 1 : 0;
}

int YoumpDsp_ProcessMixerInstance(int instanceId, float* samples, int frameCount, int channels)
{
    const bool isMicCapture = ((instanceId & 0x100) != 0);
    const int safeInst = ClampSlotIndex(instanceId);
    if (safeInst < 0 || safeInst >= MAX_DSP_INSTANCES) return 1;
    if (frameCount < 0 || samples == nullptr) return 1;
    if (channels < 1 || channels > 8) return 2;
    if (frameCount == 0) return 0;
    if (frameCount > (std::numeric_limits<int>::max)() / channels) return 3;

    YoumpSharedDspPage* shm = g_shmPage.load(std::memory_order_acquire);
    const bool shmWritable = g_shmIsWritable.load(std::memory_order_relaxed);
    if (shm == nullptr || !shmWritable)
    {
#ifdef _WIN32
        static std::atomic<uint32_t> s_retryCounter{ 0U };
        if ((s_retryCounter.fetch_add(1U, std::memory_order_relaxed) & 0xFFU) == 0U)
        {
            RequestAsyncSharedDspPageMap();
        }
#endif
    }

    if (g_legacyGainMode.load(std::memory_order_acquire) ||
        (shm != nullptr && shm->legacyGainMode != 0))
    {
        const float mult = (shm != nullptr) ? shm->gainLinear : g_gainLinear.load(std::memory_order_relaxed);
        return ProcessWithMultiplier(samples, frameCount, channels, mult);
    }

#ifdef _WIN32
    const uint64_t nowTick = GetTickCount64();
#else
    static uint64_t s_fakeTick = 1000ULL;
    const uint64_t nowTick = ++s_fakeTick;
#endif

    float inPeak = 0.0f;
    const int totalSamples = frameCount * channels;
    for (int i = 0; i < totalSamples; ++i)
    {
        float a = std::abs(samples[i]);
        if (std::isfinite(a) && a > inPeak) inPeak = a;
    }

    if (inPeak > 1.0e-5f || isMicCapture)
    {
        g_slotLastTick[safeInst].store(nowTick, std::memory_order_relaxed);
    }

    YoumpMixerParams p = {};
    p.trimGain = g_paramTrim.load(std::memory_order_relaxed);
    p.eqLowDb = g_paramEqLow.load(std::memory_order_relaxed);
    p.eqMidDb = g_paramEqMid.load(std::memory_order_relaxed);
    p.eqHighDb = g_paramEqHigh.load(std::memory_order_relaxed);
    p.colorKnob = g_paramColorKnob.load(std::memory_order_relaxed);
    p.channelFader = g_paramChannelFader.load(std::memory_order_relaxed);
    p.masterVolume = g_paramMasterVolume.load(std::memory_order_relaxed);
    p.balance = g_paramBalance.load(std::memory_order_relaxed);
    p.limiterEnabled = g_paramLimiter.load(std::memory_order_relaxed);

    const bool useSharedPage = (shm != nullptr) &&
        (!g_paramsExplicitlySet.load(std::memory_order_relaxed) ||
            isMicCapture ||
            safeInst > 0 ||
            shm->channelCount > 0);

    if (useSharedPage)
    {
        if (shmWritable)
        {
#ifdef _WIN32
            InterlockedIncrement(&shm->apoHeartbeat);
#else
            shm->apoHeartbeat++;
#endif
        }

        const uint64_t appLastTick = *reinterpret_cast<const volatile uint64_t*>(&shm->lastApoProcessTick);
        if (appLastTick > 0ULL && nowTick > appLastTick && (nowTick - appLastTick) > APP_HEARTBEAT_TIMEOUT_MS)
        {
            p = MakeFlatMixerParams();
        }
        else
        {
            const int chCount = ClampIntValue(
                static_cast<int>(*reinterpret_cast<const volatile int32_t*>(&shm->channelCount)),
                0, 16);

            bool isMicChan[16] = {};
            float rawPeakSnap[16] = {};
            float attenPeakSnap[16] = {};
            float chanGainEst[16] = {};
            YoumpMixerParams chanParamsSnap[16] = {};
            int micChanIdx = -1;
            int renderChanCount = 0;
            int onlyRenderChanIdx = -1;

            for (int c = 0; c < chCount; ++c)
            {
                const float rPk = *reinterpret_cast<const volatile float*>(&shm->channelRawPeak[c]);
                const float aPk = *reinterpret_cast<const volatile float*>(&shm->channelAttenPeak[c]);
                chanParamsSnap[c] = ReadConsistentParams(shm->channelParams[c]);
                chanGainEst[c] = EstimateChannelPostEqGain(chanParamsSnap[c]);

                rawPeakSnap[c] = std::isfinite(rPk) ? rPk : 0.0f;
                attenPeakSnap[c] = std::isfinite(aPk) ? aPk : 0.0f;
                isMicChan[c] = (chanParamsSnap[c].balance < -0.5f);

                if (isMicChan[c])
                {
                    if (micChanIdx < 0) micChanIdx = c;
                }
                else
                {
                    renderChanCount++;
                    if (onlyRenderChanIdx < 0) onlyRenderChanIdx = c;
                }
            }

            static std::atomic<int> s_lastObservedChCount{ -1 };
            const int prevChCount = s_lastObservedChCount.exchange(chCount, std::memory_order_relaxed);
            if (prevChCount != -1 && prevChCount != chCount)
            {
                for (int s = 0; s < MAX_DSP_INSTANCES; ++s)
                {
                    ResetSlotChannelLock(s);
                }
            }

            if (isMicCapture)
            {
                if (micChanIdx >= 0 && micChanIdx < chCount)
                {
                    p = chanParamsSnap[micChanIdx];
                    p.balance = -1.0f;
                }
                else
                {
                    p = MakeFlatMixerParams();
                    p.balance = -1.0f;
                }

                if (shmWritable)
                {
#ifdef _WIN32
                    InterlockedIncrement(reinterpret_cast<volatile long*>(&shm->reserved0));
#else
                    shm->reserved0++;
#endif
                }
            }
            else if (chCount == 0)
            {
                p = ReadConsistentParams(shm->params);
                p.channelFader = 1.0f;
                p.masterVolume = 1.0f;
                p.balance = 0.5f;
            }
            else if (renderChanCount <= 0)
            {
                p = MakeFlatMixerParams();
            }
            else
            {
                bool chanTaken[16] = {};
                for (int s = 0; s < MAX_DSP_INSTANCES; ++s)
                {
                    if (s != safeInst && g_slotInUse[s].load(std::memory_order_relaxed))
                    {
                        const int tc = g_slotLockedChan[s].load(std::memory_order_relaxed);
                        if (tc >= 0 && tc < chCount && !isMicChan[tc])
                        {
                            const uint64_t lastT = g_slotLastTick[s].load(std::memory_order_relaxed);
                            const bool isIntentionallySilent = (chanGainEst[tc] < 0.05f);
                            if ((lastT > 0ULL && (nowTick - lastT) <= 250ULL) ||
                                rawPeakSnap[tc] > 0.0005f ||
                                (isIntentionallySilent && lastT > 0ULL && (nowTick - lastT) <= 2000ULL))
                            {
                                chanTaken[tc] = true;
                            }
                            else if (lastT > 0ULL && (nowTick - lastT) > 1000ULL)
                            {
                                ResetSlotChannelLock(s);
                            }
                        }
                    }
                }

                const float prevSlotOutPeak = g_apoInstances[safeInst].lastFramePeakOut.load(std::memory_order_relaxed);
                int lockedC = g_slotLockedChan[safeInst].load(std::memory_order_relaxed);
                if (lockedC < 0 || lockedC >= chCount || isMicChan[lockedC])
                {
                    ResetSlotChannelLock(safeInst);
                    lockedC = -1;
                }
                else if (rawPeakSnap[lockedC] < 0.0002f &&
                         inPeak > 0.015f &&
                         prevSlotOutPeak > 0.015f &&
                         chanGainEst[lockedC] >= 0.05f)
                {
                    const int misCount = g_slotMismatchFrames[safeInst].fetch_add(1, std::memory_order_relaxed) + 1;
                    if (misCount >= MISMATCH_FRAMES_THRESHOLD)
                    {
                        ResetSlotChannelLock(safeInst);
                        lockedC = -1;
                    }
                }
                else
                {
                    g_slotMismatchFrames[safeInst].store(0, std::memory_order_relaxed);
                }

                if (lockedC < 0)
                {
                    if (renderChanCount == 1 && onlyRenderChanIdx >= 0)
                    {
                        if (inPeak > 1.0e-5f)
                        {
                            g_slotLockedChan[safeInst].store(onlyRenderChanIdx, std::memory_order_relaxed);
                        }
                        lockedC = onlyRenderChanIdx;
                    }
                    else
                    {
                        const float prevEnvPeak = g_slotEnvPeak[safeInst].load(std::memory_order_relaxed);
                        const float newEnvPeak = (inPeak >= prevEnvPeak)
                            ? inPeak
                            : (prevEnvPeak * 0.80f + inPeak * 0.20f);
                        g_slotEnvPeak[safeInst].store(newEnvPeak, std::memory_order_relaxed);

                        int activeUntakenCount = 0;
                        int singleActiveIdx = -1;
                        int firstUntakenIdx = -1;
                        int activeAnyCount = 0;
                        int singleActiveAnyIdx = -1;

                        for (int c = 0; c < chCount; ++c)
                        {
                            if (isMicChan[c]) continue;

                            if (rawPeakSnap[c] > 0.0003f)
                            {
                                activeAnyCount++;
                                singleActiveAnyIdx = c;
                            }
                            if (!chanTaken[c])
                            {
                                if (firstUntakenIdx < 0) firstUntakenIdx = c;
                                if (rawPeakSnap[c] > 0.0003f)
                                {
                                    activeUntakenCount++;
                                    singleActiveIdx = c;
                                }
                            }
                        }

                        if (inPeak > 1.0e-5f)
                        {
                            const int framesMatched = g_slotMatchFrames[safeInst].fetch_add(1, std::memory_order_relaxed) + 1;

                            for (int c = 0; c < chCount; ++c)
                            {
                                if (isMicChan[c]) continue;

                                const float rawPk = rawPeakSnap[c];
                                const float attPk = attenPeakSnap[c];
                                const float envUnscaled = newEnvPeak;
                                const float envScaled = std::min(1.0f, newEnvPeak * std::max(0.05f, chanGainEst[c]));

                                const float diffAtt = std::min(std::abs(envUnscaled - attPk), std::abs(envScaled - attPk));
                                const float diffRaw = std::min(std::abs(envUnscaled - rawPk), std::abs(envScaled - rawPk)) * 1.02f;
                                const bool closerToRaw = (diffRaw < diffAtt);
                                const float bestDiff = closerToRaw ? diffRaw : diffAtt;
                                const float refLevel = closerToRaw
                                    ? std::max(0.02f, rawPk)
                                    : std::max(0.0002f, attPk);
                                const float relDiff = std::min(10.0f, bestDiff / refLevel);
                                float diff = bestDiff * 0.75f + (relDiff * 0.05f);

                                if (rawPk < 0.0002f && chanGainEst[c] >= 0.05f) diff += 1.5f;
                                if (chanTaken[c]) diff += 0.25f;

                                const float prevErr = g_slotChanErr[safeInst][c].load(std::memory_order_relaxed);
                                g_slotChanErr[safeInst][c].store(prevErr * 0.78f + diff * 0.22f, std::memory_order_relaxed);
                            }

                            if (activeUntakenCount == 1 && singleActiveIdx >= 0)
                            {
                                if (framesMatched >= 2)
                                {
                                    g_slotLockedChan[safeInst].store(singleActiveIdx, std::memory_order_relaxed);
                                    g_slotMismatchFrames[safeInst].store(0, std::memory_order_relaxed);
                                }
                                lockedC = singleActiveIdx;
                            }
                            else if (activeUntakenCount == 0 && activeAnyCount == 1 && singleActiveAnyIdx >= 0)
                            {
                                if (framesMatched >= 3)
                                {
                                    g_slotLockedChan[safeInst].store(singleActiveAnyIdx, std::memory_order_relaxed);
                                    g_slotMismatchFrames[safeInst].store(0, std::memory_order_relaxed);
                                }
                                lockedC = singleActiveAnyIdx;
                            }
                            else
                            {
                                int bestC = -1;
                                float bestErr = 1e9f;
                                for (int c = 0; c < chCount; ++c)
                                {
                                    if (isMicChan[c] || chanTaken[c] || (rawPeakSnap[c] <= 0.0002f && chanGainEst[c] >= 0.05f)) continue;
                                    const float curErr = g_slotChanErr[safeInst][c].load(std::memory_order_relaxed);
                                    if (curErr < bestErr)
                                    {
                                        bestErr = curErr;
                                        bestC = c;
                                    }
                                }

                                if (bestC < 0)
                                {
                                    for (int c = 0; c < chCount; ++c)
                                    {
                                        if (isMicChan[c] || (rawPeakSnap[c] <= 0.0002f && chanGainEst[c] >= 0.05f)) continue;
                                        const float curErr = g_slotChanErr[safeInst][c].load(std::memory_order_relaxed);
                                        if (curErr < bestErr)
                                        {
                                            bestErr = curErr;
                                            bestC = c;
                                        }
                                    }
                                }

                                if (bestC >= 0 && framesMatched >= 5)
                                {
                                    g_slotLockedChan[safeInst].store(bestC, std::memory_order_relaxed);
                                    g_slotMismatchFrames[safeInst].store(0, std::memory_order_relaxed);
                                    lockedC = bestC;
                                }
                                else if (bestC >= 0)
                                {
                                    lockedC = bestC;
                                }
                            }
                        }

                        if (lockedC < 0)
                        {
                            if (singleActiveIdx >= 0)
                                lockedC = singleActiveIdx;
                            else if (singleActiveAnyIdx >= 0)
                                lockedC = singleActiveAnyIdx;
                            else if (firstUntakenIdx >= 0)
                                lockedC = firstUntakenIdx;
                        }
                    }
                }

                if (lockedC >= 0 && lockedC < chCount && !isMicChan[lockedC])
                {
                    p = chanParamsSnap[lockedC];
                }
                else if (renderChanCount == 1 && onlyRenderChanIdx >= 0)
                {
                    p = chanParamsSnap[onlyRenderChanIdx];
                }
                else
                {
                    p = MakeFlatMixerParams();
                }

                p.channelFader = 1.0f;
                p.masterVolume = 1.0f;
                p.balance = 0.5f;
            }
        }
    }

    g_apoHeartbeat.fetch_add(1U, std::memory_order_relaxed);

    YoumpMixerMeters m = {};
    const int res = g_apoInstances[safeInst].ProcessBuffer(samples, frameCount, channels, p, &m);
    if (res == 0)
    {
        const float rawOutPeak = g_apoInstances[safeInst].lastFramePeakOut.load(std::memory_order_relaxed);
        const bool hasTail = (rawOutPeak > 1.0e-5f);
        g_slotHasActiveOutput[safeInst].store(hasTail, std::memory_order_relaxed);

        const bool hasOutputSignal = (rawOutPeak > 0.0002f);
        if (hasOutputSignal)
        {
            g_slotLastTick[safeInst].store(nowTick, std::memory_order_relaxed);
        }

        bool anyOtherActive = false;
        if (inPeak <= 0.0002f && !hasOutputSignal && safeInst == 0)
        {
            for (int s = 1; s < MAX_DSP_INSTANCES; ++s)
            {
                if (g_slotHasActiveOutput[s].load(std::memory_order_relaxed))
                {
                    anyOtherActive = true;
                    break;
                }
            }
        }

        if (inPeak > 0.0002f || hasOutputSignal || (safeInst == 0 && !anyOtherActive))
        {
            g_meterPfl.store(m.channelPeakPfl, std::memory_order_relaxed);
            g_meterMasterL.store(m.masterPeakL, std::memory_order_relaxed);
            g_meterMasterR.store(m.masterPeakR, std::memory_order_relaxed);
            if (shm != nullptr && shmWritable)
            {
                shm->meters = m;
            }
        }
    }
    return res;
}

int YoumpDsp_ProcessMixer(float* samples, int frameCount, int channels)
{
    return YoumpDsp_ProcessMixerInstance(0, samples, frameCount, channels);
}

int YoumpDsp_ProcessChannel(int channelIndex, float* samples, int frameCount, int channels, const YoumpMixerParams* params, YoumpMixerMeters* outMeters)
{
    if (!params) return 1;
    const int safeCh = ClampIntValue(channelIndex, 0, MAX_CH_INSTANCES - 1);
    if (safeCh < 0 || safeCh >= MAX_CH_INSTANCES) return 1;
    const YoumpMixerParams safeParams = SanitizeMixerParams(*params);
    return g_channelInstances[safeCh].ProcessBuffer(samples, frameCount, channels, safeParams, outMeters);
}
// --- END OF FILE YoumpDsp.cpp ---