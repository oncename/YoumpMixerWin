// --- START OF FILE YoumpApo.cpp ---
#include "YoumpApo.h"
#include "YoumpDsp.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <new>
#include <ks.h>
#include <ksmedia.h>
#include <mmdeviceapi.h>

#ifndef AUDIOMEDIATYPE_EQUAL_FORMAT_TYPES
#define AUDIOMEDIATYPE_EQUAL_FORMAT_TYPES 0x00000002
#endif
#ifndef AUDIOMEDIATYPE_EQUAL_FORMAT_DATA
#define AUDIOMEDIATYPE_EQUAL_FORMAT_DATA 0x00000004
#endif
#ifndef AUDIOMEDIATYPE_EQUAL_FORMAT_USER_DATA
#define AUDIOMEDIATYPE_EQUAL_FORMAT_USER_DATA 0x00000008
#endif

namespace
{
    const CLSID CLSID_YoumpApo =
    { 0xA7D6D67E, 0x4F8D, 0x4A7D,
      { 0x9C, 0x10, 0x3A, 0x7F, 0x6C, 0x2D, 0x91, 0x01 } };

    // Совместимое описание структуры инициализации Windows 10/11 APOInitSystemEffects3
    // (на случай сборки со старой версией Windows SDK, где APOInitSystemEffects3 ещё не объявлена)
    struct YoumpAPOInitSystemEffects3Compat
    {
        APOInitBaseStruct   APOInit;
        IPropertyStore* pAPOEndpointProperties;
        IServiceProvider* pServiceProvider;
        IMMDeviceCollection* pDeviceCollection;
        UINT                nSoftwareIoDeviceInCollection;
        UINT                nSoftwareIoConnectorIndex;
        GUID                AudioProcessingMode;
        BOOL                InitializeForDiscoveryOnly;
    };

    std::atomic<long> g_objectCount{ 0 };
    std::atomic<long> g_serverLockCount{ 0 };

    DWORD DefaultChannelMaskForCount(UINT16 channels) noexcept
    {
        switch (channels)
        {
        case 1: return SPEAKER_FRONT_CENTER;
        case 2: return SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT;
        case 3: return SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT | SPEAKER_FRONT_CENTER;
        case 4: return SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT | SPEAKER_BACK_LEFT | SPEAKER_BACK_RIGHT;
        case 5: return SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT | SPEAKER_FRONT_CENTER | SPEAKER_BACK_LEFT | SPEAKER_BACK_RIGHT;
        case 6: return KSAUDIO_SPEAKER_5POINT1_SURROUND;
        case 7: return KSAUDIO_SPEAKER_5POINT1_SURROUND | SPEAKER_BACK_CENTER;
        case 8: return KSAUDIO_SPEAKER_7POINT1_SURROUND;
        default: return 0;
        }
    }

    // Реализация IAudioMediaType для возврата S_FALSE (предложение ближайшего Float32 формата движку audiodg.exe)
    class Float32AudioMediaType final : public IAudioMediaType
    {
    public:
        Float32AudioMediaType(UINT32 sampleRate, UINT16 channels, DWORD channelMask = 0) noexcept
            : refCount_(1)
        {
            g_objectCount.fetch_add(1, std::memory_order_relaxed);

            const UINT16 safeChannels = std::clamp<UINT16>(channels, 1, 8);
            const UINT32 safeRate = (sampleRate >= 8000 && sampleRate <= 384000) ? sampleRate : 48000;
            const UINT16 blockAlign = static_cast<UINT16>(safeChannels * sizeof(float));

            ZeroMemory(&fmt_, sizeof(fmt_));
            fmt_.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
            fmt_.Format.nChannels = safeChannels;
            fmt_.Format.nSamplesPerSec = safeRate;
            fmt_.Format.wBitsPerSample = 32;
            fmt_.Format.nBlockAlign = blockAlign;
            fmt_.Format.nAvgBytesPerSec = safeRate * blockAlign;
            fmt_.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
            fmt_.Samples.wValidBitsPerSample = 32;
            fmt_.dwChannelMask = (channelMask != 0) ? channelMask : DefaultChannelMaskForCount(safeChannels);
            fmt_.SubFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        }

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
        {
            if (ppv == nullptr)
                return E_POINTER;

            *ppv = nullptr;
            if (riid == __uuidof(IUnknown) || riid == __uuidof(IAudioMediaType))
            {
                *ppv = static_cast<IAudioMediaType*>(this);
                AddRef();
                return S_OK;
            }
            return E_NOINTERFACE;
        }

