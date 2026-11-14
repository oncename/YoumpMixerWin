// --- START OF FILE YoumpApo.h ---
#pragma once

#include <windows.h>
#include <audioclient.h>
#include <unknwn.h>
#include <audioenginebaseapo.h>
#include <AudioMediaType.h>
#include <atomic>

// Non-delegating IUnknown interface required for COM aggregation inside Windows 11 audiodg.exe
struct INonDelegatingUnknown
{
    virtual HRESULT STDMETHODCALLTYPE NonDelegatingQueryInterface(REFIID riid, void** ppv) = 0;
    virtual ULONG STDMETHODCALLTYPE NonDelegatingAddRef() = 0;
    virtual ULONG STDMETHODCALLTYPE NonDelegatingRelease() = 0;
};

class YoumpApo final :
    public INonDelegatingUnknown,
    public IAudioProcessingObject,
    public IAudioProcessingObjectRT,
    public IAudioProcessingObjectConfiguration,
    public IAudioSystemEffects2
{
public:
    explicit YoumpApo(IUnknown* pOuter = nullptr);

    // INonDelegatingUnknown (inner identity & lifetime)
    HRESULT STDMETHODCALLTYPE NonDelegatingQueryInterface(REFIID riid, void** ppv) override;
    ULONG STDMETHODCALLTYPE NonDelegatingAddRef() override;
    ULONG STDMETHODCALLTYPE NonDelegatingRelease() override;

    // Delegating IUnknown (forwards to pOuterUnknown_ when aggregated by audiodg.exe)
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override;
    ULONG STDMETHODCALLTYPE AddRef() override;
    ULONG STDMETHODCALLTYPE Release() override;

    // IAudioProcessingObject
    HRESULT STDMETHODCALLTYPE Reset() override;
    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* pTime) override;
    HRESULT STDMETHODCALLTYPE GetRegistrationProperties(
        APO_REG_PROPERTIES** ppRegProps) override;
    HRESULT STDMETHODCALLTYPE Initialize(UINT32 cbDataSize, BYTE* pbyData) override;
    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(
        IAudioMediaType* pOppositeFormat,
        IAudioMediaType* pRequestedInputFormat,
        IAudioMediaType** ppSupportedInputFormat) override;
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(
        IAudioMediaType* pOppositeFormat,
        IAudioMediaType* pRequestedOutputFormat,
        IAudioMediaType** ppSupportedOutputFormat) override;
    HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* pu32ChannelCount) override;

    // IAudioProcessingObjectRT
    void STDMETHODCALLTYPE APOProcess(
        UINT32 u32NumInputConnections,
        APO_CONNECTION_PROPERTY** ppInputConnections,
        UINT32 u32NumOutputConnections,
        APO_CONNECTION_PROPERTY** ppOutputConnections) override;
    UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 u32OutputFrameCount) override;
    UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 u32InputFrameCount) override;

    // IAudioProcessingObjectConfiguration
    HRESULT STDMETHODCALLTYPE LockForProcess(
        UINT32 u32NumInputConnections,
        APO_CONNECTION_DESCRIPTOR** ppInputConnections,
        UINT32 u32NumOutputConnections,
        APO_CONNECTION_DESCRIPTOR** ppOutputConnections) override;
    HRESULT STDMETHODCALLTYPE UnlockForProcess() override;

    // IAudioSystemEffects2
    HRESULT STDMETHODCALLTYPE GetEffectsList(
        LPGUID* ppEffectsIds,
        UINT* pcEffects,
        HANDLE Event) override;

private:
    ~YoumpApo();

    volatile LONG refCount_;
    IUnknown* pOuterUnknown_;
    std::atomic<int> instanceId_;
    std::atomic<UINT32> channels_;
    std::atomic<UINT32> sampleRate_;
    std::atomic<UINT32> maxFrameCount_;
    std::atomic<bool> locked_;
    std::atomic<bool> isMicCapture_;
};
// --- END OF FILE YoumpApo.h ---