        ULONG STDMETHODCALLTYPE AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&refCount_));
        }

        ULONG STDMETHODCALLTYPE Release() override
        {
            LONG count = InterlockedDecrement(&refCount_);
            if (count == 0)
            {
                delete this;
                return 0;
            }
            return static_cast<ULONG>(count);
        }

        HRESULT STDMETHODCALLTYPE IsCompressedFormat(BOOL* pfCompressed) override
        {
            if (pfCompressed == nullptr)
                return E_POINTER;
            *pfCompressed = FALSE;
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE IsEqual(IAudioMediaType* pIAudioType, DWORD* pdwFlags) override
        {
            if (pIAudioType == nullptr || pdwFlags == nullptr)
                return E_POINTER;

            *pdwFlags = 0;
            const WAVEFORMATEX* other = pIAudioType->GetAudioFormat();
            if (other == nullptr)
                return E_INVALIDARG;

            UNCOMPRESSEDAUDIOFORMAT otherUnc = {};
            if (SUCCEEDED(pIAudioType->GetUncompressedAudioFormat(&otherUnc)))
            {
                if (otherUnc.guidFormatType == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
                {
                    *pdwFlags |= AUDIOMEDIATYPE_EQUAL_FORMAT_TYPES;
                    const bool maskMatch = (otherUnc.dwChannelMask == 0 ||
                        fmt_.dwChannelMask == 0 ||
                        otherUnc.dwChannelMask == fmt_.dwChannelMask);
                    if (otherUnc.dwSamplesPerFrame == fmt_.Format.nChannels &&
                        otherUnc.dwBytesPerSampleContainer == sizeof(float) &&
                        otherUnc.dwValidBitsPerSample == 32 &&
                        static_cast<UINT32>(otherUnc.fFramesPerSecond + 0.5f) == fmt_.Format.nSamplesPerSec &&
                        maskMatch)
                    {
                        *pdwFlags |= AUDIOMEDIATYPE_EQUAL_FORMAT_DATA;
                    }
                }
            }
            else if (other->wFormatTag == WAVE_FORMAT_IEEE_FLOAT ||
                (other->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
                    other->cbSize >= sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX) &&
                    reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(other)->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT))
            {
                *pdwFlags |= AUDIOMEDIATYPE_EQUAL_FORMAT_TYPES;
                if (other->nChannels == fmt_.Format.nChannels &&
                    other->nSamplesPerSec == fmt_.Format.nSamplesPerSec &&
                    other->wBitsPerSample == 32 &&
                    other->nBlockAlign == fmt_.Format.nBlockAlign)
                {
                    *pdwFlags |= AUDIOMEDIATYPE_EQUAL_FORMAT_DATA;
                }
            }

            *pdwFlags |= AUDIOMEDIATYPE_EQUAL_FORMAT_USER_DATA;

            constexpr DWORD kAllEqualFlags =
                AUDIOMEDIATYPE_EQUAL_FORMAT_TYPES |
                AUDIOMEDIATYPE_EQUAL_FORMAT_DATA |
                AUDIOMEDIATYPE_EQUAL_FORMAT_USER_DATA;

            return ((*pdwFlags & kAllEqualFlags) == kAllEqualFlags) ? S_OK : S_FALSE;
        }

        const WAVEFORMATEX* STDMETHODCALLTYPE GetAudioFormat() override
        {
            return &fmt_.Format;
        }

        HRESULT STDMETHODCALLTYPE GetUncompressedAudioFormat(
            UNCOMPRESSEDAUDIOFORMAT* pUncompressedAudioFormat) override
        {
            if (pUncompressedAudioFormat == nullptr)
                return E_POINTER;

            pUncompressedAudioFormat->guidFormatType = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
            pUncompressedAudioFormat->dwSamplesPerFrame = fmt_.Format.nChannels;
            pUncompressedAudioFormat->dwBytesPerSampleContainer = sizeof(float);
            pUncompressedAudioFormat->dwValidBitsPerSample = 32;
            pUncompressedAudioFormat->fFramesPerSecond = static_cast<FLOAT>(fmt_.Format.nSamplesPerSec);
            pUncompressedAudioFormat->dwChannelMask = fmt_.dwChannelMask;
            return S_OK;
        }

    private:
        ~Float32AudioMediaType()
        {
            g_objectCount.fetch_sub(1, std::memory_order_relaxed);
        }

        volatile LONG refCount_;
        WAVEFORMATEXTENSIBLE fmt_;
    };

    bool IsSupportedFloat32Format(IAudioMediaType* mediaType) noexcept
    {
        if (mediaType == nullptr)
            return false;

        const WAVEFORMATEX* format = mediaType->GetAudioFormat();
        if (format == nullptr)
            return false;

        if ((format->nChannels < 1 || format->nChannels > 8) ||
            (format->nSamplesPerSec < 8000 || format->nSamplesPerSec > 384000) ||
            format->wBitsPerSample != 32)
        {
            return false;
        }

        const UINT32 expectedBlockAlign =
            static_cast<UINT32>(format->nChannels) * sizeof(float);

        if (format->nBlockAlign != expectedBlockAlign)
            return false;

        const ULONGLONG expectedAvgBytesPerSec =
            static_cast<ULONGLONG>(format->nSamplesPerSec) *
            expectedBlockAlign;

        if (expectedAvgBytesPerSec != format->nAvgBytesPerSec)
            return false;

        if (format->wFormatTag == WAVE_FORMAT_IEEE_FLOAT)
            return true;

        if (format->wFormatTag != WAVE_FORMAT_EXTENSIBLE)
            return false;

        if (format->cbSize <
            sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX))
        {
            return false;
        }

        const auto* extensible =
            reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(format);

        return extensible->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT &&
            (extensible->Samples.wValidBitsPerSample == 32 ||
                extensible->Samples.wValidBitsPerSample == 0);
    }

    bool AreCompatibleFloat32Formats(
        IAudioMediaType* input,
        IAudioMediaType* output) noexcept
    {
        if (!IsSupportedFloat32Format(input) ||
            !IsSupportedFloat32Format(output))
        {
            return false;
        }

        const WAVEFORMATEX* inFormat = input->GetAudioFormat();
        const WAVEFORMATEX* outFormat = output->GetAudioFormat();

        return inFormat->nChannels == outFormat->nChannels &&
            inFormat->nSamplesPerSec == outFormat->nSamplesPerSec &&
            inFormat->wBitsPerSample == outFormat->wBitsPerSample;
    }

    HRESULT CreateSuggestedFloat32Format(
        IAudioMediaType* pOppositeFormat,
        IAudioMediaType* pRequestedFormat,
        IAudioMediaType** ppSupportedFormat,
        UINT16* outNegotiatedChannels)
    {
        if (pOppositeFormat != nullptr && IsSupportedFloat32Format(pOppositeFormat))
        {
            const WAVEFORMATEX* oppFmt = pOppositeFormat->GetAudioFormat();
            if (outNegotiatedChannels != nullptr && oppFmt != nullptr)
            {
                *outNegotiatedChannels = oppFmt->nChannels;
            }
            pOppositeFormat->AddRef();
            *ppSupportedFormat = pOppositeFormat;
            return S_FALSE;
        }

        UINT32 targetRate = 48000;
        UINT16 targetChannels = 2;
        DWORD targetMask = 0;

        const WAVEFORMATEX* refFmt = nullptr;
        if (pOppositeFormat != nullptr && pOppositeFormat->GetAudioFormat() != nullptr)
        {
            refFmt = pOppositeFormat->GetAudioFormat();
        }
        else if (pRequestedFormat != nullptr && pRequestedFormat->GetAudioFormat() != nullptr)
        {
            refFmt = pRequestedFormat->GetAudioFormat();
        }

        if (refFmt != nullptr)
        {
            if (refFmt->nSamplesPerSec >= 8000 && refFmt->nSamplesPerSec <= 384000)
            {
                targetRate = refFmt->nSamplesPerSec;
            }
            if (refFmt->nChannels >= 1 && refFmt->nChannels <= 8)
            {
                targetChannels = refFmt->nChannels;
            }
            if (refFmt->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
                refFmt->cbSize >= sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX))
            {
                targetMask = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(refFmt)->dwChannelMask;
            }
        }

        auto* suggested = new (std::nothrow) Float32AudioMediaType(targetRate, targetChannels, targetMask);
        if (suggested == nullptr)
        {
            return E_OUTOFMEMORY;
        }

        if (outNegotiatedChannels != nullptr)
        {
            *outNegotiatedChannels = targetChannels;
        }
        *ppSupportedFormat = suggested;
        return S_FALSE;
    }

    void PassthroughOrSilenceBuffer(
        const APO_CONNECTION_PROPERTY* input,
        APO_CONNECTION_PROPERTY* output,
        UINT32 frames,
        UINT32 channels) noexcept
    {
        const UINT32 safeChannels = (channels >= 1 && channels <= 8) ? channels : 2;
        const size_t byteCount = static_cast<size_t>(frames) * safeChannels * sizeof(float);

        if (frames == 0)
        {
            output->u32ValidFrameCount = 0;
            output->u32BufferFlags = BUFFER_SILENT;
            return;
        }

        if (input != nullptr &&
            input->u32BufferFlags == BUFFER_VALID &&
            input->pBuffer != 0 &&
            output->pBuffer != 0)
        {
            if (output->pBuffer != input->pBuffer && byteCount > 0)
            {
                std::memcpy(
                    reinterpret_cast<void*>(output->pBuffer),
                    reinterpret_cast<const void*>(input->pBuffer),
                    byteCount);
            }
            output->u32ValidFrameCount = frames;
            output->u32BufferFlags = BUFFER_VALID;
        }
        else
        {
            if (output->pBuffer != 0 && byteCount > 0)
            {
                std::memset(reinterpret_cast<void*>(output->pBuffer), 0, byteCount);
            }
            output->u32ValidFrameCount = frames;
            output->u32BufferFlags = BUFFER_SILENT;
        }
    }

    bool CheckPropertyStoreForMicFlag(IPropertyStore* store) noexcept
    {
        if (store == nullptr)
            return false;

        // 1. Пользовательский маркер Yoump для микрофонного входа ({d04e05a6-594b-4fb6-a80d-01af5eed7d1d}, 99)
        const PROPERTYKEY pkMicCustom = {
            { 0xd04e05a6, 0x594b, 0x4fb6,{ 0xa8, 0x0d, 0x01, 0xaf, 0x5e, 0xed, 0x7d, 0x1d } },
            99
        };

        PROPVARIANT pv;
        PropVariantInit(&pv);
        bool isMic = false;

        if (SUCCEEDED(store->GetValue(pkMicCustom, &pv)))
        {
            if ((pv.vt == VT_UI4 && pv.ulVal == 1) ||
                (pv.vt == VT_I4 && pv.lVal == 1))
            {
                isMic = true;
            }
            PropVariantClear(&pv);
        }

        if (isMic)
            return true;

        // 2. Резервная проверка PKEY_AudioEndpoint_FormFactor ({1da5d803-d492-4edd-8c23-e0c0ffee7f0e}, 0) == Microphone (4) или Headset (5)
        const PROPERTYKEY pkFormFactor = {
            { 0x1da5d803, 0xd492, 0x4edd,{ 0x8c, 0x23, 0xe0, 0xc0, 0xff, 0xee, 0x7f, 0x0e } },
            0
        };

        PropVariantInit(&pv);
        if (SUCCEEDED(store->GetValue(pkFormFactor, &pv)))
        {
            if ((pv.vt == VT_UI4 && (pv.ulVal == 4U || pv.ulVal == 5U)) ||
                (pv.vt == VT_I4 && (pv.lVal == 4 || pv.lVal == 5)))
            {
                isMic = true;
            }
            PropVariantClear(&pv);
        }

        return isMic;
    }

    bool CheckDeviceCollectionForCaptureEndpoint(
        IMMDeviceCollection* collection,
        UINT preferredDeviceIndex) noexcept
    {
        if (collection == nullptr)
            return false;

        UINT count = 0;
        if (FAILED(collection->GetCount(&count)) || count == 0)
            return false;

        auto inspectIndex = [&](UINT idx, bool& outIsCapture, bool& outChecked) noexcept
            {
                outIsCapture = false;
                outChecked = false;
                IMMDevice* dev = nullptr;
                if (SUCCEEDED(collection->Item(idx, &dev)) && dev != nullptr)
                {
                    IMMEndpoint* endpoint = nullptr;
                    if (SUCCEEDED(dev->QueryInterface(__uuidof(IMMEndpoint), reinterpret_cast<void**>(&endpoint))) &&
                        endpoint != nullptr)
                    {
                        EDataFlow flow = eRender;
                        if (SUCCEEDED(endpoint->GetDataFlow(&flow)))
                        {
                            outChecked = true;
                            outIsCapture = (flow == eCapture);
                        }
                        endpoint->Release();
                    }
                    dev->Release();
                }
            };

        bool isCapture = false;
        bool checked = false;
        if (preferredDeviceIndex < count)
        {
            inspectIndex(preferredDeviceIndex, isCapture, checked);
            if (checked)
                return isCapture;
        }

        for (UINT i = 0; i < count; ++i)
        {
            inspectIndex(i, isCapture, checked);
            if (checked && isCapture)
                return true;
        }

        return false;
    }
}

YoumpApo::YoumpApo(IUnknown* pOuter)
    : refCount_(1),
    pOuterUnknown_(pOuter != nullptr
        ? pOuter
        : reinterpret_cast<IUnknown*>(static_cast<INonDelegatingUnknown*>(this))),
    instanceId_(-1),
    channels_(2),
    sampleRate_(48000),
    maxFrameCount_(0),
    locked_(false),
    isMicCapture_(false)
{
    g_objectCount.fetch_add(1, std::memory_order_relaxed);
}

YoumpApo::~YoumpApo()
{
    const int slot = instanceId_.exchange(-1, std::memory_order_acq_rel);
    if (slot >= 0)
    {
        YoumpDsp_ReleaseSlot(slot);
    }
    g_objectCount.fetch_sub(1, std::memory_order_relaxed);
}

HRESULT STDMETHODCALLTYPE YoumpApo::NonDelegatingQueryInterface(
    REFIID riid,
    void** ppv)
{
    if (ppv == nullptr)
        return E_POINTER;

    *ppv = nullptr;

    if (riid == __uuidof(IUnknown))
    {
        *ppv = reinterpret_cast<IUnknown*>(static_cast<INonDelegatingUnknown*>(this));
        NonDelegatingAddRef();
        return S_OK;
    }
    else if (riid == __uuidof(IAudioProcessingObject))
    {
        *ppv = static_cast<IAudioProcessingObject*>(this);
    }
    else if (riid == __uuidof(IAudioProcessingObjectRT))
    {
        *ppv = static_cast<IAudioProcessingObjectRT*>(this);
    }
    else if (riid == __uuidof(IAudioProcessingObjectConfiguration))
    {
        *ppv = static_cast<IAudioProcessingObjectConfiguration*>(this);
    }
    else if (riid == __uuidof(IAudioSystemEffects))
    {
        *ppv = static_cast<IAudioSystemEffects*>(this);
    }
    else if (riid == __uuidof(IAudioSystemEffects2))
    {
        *ppv = static_cast<IAudioSystemEffects2*>(this);
    }
    else
    {
        return E_NOINTERFACE;
    }

    pOuterUnknown_->AddRef();
    return S_OK;
}

ULONG STDMETHODCALLTYPE YoumpApo::NonDelegatingAddRef()
{
    return static_cast<ULONG>(InterlockedIncrement(&refCount_));
}

ULONG STDMETHODCALLTYPE YoumpApo::NonDelegatingRelease()
{
    LONG count = InterlockedDecrement(&refCount_);
    if (count == 0)
    {
        delete this;
        return 0;
    }
    return static_cast<ULONG>(count);
}

HRESULT STDMETHODCALLTYPE YoumpApo::QueryInterface(
    REFIID riid,
    void** ppv)
{
    return pOuterUnknown_->QueryInterface(riid, ppv);
}

ULONG STDMETHODCALLTYPE YoumpApo::AddRef()
{
    return pOuterUnknown_->AddRef();
}

ULONG STDMETHODCALLTYPE YoumpApo::Release()
{
    return pOuterUnknown_->Release();
}

HRESULT STDMETHODCALLTYPE YoumpApo::Reset()
{
    const int slot = instanceId_.load(std::memory_order_acquire);
    const UINT32 sr = sampleRate_.load(std::memory_order_relaxed);
    if (slot >= 0 && sr > 0)
    {
        YoumpDsp_InitMixerInstance(slot, static_cast<int>(sr));
    }
    return S_OK;
}

HRESULT STDMETHODCALLTYPE YoumpApo::GetLatency(HNSTIME* pTime)
{
    if (pTime == nullptr)
        return E_POINTER;

    *pTime = 0;
    return S_OK;
}

HRESULT STDMETHODCALLTYPE YoumpApo::GetRegistrationProperties(
    APO_REG_PROPERTIES** ppRegProps)
{
    if (ppRegProps == nullptr)
        return E_POINTER;

    *ppRegProps = nullptr;

    constexpr UINT32 interfaceCount = 2;
    constexpr SIZE_T propsSize =
        offsetof(APO_REG_PROPERTIES, iidAPOInterfaceList) +
        interfaceCount * sizeof(IID);

    auto* props = static_cast<APO_REG_PROPERTIES*>(
        CoTaskMemAlloc(propsSize));

    if (props == nullptr)
        return E_OUTOFMEMORY;

    ZeroMemory(props, propsSize);

    props->clsid = CLSID_YoumpApo;
    props->Flags = static_cast<APO_FLAG>(
        APO_FLAG_INPLACE |
        APO_FLAG_SAMPLESPERFRAME_MUST_MATCH |
        APO_FLAG_FRAMESPERSECOND_MUST_MATCH |
        APO_FLAG_BITSPERSAMPLE_MUST_MATCH);

    wcscpy_s(props->szFriendlyName, L"Yoump System Audio APO");
    wcscpy_s(props->szCopyrightInfo, L"Yoump");

    props->u32MajorVersion = 1;
    props->u32MinorVersion = 0;

    props->u32MinInputConnections = 1;
    props->u32MaxInputConnections = 1;
    props->u32MinOutputConnections = 1;
    props->u32MaxOutputConnections = 1;
    props->u32MaxInstances = YOUMP_DSP_MAX_INSTANCES;
    props->u32NumAPOInterfaces = interfaceCount;
    props->iidAPOInterfaceList[0] = __uuidof(IAudioSystemEffects);
    props->iidAPOInterfaceList[1] = __uuidof(IAudioSystemEffects2);

    *ppRegProps = props;
    return S_OK;
}

HRESULT STDMETHODCALLTYPE YoumpApo::Initialize(
    UINT32 cbDataSize,
    BYTE* pbyData)
{
    if ((cbDataSize == 0 && pbyData != nullptr) ||
        (cbDataSize > 0 && pbyData == nullptr))
    {
        return E_INVALIDARG;
    }

    // Предварительно инициализируем доступ к общей памяти вне RT-потока
    YoumpDsp_GetVersion();

    if (pbyData == nullptr || cbDataSize < sizeof(APOInitBaseStruct))
        return S_OK;

    const auto* baseInit = reinterpret_cast<const APOInitBaseStruct*>(pbyData);
    const UINT32 effectiveSize = (baseInit->cbSize >= sizeof(APOInitBaseStruct) && baseInit->cbSize <= cbDataSize)
        ? baseInit->cbSize
        : cbDataSize;

    bool detectedMic = false;

    // 1. Во всех версиях (APOInitSystemEffects, APOInitSystemEffects2, APOInitSystemEffects3)
    // первое поле после APOInitBaseStruct — это IPropertyStore* pAPOEndpointProperties
    constexpr SIZE_T kMinSizeForEndpointProps =
        offsetof(APOInitSystemEffects, pAPOEndpointProperties) + sizeof(IPropertyStore*);

    if (effectiveSize >= kMinSizeForEndpointProps)
    {
        const auto* sysInit = reinterpret_cast<const APOInitSystemEffects*>(pbyData);
        if (CheckPropertyStoreForMicFlag(sysInit->pAPOEndpointProperties))
        {
            detectedMic = true;
        }
    }

    // 2. Разбор специфичных полей для v1, v2 и v3 (Windows 10 / 11)
    if (!detectedMic)
    {
        if (effectiveSize >= sizeof(APOInitSystemEffects2))
        {
            const auto* sysInit2 = reinterpret_cast<const APOInitSystemEffects2*>(pbyData);
            if (CheckPropertyStoreForMicFlag(sysInit2->pAPOSystemEffectsProperties) ||
                CheckDeviceCollectionForCaptureEndpoint(sysInit2->pDeviceCollection, sysInit2->nSoftwareIoDeviceInCollection))
            {
                detectedMic = true;
            }
        }
        else if (effectiveSize == sizeof(YoumpAPOInitSystemEffects3Compat))
        {
            const auto* sysInit3 = reinterpret_cast<const YoumpAPOInitSystemEffects3Compat*>(pbyData);
            if (CheckDeviceCollectionForCaptureEndpoint(sysInit3->pDeviceCollection, sysInit3->nSoftwareIoDeviceInCollection))
            {
                detectedMic = true;
            }
        }
        else if (effectiveSize >= sizeof(APOInitSystemEffects))
        {
            const auto* sysInit1 = reinterpret_cast<const APOInitSystemEffects*>(pbyData);
            if (CheckPropertyStoreForMicFlag(sysInit1->pAPOSystemEffectsProperties) ||
                CheckDeviceCollectionForCaptureEndpoint(sysInit1->pDeviceCollection, 0))
            {
                detectedMic = true;
            }
        }
    }

    if (detectedMic)
    {
        isMicCapture_.store(true, std::memory_order_release);
    }

    return S_OK;
}


HRESULT STDMETHODCALLTYPE YoumpApo::IsInputFormatSupported(
    IAudioMediaType* pOppositeFormat,
    IAudioMediaType* pRequestedInputFormat,
    IAudioMediaType** ppSupportedInputFormat)
{
    if (ppSupportedInputFormat == nullptr)
        return E_POINTER;

    *ppSupportedInputFormat = nullptr;

    if (pRequestedInputFormat == nullptr)
        return E_POINTER;

    if (pOppositeFormat != nullptr && !IsSupportedFloat32Format(pOppositeFormat))
        return APOERR_FORMAT_NOT_SUPPORTED;

    const bool isCurrentlyLocked = locked_.load(std::memory_order_acquire);

    if (IsSupportedFloat32Format(pRequestedInputFormat) &&
        (pOppositeFormat == nullptr || AreCompatibleFloat32Formats(pRequestedInputFormat, pOppositeFormat)))
    {
        const WAVEFORMATEX* fmt = pRequestedInputFormat->GetAudioFormat();
        if (!isCurrentlyLocked && fmt != nullptr && fmt->nChannels >= 1 && fmt->nChannels <= 8)
        {
            channels_.store(fmt->nChannels, std::memory_order_relaxed);
        }
        pRequestedInputFormat->AddRef();
        *ppSupportedInputFormat = pRequestedInputFormat;
        return S_OK;
    }

    UINT16 negCh = 0;
    HRESULT hr = CreateSuggestedFloat32Format(
        pOppositeFormat,
        pRequestedInputFormat,
        ppSupportedInputFormat,
        &negCh);

    if (!isCurrentlyLocked && pOppositeFormat != nullptr && SUCCEEDED(hr) && negCh >= 1 && negCh <= 8)
    {
        channels_.store(negCh, std::memory_order_relaxed);
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE YoumpApo::IsOutputFormatSupported(
    IAudioMediaType* pOppositeFormat,
    IAudioMediaType* pRequestedOutputFormat,
    IAudioMediaType** ppSupportedOutputFormat)
{
    if (ppSupportedOutputFormat == nullptr)
        return E_POINTER;

    *ppSupportedOutputFormat = nullptr;

    if (pRequestedOutputFormat == nullptr)
        return E_POINTER;

    if (pOppositeFormat != nullptr && !IsSupportedFloat32Format(pOppositeFormat))
        return APOERR_FORMAT_NOT_SUPPORTED;

    const bool isCurrentlyLocked = locked_.load(std::memory_order_acquire);

    if (IsSupportedFloat32Format(pRequestedOutputFormat) &&
        (pOppositeFormat == nullptr || AreCompatibleFloat32Formats(pOppositeFormat, pRequestedOutputFormat)))
    {
        const WAVEFORMATEX* fmt = pRequestedOutputFormat->GetAudioFormat();
        if (!isCurrentlyLocked && fmt != nullptr && fmt->nChannels >= 1 && fmt->nChannels <= 8)
        {
            channels_.store(fmt->nChannels, std::memory_order_relaxed);
        }
        pRequestedOutputFormat->AddRef();
        *ppSupportedOutputFormat = pRequestedOutputFormat;
        return S_OK;
    }

    UINT16 negCh = 0;
    HRESULT hr = CreateSuggestedFloat32Format(
        pOppositeFormat,
        pRequestedOutputFormat,
        ppSupportedOutputFormat,
        &negCh);

    if (!isCurrentlyLocked && pOppositeFormat != nullptr && SUCCEEDED(hr) && negCh >= 1 && negCh <= 8)
    {
        channels_.store(negCh, std::memory_order_relaxed);
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE YoumpApo::GetInputChannelCount(
    UINT32* pu32ChannelCount)
{
    if (pu32ChannelCount == nullptr)
        return E_POINTER;

    const UINT32 ch = channels_.load(std::memory_order_relaxed);
    *pu32ChannelCount = (ch >= 1 && ch <= 8) ? ch : 2;
    return S_OK;
}

HRESULT STDMETHODCALLTYPE YoumpApo::GetEffectsList(
    LPGUID* ppEffectsIds,
    UINT* pcEffects,
    HANDLE Event)
{
    UNREFERENCED_PARAMETER(Event);
    if (ppEffectsIds == nullptr || pcEffects == nullptr)
        return E_POINTER;

    *ppEffectsIds = nullptr;
    *pcEffects = 0;
    return S_OK;
}

void STDMETHODCALLTYPE YoumpApo::APOProcess(
    UINT32 u32NumInputConnections,
    APO_CONNECTION_PROPERTY** ppInputConnections,
    UINT32 u32NumOutputConnections,
    APO_CONNECTION_PROPERTY** ppOutputConnections)
{
    if (u32NumOutputConnections == 0 ||
        ppOutputConnections == nullptr ||
        ppOutputConnections[0] == nullptr)
    {
        return;
    }

    APO_CONNECTION_PROPERTY* output = ppOutputConnections[0];
    APO_CONNECTION_PROPERTY* input =
        (u32NumInputConnections > 0 && ppInputConnections != nullptr)
        ? ppInputConnections[0]
        : nullptr;

    if (input == nullptr)
    {
        output->u32ValidFrameCount = 0;
        output->u32BufferFlags = BUFFER_SILENT;
        return;
    }

    const bool isLocked = locked_.load(std::memory_order_acquire);
    const int slot = instanceId_.load(std::memory_order_acquire);
    const UINT32 channels = channels_.load(std::memory_order_relaxed);
    const UINT32 maxFrames = maxFrameCount_.load(std::memory_order_relaxed);
    const bool isMic = isMicCapture_.load(std::memory_order_relaxed);
    const UINT32 frames = input->u32ValidFrameCount;

    // Если APO ещё не заблокирован, фреймов 0 или размер превысил maxFrameCount_ —
    // выполняем чистый passthrough без разрыва графа и обязательно выставляем выходные флаги
    if (!isLocked ||
        slot < 0 ||
        channels == 0 ||
        frames == 0 ||
        (maxFrames > 0 && frames > maxFrames))
    {
        PassthroughOrSilenceBuffer(input, output, frames, channels);
        return;
    }

    const size_t totalSamples = static_cast<size_t>(frames) * channels;
    const size_t totalBytes = totalSamples * sizeof(float);
    const int dspInstanceId = isMic ? (slot | 0x100) : slot;

    if (input->u32BufferFlags == BUFFER_SILENT)
    {
        if (output->pBuffer == 0)
        {
            output->u32ValidFrameCount = frames;
            output->u32BufferFlags = BUFFER_SILENT;
            return;
        }

        auto* outSamples = reinterpret_cast<float*>(output->pBuffer);
        std::memset(outSamples, 0, totalBytes);

        // Прогоняем тишину через DSP, чтобы обновлялся apoHeartbeat, плавно затухали meters и разряжались фильтры
        const int silentDspResult = YoumpDsp_ProcessMixerInstance(
            dspInstanceId,
            outSamples,
            static_cast<int>(frames),
            static_cast<int>(channels));

        if (silentDspResult != 0)
        {
            std::memset(outSamples, 0, totalBytes);
            output->u32ValidFrameCount = frames;
            output->u32BufferFlags = BUFFER_SILENT;
            return;
        }

        const bool hasTailSignal = (YoumpDsp_InstanceHasActiveOutput(slot) != 0);
        if (!hasTailSignal)
        {
            // По контракту Windows Audio Engine буфер с флагом BUFFER_SILENT обязан содержать строгие нули
            std::memset(outSamples, 0, totalBytes);
        }

        output->u32ValidFrameCount = frames;
        output->u32BufferFlags = hasTailSignal ? BUFFER_VALID : BUFFER_SILENT;
        return;
    }

    if (input->u32BufferFlags != BUFFER_VALID ||
        input->pBuffer == 0 ||
        output->pBuffer == 0)
    {
        PassthroughOrSilenceBuffer(nullptr, output, frames, channels);
        return;
    }

    auto* inSamples = reinterpret_cast<float*>(input->pBuffer);
    auto* outSamples = reinterpret_cast<float*>(output->pBuffer);

    if (inSamples != outSamples)
    {
        std::memcpy(outSamples, inSamples, totalBytes);
    }

    const int dspRes = YoumpDsp_ProcessMixerInstance(
        dspInstanceId,
        outSamples,
        static_cast<int>(frames),
        static_cast<int>(channels));

    if (dspRes != 0)
    {
        // Гарантируем отсутствие NaN/Inf в выходном буфере при сбое
        for (size_t i = 0; i < totalSamples; ++i)
        {
            if (!std::isfinite(outSamples[i]))
            {
                outSamples[i] = 0.0f;
            }
        }
    }

    output->u32ValidFrameCount = frames;
    output->u32BufferFlags = BUFFER_VALID;
}

UINT32 STDMETHODCALLTYPE YoumpApo::CalcInputFrames(
    UINT32 u32OutputFrameCount)
{
    return u32OutputFrameCount;
}

UINT32 STDMETHODCALLTYPE YoumpApo::CalcOutputFrames(
    UINT32 u32InputFrameCount)
{
    return u32InputFrameCount;
}

HRESULT STDMETHODCALLTYPE YoumpApo::LockForProcess(
    UINT32 u32NumInputConnections,
    APO_CONNECTION_DESCRIPTOR** ppInputConnections,
    UINT32 u32NumOutputConnections,
    APO_CONNECTION_DESCRIPTOR** ppOutputConnections)
{
    if (locked_.load(std::memory_order_acquire))
        return APOERR_ALREADY_INITIALIZED;

    if (u32NumInputConnections != 1 ||
        u32NumOutputConnections != 1 ||
        ppInputConnections == nullptr ||
        ppOutputConnections == nullptr ||
        ppInputConnections[0] == nullptr ||
        ppOutputConnections[0] == nullptr)
    {
        return E_INVALIDARG;
    }

    APO_CONNECTION_DESCRIPTOR* input = ppInputConnections[0];
    APO_CONNECTION_DESCRIPTOR* output = ppOutputConnections[0];

    if (input->u32Signature != APO_CONNECTION_DESCRIPTOR_SIGNATURE ||
        output->u32Signature != APO_CONNECTION_DESCRIPTOR_SIGNATURE)
    {
        return E_INVALIDARG;
    }

    if ((input->Type != APO_CONNECTION_BUFFER_TYPE_EXTERNAL &&
        input->Type != APO_CONNECTION_BUFFER_TYPE_DEPENDANT &&
        input->Type != APO_CONNECTION_BUFFER_TYPE_ALLOCATED) ||
        (output->Type != APO_CONNECTION_BUFFER_TYPE_EXTERNAL &&
            output->Type != APO_CONNECTION_BUFFER_TYPE_DEPENDANT &&
            output->Type != APO_CONNECTION_BUFFER_TYPE_ALLOCATED))
    {
        return E_INVALIDARG;
    }

    if (input->u32MaxFrameCount == 0 || output->u32MaxFrameCount == 0)
    {
        return E_INVALIDARG;
    }

    if (!AreCompatibleFloat32Formats(input->pFormat, output->pFormat))
        return APOERR_FORMAT_NOT_SUPPORTED;

    const WAVEFORMATEX* format = input->pFormat->GetAudioFormat();

    const int prevSlot = instanceId_.exchange(-1, std::memory_order_acq_rel);
    if (prevSlot >= 0)
    {
        YoumpDsp_ReleaseSlot(prevSlot);
    }

    const int slot = YoumpDsp_AcquireSlot(static_cast<int>(format->nSamplesPerSec));
    if (slot < 0)
    {
        maxFrameCount_.store(0, std::memory_order_relaxed);
        return E_OUTOFMEMORY;
    }

    const UINT32 negotiatedMaxFrames = (input->u32MaxFrameCount < output->u32MaxFrameCount)
        ? input->u32MaxFrameCount
        : output->u32MaxFrameCount;

    channels_.store(format->nChannels, std::memory_order_relaxed);
    sampleRate_.store(format->nSamplesPerSec, std::memory_order_relaxed);
    maxFrameCount_.store(negotiatedMaxFrames, std::memory_order_relaxed);
    instanceId_.store(slot, std::memory_order_release);
    locked_.store(true, std::memory_order_release);

    return S_OK;
}

HRESULT STDMETHODCALLTYPE YoumpApo::UnlockForProcess()
{
    locked_.store(false, std::memory_order_release);
    const int slot = instanceId_.exchange(-1, std::memory_order_acq_rel);
    if (slot >= 0)
    {
        YoumpDsp_ReleaseSlot(slot);
    }

    maxFrameCount_.store(0, std::memory_order_relaxed);
    return S_OK;
}

class YoumpApoClassFactory final : public IClassFactory
{
public:
    YoumpApoClassFactory() : refCount_(1)
    {
        g_objectCount.fetch_add(1, std::memory_order_relaxed);
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID riid,
        void** ppv) override
    {
        if (ppv == nullptr)
            return E_POINTER;

        *ppv = nullptr;

        if (riid == __uuidof(IUnknown) ||
            riid == __uuidof(IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }

        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override
    {
        return static_cast<ULONG>(InterlockedIncrement(&refCount_));
    }

    ULONG STDMETHODCALLTYPE Release() override
    {
        LONG count = InterlockedDecrement(&refCount_);
        if (count == 0)
        {
            delete this;
            return 0;
        }
        return static_cast<ULONG>(count);
    }

    HRESULT STDMETHODCALLTYPE CreateInstance(
        IUnknown* pOuter,
        REFIID riid,
        void** ppv) override
    {
        if (ppv == nullptr)
            return E_POINTER;

        *ppv = nullptr;

        if (pOuter != nullptr && riid != __uuidof(IUnknown))
            return CLASS_E_NOAGGREGATION;

        auto* instance = new (std::nothrow) YoumpApo(pOuter);
        if (instance == nullptr)
            return E_OUTOFMEMORY;

        HRESULT hr = instance->NonDelegatingQueryInterface(riid, ppv);
        instance->NonDelegatingRelease();

        return hr;
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL fLock) override
    {
        if (fLock)
        {
            g_serverLockCount.fetch_add(1, std::memory_order_relaxed);
        }
        else
        {
            long current = g_serverLockCount.load(std::memory_order_relaxed);
            while (current > 0 &&
                !g_serverLockCount.compare_exchange_weak(
                    current,
                    current - 1,
                    std::memory_order_relaxed))
            {
            }
        }
        return S_OK;
    }

private:
    ~YoumpApoClassFactory()
    {
        g_objectCount.fetch_sub(1, std::memory_order_relaxed);
    }

    volatile LONG refCount_;
};

_Check_return_
STDAPI DllGetClassObject(
    _In_ REFCLSID rclsid,
    _In_ REFIID riid,
    _Outptr_ LPVOID* ppv)
{
    if (ppv == nullptr)
        return E_POINTER;

    *ppv = nullptr;

    if (rclsid != CLSID_YoumpApo)
        return CLASS_E_CLASSNOTAVAILABLE;

    auto* factory = new (std::nothrow) YoumpApoClassFactory();
    if (factory == nullptr)
        return E_OUTOFMEMORY;

    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();

    return hr;
}

__control_entrypoint(DllExport)
STDAPI DllCanUnloadNow(void)
{
    if (g_objectCount.load(std::memory_order_relaxed) == 0 &&
        g_serverLockCount.load(std::memory_order_relaxed) == 0)
    {
        return S_OK;
    }
    return S_FALSE;
}
// --- END OF FILE YoumpApo.cpp ---