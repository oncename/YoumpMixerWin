' --- START OF FILE SystemAudioEngine.vb ---
Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Win32
Imports NAudio.CoreAudioApi
Imports NAudio.CoreAudioApi.Interfaces
Imports NAudio.Midi
Imports NAudio.Wasapi.CoreAudioApi.Interfaces
Imports NAudio.Wave
Imports yoump.Audio
Imports yoump.Audio.Native
Imports yoump.IServices

Friend NotInheritable Class ReliableWinMmSysExSender
    Private Const MHDR_DONE As UInteger = 1UI
    Private Const MMSYSERR_NOERROR As Integer = 0

    <StructLayout(LayoutKind.Sequential, Pack:=1)>
    Private Structure MIDIHDR_Sequential
        Public lpData As IntPtr
        Public dwBufferLength As UInteger
        Public dwBytesRecorded As UInteger
        Public dwUser As IntPtr
        Public dwFlags As UInteger
        Public lpNext As IntPtr
        Public reserved As IntPtr
        Public dwOffset As UInteger
        Public dwReserved0 As IntPtr
        Public dwReserved1 As IntPtr
        Public dwReserved2 As IntPtr
        Public dwReserved3 As IntPtr
        Public dwReserved4 As IntPtr
        Public dwReserved5 As IntPtr
        Public dwReserved6 As IntPtr
        Public dwReserved7 As IntPtr
    End Structure

    Private Shared ReadOnly _flagsOffset As Integer = Marshal.OffsetOf(GetType(MIDIHDR_Sequential), NameOf(MIDIHDR_Sequential.dwFlags)).ToInt32()

    <DllImport("winmm.dll", ExactSpelling:=True)>
    Private Shared Function midiOutOpen(ByRef lphMidiOut As IntPtr, uDeviceID As UInteger, dwCallback As IntPtr, dwInstance As IntPtr, dwFlags As UInteger) As Integer
    End Function

    <DllImport("winmm.dll", ExactSpelling:=True)>
    Private Shared Function midiOutReset(hMidiOut As IntPtr) As Integer
    End Function

    <DllImport("winmm.dll", ExactSpelling:=True)>
    Private Shared Function midiOutClose(hMidiOut As IntPtr) As Integer
    End Function

    <DllImport("winmm.dll", ExactSpelling:=True)>
    Private Shared Function midiOutPrepareHeader(hMidiOut As IntPtr, lpMidiOutHdr As IntPtr, uSize As UInteger) As Integer
    End Function

    <DllImport("winmm.dll", ExactSpelling:=True)>
    Private Shared Function midiOutLongMsg(hMidiOut As IntPtr, lpMidiOutHdr As IntPtr, uSize As UInteger) As Integer
    End Function

    <DllImport("winmm.dll", ExactSpelling:=True)>
    Private Shared Function midiOutUnprepareHeader(hMidiOut As IntPtr, lpMidiOutHdr As IntPtr, uSize As UInteger) As Integer
    End Function

    Public Shared Function SendSysExMessages(midiOutDeviceIndex As Integer, ParamArray payloads()() As Byte) As Boolean
        If midiOutDeviceIndex < 0 OrElse payloads Is Nothing OrElse payloads.Length = 0 Then Return False
        Dim hMidiOut As IntPtr = IntPtr.Zero
        Try
            Dim openRes As Integer = midiOutOpen(hMidiOut, CUInt(midiOutDeviceIndex), IntPtr.Zero, IntPtr.Zero, 0UI)
            If openRes <> MMSYSERR_NOERROR OrElse hMidiOut = IntPtr.Zero Then Return False
            For Each sysex As Byte() In payloads
                If sysex Is Nothing OrElse sysex.Length = 0 Then Continue For
                SendSingleSysExBuffer(hMidiOut, sysex)
            Next
            Return True
        Catch
            Return False
        Finally
            If hMidiOut <> IntPtr.Zero Then
                Try : midiOutReset(hMidiOut) : Catch : End Try
                Try : midiOutClose(hMidiOut) : Catch : End Try
            End If
        End Try
    End Function

    Private Shared Sub SendSingleSysExBuffer(hMidiOut As IntPtr, sysex() As Byte)
        Dim dataPtr As IntPtr = IntPtr.Zero
        Dim hdrPtr As IntPtr = IntPtr.Zero
        Dim uSize As UInteger = 0UI
        Dim isPrepared As Boolean = False
        Try
            dataPtr = Marshal.AllocHGlobal(sysex.Length)
            Marshal.Copy(sysex, 0, dataPtr, sysex.Length)

            uSize = CUInt(Marshal.SizeOf(GetType(MIDIHDR_Sequential)))
            Dim allocBytes As Integer = Math.Max(128, CInt(uSize))
            hdrPtr = Marshal.AllocHGlobal(allocBytes)
            For b As Integer = 0 To allocBytes - 1
                Marshal.WriteByte(hdrPtr, b, 0)
            Next

            Dim hdrSeq As New MIDIHDR_Sequential() With {
                .lpData = dataPtr,
                .dwBufferLength = CUInt(sysex.Length),
                .dwBytesRecorded = CUInt(sysex.Length),
                .dwFlags = 0UI
            }
            Marshal.StructureToPtr(hdrSeq, hdrPtr, False)

            Dim prepRes As Integer = midiOutPrepareHeader(hMidiOut, hdrPtr, uSize)
            If prepRes = MMSYSERR_NOERROR Then
                isPrepared = True
                Dim sendRes As Integer = midiOutLongMsg(hMidiOut, hdrPtr, uSize)
                If sendRes = MMSYSERR_NOERROR Then
                    For waitStep As Integer = 1 To 30
                        Dim flags As Integer = Marshal.ReadInt32(hdrPtr, _flagsOffset)
                        If (CUInt(flags) And MHDR_DONE) <> 0UI Then Exit For
                        Thread.Sleep(4)
                    Next
                    Thread.Sleep(15)
                End If
            End If
        Catch
        Finally
            If isPrepared AndAlso hMidiOut <> IntPtr.Zero AndAlso hdrPtr <> IntPtr.Zero Then
                Try : midiOutReset(hMidiOut) : Catch : End Try
                Try : midiOutUnprepareHeader(hMidiOut, hdrPtr, uSize) : Catch : End Try
            End If
            If hdrPtr <> IntPtr.Zero Then Marshal.FreeHGlobal(hdrPtr)
            If dataPtr <> IntPtr.Zero Then Marshal.FreeHGlobal(dataPtr)
        End Try
    End Sub
End Class

Friend NotInheritable Class WindowsAudioPolicyRouter
    <ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>
    Private Interface IPolicyConfig
        <PreserveSig> Function GetMixFormat(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, ByRef ppFormat As IntPtr) As Integer
        <PreserveSig> Function GetDeviceFormat(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, bDefault As Integer, ByRef ppFormat As IntPtr) As Integer
        <PreserveSig> Function ResetDeviceFormat(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String) As Integer
        <PreserveSig> Function SetDeviceFormat(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, pEndpointFormat As IntPtr, mixFormat As IntPtr) As Integer
        <PreserveSig> Function GetProcessingPeriod(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, bDefault As Integer, ByRef pmftDefaultPeriod As Long, ByRef pmftMinimumPeriod As Long) As Integer
        <PreserveSig> Function SetProcessingPeriod(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, ByRef pmftPeriod As Long) As Integer
        <PreserveSig> Function GetShareMode(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, ByRef pMode As IntPtr) As Integer
        <PreserveSig> Function SetShareMode(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, mode As IntPtr) As Integer
        <PreserveSig> Function GetPropertyValue(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, bFxStore As Integer, ByRef key As IntPtr, ByRef pv As IntPtr) As Integer
        <PreserveSig> Function SetPropertyValue(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, bFxStore As Integer, ByRef key As IntPtr, ByRef pv As IntPtr) As Integer
        <PreserveSig> Function SetDefaultEndpoint(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, role As Integer) As Integer
        <PreserveSig> Function SetEndpointVisibility(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, bVisible As Integer) As Integer
    End Interface

    <ComImport, Guid("568b9108-44bf-40b4-9006-86afe5b5a620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>
    Private Interface IPolicyConfigVista
        <PreserveSig> Function GetMixFormat(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, ByRef ppFormat As IntPtr) As Integer
        <PreserveSig> Function GetDeviceFormat(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, bDefault As Integer, ByRef ppFormat As IntPtr) As Integer
        <PreserveSig> Function SetDeviceFormat(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, pEndpointFormat As IntPtr, mixFormat As IntPtr) As Integer
        <PreserveSig> Function GetProcessingPeriod(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, bDefault As Integer, ByRef pmftDefaultPeriod As Long, ByRef pmftMinimumPeriod As Long) As Integer
        <PreserveSig> Function SetProcessingPeriod(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, ByRef pmftPeriod As Long) As Integer
        <PreserveSig> Function GetShareMode(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, ByRef pMode As IntPtr) As Integer
        <PreserveSig> Function SetShareMode(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, mode As IntPtr) As Integer
        <PreserveSig> Function GetPropertyValue(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, bFxStore As Integer, ByRef key As IntPtr, ByRef pv As IntPtr) As Integer
        <PreserveSig> Function SetPropertyValue(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, bFxStore As Integer, ByRef key As IntPtr, ByRef pv As IntPtr) As Integer
        <PreserveSig> Function SetDefaultEndpoint(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, role As Integer) As Integer
        <PreserveSig> Function SetEndpointVisibility(<MarshalAs(UnmanagedType.LPWStr)> pszDeviceName As String, bVisible As Integer) As Integer
    End Interface

    <UnmanagedFunctionPointer(CallingConvention.StdCall)>
    Private Delegate Function SetPersistedDefaultAudioEndpointDelegate(thisPtr As IntPtr, processId As UInteger, flow As Integer, role As Integer, deviceId As IntPtr) As Integer

    <UnmanagedFunctionPointer(CallingConvention.StdCall)>
    Private Delegate Function ClearAllPersistedEndpointsDelegate(thisPtr As IntPtr) As Integer

    <DllImport("combase.dll", ExactSpelling:=True)>
    Private Shared Function RoInitialize(initType As Integer) As Integer
    End Function

    <DllImport("combase.dll", ExactSpelling:=True)>
    Private Shared Function RoGetActivationFactory(activatableClassId As IntPtr, ByRef iid As Guid, ByRef factory As IntPtr) As Integer
    End Function

    <DllImport("combase.dll", ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Shared Function WindowsCreateString(<MarshalAs(UnmanagedType.LPWStr)> sourceString As String, length As UInteger, ByRef hstring As IntPtr) As Integer
    End Function

    <DllImport("combase.dll", ExactSpelling:=True)>
    Private Shared Function WindowsDeleteString(hstring As IntPtr) As Integer
    End Function

    Private Shared ReadOnly PolicyConfigClsid As New Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")
    Private Shared ReadOnly AudioPolicyConfigWin11Iid As New Guid("ab3d4648-e242-459f-b02f-541c70306324")
    Private Shared ReadOnly AudioPolicyConfigWin10Iid As New Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258")
    Private Shared _factoryUnavailable As Integer = 0

    Private Shared Function FormatSwdEndpointId(mmDeviceId As String) As String
        If String.IsNullOrWhiteSpace(mmDeviceId) Then Return ""
        If mmDeviceId.StartsWith("\\?\") Then Return mmDeviceId
        Return $"\\?\SWD#MMDEVAPI#{mmDeviceId}#{{e6327cad-dcec-4949-ae8a-991e976a79d2}}"
    End Function

    Private Shared Function AcquireAudioPolicyConfigFactoryPtr() As IntPtr
        If Volatile.Read(_factoryUnavailable) <> 0 Then Return IntPtr.Zero
        Try : RoInitialize(1) : Catch : End Try
        Dim hClassId As IntPtr = IntPtr.Zero
        Try
            Const className As String = "Windows.Media.Internal.AudioPolicyConfig"
            If WindowsCreateString(className, CUInt(className.Length), hClassId) <> 0 OrElse hClassId = IntPtr.Zero Then
                Return IntPtr.Zero
            End If
            Dim knownIids As Guid() = {AudioPolicyConfigWin11Iid, AudioPolicyConfigWin10Iid}
            For Each candIid As Guid In knownIids
                Dim kIid As Guid = candIid
                Dim directPtr As IntPtr = IntPtr.Zero
                If RoGetActivationFactory(hClassId, kIid, directPtr) = 0 AndAlso directPtr <> IntPtr.Zero Then
                    Return directPtr
                End If
            Next
            Interlocked.Exchange(_factoryUnavailable, 1)
        Catch
            Interlocked.Exchange(_factoryUnavailable, 1)
        Finally
            If hClassId <> IntPtr.Zero Then
                Try : WindowsDeleteString(hClassId) : Catch : End Try
            End If
        End Try
        Return IntPtr.Zero
    End Function

    Public Shared Sub ClearPerAppPersistedEndpoints()
        Try
            Const regPath1 As String = "Software\Microsoft\Multimedia\Audio\DefaultEndpoint"
            Using baseKey As RegistryKey = Registry.CurrentUser.OpenSubKey(regPath1, writable:=True)
                If baseKey IsNot Nothing Then
                    For Each subName As String In baseKey.GetSubKeyNames()
                        Try : baseKey.DeleteSubKeyTree(subName, throwOnMissingSubKey:=False) : Catch : End Try
                    Next
                End If
            End Using
        Catch
        End Try

        Dim factoryPtr As IntPtr = AcquireAudioPolicyConfigFactoryPtr()
        If factoryPtr <> IntPtr.Zero Then
            Try
                Dim vtbl As IntPtr = Marshal.ReadIntPtr(factoryPtr)
                If vtbl <> IntPtr.Zero Then
                    Dim clearPtr As IntPtr = Marshal.ReadIntPtr(vtbl, 27 * IntPtr.Size)
                    If clearPtr <> IntPtr.Zero Then
                        Dim clearFn As ClearAllPersistedEndpointsDelegate = Marshal.GetDelegateForFunctionPointer(Of ClearAllPersistedEndpointsDelegate)(clearPtr)
                        clearFn(factoryPtr)
                    End If
                End If
            Catch
            Finally
                Try : Marshal.Release(factoryPtr) : Catch : End Try
            End Try
        End If
    End Sub

    Public Shared Sub SanitizeMediaPlayerAudioConfigs(targetEndpoint12Id As String)
        If Not MixerHardwareStateStore.Current.Audio.SanitizeMediaPlayerRegistry Then Return

        Dim blockedKeywords As String() = MixerHardwareStateStore.Current.Audio.BlockedOutputKeywords
        If blockedKeywords Is Nothing OrElse blockedKeywords.Length = 0 Then
            blockedKeywords = {"3-4"}
        End If

        Dim mpcRendererPaths As String() = {
            "Software\MPC-HC\MPC-HC\Internal Filters\MPC Audio Renderer",
            "Software\Gabest\MPC-HC\Internal Filters\MPC Audio Renderer",
            "Software\MPC-BE Filters\MPC Audio Renderer"
        }
        For Each regPath As String In mpcRendererPaths
            Try
                Using k As RegistryKey = Registry.CurrentUser.OpenSubKey(regPath, writable:=True)
                    If k IsNot Nothing Then
                        Dim sd As String = Convert.ToString(k.GetValue("SoundDevice", ""))
                        If Not String.IsNullOrEmpty(sd) Then
                            For Each blk In blockedKeywords
                                If Not String.IsNullOrEmpty(blk) AndAlso sd.IndexOf(blk, StringComparison.OrdinalIgnoreCase) >= 0 Then
                                    k.SetValue("SoundDevice", "", RegistryValueKind.String)
                                    Exit For
                                End If
                            Next
                        End If
                    End If
                End Using
            Catch
            End Try
        Next
    End Sub

    Public Shared Function TryRouteProcessToEndpoint(pid As Integer, targetEndpointId As String) As Boolean
        If Not MixerHardwareStateStore.Current.Audio.AutoRouteAppsToPrimaryOutput Then Return False
        If pid <= 4 OrElse String.IsNullOrWhiteSpace(targetEndpointId) Then Return False
        Dim swdId As String = FormatSwdEndpointId(targetEndpointId)
        Dim hDeviceId As IntPtr = IntPtr.Zero
        Dim factoryPtr As IntPtr = IntPtr.Zero
        Try
            factoryPtr = AcquireAudioPolicyConfigFactoryPtr()
            If factoryPtr = IntPtr.Zero Then Return False
            If WindowsCreateString(swdId, CUInt(swdId.Length), hDeviceId) <> 0 OrElse hDeviceId = IntPtr.Zero Then Return False

            Dim vtbl As IntPtr = Marshal.ReadIntPtr(factoryPtr)
            If vtbl = IntPtr.Zero Then Return False

            Dim setEndpointPtr As IntPtr = Marshal.ReadIntPtr(vtbl, 25 * IntPtr.Size)
            If setEndpointPtr = IntPtr.Zero Then Return False
            Dim setEndpointFn As SetPersistedDefaultAudioEndpointDelegate = Marshal.GetDelegateForFunctionPointer(Of SetPersistedDefaultAudioEndpointDelegate)(setEndpointPtr)

            Dim hr0 As Integer = setEndpointFn(factoryPtr, CUInt(pid), 0, 0, hDeviceId)
            Dim hr1 As Integer = setEndpointFn(factoryPtr, CUInt(pid), 0, 1, hDeviceId)
            Dim hr2 As Integer = setEndpointFn(factoryPtr, CUInt(pid), 0, 2, hDeviceId)
            Return (hr0 = 0 OrElse hr1 = 0 OrElse hr2 = 0)
        Catch
            Return False
        Finally
            If factoryPtr <> IntPtr.Zero Then
                Try : Marshal.Release(factoryPtr) : Catch : End Try
            End If
            If hDeviceId <> IntPtr.Zero Then
                Try : WindowsDeleteString(hDeviceId) : Catch : End Try
            End If
        End Try
    End Function

    Public Shared Function TrySetDefaultRenderEndpoint(deviceId As String) As Boolean
        If String.IsNullOrWhiteSpace(deviceId) Then Return False
        Dim comObj As Object = Nothing
        Try
            Dim t As Type = Type.GetTypeFromCLSID(PolicyConfigClsid, False)
            If t Is Nothing Then Return False
            comObj = Activator.CreateInstance(t)
            If comObj Is Nothing Then Return False

            Dim pc As IPolicyConfig = TryCast(comObj, IPolicyConfig)
            If pc IsNot Nothing Then
                pc.SetDefaultEndpoint(deviceId, 0)
                pc.SetDefaultEndpoint(deviceId, 1)
                pc.SetDefaultEndpoint(deviceId, 2)
                Return True
            End If

            Dim pcv As IPolicyConfigVista = TryCast(comObj, IPolicyConfigVista)
            If pcv IsNot Nothing Then
                pcv.SetDefaultEndpoint(deviceId, 0)
                pcv.SetDefaultEndpoint(deviceId, 1)
                pcv.SetDefaultEndpoint(deviceId, 2)
                Return True
            End If
        Catch
        Finally
            If comObj IsNot Nothing AndAlso Marshal.IsComObject(comObj) Then
                Try : Marshal.ReleaseComObject(comObj) : Catch : End Try
            End If
        End Try
        Return False
    End Function
End Class

Friend NotInheritable Class SessionChannelVolumeHelper
    <ComImport, Guid("1C158861-B533-4B30-B1CF-E853E51C59B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>
    Private Interface IChannelAudioVolume
        <PreserveSig> Function GetChannelCount(ByRef pdwCount As UInteger) As Integer
        <PreserveSig> Function SetChannelVolume(dwIndex As UInteger, fLevel As Single, ByRef EventContext As Guid) As Integer
        <PreserveSig> Function GetChannelVolume(dwIndex As UInteger, ByRef pfLevel As Single) As Integer
        <PreserveSig> Function SetAllVolumes(dwCount As UInteger, <MarshalAs(UnmanagedType.LPArray, ArraySubType:=UnmanagedType.R4)> pfVolumes As Single(), ByRef EventContext As Guid) As Integer
        <PreserveSig> Function GetAllVolumes(dwCount As UInteger, <Out, MarshalAs(UnmanagedType.LPArray, ArraySubType:=UnmanagedType.R4)> pfVolumes As Single()) As Integer
    End Interface

    Private Shared ReadOnly _iidChannelAudioVolume As New Guid("1C158861-B533-4B30-B1CF-E853E51C59B8")
    Private Shared ReadOnly _ctrlField As FieldInfo = GetType(AudioSessionControl).GetField("audioSessionControlInterface", BindingFlags.Instance Or BindingFlags.NonPublic)
    Private Shared ReadOnly _ctrlField2 As FieldInfo = GetType(AudioSessionControl).GetField("audioSessionControlInterface2", BindingFlags.Instance Or BindingFlags.NonPublic)

    Public Shared Sub TrySetSessionChannelVolumes(session As AudioSessionControl, volumeL As Single, volumeR As Single)
        If session Is Nothing Then Return
        Dim unk As IntPtr = IntPtr.Zero
        Dim chVolPtr As IntPtr = IntPtr.Zero
        Dim typedObj As Object = Nothing
        Dim rawCom As Object = Nothing
        Try
            If _ctrlField IsNot Nothing Then rawCom = _ctrlField.GetValue(session)
            If rawCom Is Nothing AndAlso _ctrlField2 IsNot Nothing Then rawCom = _ctrlField2.GetValue(session)
            If rawCom Is Nothing Then Return

            Dim clampedL As Single = Math.Max(0.0F, Math.Min(1.0F, volumeL))
            Dim clampedR As Single = Math.Max(0.0F, Math.Min(1.0F, volumeR))

            Dim directChVol As IChannelAudioVolume = TryCast(rawCom, IChannelAudioVolume)
            If directChVol IsNot Nothing Then
                Dim chCount As UInteger = 0UI
                If directChVol.GetChannelCount(chCount) = 0 AndAlso chCount > 0UI Then
                    Dim ctx As Guid = Guid.Empty
                    If chCount >= 2UI Then
                        Dim vols(CInt(chCount) - 1) As Single
                        vols(0) = clampedL
                        vols(1) = clampedR
                        For c As Integer = 2 To CInt(chCount) - 1
                            vols(c) = Math.Max(clampedL, clampedR)
                        Next
                        directChVol.SetAllVolumes(chCount, vols, ctx)
                    Else
                        directChVol.SetChannelVolume(0UI, Math.Max(clampedL, clampedR), ctx)
                    End If
                End If
                Return
            End If

            If Marshal.IsComObject(rawCom) Then
                unk = Marshal.GetIUnknownForObject(rawCom)
                If unk <> IntPtr.Zero Then
                    Dim iid As Guid = _iidChannelAudioVolume
                    If Marshal.QueryInterface(unk, iid, chVolPtr) = 0 AndAlso chVolPtr <> IntPtr.Zero Then
                        typedObj = Marshal.GetObjectForIUnknown(chVolPtr)
                        Dim chVol As IChannelAudioVolume = TryCast(typedObj, IChannelAudioVolume)
                        If chVol IsNot Nothing Then
                            Dim chCount As UInteger = 0UI
                            If chVol.GetChannelCount(chCount) = 0 AndAlso chCount > 0UI Then
                                Dim ctx As Guid = Guid.Empty
                                If chCount >= 2UI Then
                                    Dim vols(CInt(chCount) - 1) As Single
                                    vols(0) = clampedL
                                    vols(1) = clampedR
                                    For c As Integer = 2 To CInt(chCount) - 1
                                        vols(c) = Math.Max(clampedL, clampedR)
                                    Next
                                    chVol.SetAllVolumes(chCount, vols, ctx)
                                Else
                                    chVol.SetChannelVolume(0UI, Math.Max(clampedL, clampedR), ctx)
                                End If
                            End If
                        End If
                    End If
                End If
            End If
        Catch
        Finally
            If typedObj IsNot Nothing AndAlso Not Object.ReferenceEquals(typedObj, rawCom) AndAlso Marshal.IsComObject(typedObj) Then
                Try : Marshal.ReleaseComObject(typedObj) : Catch : End Try
            End If
            If chVolPtr <> IntPtr.Zero Then
                Try : Marshal.Release(chVolPtr) : Catch : End Try
            End If
            If unk <> IntPtr.Zero Then
                Try : Marshal.Release(unk) : Catch : End Try
            End If
        End Try
    End Sub
End Class

Public Class AudioEndpointInfo
    Public Property Id As String = ""
    Public Property FriendlyName As String = ""
    Public Property SampleRate As Integer = 48000
    Public Property Channels As Integer = 2
    Public Property BitsPerSample As Integer = 32
    Public Property IsBluetooth As Boolean = False
    Public Property IsHighResLdac As Boolean = False
    Public Property IsLowQualityHfp As Boolean = False

    Public Function GetDisplayLabel() As String
        If IsBluetooth Then
            Dim khz As String = (SampleRate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
            If IsHighResLdac AndAlso SampleRate >= 88200 Then
                Return $"{FriendlyName} [BT LDAC {khz} kHz]"
            ElseIf IsHighResLdac Then
                Return $"{FriendlyName} [BT Stereo {khz} kHz]"
            ElseIf IsLowQualityHfp Then
                Return $"{FriendlyName} [HFP {khz} kHz Mono]"
            End If
        End If
        Return FriendlyName
    End Function
End Class

Public Class SystemPlaybackController
    Implements IPlaybackController

    Private ReadOnly _startTick As Long = Environment.TickCount64

    Public ReadOnly Property IsPlaying As Boolean Implements IPlaybackController.IsPlaying
        Get
            Return True
        End Get
    End Property

    Public ReadOnly Property CurrentVirtualTime As TimeSpan Implements IPlaybackController.CurrentVirtualTime
        Get
            Dim elapsedSec As Double = ((Environment.TickCount64 - _startTick) / 1000.0) Mod 60.0
            Return TimeSpan.FromSeconds(elapsedSec)
        End Get
    End Property
End Class

Public Class MasterBusRenderer
    Private ReadOnly _player As NAudioSyncPlayer

    Public Sub New(player As NAudioSyncPlayer)
        _player = player
    End Sub

    Public ReadOnly Property CurrentVolume As Single
        Get
            Return If(_player IsNot Nothing, _player.Volume, 100.0F)
        End Get
    End Property

    Public Sub SetVolume(volPercent As Integer)
        Dim clamped As Single = Math.Max(0.0F, Math.Min(100.0F, CSng(volPercent)))
        If _player IsNot Nothing Then
            _player.Volume = clamped
        End If
    End Sub
End Class

Friend NotInheritable Class ProgramMixerChannel
    Private Const WAVE_HISTORY_LEN As Integer = 200
    Private Const WAVE_STEP_INTERVAL_MS As Long = 40L

    Public ReadOnly Property TrackId As Guid
    Public Property ProgramKey As String
    Public Property DisplayName As String

    Private ReadOnly _pidSet As New HashSet(Of Integer)()
    Private ReadOnly _rawProcessNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _pidLock As New Object()
    Private _lastNameProbeTicks As Long = 0
    Private _lastNameProbeResult As Boolean = False

    Private _fader01 As Single = 1.0F
    Private _trim01 As Single = 0.5F
    Private _hi01 As Single = 0.5F
    Private _mid01 As Single = 0.5F
    Private _low01 As Single = 0.5F
    Private _color01 As Single = 0.5F
    Private _colorMode As Integer = 0
    Private _isMuted As Integer = 0
    Private _isSolo As Integer = 0
    Private _isEffectivelyMuted As Integer = 0
    Private _isApoPostProcessedPeak As Integer = 0

    Public Property HasUserInteracted As Boolean = False
    Public LastUserInteractTicks As Long = 0
    Public Property LastAppliedSessionVolume As Single = -1.0F
    Public Property HasActiveWindowsSession As Boolean = False
    Public Property LastSeenActiveTicks As Long = Environment.TickCount64

    Private _channelPeakPfl As Single = 0.0F
    Private _rawSessionMeterPeak As Single = 0.0F
    Private _envBass As Single = 0.0F
    Private _envMid As Single = 0.0F
    Private _envFloor As Single = 0.0F
    Private _prevRawPeak As Single = 0.0F
    Private _smoothLowEnv As Single = 0.0F
    Private _lastWaveAdvanceTicks As Long = 0
    Private _lastRealtimePcmTicks As Long = 0

    Private ReadOnly _waveHistory(WAVE_HISTORY_LEN - 1) As Single
    Private ReadOnly _wavePeakTop(WAVE_HISTORY_LEN - 1) As Single
    Private ReadOnly _wavePeakBot(WAVE_HISTORY_LEN - 1) As Single
    Private ReadOnly _waveHistoryLow(WAVE_HISTORY_LEN - 1) As Single
    Private ReadOnly _waveHistoryMid(WAVE_HISTORY_LEN - 1) As Single
    Private ReadOnly _waveHistoryHigh(WAVE_HISTORY_LEN - 1) As Single
    Private ReadOnly _waveTransient(WAVE_HISTORY_LEN - 1) As Single
    Private _waveWriteIdx As Integer = 0
    Private _totalWaveFrames As Long = 0
    Private ReadOnly _waveLock As New Object()

    Public Sub New(trackId As Guid, programKey As String, displayName As String)
        Me.TrackId = trackId
        Me.ProgramKey = programKey
        Me.DisplayName = displayName
        Me.LastSeenActiveTicks = Environment.TickCount64
    End Sub

    Public ReadOnly Property LastWaveAdvanceTicks As Long
        Get
            Return Interlocked.Read(_lastWaveAdvanceTicks)
        End Get
    End Property

    Public Property IsApoPostProcessedPeak As Boolean
        Get
            Return Volatile.Read(_isApoPostProcessedPeak) <> 0
        End Get
        Set(value As Boolean)
            Volatile.Write(_isApoPostProcessedPeak, If(value, 1, 0))
        End Set
    End Property

    Public Property ColorFxMode As Integer
        Get
            Return Volatile.Read(_colorMode)
        End Get
        Set(value As Integer)
            Volatile.Write(_colorMode, Math.Max(0, Math.Min(3, value)))
        End Set
    End Property

    Public Property IsMuted As Boolean
        Get
            Return Volatile.Read(_isMuted) <> 0
        End Get
        Set(value As Boolean)
            Volatile.Write(_isMuted, If(value, 1, 0))
        End Set
    End Property

    Public Property IsSolo As Boolean
        Get
            Return Volatile.Read(_isSolo) <> 0
        End Get
        Set(value As Boolean)
            Volatile.Write(_isSolo, If(value, 1, 0))
        End Set
    End Property

    Public Property IsEffectivelyMuted As Boolean
        Get
            Return Volatile.Read(_isEffectivelyMuted) <> 0
        End Get
        Set(value As Boolean)
            Volatile.Write(_isEffectivelyMuted, If(value, 1, 0))
        End Set
    End Property

    Public Function HasActiveEqTrimOrFilter() As Boolean
        Return Math.Abs(Volatile.Read(_trim01) - 0.5F) > 0.008F OrElse
               Math.Abs(Volatile.Read(_hi01) - 0.5F) > 0.008F OrElse
               Math.Abs(Volatile.Read(_mid01) - 0.5F) > 0.008F OrElse
               Math.Abs(Volatile.Read(_low01) - 0.5F) > 0.008F OrElse
               Math.Abs(Volatile.Read(_color01) - 0.5F) > 0.008F
    End Function

    Public Property ChannelPeakPfl As Single
        Get
            Return Volatile.Read(_channelPeakPfl)
        End Get
        Set(value As Single)
            Volatile.Write(_channelPeakPfl, value)
        End Set
    End Property

    Public Property RawSessionMeterPeak As Single
        Get
            Return Volatile.Read(_rawSessionMeterPeak)
        End Get
        Set(value As Single)
            Volatile.Write(_rawSessionMeterPeak, Math.Max(0.0F, Math.Min(1.0F, value)))
        End Set
    End Property

    Public ReadOnly Property Fader01 As Single
        Get
            Return Volatile.Read(_fader01)
        End Get
    End Property

    Public Sub SetEqAndTrim(lowDb As Single, midDb As Single, hiDb As Single, trimGain0To2 As Single, Optional userInitiated As Boolean = True)
        Dim safeTrim As Single = Math.Max(0.0F, Math.Min(4.0F, trimGain0To2))
        Volatile.Write(_low01, yoump.MediaParameterBroker.DecibelsToValue01(lowDb))
        Volatile.Write(_mid01, yoump.MediaParameterBroker.DecibelsToValue01(midDb))
        Volatile.Write(_hi01, yoump.MediaParameterBroker.DecibelsToValue01(hiDb))
        Volatile.Write(_trim01, Math.Max(0.0F, Math.Min(1.0F, safeTrim * 0.5F)))

        If userInitiated Then
            HasUserInteracted = True
            Interlocked.Exchange(LastUserInteractTicks, Environment.TickCount64)
        End If
    End Sub

    Public Sub SetColorFilter(colorKnob01 As Single, Optional userInitiated As Boolean = True)
        Volatile.Write(_color01, Math.Max(0.0F, Math.Min(1.0F, colorKnob01)))
        If userInitiated Then
            HasUserInteracted = True
            Interlocked.Exchange(LastUserInteractTicks, Environment.TickCount64)
        End If
    End Sub

    Public Sub SetChannelFader(fader01 As Single, Optional userInitiated As Boolean = True)
        Volatile.Write(_fader01, Math.Max(0.0F, Math.Min(1.0F, fader01)))
        If userInitiated Then
            HasUserInteracted = True
            Interlocked.Exchange(LastUserInteractTicks, Environment.TickCount64)
        End If
    End Sub

    Public ReadOnly Property Trim01 As Single
        Get
            Return Volatile.Read(_trim01)
        End Get
    End Property

    Public ReadOnly Property Low01 As Single
        Get
            Return Volatile.Read(_low01)
        End Get
    End Property

    Public ReadOnly Property Mid01 As Single
        Get
            Return Volatile.Read(_mid01)
        End Get
    End Property

    Public ReadOnly Property Hi01 As Single
        Get
            Return Volatile.Read(_hi01)
        End Get
    End Property

    Public ReadOnly Property Color01 As Single
        Get
            Return Volatile.Read(_color01)
        End Get
    End Property

    Public Shared Function LinearToPioneerMeter01(linearAmp As Single) As Single
        If Single.IsNaN(linearAmp) OrElse Single.IsInfinity(linearAmp) OrElse linearAmp <= 0.0015F Then
            Return 0.0F
        End If

        Dim db As Single = CSng(20.0 * Math.Log10(linearAmp / 0.85F))
        Dim thresholds As Single() = {-24.0F, -15.0F, -10.0F, -7.0F, -5.0F, -3.0F, -2.0F, -1.0F, 0.0F, 1.0F, 2.0F, 4.0F, 7.0F, 10.0F, 12.0F}

        If db < thresholds(0) Then Return 0.0F
        For i As Integer = 0 To thresholds.Length - 2
            If db >= thresholds(i) AndAlso db < thresholds(i + 1) Then
                Dim t As Single = (db - thresholds(i)) / (thresholds(i + 1) - thresholds(i))
                Return ((i + t + 0.15F) / 15.0F)
            End If
        Next
        Return 1.0F
    End Function

    Public Function GetPostEqPreFaderLinearAmplitude() As Single
        Dim raw As Single = Volatile.Read(_rawSessionMeterPeak)
        If raw <= 0.0005F Then Return 0.0F

        ' Если сигнал микрофона прошёл через Capture APO или Render-сессия прошла через SFX APO в audiodg.exe,
        ' системный пик уже включает Trim, EQ и ColorFX
        Dim isRenderApoActive As Boolean = (Not Me.ProgramKey.Equals("MIC", StringComparison.OrdinalIgnoreCase) AndAlso
                                            NativeCppAutoBuilderAndInstaller.IsKernelApoProcessingAudio())
        If Me.IsApoPostProcessedPeak OrElse isRenderApoActive Then
            Return raw
        End If

        Dim trimLin As Single = BasicEqualizerEffect.CalculatePioneerTrimLinear(Volatile.Read(_trim01) * 2.0F)
        Dim lowLin As Single = BasicEqualizerEffect.DbToIsolatorLinear(yoump.MediaParameterBroker.ValueToDecibels(Volatile.Read(_low01)))
        Dim midLin As Single = BasicEqualizerEffect.DbToIsolatorLinear(yoump.MediaParameterBroker.ValueToDecibels(Volatile.Read(_mid01)))
        Dim hiLin As Single = BasicEqualizerEffect.DbToIsolatorLinear(yoump.MediaParameterBroker.ValueToDecibels(Volatile.Read(_hi01)))
        Dim eqGain As Single = (lowLin * 0.45F) + (midLin * 0.35F) + (hiLin * 0.2F)

        Dim c As Single = Volatile.Read(_color01)
        Dim mode As Integer = Volatile.Read(_colorMode) And 3
        Dim colorGain As Single = 1.0F

        If mode = 0 Then
            If c < 0.025F Then
                colorGain = Math.Max(0.0F, c / 0.025F) * 0.2F
            ElseIf c > 0.975F Then
                colorGain = Math.Max(0.0F, (1.0F - c) / 0.025F) * 0.2F
            ElseIf c < 0.48F Then
                colorGain = 0.25F + 0.75F * (c / 0.48F)
            ElseIf c > 0.52F Then
                colorGain = 0.25F + 0.75F * ((1.0F - c) / 0.48F)
            End If
        ElseIf mode = 3 AndAlso c > 0.52F Then
            colorGain = 1.0F + ((c - 0.5F) * 0.45F)
        End If

        Return raw * trimLin * eqGain * colorGain
    End Function

    Private _accTop As Single = 0.0F
    Private _accBot As Single = 0.0F
    Private _accLow As Single = 0.0F
    Private _accMid As Single = 0.0F
    Private _accHigh As Single = 0.0F
    Private _accHot As Single = 0.0F

    Private _prevBarLow As Single = 0.0F
    Private _prevBarMid As Single = 0.0F
    Private _prevBarHigh As Single = 0.0F
    Private _accPeak As Single = 0.0F
    Private _prevBarTop As Single = 0.0F
    Private _prevBarBot As Single = 0.0F

    Public Sub PushExactWaveBar(peakTop As Single,
                                peakBot As Single,
                                rawLow As Single,
                                rawMid As Single,
                                rawHigh As Single,
                                transientHot As Single,
                                Optional updatePflMeter As Boolean = False)
        Dim nowTicks As Long = Environment.TickCount64
        Interlocked.Exchange(_lastRealtimePcmTicks, nowTicks)

        Dim safeTop As Single = If(Single.IsNaN(peakTop) OrElse Single.IsInfinity(peakTop), 0.0F, Math.Max(0.0F, Math.Min(1.0F, peakTop)))
        Dim safeBot As Single = If(Single.IsNaN(peakBot) OrElse Single.IsInfinity(peakBot), 0.0F, Math.Max(0.0F, Math.Min(1.0F, peakBot)))
        Dim safeLow As Single = Math.Max(0.0F, Math.Min(1.0F, rawLow))
        Dim safeMid As Single = Math.Max(0.0F, Math.Min(1.0F, rawMid))
        Dim safeHigh As Single = Math.Max(0.0F, Math.Min(1.0F, rawHigh))
        Dim safeHot As Single = Math.Max(0.0F, Math.Min(1.0F, transientHot))
        Dim maxPk As Single = Math.Max(safeTop, safeBot)

        If updatePflMeter Then
            Me.RawSessionMeterPeak = maxPk
            Dim pflAmp As Single = GetPostEqPreFaderLinearAmplitude()
            Dim targetMeter01 As Single = LinearToPioneerMeter01(pflAmp)
            Dim decayedPfl As Single = Volatile.Read(_channelPeakPfl) * 0.84F
            Volatile.Write(_channelPeakPfl, Math.Max(decayedPfl, targetMeter01))
        End If

        SyncLock _waveLock
            If safeTop > _accTop Then _accTop = safeTop
            If safeBot > _accBot Then _accBot = safeBot
            If safeHot > _accHot Then _accHot = safeHot

            If maxPk >= _accPeak Then
                _accPeak = maxPk
                _accLow = safeLow
                _accMid = safeMid
                _accHigh = safeHigh
            End If

            If (nowTicks - Interlocked.Read(_lastWaveAdvanceTicks)) >= WAVE_STEP_INTERVAL_MS Then
                Interlocked.Exchange(_lastWaveAdvanceTicks, nowTicks)

                If _accTop < 0.005F AndAlso _accBot < 0.005F Then
                    _prevBarTop *= 0.45F
                    _prevBarBot *= 0.45F
                    If _prevBarTop < 0.01F Then _prevBarTop = 0.0F
                    If _prevBarBot < 0.01F Then _prevBarBot = 0.0F
                    _prevBarLow *= 0.45F
                    _prevBarMid *= 0.45F
                    _prevBarHigh *= 0.45F
                Else
                    _prevBarTop = If(_accTop >= _prevBarTop, _accTop, _prevBarTop * 0.38F + _accTop * 0.62F)
                    _prevBarBot = If(_accBot >= _prevBarBot, _accBot, _prevBarBot * 0.38F + _accBot * 0.62F)
                    _prevBarLow = _prevBarLow * 0.35F + _accLow * 0.65F
                    _prevBarMid = _prevBarMid * 0.35F + _accMid * 0.65F
                    _prevBarHigh = _prevBarHigh * 0.35F + _accHigh * 0.65F
                End If

                _waveHistory(_waveWriteIdx) = Math.Max(_prevBarTop, _prevBarBot)
                _wavePeakTop(_waveWriteIdx) = _prevBarTop
                _wavePeakBot(_waveWriteIdx) = _prevBarBot
                _waveHistoryLow(_waveWriteIdx) = _prevBarLow
                _waveHistoryMid(_waveWriteIdx) = _prevBarMid
                _waveHistoryHigh(_waveWriteIdx) = _prevBarHigh
                _waveTransient(_waveWriteIdx) = _accHot

                _waveWriteIdx = (_waveWriteIdx + 1) Mod WAVE_HISTORY_LEN
                _totalWaveFrames += 1L

                _accPeak = 0.0F
                _accTop = 0.0F
                _accBot = 0.0F
                _accLow = 0.0F
                _accMid = 0.0F
                _accHigh = 0.0F
                _accHot = 0.0F
            End If
        End SyncLock
    End Sub

    Public Sub PushRealtimePcmSubSlice(peakTop As Single,
                                       peakBot As Single,
                                       rawLow As Single,
                                       rawMid As Single,
                                       rawHigh As Single,
                                       transientHot As Single,
                                       Optional updatePflMeter As Boolean = False)
        PushExactWaveBar(peakTop, peakBot, rawLow, rawMid, rawHigh, transientHot, updatePflMeter)
    End Sub

    Public Sub UpdateMetersFromThreeBandPeak(rawPeak As Single, rawLow As Single, rawMid As Single, rawHigh As Single)
        Dim safeRaw As Single = If(Single.IsNaN(rawPeak) OrElse Single.IsInfinity(rawPeak), 0.0F, Math.Max(0.0F, Math.Min(1.0F, rawPeak)))
        Me.RawSessionMeterPeak = safeRaw

        Dim pflAmp As Single = GetPostEqPreFaderLinearAmplitude()
        Dim targetMeter01 As Single = LinearToPioneerMeter01(pflAmp)

        Dim decayedPfl As Single = Volatile.Read(_channelPeakPfl) * 0.8F
        Volatile.Write(_channelPeakPfl, Math.Max(decayedPfl, targetMeter01))

        Dim nowTicks As Long = Environment.TickCount64
        If (nowTicks - Interlocked.Read(_lastRealtimePcmTicks)) < (WAVE_STEP_INTERVAL_MS + 40L) Then
            Return
        End If

        If (nowTicks - Interlocked.Read(_lastWaveAdvanceTicks)) >= WAVE_STEP_INTERVAL_MS Then
            Dim trHot As Single = Math.Max(0.0F, safeRaw - _prevRawPeak) * 2.5F
            _prevRawPeak = safeRaw
            PushExactWaveBar(safeRaw, safeRaw * 0.94F, rawLow, rawMid, rawHigh, trHot, False)
        End If
    End Sub

    Public Sub UpdateMetersFromStereoSessionPeak(rawPeakL As Single,
                                                 rawPeakR As Single,
                                                 specLowRatio As Single,
                                                 specMidRatio As Single,
                                                 specHighRatio As Single)
        Dim safeL As Single = If(Single.IsNaN(rawPeakL) OrElse Single.IsInfinity(rawPeakL), 0.0F, Math.Max(0.0F, Math.Min(1.0F, rawPeakL)))
        Dim safeR As Single = If(Single.IsNaN(rawPeakR) OrElse Single.IsInfinity(rawPeakR), 0.0F, Math.Max(0.0F, Math.Min(1.0F, rawPeakR)))
        If safeR < 0.002F AndAlso safeL > 0.008F Then safeR = safeL
        If safeL < 0.002F AndAlso safeR > 0.008F Then safeL = safeR

        Dim safeRaw As Single = Math.Max(safeL, safeR)
        Me.RawSessionMeterPeak = safeRaw

        Dim pflAmp As Single = GetPostEqPreFaderLinearAmplitude()
        Dim targetMeter01 As Single = LinearToPioneerMeter01(pflAmp)
        Dim decayedPfl As Single = Volatile.Read(_channelPeakPfl) * 0.8F
        Volatile.Write(_channelPeakPfl, Math.Max(decayedPfl, targetMeter01))

        Dim nowTicks As Long = Environment.TickCount64
        If (nowTicks - Interlocked.Read(_lastRealtimePcmTicks)) < 65 Then
            Return
        End If

        If safeRaw < 0.002F Then
            _envBass *= 0.45F
            _envMid *= 0.45F
            _envFloor *= 0.5F
            _prevRawPeak = 0.0F
            If (nowTicks - Interlocked.Read(_lastWaveAdvanceTicks)) >= WAVE_STEP_INTERVAL_MS Then
                PushExactWaveBar(0.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F, False)
            End If
            Return
        End If

        If safeRaw < _envFloor Then
            _envFloor = _envFloor * 0.65F + safeRaw * 0.35F
        Else
            _envFloor = _envFloor * 0.93F + safeRaw * 0.07F
        End If

        Dim floorBase As Single = _envFloor * 0.72F
        Dim floorSub As Single = Math.Max(0.0F, safeRaw - floorBase) / Math.Max(0.18F, 1.0F - floorBase)
        Dim transientDelta As Single = Math.Max(0.0F, safeRaw - _prevRawPeak)
        _prevRawPeak = safeRaw

        Dim shapedAmp As Single = Math.Min(0.96F, CSng(Math.Pow(Math.Min(1.0F, floorSub), 1.85)) * 0.72F + (safeRaw * 0.14F) + (transientDelta * 1.6F))
        Dim rawLow As Single = Math.Min(1.0F, shapedAmp * specLowRatio)
        Dim rawMid As Single = Math.Min(1.0F, shapedAmp * specMidRatio)
        Dim rawHigh As Single = Math.Min(1.0F, (shapedAmp * specHighRatio * 0.85F) + (transientDelta * 2.0F))

        If (nowTicks - Interlocked.Read(_lastWaveAdvanceTicks)) >= WAVE_STEP_INTERVAL_MS Then
            Dim topPk As Single = shapedAmp * (safeL / Math.Max(0.001F, safeRaw))
            Dim botPk As Single = shapedAmp * (safeR / Math.Max(0.001F, safeRaw))
            PushExactWaveBar(topPk, botPk, rawLow, rawMid, rawHigh, transientDelta * 3.0F, False)
        End If
    End Sub

    Public Sub UpdateMetersFromSessionPeak(rawPeak As Single)
        UpdateMetersFromStereoSessionPeak(rawPeak, rawPeak, 0.85F, 0.78F, 0.52F)
    End Sub

    Public Sub AddPid(pid As Integer)
        If pid <= 4 Then Return
        SyncLock _pidLock
            _pidSet.Add(pid)
        End SyncLock
    End Sub

    Public Sub SyncProcessIds(pids As IEnumerable(Of Integer), Optional rawProcNames As IEnumerable(Of String) = Nothing)
        SyncLock _pidLock
            If pids IsNot Nothing Then
                _pidSet.Clear()
                For Each p In pids
                    If p > 4 Then _pidSet.Add(p)
                Next
            End If
            If rawProcNames IsNot Nothing Then
                For Each rn In rawProcNames
                    If Not String.IsNullOrWhiteSpace(rn) Then
                        _rawProcessNames.Add(rn.Trim())
                    End If
                Next
            End If
        End SyncLock
        LastSeenActiveTicks = Environment.TickCount64
    End Sub

    Public Function ContainsPid(pid As Integer) As Boolean
        SyncLock _pidLock
            Return _pidSet.Contains(pid)
        End SyncLock
    End Function

    Public Function IsAnyProcessAliveNow() As Boolean
        If ProgramKey = "SYSTEM" OrElse ProgramKey = "MIC" Then Return True

        Dim namesToCheck As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim pidsToCheck As New List(Of Integer)()
        SyncLock _pidLock
            For Each rn In _rawProcessNames
                namesToCheck.Add(rn)
            Next
            pidsToCheck.AddRange(_pidSet)
        End SyncLock

        For Each pid In pidsToCheck
            Try
                Dim exeName As String = NAudioSyncPlayer.TryGetProcessNameFast(pid)
                If Not String.IsNullOrEmpty(exeName) AndAlso
                   (namesToCheck.Count = 0 OrElse namesToCheck.Contains(exeName)) Then
                    Return True
                End If
            Catch
            End Try
        Next

        If namesToCheck.Count = 0 Then Return False

        Dim nowTicks As Long = Environment.TickCount64
        If (nowTicks - Interlocked.Read(_lastNameProbeTicks)) < 1500L Then
            Return Volatile.Read(_lastNameProbeResult)
        End If
        Interlocked.Exchange(_lastNameProbeTicks, nowTicks)

        Dim foundByName As Boolean = False
        For Each rawName In namesToCheck
            Try
                Dim procs As Process() = Process.GetProcessesByName(rawName)
                Dim anyAlive As Boolean = (procs IsNot Nothing AndAlso procs.Length > 0)
                If procs IsNot Nothing Then
                    For Each p In procs
                        Try : p.Dispose() : Catch : End Try
                    Next
                End If
                If anyAlive Then
                    foundByName = True
                    Exit For
                End If
            Catch
            End Try
        Next

        Volatile.Write(_lastNameProbeResult, foundByName)
        Return foundByName
    End Function

    Public Function IsProgramStillRunningInWindows(Optional alreadyCheckedAlive As Boolean? = Nothing) As Boolean
        Dim isAlive As Boolean = If(alreadyCheckedAlive.HasValue, alreadyCheckedAlive.Value, IsAnyProcessAliveNow())
        If isAlive Then Return True
        Dim timeoutMs As Integer = Math.Max(2000, MixerHardwareStateStore.Current.ProcessRules.InactiveChannelTimeoutMs)
        Return (Environment.TickCount64 - LastSeenActiveTicks) < timeoutMs
    End Function

    Public Function GetWaveSnapshot() As Single()
        Dim res(WAVE_HISTORY_LEN - 1) As Single
        SyncLock _waveLock
            For i As Integer = 0 To WAVE_HISTORY_LEN - 1
                res(i) = _waveHistory((_waveWriteIdx + i) Mod WAVE_HISTORY_LEN)
            Next
        End SyncLock
        Return res
    End Function

    Public Sub GetThreeBandWaveSnapshot(ByRef lowPeaks As Single(),
                                        ByRef midPeaks As Single(),
                                        ByRef highPeaks As Single(),
                                        Optional ByRef peakTop As Single() = Nothing,
                                        Optional ByRef peakBot As Single() = Nothing,
                                        Optional ByRef transients As Single() = Nothing)
        Dim lArr(WAVE_HISTORY_LEN - 1) As Single
        Dim mArr(WAVE_HISTORY_LEN - 1) As Single
        Dim hArr(WAVE_HISTORY_LEN - 1) As Single
        Dim tArr(WAVE_HISTORY_LEN - 1) As Single
        Dim bArr(WAVE_HISTORY_LEN - 1) As Single
        Dim trArr(WAVE_HISTORY_LEN - 1) As Single
        SyncLock _waveLock
            For i As Integer = 0 To WAVE_HISTORY_LEN - 1
                Dim idx As Integer = (_waveWriteIdx + i) Mod WAVE_HISTORY_LEN
                lArr(i) = _waveHistoryLow(idx)
                mArr(i) = _waveHistoryMid(idx)
                hArr(i) = _waveHistoryHigh(idx)
                tArr(i) = _wavePeakTop(idx)
                bArr(i) = _wavePeakBot(idx)
                trArr(i) = _waveTransient(idx)
            Next
        End SyncLock
        lowPeaks = lArr
        midPeaks = mArr
        highPeaks = hArr
        peakTop = tArr
        peakBot = bArr
        transients = trArr
    End Sub
End Class

Public Class NAudioSyncPlayer
    Implements IDisposable

    Private Const PROCESS_QUERY_LIMITED_INFORMATION As UInteger = &H1000UI
    Private Const STILL_ACTIVE As UInteger = 259UI
    Private Const IgnoredProcessMarker As String = "__IGNORED__"

    <DllImport("kernel32.dll", ExactSpelling:=True, SetLastError:=True)>
    Private Shared Function OpenProcess(dwDesiredAccess As UInteger, <MarshalAs(UnmanagedType.Bool)> bInheritHandle As Boolean, dwProcessId As UInteger) As IntPtr
    End Function

    <DllImport("kernel32.dll", ExactSpelling:=True, SetLastError:=True)>
    Private Shared Function GetExitCodeProcess(hProcess As IntPtr, ByRef lpExitCode As UInteger) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function QueryFullProcessImageNameW(hProcess As IntPtr, dwFlags As UInteger, lpExeName As StringBuilder, ByRef lpdwSize As UInteger) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    <DllImport("kernel32.dll", ExactSpelling:=True, SetLastError:=True)>
    Private Shared Function CloseHandle(hObject As IntPtr) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    Private ReadOnly _enumerator As New MMDeviceEnumerator()
    Private _model As ProjectModel
    Private _uiContext As SynchronizationContext
    Private _captureDevice As MMDevice
    Private _outputDevice As MMDevice
    Private _outputMonitorCapture As WasapiLoopbackCapture
    Private _micCaptureDevice As MMDevice
    Private _mainCaptureDevice As MMDevice
    Private _micOutputDevice As MMDevice
    Private _micCapture As WasapiCapture
    Private _micWaveProvider As BufferedWaveProvider
    Private _micWaveOut As WasapiOut
    Private _micMonitoringEnabled As Boolean = False
    Private _asioOut As AsioOut
    Private _asioOutputProvider As AsioDirectSampleProvider
    Private _asioActiveDriverName As String = ""
    Private ReadOnly _asioBufferLock As New Object()
    Private _asioInterleavedBuffer As Single() = Array.Empty(Of Single)()
    Private _asioProcessedMonitorBuffer As Single() = Array.Empty(Of Single)()
    Private _asioMonitorSamplesReady As Integer = 0

    Public ReadOnly Property IsAsioActive As Boolean
        Get
            Return _asioOut IsNot Nothing AndAlso Not String.IsNullOrEmpty(_asioActiveDriverName)
        End Get
    End Property

    Public ReadOnly Property ActiveAsioDriverName As String
        Get
            Return _asioActiveDriverName
        End Get
    End Property

    Public Function GetAvailableAsioDrivers() As List(Of String)
        Dim result As New List(Of String)()
        Try
            Dim names As String() = AsioOut.GetDriverNames()
            If names IsNot Nothing Then
                For Each n As String In names
                    If Not String.IsNullOrWhiteSpace(n) Then
                        result.Add(n.Trim())
                    End If
                Next
            End If
        Catch
        End Try
        Return result
    End Function

    Public Sub ShowAsioControlPanel()
        Try
            If _asioOut IsNot Nothing Then
                _asioOut.ShowControlPanel()
                Return
            End If
        Catch
        End Try
        OpenRolandQuadCaptureControlPanel()
    End Sub

    Public Shared Function OpenRolandQuadCaptureControlPanel() As Boolean
        Try
            Dim candidatePaths As String() = {
                "C:\Program Files\Roland\QUAD-CAPTURE\Files\RDPS1117.exe",
                "C:\Program Files\Roland\QUAD-CAPTURE\QuadCaptureControlPanel.exe",
                "C:\Program Files (x86)\Roland\QUAD-CAPTURE\Files\RDPS1117.exe",
                "C:\Program Files (x86)\Roland\QUAD-CAPTURE\QuadCaptureControlPanel.exe"
            }
            For Each p In candidatePaths
                If File.Exists(p) Then
                    Process.Start(New ProcessStartInfo(p) With {.UseShellExecute = True})
                    Return True
                End If
            Next

            Using asioKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\ASIO\QUAD-CAPTURE")
                Dim clsid As String = Convert.ToString(asioKey?.GetValue("CLSID", ""))
                If Not String.IsNullOrWhiteSpace(clsid) Then
                    Using clsidKey = Registry.ClassesRoot.OpenSubKey($"CLSID\{clsid}\InprocServer32")
                        Dim dllPath As String = Convert.ToString(clsidKey?.GetValue("", ""))
                        If Not String.IsNullOrWhiteSpace(dllPath) AndAlso File.Exists(dllPath) Then
                            Dim dir As String = Path.GetDirectoryName(dllPath)
                            If Directory.Exists(dir) Then
                                For Each exe In Directory.GetFiles(dir, "*.exe")
                                    Process.Start(New ProcessStartInfo(exe) With {.UseShellExecute = True})
                                    Return True
                                Next
                            End If
                        End If
                    End Using
                End If
            End Using
        Catch
        End Try
        Return False
    End Function

    Public Property MicMonitoringEnabled As Boolean
        Get
            Return Volatile.Read(_micMonitoringEnabled)
        End Get
        Set(value As Boolean)
            Volatile.Write(_micMonitoringEnabled, value)
            MixerHardwareStateStore.Current.Audio.MicMonitoringEnabled = value
            MixerHardwareStateStore.RequestSaveDeferred()
            Try
                If Not value Then
                    _micWaveProvider?.ClearBuffer()
                ElseIf _micWaveOut IsNot Nothing AndAlso _micWaveOut.PlaybackState <> PlaybackState.Playing Then
                    _micWaveOut.Play()
                End If
            Catch
            End Try
        End Set
    End Property

    Private _liveOutputHardwarePeak As Single = 0.0F
    Private _loopLp250_1 As Single = 0.0F
    Private _loopLp250_2 As Single = 0.0F
    Private _loopHpMid_1 As Single = 0.0F
    Private _loopHpMid_2 As Single = 0.0F
    Private _loopLp3500_1 As Single = 0.0F
    Private _loopLp3500_2 As Single = 0.0F
    Private _loopPrevSubPeak As Single = 0.0F
    Private _loopRefPeak As Single = 0.25F
    Private _loopSlowFloor As Single = 0.0F
    Private _liveSpecRatioLow As Single = 0.85F
    Private _liveSpecRatioMid As Single = 0.78F
    Private _liveSpecRatioHigh As Single = 0.55F

    Private _masterEffects As EffectChainProvider

    Private ReadOnly _programChannels As New Dictionary(Of Guid, ProgramMixerChannel)()
    Private _channelsSnapshot As ProgramMixerChannel() = Array.Empty(Of ProgramMixerChannel)()
    Private ReadOnly _channelsLock As New Object()
    Private ReadOnly _engineLock As New Object()
    Private ReadOnly _pidToProgramCache As New ConcurrentDictionary(Of Integer, (Key As String, Title As String, RawProcessName As String))()
    Private ReadOnly _lastRouteAttemptTicks As New ConcurrentDictionary(Of Integer, Long)()

    Private ReadOnly _syncSignal As New AutoResetEvent(False)
    Private _sessionWorkerThread As Thread
    Private _forceImmediateApply As Integer = 0
    Private _lastDiscoveryTicks As Long = 0

    Private _cachedCaptureId As String = ""
    Private _cachedCaptureName As String = "1-2 (QUAD-CAPTURE) [Прямой C++ APO]"
    Private _cachedOutputId As String = ""
    Private _cachedOutputName As String = "1-2 (QUAD-CAPTURE)"
    Private _speakerQuad12Id As String = ""
    Private _lastLdacGuardStatus As String = ""
    Private _lastBtHotplugProbeTicks As Long = 0
    Private _cachedEndpoints As New List(Of AudioEndpointInfo)()

    Public ReadOnly Property LastLdacGuardStatus As String
        Get
            Return _lastLdacGuardStatus
        End Get
    End Property

    Private ReadOnly _nativeMixer As New NativeMixerEffect()
    Private ReadOnly _balanceEffect As New StereoBalanceEffect()

    Private _volume As Single = 100.0F
    Private _balance As Single = 0.5F
    Private _masterUserInteracted As Boolean = False
    Private _balanceUserInteracted As Boolean = False
    Private _lastAppliedBalance As Single = 0.5F
    Private _targetSampleRate As Integer = 48000
    Private _talkoverDuckEnvelope As Single = 0.0F
    Private _focusedTrackId As Guid = Guid.Empty
    Private _isDisposed As Boolean = False

    Public ReadOnly Property IsDisposed As Boolean
        Get
            Return _isDisposed
        End Get
    End Property

    Public ReadOnly Property CurrentSampleRate As Integer
        Get
            Return Math.Max(8000, _targetSampleRate)
        End Get
    End Property

    Public Property FocusedTrackId As Guid
        Get
            Return _focusedTrackId
        End Get
        Set(value As Guid)
            _focusedTrackId = value
        End Set
    End Property

    Public ReadOnly Property MasterEffects As EffectChainProvider
        Get
            Return _masterEffects
        End Get
    End Property

    Public ReadOnly Property MasterBalanceEffect As StereoBalanceEffect
        Get
            Return _balanceEffect
        End Get
    End Property

    Public ReadOnly Property NativeMixer As NativeMixerEffect
        Get
            Return _nativeMixer
        End Get
    End Property

    Public ReadOnly Property CaptureDeviceId As String
        Get
            Return _cachedCaptureId
        End Get
    End Property

    Public ReadOnly Property OutputDeviceId As String
        Get
            Return _cachedOutputId
        End Get
    End Property

    Public ReadOnly Property CaptureDeviceName As String
        Get
            Return _cachedCaptureName
        End Get
    End Property

    Public ReadOnly Property OutputDeviceName As String
        Get
            Return _cachedOutputName
        End Get
    End Property

    Public Property Volume As Single
        Get
            Return Volatile.Read(_volume)
        End Get
        Set(value As Single)
            Dim clamped As Single = Math.Max(0.0F, Math.Min(100.0F, value))
            Dim prev As Single = Volatile.Read(_volume)
            Volatile.Write(_volume, clamped)
            If Math.Abs(clamped - prev) > 0.01F Then
                _masterUserInteracted = True
                TriggerFastSessionSync(forceVolumeWrite:=True)
            End If
        End Set
    End Property

    Public Sub New(Optional autoStart As Boolean = True, Optional model As ProjectModel = Nothing)
        _uiContext = SynchronizationContext.Current
        If model IsNot Nothing Then
            AttachProjectModel(model)
        End If
        StartSessionWorkerThread()
        If autoStart Then
            StartEngine(Nothing, Nothing, False)
        End If
    End Sub

    Public Sub ApplyInitialSavedState(savedState As MixerHardwareStateStore)
        If savedState Is Nothing Then Return
        If savedState.Audio.MicIrPreset = 7 AndAlso
           Not String.IsNullOrWhiteSpace(savedState.Audio.MicCustomIrFilePath) AndAlso
           File.Exists(savedState.Audio.MicCustomIrFilePath) Then
            NativeCppAutoBuilderAndInstaller.LoadAndUploadCustomWavIr(savedState.Audio.MicCustomIrFilePath, Me.CurrentSampleRate)
        End If
        NativeCppAutoBuilderAndInstaller.WriteMicConvolutionIrParams(
            savedState.Audio.MicIrPreset,
            savedState.Audio.MicIrMix,
            savedState.Audio.MicNoiseGateThreshold,
            savedState.Audio.GetPackedMicExtraFlags())

        _pidToProgramCache.Clear()

        Volatile.Write(_volume, Math.Max(0.0F, Math.Min(100.0F, savedState.MasterVolume01 * 100.0F)))
        Dim newBal As Single = Math.Max(0.0F, Math.Min(1.0F, savedState.MasterBalance01))
        If Math.Abs(newBal - _lastAppliedBalance) > 0.001F OrElse Math.Abs(newBal - 0.5F) > 0.002F Then
            _balanceUserInteracted = True
        End If
        Volatile.Write(_balance, newBal)
        Me.MicMonitoringEnabled = savedState.Audio.MicMonitoringEnabled
        _balanceEffect.Balance = newBal

        If _model?.Tracks IsNot Nothing Then
            SyncLock _model.SyncRoot
                For i As Integer = 0 To _model.Tracks.Count - 1
                    Dim t As TrackModel = _model.Tracks(i)
                    If t Is Nothing OrElse t.Type <> TargetFormatType.Audio Then Continue For
                    Dim prof As ChannelProfileConfig = MixerHardwareStateStore.GetOrCreateChannelProfile(t.Name)
                    t.Trim = prof.Trim
                    t.EqHi = prof.EqHi
                    t.EqMid = prof.EqMid
                    t.EqLow = prof.EqLow
                    t.ColorFx = prof.ColorFx
                    t.ColorFxMode = prof.ColorFxMode
                    t.WaveformZoom = prof.WaveformZoom
                    t.IsMuted = prof.IsMuted
                    t.IsSolo = prof.IsSolo
                    If t.Clips IsNot Nothing AndAlso t.Clips.Count > 0 Then
                        t.Clips(0).Volume = prof.Fader
                    End If
                Next
            End SyncLock
        End If

        SyncChannelsWithProjectModel(userInitiated:=False)
        Interlocked.Exchange(_lastDiscoveryTicks, 0)
        TriggerFastSessionSync(forceVolumeWrite:=True)
    End Sub

    Private Sub StartSessionWorkerThread()
        If _sessionWorkerThread IsNot Nothing AndAlso _sessionWorkerThread.IsAlive Then Return
        _sessionWorkerThread = New Thread(AddressOf SessionWorkerLoop) With {
            .IsBackground = True,
            .Name = "YoumpDirectAudioSessionWorker",
            .Priority = ThreadPriority.AboveNormal
        }
        _sessionWorkerThread.SetApartmentState(ApartmentState.MTA)
        _sessionWorkerThread.Start()
    End Sub

    Private Sub SessionWorkerLoop()
        Using workerEnumerator As New MMDeviceEnumerator()
            While Not _isDisposed
                Try
                    Dim rulesCfg = MixerHardwareStateStore.Current.ProcessRules
                    Dim pollMs As Integer = Math.Max(5, Math.Min(200, rulesCfg.SessionPollIntervalMs))
                    Dim discMs As Integer = Math.Max(100, rulesCfg.DiscoveryIntervalMs)

                    _syncSignal.WaitOne(pollMs)
                    If _isDisposed Then Exit While

                    Dim nowTicks As Long = Environment.TickCount64
                    Dim forceNow As Boolean = (Interlocked.Exchange(_forceImmediateApply, 0) <> 0)
                    Dim needDiscovery As Boolean = ((nowTicks - Interlocked.Read(_lastDiscoveryTicks)) >= discMs)

                    If needDiscovery Then
                        Interlocked.Exchange(_lastDiscoveryTicks, nowTicks)
                        If DiscoverAndSyncProgramChannels(workerEnumerator) Then
                            Continue While
                        End If
                    End If

                    ApplyProgramChannelsAndUpdateMeters(workerEnumerator, refreshSessions:=needDiscovery OrElse forceNow, forceWriteAll:=forceNow)
                Catch
                End Try
            End While
        End Using
    End Sub

    Public Sub AttachProjectModel(model As ProjectModel)
        _model = model
        If SynchronizationContext.Current IsNot Nothing Then
            _uiContext = SynchronizationContext.Current
        End If
        SyncChannelsWithProjectModel(userInitiated:=False)
    End Sub

    Friend Function GetActiveProgramChannelsSnapshot() As ProgramMixerChannel()
        Return Volatile.Read(_channelsSnapshot)
    End Function

    Private Sub SyncChannelsWithProjectModel(Optional userInitiated As Boolean = False)
        If _model?.Tracks Is Nothing Then Return
        SyncLock _channelsLock
            SyncLock _model.SyncRoot
                Dim ordered As New List(Of ProgramMixerChannel)(_model.Tracks.Count)
                For i As Integer = 0 To _model.Tracks.Count - 1
                    Dim t As TrackModel = _model.Tracks(i)
                    If t.Type <> TargetFormatType.Audio Then Continue For

                    Dim ch As ProgramMixerChannel = Nothing
                    If Not _programChannels.TryGetValue(t.Id, ch) Then
                        Dim key As String = If(t.Name = "MAIN MIC" OrElse t.Name = "MIC 1-2" OrElse t.Name = "MIC",
                                               "MIC",
                                               If(String.IsNullOrWhiteSpace(t.Name) OrElse t.Name = "SYS MIX", "SYSTEM", t.Name.ToUpperInvariant()))
                        ch = New ProgramMixerChannel(t.Id, key, t.Name)
                        _programChannels(t.Id) = ch
                    End If

                    Dim fader As Single = If(t.Clips.Count > 0, t.Clips(0).Volume, 1.0F)
                    ch.SetChannelFader(fader, userInitiated)
                    ch.SetEqAndTrim(
                        yoump.MediaParameterBroker.ValueToDecibels(t.EqLow),
                        yoump.MediaParameterBroker.ValueToDecibels(t.EqMid),
                        yoump.MediaParameterBroker.ValueToDecibels(t.EqHi),
                        t.Trim * 2.0F,
                        userInitiated
                    )
                    ch.SetColorFilter(t.ColorFx, userInitiated)
                    ch.ColorFxMode = t.ColorFxMode
                    ch.IsMuted = t.IsMuted
                    ch.IsSolo = t.IsSolo
                    ordered.Add(ch)
                Next
                Volatile.Write(_channelsSnapshot, ordered.ToArray())
            End SyncLock
            RecalculateEffectiveMuteStates()
        End SyncLock
    End Sub

    Private Sub RecalculateEffectiveMuteStates()
        Dim snap As ProgramMixerChannel() = Volatile.Read(_channelsSnapshot)
        If snap Is Nothing OrElse snap.Length = 0 Then Return

        Dim anySolo As Boolean = False
        For i As Integer = 0 To snap.Length - 1
            If snap(i) IsNot Nothing AndAlso snap(i).IsSolo Then
                anySolo = True
                Exit For
            End If
        Next

        For i As Integer = 0 To snap.Length - 1
            Dim ch As ProgramMixerChannel = snap(i)
            If ch Is Nothing Then Continue For
            Dim effMuted As Boolean = ch.IsMuted OrElse (anySolo AndAlso Not ch.IsSolo)
            ch.IsEffectivelyMuted = effMuted
        Next
    End Sub

    Public Function GetCachedOutputDeviceInfos() As List(Of AudioEndpointInfo)
        Dim cur As List(Of AudioEndpointInfo) = Volatile.Read(_cachedEndpoints)
        If cur Is Nothing OrElse cur.Count = 0 Then
            RefreshEndpointsCache()
            cur = Volatile.Read(_cachedEndpoints)
        End If
        Return cur
    End Function

    Public Structure BluetoothEndpointDiagnostics
        Public DeviceId As String
        Public FriendlyName As String
        Public SampleRate As Integer
        Public Channels As Integer
        Public BitsPerSample As Integer
        Public IsBluetooth As Boolean
        Public IsHighResLdacCapable As Boolean
        Public IsLowQualityHfp As Boolean
    End Structure

    Public Shared Function AnalyzeAudioEndpoint(dev As MMDevice) As BluetoothEndpointDiagnostics
        Dim diag As New BluetoothEndpointDiagnostics() With {
            .DeviceId = "",
            .FriendlyName = "",
            .SampleRate = 48000,
            .Channels = 2,
            .BitsPerSample = 32,
            .IsBluetooth = False,
            .IsHighResLdacCapable = False,
            .IsLowQualityHfp = False
        }
        If dev Is Nothing Then Return diag

        Try
            diag.DeviceId = dev.ID
            diag.FriendlyName = dev.FriendlyName

            Dim idLower As String = If(dev.ID, "").ToLowerInvariant()
            Dim nameLower As String = If(dev.FriendlyName, "").ToLowerInvariant()
            Dim btKws As String() = MixerHardwareStateStore.Current.Audio.BluetoothHeadphoneKeywords
            Dim matchesBtKeyword As Boolean = MatchesAnyHardwareKeyword(dev.FriendlyName, btKws)

            diag.IsBluetooth = matchesBtKeyword OrElse
                               idLower.Contains("bth") OrElse
                               nameLower.Contains("bluetooth") OrElse
                               nameLower.Contains("space travel") OrElse
                               nameLower.Contains("moondrop") OrElse
                               nameLower.Contains("hands-free") OrElse
                               nameLower.Contains("головной телефон")

            Dim mixFmt As WaveFormat = dev.AudioClient?.MixFormat
            If mixFmt IsNot Nothing Then
                diag.SampleRate = mixFmt.SampleRate
                diag.Channels = mixFmt.Channels
                diag.BitsPerSample = mixFmt.BitsPerSample
            End If

            diag.IsLowQualityHfp = (diag.SampleRate <= 16000) OrElse
                                   (diag.IsBluetooth AndAlso diag.Channels = 1 AndAlso diag.SampleRate < 32000) OrElse
                                   nameLower.Contains("hands-free") OrElse
                                   nameLower.Contains("головной телефон") OrElse
                                   idLower.Contains("bthhfenum")

            diag.IsHighResLdacCapable = diag.IsBluetooth AndAlso
                                        Not diag.IsLowQualityHfp AndAlso
                                        diag.Channels >= 2 AndAlso
                                        diag.SampleRate >= 44100
        Catch
        End Try
        Return diag
    End Function

    Public Sub RefreshEndpointsCache()
        Dim list As New List(Of AudioEndpointInfo)()
        Dim audioCfg = MixerHardwareStateStore.Current.Audio
        Try
            For Each dev In _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                Try
                    Dim diag As BluetoothEndpointDiagnostics = AnalyzeAudioEndpoint(dev)
                    If audioCfg.BluetoothMicHighQualityOnly AndAlso diag.IsLowQualityHfp Then
                        Continue For
                    End If
                    list.Add(New AudioEndpointInfo() With {
                        .Id = diag.DeviceId,
                        .FriendlyName = diag.FriendlyName,
                        .SampleRate = diag.SampleRate,
                        .Channels = diag.Channels,
                        .BitsPerSample = diag.BitsPerSample,
                        .IsBluetooth = diag.IsBluetooth,
                        .IsHighResLdac = diag.IsHighResLdacCapable,
                        .IsLowQualityHfp = diag.IsLowQualityHfp
                    })
                Catch
                Finally
                    Try : dev.Dispose() : Catch : End Try
                End Try
            Next
        Catch
        End Try
        Volatile.Write(_cachedEndpoints, list)
    End Sub

    Private Shared Function IsBlockedEndpointName(friendlyName As String, blockedKeywords() As String) As Boolean
        If String.IsNullOrEmpty(friendlyName) OrElse blockedKeywords Is Nothing Then Return False
        For Each blk In blockedKeywords
            If Not String.IsNullOrEmpty(blk) AndAlso friendlyName.IndexOf(blk, StringComparison.OrdinalIgnoreCase) >= 0 Then
                Return True
            End If
        Next
        Return False
    End Function

    Private Shared Function MatchesAnyHardwareKeyword(friendlyName As String, hwKeywords() As String) As Boolean
        If String.IsNullOrEmpty(friendlyName) Then Return False
        If hwKeywords Is Nothing OrElse hwKeywords.Length = 0 Then Return True
        For Each kw In hwKeywords
            If Not String.IsNullOrEmpty(kw) AndAlso friendlyName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 Then
                Return True
            End If
        Next
        Return False
    End Function

    Public Function FindHighQualityBluetoothOutputId(Optional ByRef outEndpoint As AudioEndpointInfo = Nothing) As String
        outEndpoint = Nothing
        Dim endpoints As List(Of AudioEndpointInfo) = GetCachedOutputDeviceInfos()
        Dim audioCfg = MixerHardwareStateStore.Current.Audio
        Dim bestEp As AudioEndpointInfo = Nothing
        Dim bestScore As Integer = 0

        For Each ep In endpoints
            If IsBlockedEndpointName(ep.FriendlyName, audioCfg.BlockedOutputKeywords) Then Continue For
            If Not ep.IsBluetooth OrElse ep.IsLowQualityHfp OrElse Not ep.IsHighResLdac Then Continue For

            Dim isPreferredModel As Boolean = MatchesAnyHardwareKeyword(ep.FriendlyName, audioCfg.BluetoothHeadphoneKeywords)
            Dim isSpaceTravel As Boolean = ep.FriendlyName.IndexOf("Space Travel", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                           ep.FriendlyName.IndexOf("MOONDROP", StringComparison.OrdinalIgnoreCase) >= 0
            Dim score As Integer = ep.SampleRate + If(isPreferredModel, 100000, 0) + If(isSpaceTravel, 250000, 0)
            If score > bestScore Then
                bestScore = score
                bestEp = ep
            End If
        Next

        If bestEp IsNot Nothing Then
            outEndpoint = bestEp
            Return bestEp.Id
        End If
        Return Nothing
    End Function

    Private Function FindQuadCapture12SpeakerOutputId() As String
        Dim endpoints As List(Of AudioEndpointInfo) = GetCachedOutputDeviceInfos()
        Dim audioCfg = MixerHardwareStateStore.Current.Audio

        If audioCfg.PreferBluetoothHeadphonesOutput Then
            Dim btEp As AudioEndpointInfo = Nothing
            Dim btOutId As String = FindHighQualityBluetoothOutputId(btEp)
            If Not String.IsNullOrEmpty(btOutId) Then
                Return btOutId
            End If
        End If

        If Not String.IsNullOrWhiteSpace(audioCfg.PreferredOutputDeviceId) Then
            For Each dev In endpoints
                If dev.Id.Equals(audioCfg.PreferredOutputDeviceId, StringComparison.OrdinalIgnoreCase) AndAlso
                   Not IsBlockedEndpointName(dev.FriendlyName, audioCfg.BlockedOutputKeywords) AndAlso
                   Not (audioCfg.BluetoothMicHighQualityOnly AndAlso dev.IsLowQualityHfp) Then
                    Return dev.Id
                End If
            Next
        End If

        Dim outKeyword As String = If(String.IsNullOrWhiteSpace(audioCfg.PreferredOutputKeyword), "1-2", audioCfg.PreferredOutputKeyword)

        For Each dev In endpoints
            If Not IsBlockedEndpointName(dev.FriendlyName, audioCfg.BlockedOutputKeywords) AndAlso
               dev.FriendlyName.IndexOf(outKeyword, StringComparison.OrdinalIgnoreCase) >= 0 AndAlso
               MatchesAnyHardwareKeyword(dev.FriendlyName, audioCfg.HardwareKeywords) Then
                Return dev.Id
            End If
        Next

        For Each dev In endpoints
            If Not IsBlockedEndpointName(dev.FriendlyName, audioCfg.BlockedOutputKeywords) AndAlso
               dev.FriendlyName.IndexOf(outKeyword, StringComparison.OrdinalIgnoreCase) >= 0 Then
                Return dev.Id
            End If
        Next

        Return Nothing
    End Function

    Public Sub StartEngine(Optional captureDeviceId As String = Nothing, Optional outputDeviceId As String = Nothing, Optional forceDirectMode As Boolean = False)
        If _isDisposed Then Return
        If _uiContext IsNot Nothing AndAlso SynchronizationContext.Current IsNot _uiContext Then
            _uiContext.Post(Sub(st) StartEngine(captureDeviceId, outputDeviceId, forceDirectMode), Nothing)
            Return
        End If
        If SynchronizationContext.Current IsNot Nothing Then
            _uiContext = SynchronizationContext.Current
        End If

        SyncLock _engineLock
            StopEngineInternal()
            RefreshEndpointsCache()

            Try
                Dim audioCfg = MixerHardwareStateStore.Current.Audio
                Dim quad12Id As String = FindQuadCapture12SpeakerOutputId()
                _speakerQuad12Id = If(quad12Id, "")

                If Not String.IsNullOrWhiteSpace(outputDeviceId) Then
                    Try
                        Dim candidateDev As MMDevice = _enumerator.GetDevice(outputDeviceId)
                        If candidateDev IsNot Nothing AndAlso
                           candidateDev.State = DeviceState.Active AndAlso
                           Not IsBlockedEndpointName(candidateDev.FriendlyName, audioCfg.BlockedOutputKeywords) Then
                            _outputDevice = candidateDev
                        Else
                            Try : candidateDev?.Dispose() : Catch : End Try
                        End If
                    Catch
                        _outputDevice = Nothing
                    End Try
                End If

                If _outputDevice Is Nothing Then
                    If Not String.IsNullOrEmpty(quad12Id) Then
                        _outputDevice = _enumerator.GetDevice(quad12Id)
                    Else
                        _outputDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    End If
                End If

                If _outputDevice Is Nothing Then Return
                Volatile.Write(_cachedOutputId, _outputDevice.ID)
                Volatile.Write(_cachedOutputName, _outputDevice.FriendlyName)

                Try
                    If audioCfg.AutoRouteAppsToPrimaryOutput Then
                        WindowsAudioPolicyRouter.ClearPerAppPersistedEndpoints()
                        WindowsAudioPolicyRouter.SanitizeMediaPlayerAudioConfigs(_cachedOutputId)
                        WindowsAudioPolicyRouter.TrySetDefaultRenderEndpoint(_cachedOutputId)
                    End If
                Catch
                End Try

                Try
                    If _outputDevice.AudioEndpointVolume IsNot Nothing Then
                        If _outputDevice.AudioEndpointVolume.Mute Then
                            _outputDevice.AudioEndpointVolume.Mute = False
                        End If
                        If _outputDevice.AudioEndpointVolume.MasterVolumeLevelScalar < 0.1F Then
                            _outputDevice.AudioEndpointVolume.MasterVolumeLevelScalar = 0.85F
                        End If
                    End If
                Catch
                End Try

                Try
                    Dim mixFmt As WaveFormat = _outputDevice.AudioClient?.MixFormat
                    If mixFmt IsNot Nothing AndAlso mixFmt.SampleRate >= 8000 Then
                        _targetSampleRate = mixFmt.SampleRate
                    End If
                Catch
                End Try

                NativeCppAutoBuilderAndInstaller.EnsureAutoCompiledAsync()

                _captureDevice = _enumerator.GetDevice(_outputDevice.ID)
                _cachedCaptureId = ""
                Dim outDiag As BluetoothEndpointDiagnostics = AnalyzeAudioEndpoint(_outputDevice)
                Dim srKhz As String = (_targetSampleRate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                If outDiag.IsHighResLdacCapable Then
                    Dim codecTag As String = If(_targetSampleRate >= 88200, $"LDAC {srKhz} kHz", $"BT Stereo {srKhz} kHz")
                    _cachedCaptureName = $"{_cachedOutputName} [{codecTag}]"
                Else
                    _cachedCaptureName = $"Прямой C++ APO: {_cachedOutputName} ({srKhz} kHz)"
                End If

                _nativeMixer.Init(_targetSampleRate, 2)
                If audioCfg.MicIrPreset = 7 AndAlso
                   Not String.IsNullOrWhiteSpace(audioCfg.MicCustomIrFilePath) AndAlso
                   File.Exists(audioCfg.MicCustomIrFilePath) Then
                    NativeCppAutoBuilderAndInstaller.LoadAndUploadCustomWavIr(audioCfg.MicCustomIrFilePath, _targetSampleRate)
                End If
                SyncChannelsWithProjectModel(userInitiated:=False)
                SyncNativeKernelDspParameters()

                _masterEffects = New EffectChainProvider(New DummySilentProvider(_targetSampleRate))

                Try
                    If _captureDevice IsNot Nothing Then
                        _outputMonitorCapture = New WasapiLoopbackCapture(_captureDevice)
                        AddHandler _outputMonitorCapture.DataAvailable, AddressOf OnOutputMonitorDataAvailable
                        AddHandler _outputMonitorCapture.RecordingStopped, AddressOf OnCaptureRecordingStopped
                        _outputMonitorCapture.StartRecording()
                    End If
                Catch
                    Try : _outputMonitorCapture?.Dispose() : Catch : End Try
                    _outputMonitorCapture = Nothing
                End Try

                If audioCfg.UseAsio Then
                    If Not StartAsioEngine(audioCfg.PreferredAsioDriverName) Then
                        StartQuadCaptureMicEngine()
                    End If
                Else
                    StartQuadCaptureMicEngine()
                End If

                Interlocked.Exchange(_lastDiscoveryTicks, 0)
                TriggerFastSessionSync(forceVolumeWrite:=True)
            Catch
            End Try
        End SyncLock
    End Sub

    Public Sub StopEngine()
        If _uiContext IsNot Nothing AndAlso SynchronizationContext.Current IsNot _uiContext Then
            _uiContext.Send(Sub(st)
                                SyncLock _engineLock
                                    StopEngineInternal()
                                End SyncLock
                            End Sub, Nothing)
            Return
        End If

        SyncLock _engineLock
            StopEngineInternal()
        End SyncLock
    End Sub

    Private Sub StopEngineInternal()
        StopAsioEngine()
        StopQuadCaptureMicEngine()

        Dim monCap As WasapiLoopbackCapture = _outputMonitorCapture
        _outputMonitorCapture = Nothing
        If monCap IsNot Nothing Then
            Try : RemoveHandler monCap.DataAvailable, AddressOf OnOutputMonitorDataAvailable : Catch : End Try
            Try : RemoveHandler monCap.RecordingStopped, AddressOf OnCaptureRecordingStopped : Catch : End Try
            Try : monCap.StopRecording() : Catch : End Try
            Try : monCap.Dispose() : Catch : End Try
        End If

        Dim capDev As MMDevice = _captureDevice
        _captureDevice = Nothing
        If capDev IsNot Nothing Then
            Try : capDev.Dispose() : Catch : End Try
        End If

        Dim outDev As MMDevice = _outputDevice
        _outputDevice = Nothing
        If outDev IsNot Nothing Then
            Try : outDev.Dispose() : Catch : End Try
        End If
    End Sub

    Private _avgLoopLow As Single = 0.05F
    Private _avgLoopMid As Single = 0.05F
    Private _avgLoopHigh As Single = 0.05F
    Private _loopPrevMono As Single = 0.0F

    Private Shared ReadOnly IeeeFloatSubFormatGuid As New Guid("00000003-0000-0010-8000-00aa00389b71")

    Private Shared Function IsWaveFormatIeeeFloat32(wf As WaveFormat) As Boolean
        If wf Is Nothing OrElse wf.BitsPerSample <> 32 Then Return False
        If wf.Encoding = WaveFormatEncoding.IeeeFloat Then Return True
        Dim wfe As WaveFormatExtensible = TryCast(wf, WaveFormatExtensible)
        If wfe IsNot Nothing AndAlso wfe.SubFormat.Equals(IeeeFloatSubFormatGuid) Then
            Return True
        End If
        Return False
    End Function

    Private Sub OnOutputMonitorDataAvailable(sender As Object, e As WaveInEventArgs)
        Dim cap As WasapiLoopbackCapture = _outputMonitorCapture
        If e Is Nothing OrElse e.BytesRecorded <= 0 OrElse e.Buffer Is Nothing OrElse cap Is Nothing Then Return
        Dim wf As WaveFormat = cap.WaveFormat
        If wf Is Nothing Then Return

        Dim buf As Byte() = e.Buffer
        Dim bytesRecorded As Integer = Math.Min(e.BytesRecorded, buf.Length)
        Dim chCount As Integer = Math.Max(1, wf.Channels)
        Dim bytesPerSample As Integer = wf.BitsPerSample \ 8
        If bytesPerSample <= 0 Then Return

        Dim frameStride As Integer = chCount * bytesPerSample
        Dim totalFrames As Integer = bytesRecorded \ frameStride
        If totalFrames <= 0 Then Return

        Dim isFloat32 As Boolean = IsWaveFormatIeeeFloat32(wf)
        Dim sampleRateHz As Single = CSng(Math.Max(8000, wf.SampleRate))
        Dim alphaLow As Single = CSng(1.0 - Math.Exp(-2.0 * Math.PI * 135.0 / sampleRateHz))
        Dim alphaMidHp As Single = CSng(1.0 - Math.Exp(-2.0 * Math.PI * 165.0 / sampleRateHz))
        Dim alphaMidLp As Single = CSng(1.0 - Math.Exp(-2.0 * Math.PI * 2600.0 / sampleRateHz))

        Dim lpL1 As Single = _loopLp250_1
        Dim lpL2 As Single = _loopLp250_2
        Dim hpM1 As Single = _loopHpMid_1
        Dim hpM2 As Single = _loopHpMid_2
        Dim lpM1 As Single = _loopLp3500_1
        Dim lpM2 As Single = _loopLp3500_2
        Dim prevSubPk As Single = _loopPrevSubPeak
        Dim refPeak As Single = _loopRefPeak
        Dim avgLow As Single = _avgLoopLow
        Dim avgMid As Single = _avgLoopMid
        Dim avgHigh As Single = _avgLoopHigh
        Dim prevMono As Single = _loopPrevMono

        Dim barFrames As Integer = Math.Max(256, CInt(sampleRateHz * 0.015F))
        Dim blockMax As Single = 0.0F
        Dim blockLow As Single = 0.0F
        Dim blockMid As Single = 0.0F
        Dim blockHigh As Single = 0.0F

        Dim snap As ProgramMixerChannel() = Volatile.Read(_channelsSnapshot)
        Dim maxActiveSessPeak As Single = 0.0001F
        If snap IsNot Nothing Then
            For cIdx As Integer = 0 To snap.Length - 1
                Dim ch As ProgramMixerChannel = snap(cIdx)
                If ch IsNot Nothing AndAlso Not ch.ProgramKey.Equals("MIC", StringComparison.OrdinalIgnoreCase) Then
                    Dim fdrCurve As Single = If(ch.IsEffectivelyMuted OrElse ch.Fader01 <= 0.001F, 0.0F, ch.Fader01 * ch.Fader01)
                    Dim weightedPk As Single = ch.RawSessionMeterPeak * fdrCurve
                    If weightedPk > maxActiveSessPeak Then
                        maxActiveSessPeak = weightedPk
                    End If
                End If
            Next
        End If

        Dim fIdx As Integer = 0
        While fIdx < totalFrames
            Dim subEnd As Integer = Math.Min(totalFrames - 1, fIdx + barFrames - 1)
            Dim subCnt As Integer = (subEnd - fIdx) + 1

            Dim maxPosL As Single = 0.0F
            Dim maxNegR As Single = 0.0F
            Dim sumSq As Single = 0.0F
            Dim sumSqLow As Single = 0.0F
            Dim sumSqMid As Single = 0.0F
            Dim sumSqHigh As Single = 0.0F

            For f As Integer = fIdx To subEnd
                Dim offset As Integer = f * frameStride
                Dim sampleL As Single = 0.0F
                Dim sampleR As Single = 0.0F

                If isFloat32 Then
                    sampleL = BitConverter.ToSingle(buf, offset)
                    sampleR = If(chCount >= 2, BitConverter.ToSingle(buf, offset + 4), sampleL)
                ElseIf wf.BitsPerSample = 16 Then
                    sampleL = BitConverter.ToInt16(buf, offset) / 32768.0F
                    sampleR = If(chCount >= 2, BitConverter.ToInt16(buf, offset + 2) / 32768.0F, sampleL)
                ElseIf wf.BitsPerSample = 24 Then
                    Dim iL As Integer = (CInt(buf(offset)) Or (CInt(buf(offset + 1)) << 8) Or (CInt(CSByte(buf(offset + 2))) << 16))
                    sampleL = CSng(iL / 8388608.0)
                    If chCount >= 2 Then
                        Dim rOff As Integer = offset + 3
                        Dim iR As Integer = (CInt(buf(rOff)) Or (CInt(buf(rOff + 1)) << 8) Or (CInt(CSByte(buf(rOff + 2))) << 16))
                        sampleR = CSng(iR / 8388608.0)
                    Else
                        sampleR = sampleL
                    End If
                ElseIf wf.BitsPerSample = 32 Then
                    sampleL = CSng(BitConverter.ToInt32(buf, offset) / 2147483648.0)
                    sampleR = If(chCount >= 2, CSng(BitConverter.ToInt32(buf, offset + 4) / 2147483648.0), sampleL)
                End If

                If Single.IsNaN(sampleL) OrElse Single.IsInfinity(sampleL) Then sampleL = 0.0F
                If Single.IsNaN(sampleR) OrElse Single.IsInfinity(sampleR) Then sampleR = 0.0F

                Dim posVal As Single = Math.Max(Math.Max(0.0F, sampleL), Math.Abs(sampleL) * 0.88F)
                Dim negVal As Single = Math.Max(Math.Max(0.0F, -sampleR), Math.Abs(sampleR) * 0.88F)
                If posVal > maxPosL Then maxPosL = posVal
                If negVal > maxNegR Then maxNegR = negVal

                Dim mono As Single = (sampleL + sampleR) * 0.5F
                Dim slewHigh As Single = Math.Abs(mono - prevMono) * 1.35F
                prevMono = mono

                lpL1 += alphaLow * (mono - lpL1)
                lpL2 += alphaLow * (lpL1 - lpL2)

                hpM1 += alphaMidHp * (mono - hpM1)
                Dim midHighPass As Single = mono - hpM1
                lpM1 += alphaMidLp * (midHighPass - lpM1)
                lpM2 += alphaMidLp * (lpM1 - lpM2)

                Dim rawHigh As Single = midHighPass - lpM1
                hpM2 += alphaMidLp * (rawHigh - hpM2)
                Dim cleanHigh As Single = (rawHigh - hpM2) + slewHigh

                Dim bLow As Single = Math.Abs(lpL2)
                Dim bMid As Single = Math.Abs(lpM2)
                Dim bHigh As Single = Math.Abs(cleanHigh)

                sumSq += mono * mono
                sumSqLow += bLow * bLow
                sumSqMid += bMid * bMid
                sumSqHigh += bHigh * bHigh
            Next

            Dim invSubCnt As Single = 1.0F / CSng(subCnt)
            Dim subRms As Single = CSng(Math.Sqrt(sumSq * invSubCnt))
            Dim rawRmsLow As Single = CSng(Math.Sqrt(sumSqLow * invSubCnt)) * 1.65F
            Dim rawRmsMid As Single = CSng(Math.Sqrt(sumSqMid * invSubCnt)) * 1.35F
            Dim rawRmsHigh As Single = CSng(Math.Sqrt(sumSqHigh * invSubCnt)) * 2.1F

            Dim subPeak As Single = Math.Max(maxPosL, maxNegR)
            If subPeak > 0.003F Then
                avgLow = avgLow * 0.96F + rawRmsLow * 0.04F
                avgMid = avgMid * 0.96F + rawRmsMid * 0.04F
                avgHigh = avgHigh * 0.96F + rawRmsHigh * 0.04F
            End If

            Dim meanAvg As Single = Math.Max(0.002F, (avgLow + avgMid + avgHigh) * 0.3333F)
            Dim gainL As Single = Math.Max(0.65F, Math.Min(2.2F, meanAvg / Math.Max(0.002F, avgLow))) * 1.24F
            Dim gainM As Single = Math.Max(0.65F, Math.Min(1.85F, meanAvg / Math.Max(0.002F, avgMid))) * 1.05F
            Dim gainH As Single = Math.Max(0.65F, Math.Min(1.95F, meanAvg / Math.Max(0.002F, avgHigh))) * 1.1F

            Dim rmsLow As Single = rawRmsLow * gainL
            Dim rmsMid As Single = rawRmsMid * gainM
            Dim rmsHigh As Single = rawRmsHigh * gainH

            If subPeak > blockMax Then blockMax = subPeak
            If rmsLow > blockLow Then blockLow = rmsLow
            If rmsMid > blockMid Then blockMid = rmsMid
            If rmsHigh > blockHigh Then blockHigh = rmsHigh

            If subPeak > refPeak Then
                refPeak = subPeak
            Else
                refPeak = Math.Max(0.12F, refPeak * 0.994F)
            End If

            Dim normRms As Single = Math.Min(1.0F, subRms / refPeak)
            If normRms < _loopSlowFloor Then
                _loopSlowFloor = _loopSlowFloor * 0.88F + normRms * 0.12F
            Else
                _loopSlowFloor = _loopSlowFloor * 0.97F + normRms * 0.03F
            End If

            Dim floorCut As Single = _loopSlowFloor * 0.72F
            Dim contrastEnv As Single = Math.Max(0.0F, normRms - floorCut) / Math.Max(0.18F, 1.0F - floorCut)
            Dim shapedContrast As Single = CSng(Math.Pow(Math.Min(1.0F, contrastEnv), 1.65))

            Dim transientAttack As Single = Math.Max(0.0F, (subPeak - prevSubPk) / refPeak)
            Dim kickImpulse As Single = Math.Max(0.0F, (rawRmsLow - avgLow * 0.92F) / Math.Max(0.01F, avgLow))
            Dim hotFlash As Single = Math.Min(1.0F, transientAttack * 2.5F)
            prevSubPk = subPeak * 0.58F + prevSubPk * 0.42F

            Dim dynamicHeight As Single = Math.Min(0.95F,
                (normRms * 0.26F) +
                (shapedContrast * 0.48F) +
                (Math.Min(1.0F, kickImpulse) * 0.24F) +
                (transientAttack * 0.42F))

            Dim stereoTopRatio As Single = If(subPeak > 0.001F, Math.Min(1.15F, maxPosL / subPeak), 1.0F)
            Dim stereoBotRatio As Single = If(subPeak > 0.001F, Math.Min(1.15F, maxNegR / subPeak), 1.0F)

            Dim topEnv As Single = Math.Min(0.95F, dynamicHeight * stereoTopRatio)
            Dim botEnv As Single = Math.Min(0.95F, dynamicHeight * stereoBotRatio)
            Dim envPeak As Single = Math.Max(topEnv, botEnv)

            Dim maxBandRms As Single = Math.Max(0.0001F, Math.Max(rmsLow, Math.Max(rmsMid, rmsHigh)))
            Dim normLow As Single = (rmsLow / maxBandRms) * envPeak
            Dim normMid As Single = (rmsMid / maxBandRms) * envPeak
            Dim normHigh As Single = (rmsHigh / maxBandRms) * envPeak

            If snap IsNot Nothing Then
                For cIdx As Integer = 0 To snap.Length - 1
                    Dim ch As ProgramMixerChannel = snap(cIdx)
                    If ch Is Nothing OrElse ch.ProgramKey.Equals("MIC", StringComparison.OrdinalIgnoreCase) Then Continue For

                    Dim isMutedOrZero As Boolean = (ch.IsEffectivelyMuted OrElse ch.Fader01 <= 0.001F)
                    Dim fdrCurve As Single = If(isMutedOrZero, 0.0F, ch.Fader01 * ch.Fader01)
                    Dim sessPk As Single = ch.RawSessionMeterPeak * fdrCurve

                    If sessPk > 0.0012F AndAlso subPeak > 0.0008F Then
                        Dim share As Single = CSng(Math.Sqrt(Math.Min(1.0F, sessPk / maxActiveSessPeak)))
                        ch.PushExactWaveBar(
                            topEnv * share,
                            botEnv * share,
                            normLow * share,
                            normMid * share,
                            normHigh * share,
                            hotFlash * share,
                            False)
                    ElseIf Not isMutedOrZero AndAlso ch.RawSessionMeterPeak <= 0.0012F Then
                        ' Обнуляем волну только когда сам источник молчит, а не когда канал в MUTE
                        ' (при MUTE волну продолжает питать пре-фейдерный UpdateMetersFromStereoSessionPeak)
                        ch.PushExactWaveBar(0.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F, False)
                    End If
                Next
            End If

            fIdx = subEnd + 1
        End While

        _loopLp250_1 = If(Single.IsNaN(lpL1) OrElse Single.IsInfinity(lpL1), 0.0F, lpL1)
        _loopLp250_2 = If(Single.IsNaN(lpL2) OrElse Single.IsInfinity(lpL2), 0.0F, lpL2)
        _loopHpMid_1 = If(Single.IsNaN(hpM1) OrElse Single.IsInfinity(hpM1), 0.0F, hpM1)
        _loopHpMid_2 = If(Single.IsNaN(hpM2) OrElse Single.IsInfinity(hpM2), 0.0F, hpM2)
        _loopLp3500_1 = If(Single.IsNaN(lpM1) OrElse Single.IsInfinity(lpM1), 0.0F, lpM1)
        _loopLp3500_2 = If(Single.IsNaN(lpM2) OrElse Single.IsInfinity(lpM2), 0.0F, lpM2)
        _loopPrevSubPeak = prevSubPk
        _loopRefPeak = If(Single.IsNaN(refPeak) OrElse Single.IsInfinity(refPeak), 0.25F, refPeak)
        _avgLoopLow = If(Single.IsNaN(avgLow) OrElse Single.IsInfinity(avgLow), 0.05F, avgLow)
        _avgLoopMid = If(Single.IsNaN(avgMid) OrElse Single.IsInfinity(avgMid), 0.05F, avgMid)
        _avgLoopHigh = If(Single.IsNaN(avgHigh) OrElse Single.IsInfinity(avgHigh), 0.05F, avgHigh)
        _loopPrevMono = If(Single.IsNaN(prevMono) OrElse Single.IsInfinity(prevMono), 0.0F, prevMono)

        Dim maxBand As Single = Math.Max(0.001F, Math.Max(blockLow, Math.Max(blockMid, blockHigh)))
        If blockMax > 0.002F Then
            Volatile.Write(_liveSpecRatioLow, Math.Min(1.0F, blockLow / maxBand))
            Volatile.Write(_liveSpecRatioMid, Math.Min(1.0F, blockMid / maxBand))
            Volatile.Write(_liveSpecRatioHigh, Math.Min(1.0F, blockHigh / maxBand))
        End If

        Dim prevOutPk As Single = Volatile.Read(_liveOutputHardwarePeak)
        Volatile.Write(_liveOutputHardwarePeak, Math.Max(blockMax, prevOutPk * 0.8F))
    End Sub

    Private Function FindHighQualityBluetoothMicDevice(ByRef outRejectedLowQualityName As String) As MMDevice
        outRejectedLowQualityName = ""
        Dim audioCfg = MixerHardwareStateStore.Current.Audio
        If Not audioCfg.UseBluetoothMic Then Return Nothing

        Dim minRate As Integer = Math.Max(16000, audioCfg.MinBluetoothMicSampleRateHz)

        Try
            For Each dev In _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                Dim keepDevice As Boolean = False
                Try
                    Dim diag As BluetoothEndpointDiagnostics = AnalyzeAudioEndpoint(dev)
                    If diag.IsBluetooth Then
                        If audioCfg.BluetoothMicHighQualityOnly AndAlso (diag.SampleRate < minRate OrElse diag.IsLowQualityHfp) Then
                            outRejectedLowQualityName = $"{diag.FriendlyName} ({diag.SampleRate \ 1000} kHz HFP заблокирован)"
                        Else
                            keepDevice = True
                            Return dev
                        End If
                    End If
                Catch
                Finally
                    If Not keepDevice Then
                        Try : dev.Dispose() : Catch : End Try
                    End If
                End Try
            Next
        Catch
        End Try
        Return Nothing
    End Function

    Private Function FindQuadCapture12MicDevice() As MMDevice
        Dim audioCfg = MixerHardwareStateStore.Current.Audio

        Dim rejectedBtMic As String = ""
        Dim hqBtMic As MMDevice = FindHighQualityBluetoothMicDevice(rejectedBtMic)
        If hqBtMic IsNot Nothing Then
            _lastLdacGuardStatus = $"BT HD Mic активен ({hqBtMic.FriendlyName})"
            Return hqBtMic
        ElseIf Not String.IsNullOrEmpty(rejectedBtMic) Then
            _lastLdacGuardStatus = $"LDAC Guard: {rejectedBtMic} -> активен студийный микрофон"
        Else
            _lastLdacGuardStatus = ""
        End If

        Dim micKw As String = If(String.IsNullOrWhiteSpace(audioCfg.PreferredMicKeyword), "1-2", audioCfg.PreferredMicKeyword)
        Dim mainKw As String = If(String.IsNullOrWhiteSpace(audioCfg.PreferredMainCaptureKeyword), "MAIN", audioCfg.PreferredMainCaptureKeyword)

        Try
            For Each dev In _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                Dim fname As String = ""
                Try : fname = dev.FriendlyName : Catch : End Try
                If fname.IndexOf(micKw, StringComparison.OrdinalIgnoreCase) >= 0 AndAlso
                   MatchesAnyHardwareKeyword(fname, audioCfg.HardwareKeywords) AndAlso
                   fname.IndexOf(mainKw, StringComparison.OrdinalIgnoreCase) < 0 Then
                    Return dev
                End If
                Try : dev.Dispose() : Catch : End Try
            Next

            Dim defMic As MMDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            If defMic IsNot Nothing Then
                Dim diag As BluetoothEndpointDiagnostics = AnalyzeAudioEndpoint(defMic)
                If Not (audioCfg.BluetoothMicHighQualityOnly AndAlso diag.IsBluetooth AndAlso diag.IsLowQualityHfp) Then
                    Return defMic
                End If
                Try : defMic.Dispose() : Catch : End Try
            End If
        Catch
        End Try
        Return Nothing
    End Function

    Private _engineRestartScheduled As Integer = 0
    Private _isInstallingApo As Integer = 0

    Private Sub OnCaptureRecordingStopped(sender As Object, e As StoppedEventArgs)
        If _isDisposed OrElse Volatile.Read(_isInstallingApo) <> 0 Then Return
        If Interlocked.CompareExchange(_engineRestartScheduled, 1, 0) = 0 Then
            ThreadPool.QueueUserWorkItem(
                Sub()
                    Try
                        Thread.Sleep(800)
                        If Not _isDisposed AndAlso Volatile.Read(_isInstallingApo) = 0 Then
                            Dim aCfg = MixerHardwareStateStore.Current.Audio
                            Dim prefOut As String = If(aCfg.PreferBluetoothHeadphonesOutput OrElse String.IsNullOrWhiteSpace(aCfg.PreferredOutputDeviceId),
                                                       Nothing,
                                                       aCfg.PreferredOutputDeviceId)
                            StartEngine(Nothing, prefOut, False)
                        End If
                    Catch
                    Finally
                        Interlocked.Exchange(_engineRestartScheduled, 0)
                    End Try
                End Sub)
        End If
    End Sub

    Private Function FindQuadCaptureMainDevice() As MMDevice
        Dim audioCfg = MixerHardwareStateStore.Current.Audio
        Dim mainKw As String = If(String.IsNullOrWhiteSpace(audioCfg.PreferredMainCaptureKeyword), "MAIN", audioCfg.PreferredMainCaptureKeyword)

        Try
            For Each dev In _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                Dim fname As String = ""
                Try : fname = dev.FriendlyName : Catch : End Try
                If fname.IndexOf(mainKw, StringComparison.OrdinalIgnoreCase) >= 0 AndAlso
                   MatchesAnyHardwareKeyword(fname, audioCfg.HardwareKeywords) Then
                    Return dev
                End If
                Try : dev.Dispose() : Catch : End Try
            Next
        Catch
        End Try
        Return Nothing
    End Function

    Private Sub EnsureMicChannelInModel()
        If _model?.Tracks Is Nothing Then Return
        Dim added As Boolean = False
        SyncLock _channelsLock
            SyncLock _model.SyncRoot
                Dim existingMic As ProgramMixerChannel = _programChannels.Values.FirstOrDefault(
                    Function(c) c.ProgramKey.Equals("MIC", StringComparison.OrdinalIgnoreCase))

                If existingMic Is Nothing Then
                    Dim micProf As ChannelProfileConfig = MixerHardwareStateStore.GetOrCreateChannelProfile("MAIN MIC")
                    Dim micTrack As New TrackModel() With {
                        .Name = "MAIN MIC",
                        .Type = TargetFormatType.Audio,
                        .Trim = micProf.Trim,
                        .EqHi = micProf.EqHi,
                        .EqMid = micProf.EqMid,
                        .EqLow = micProf.EqLow,
                        .ColorFx = micProf.ColorFx,
                        .ColorFxMode = micProf.ColorFxMode,
                        .WaveformZoom = micProf.WaveformZoom,
                        .IsMuted = micProf.IsMuted,
                        .IsSolo = micProf.IsSolo
                    }
                    micTrack.Clips.Add(New ClipModel() With {
                        .TimelineStart = TimeSpan.Zero,
                        .TimelineEnd = TimeSpan.FromSeconds(60),
                        .Volume = micProf.Fader
                    })
                    _model.Tracks.Insert(0, micTrack)

                    existingMic = New ProgramMixerChannel(micTrack.Id, "MIC", "MAIN MIC")
                    existingMic.SetChannelFader(micProf.Fader, userInitiated:=False)
                    existingMic.SetEqAndTrim(
                        yoump.MediaParameterBroker.ValueToDecibels(micProf.EqLow),
                        yoump.MediaParameterBroker.ValueToDecibels(micProf.EqMid),
                        yoump.MediaParameterBroker.ValueToDecibels(micProf.EqHi),
                        micProf.Trim * 2.0F,
                        userInitiated:=False)
                    existingMic.SetColorFilter(micProf.ColorFx, userInitiated:=False)
                    existingMic.ColorFxMode = micProf.ColorFxMode
                    existingMic.IsMuted = micProf.IsMuted
                    existingMic.IsSolo = micProf.IsSolo
                    _programChannels(micTrack.Id) = existingMic

                    Dim ordered As New List(Of ProgramMixerChannel)(_model.Tracks.Count)
                    For idx As Integer = 0 To _model.Tracks.Count - 1
                        Dim ch As ProgramMixerChannel = Nothing
                        If _programChannels.TryGetValue(_model.Tracks(idx).Id, ch) AndAlso ch IsNot Nothing Then
                            ordered.Add(ch)
                        End If
                    Next
                    Volatile.Write(_channelsSnapshot, ordered.ToArray())
                    RecalculateEffectiveMuteStates()
                    added = True
                End If
            End SyncLock
        End SyncLock

        If added Then
            If _uiContext IsNot Nothing Then
                _uiContext.Post(Sub(st) _model.NotifyStateChanged(), Nothing)
            Else
                _model.NotifyStateChanged()
            End If
        End If
    End Sub

    Private Sub StartQuadCaptureMicEngine()
        StopQuadCaptureMicEngine()
        Try
            Dim audioCfg = MixerHardwareStateStore.Current.Audio
            _micCaptureDevice = FindQuadCapture12MicDevice()
            _mainCaptureDevice = FindQuadCaptureMainDevice()

            If _mainCaptureDevice IsNot Nothing Then
                Try
                    If _mainCaptureDevice.AudioEndpointVolume IsNot Nothing Then
                        _mainCaptureDevice.AudioEndpointVolume.Mute = False
                        _mainCaptureDevice.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0F
                    End If
                Catch
                End Try
            End If

            If _micCaptureDevice Is Nothing Then Return

            Try
                If _micCaptureDevice.AudioEndpointVolume IsNot Nothing Then
                    _micCaptureDevice.AudioEndpointVolume.Mute = False
                    _micCaptureDevice.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0F
                End If
            Catch
            End Try

            EnsureMicChannelInModel()

            Dim capLatency As Integer = Math.Max(3, Math.Min(100, audioCfg.MicCaptureLatencyMs))
            Dim outLatency As Integer = Math.Max(5, Math.Min(200, audioCfg.MicOutputLatencyMs))
            Dim bufDurMs As Integer = Math.Max(30, Math.Min(1000, audioCfg.MicBufferDurationMs))

            _micCapture = New WasapiCapture(_micCaptureDevice, True, capLatency)

            If _outputDevice IsNot Nothing AndAlso _micCapture.WaveFormat IsNot Nothing Then
                Try
                    Dim micSr As Integer = Math.Max(8000, _micCapture.WaveFormat.SampleRate)
                    Dim outSr As Integer = Math.Max(8000, _targetSampleRate)
                    Dim floatStereoFmt As WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(micSr, 2)
                    _micWaveProvider = New BufferedWaveProvider(floatStereoFmt) With {
                        .DiscardOnBufferOverflow = True,
                        .BufferDuration = TimeSpan.FromMilliseconds(bufDurMs),
                        .ReadFully = True
                    }
                    _micOutputDevice = _enumerator.GetDevice(_outputDevice.ID)
                    _micWaveOut = New WasapiOut(_micOutputDevice, AudioClientShareMode.Shared, True, outLatency)
                    If micSr <> outSr Then
                        Dim resampler As New NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(_micWaveProvider.ToSampleProvider(), outSr)
                        _micWaveOut.Init(New NAudio.Wave.SampleProviders.SampleToWaveProvider(resampler))
                    Else
                        _micWaveOut.Init(_micWaveProvider)
                    End If
                    If Volatile.Read(_micMonitoringEnabled) Then
                        _micWaveOut.Play()
                    End If
                Catch
                    Try : _micWaveOut?.Dispose() : Catch : End Try
                    _micWaveOut = Nothing
                    Try : _micOutputDevice?.Dispose() : Catch : End Try
                    _micOutputDevice = Nothing
                    _micWaveProvider = Nothing
                End Try
            End If

            AddHandler _micCapture.DataAvailable, AddressOf OnMicDataAvailable
            AddHandler _micCapture.RecordingStopped, AddressOf OnCaptureRecordingStopped
            _micCapture.StartRecording()
        Catch
            StopQuadCaptureMicEngine()
        End Try
    End Sub

    Private Sub StopQuadCaptureMicEngine()
        Dim micCap As WasapiCapture = _micCapture
        _micCapture = Nothing
        If micCap IsNot Nothing Then
            Try : RemoveHandler micCap.DataAvailable, AddressOf OnMicDataAvailable : Catch : End Try
            Try : RemoveHandler micCap.RecordingStopped, AddressOf OnCaptureRecordingStopped : Catch : End Try
            Try : micCap.StopRecording() : Catch : End Try
            Try : micCap.Dispose() : Catch : End Try
        End If

        Dim micOut As WasapiOut = _micWaveOut
        _micWaveOut = Nothing
        If micOut IsNot Nothing Then
            Try : micOut.Stop() : Catch : End Try
            Try : micOut.Dispose() : Catch : End Try
        End If

        Dim micOutDev As MMDevice = _micOutputDevice
        _micOutputDevice = Nothing
        If micOutDev IsNot Nothing Then
            Try : micOutDev.Dispose() : Catch : End Try
        End If

        _micWaveProvider = Nothing

        Dim micDev As MMDevice = _micCaptureDevice
        _micCaptureDevice = Nothing
        If micDev IsNot Nothing Then
            Try : micDev.Dispose() : Catch : End Try
        End If

        Dim mainDev As MMDevice = _mainCaptureDevice
        _mainCaptureDevice = Nothing
        If mainDev IsNot Nothing Then
            Try : mainDev.Dispose() : Catch : End Try
        End If
    End Sub

    Public Function StartAsioEngine(Optional preferredDriver As String = Nothing) As Boolean
        StopAsioEngine()
        StopQuadCaptureMicEngine()

        Dim drivers As List(Of String) = GetAvailableAsioDrivers()
        If drivers.Count = 0 Then Return False

        Dim audioCfg = MixerHardwareStateStore.Current.Audio
        Dim targetDriver As String = ""

        If Not String.IsNullOrWhiteSpace(preferredDriver) Then
            For Each d In drivers
                If d.Equals(preferredDriver, StringComparison.OrdinalIgnoreCase) OrElse
                   d.IndexOf(preferredDriver, StringComparison.OrdinalIgnoreCase) >= 0 Then
                    targetDriver = d
                    Exit For
                End If
            Next
        End If

        If String.IsNullOrEmpty(targetDriver) AndAlso audioCfg.HardwareKeywords IsNot Nothing Then
            For Each kw In audioCfg.HardwareKeywords
                If String.IsNullOrWhiteSpace(kw) Then Continue For
                For Each d In drivers
                    If d.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 Then
                        targetDriver = d
                        Exit For
                    End If
                Next
                If Not String.IsNullOrEmpty(targetDriver) Then Exit For
            Next
        End If

        If String.IsNullOrEmpty(targetDriver) Then
            targetDriver = drivers(0)
        End If

        If _uiContext IsNot Nothing AndAlso SynchronizationContext.Current IsNot _uiContext Then
            Dim startedOnUi As Boolean = False
            _uiContext.Send(Sub(st)
                                startedOnUi = StartAsioInternalOnStaThread(targetDriver)
                            End Sub, Nothing)
            Return startedOnUi
        Else
            Return StartAsioInternalOnStaThread(targetDriver)
        End If
    End Function

    Private Function StartAsioInternalOnStaThread(driverName As String) As Boolean
        Try
            Dim audioCfg = MixerHardwareStateStore.Current.Audio
            _asioOut = New AsioOut(driverName)

            Dim availIn As Integer = _asioOut.DriverInputChannelCount
            Dim availOut As Integer = _asioOut.DriverOutputChannelCount
            Dim recChannels As Integer = Math.Min(2, Math.Max(0, availIn))
            Dim outChannels As Integer = Math.Min(2, Math.Max(0, availOut))

            If availIn > 2 AndAlso audioCfg.AsioInputChannelOffset >= 0 AndAlso (audioCfg.AsioInputChannelOffset + recChannels) <= availIn Then
                _asioOut.InputChannelOffset = audioCfg.AsioInputChannelOffset
            End If

            If availOut > 2 AndAlso audioCfg.AsioOutputChannelOffset >= 0 AndAlso (audioCfg.AsioOutputChannelOffset + outChannels) <= availOut Then
                _asioOut.ChannelOffset = audioCfg.AsioOutputChannelOffset
            End If

            EnsureMicChannelInModel()

            Dim candidateRates As Integer() = {
                If(_targetSampleRate >= 8000, _targetSampleRate, 48000),
                48000,
                44100,
                96000
            }
            Dim asioInitialized As Boolean = False
            For Each candSr As Integer In candidateRates.Distinct()
                Try
                    _asioOutputProvider = New AsioDirectSampleProvider(Me, candSr, Math.Max(1, outChannels))
                    If outChannels > 0 AndAlso recChannels > 0 Then
                        _asioOut.InitRecordAndPlayback(New NAudio.Wave.SampleProviders.SampleToWaveProvider(_asioOutputProvider), recChannels, candSr)
                    ElseIf recChannels > 0 Then
                        _asioOut.InitRecordAndPlayback(Nothing, recChannels, candSr)
                    ElseIf outChannels > 0 Then
                        _asioOut.Init(New NAudio.Wave.SampleProviders.SampleToWaveProvider(_asioOutputProvider))
                    Else
                        Exit For
                    End If
                    _targetSampleRate = candSr
                    _nativeMixer.Init(_targetSampleRate, 2)
                    asioInitialized = True
                    Exit For
                Catch
                End Try
            Next

            If Not asioInitialized Then
                _asioOut.Dispose()
                _asioOut = Nothing
                Return False
            End If

            AddHandler _asioOut.AudioAvailable, AddressOf OnAsioAudioAvailable
            _asioOut.Play()

            _asioActiveDriverName = driverName
            _cachedCaptureName = $"ASIO: {driverName} [Ультра-низкая задержка]"
            audioCfg.PreferredAsioDriverName = driverName
            Return True
        Catch
            StopAsioEngine()
            Return False
        End Try
    End Function

    Public Sub StopAsioEngine()
        Dim aOut As AsioOut = Interlocked.Exchange(_asioOut, Nothing)
        _asioActiveDriverName = ""
        _asioOutputProvider = Nothing
        SyncLock _asioBufferLock
            _asioMonitorSamplesReady = 0
        End SyncLock

        If aOut IsNot Nothing Then
            Dim cleanupAction As Action =
                Sub()
                    Try : RemoveHandler aOut.AudioAvailable, AddressOf OnAsioAudioAvailable : Catch : End Try
                    Try : aOut.Stop() : Catch : End Try
                    Try : aOut.Dispose() : Catch : End Try
                End Sub

            If _uiContext IsNot Nothing AndAlso SynchronizationContext.Current IsNot _uiContext Then
                Try
                    _uiContext.Send(Sub(st) cleanupAction(), Nothing)
                Catch
                    cleanupAction()
                End Try
            Else
                cleanupAction()
            End If
        End If
    End Sub

    Private Sub OnAsioAudioAvailable(sender As Object, e As AsioAudioAvailableEventArgs)
        If e Is Nothing OrElse e.SamplesPerBuffer <= 0 Then Return

        Dim frameCount As Integer = e.SamplesPerBuffer
        Dim actualInChannels As Integer = If(e.InputBuffers IsNot Nothing, e.InputBuffers.Length, 0)
        If actualInChannels <= 0 Then Return

        Dim totalSamples As Integer = frameCount * actualInChannels
        If _asioInterleavedBuffer.Length < totalSamples Then
            _asioInterleavedBuffer = New Single(Math.Max(totalSamples, 4096) - 1) {}
        End If

        Try
            e.GetAsInterleavedSamples(_asioInterleavedBuffer)
        Catch
            Return
        End Try

        Dim snap As ProgramMixerChannel() = Volatile.Read(_channelsSnapshot)
        Dim micCh As ProgramMixerChannel = Nothing
        If snap IsNot Nothing Then
            For i As Integer = 0 To snap.Length - 1
                If snap(i) IsNot Nothing AndAlso snap(i).ProgramKey.Equals("MIC", StringComparison.OrdinalIgnoreCase) Then
                    micCh = snap(i)
                    Exit For
                End If
            Next
        End If
        If micCh Is Nothing Then Return
        micCh.IsApoPostProcessedPeak = False
        micCh.LastSeenActiveTicks = Environment.TickCount64

        Dim sampleRateHz As Single = CSng(Math.Max(8000, _targetSampleRate))
        Dim alphaLow As Single = CSng(1.0 - Math.Exp(-2.0 * Math.PI * 130.0 / sampleRateHz))
        Dim alphaMid As Single = CSng(1.0 - Math.Exp(-2.0 * Math.PI * 2500.0 / sampleRateHz))
        Dim lpL1 As Single = _micSplitLp250
        Dim lpL2 As Single = _micSplitLp250_2
        Dim hpM1 As Single = _micHpMid_1
        Dim lpM1 As Single = _micSplitLp3500
        Dim lpM2 As Single = _micSplitLp3500_2
        Dim prevSubPk As Single = _micPrevSubPeak
        Dim refPeak As Single = _micRefPeak
        Dim gate As Single = Math.Max(0.0F, MixerHardwareStateStore.Current.Audio.MicNoiseGateThreshold)
        Dim xlrMode As Integer = MixerHardwareStateStore.Current.Audio.MicXlrInputMode

        Dim maxPos As Single = 0.0F
        Dim maxNeg As Single = 0.0F
        Dim sumLow As Single = 0.0F
        Dim sumMid As Single = 0.0F
        Dim sumHigh As Single = 0.0F

        For f As Integer = 0 To frameCount - 1
            Dim baseIdx As Integer = f * actualInChannels
            Dim sampleL As Single = _asioInterleavedBuffer(baseIdx)
            Dim sampleR As Single = If(actualInChannels >= 2, _asioInterleavedBuffer(baseIdx + 1), sampleL)

            If Single.IsNaN(sampleL) OrElse Single.IsInfinity(sampleL) Then sampleL = 0.0F
            If Single.IsNaN(sampleR) OrElse Single.IsInfinity(sampleR) Then sampleR = 0.0F

            Dim mono As Single
            Select Case xlrMode
                Case 1 : mono = sampleL
                Case 2 : mono = sampleR
                Case 3 : mono = (sampleL + sampleR) * 0.707106769F
                Case Else : mono = If(Math.Abs(sampleL) >= Math.Abs(sampleR), sampleL, sampleR)
            End Select
            Dim posV As Single = Math.Max(Math.Max(0.0F, mono), Math.Abs(mono) * 0.85F)
            Dim negV As Single = Math.Max(Math.Max(0.0F, -mono), Math.Abs(mono) * 0.85F)
            If posV > maxPos Then maxPos = posV
            If negV > maxNeg Then maxNeg = negV

            lpL1 += alphaLow * (mono - lpL1)
            lpL2 += alphaLow * (lpL1 - lpL2)

            Dim hp1 As Single = mono - lpL1
            hpM1 += alphaLow * (hp1 - hpM1)
            Dim cleanNoBass As Single = hp1 - hpM1

            lpM1 += alphaMid * (cleanNoBass - lpM1)
            lpM2 += alphaMid * (lpM1 - lpM2)

            Dim bLow As Single = Math.Abs(lpL2)
            Dim bMid As Single = Math.Abs(lpM2)
            Dim bHigh As Single = Math.Abs(cleanNoBass - lpM1)

            sumLow += bLow * bLow
            sumMid += bMid * bMid
            sumHigh += bHigh * bHigh
        Next

        _micSplitLp250 = If(Single.IsNaN(lpL1) OrElse Single.IsInfinity(lpL1), 0.0F, lpL1)
        _micSplitLp250_2 = If(Single.IsNaN(lpL2) OrElse Single.IsInfinity(lpL2), 0.0F, lpL2)
        _micHpMid_1 = If(Single.IsNaN(hpM1) OrElse Single.IsInfinity(hpM1), 0.0F, hpM1)
        _micSplitLp3500 = If(Single.IsNaN(lpM1) OrElse Single.IsInfinity(lpM1), 0.0F, lpM1)
        _micSplitLp3500_2 = If(Single.IsNaN(lpM2) OrElse Single.IsInfinity(lpM2), 0.0F, lpM2)

        Dim invCnt As Single = 1.0F / CSng(frameCount)
        Dim rmsLow As Single = CSng(Math.Sqrt(sumLow * invCnt)) * 1.25F
        Dim rmsMid As Single = CSng(Math.Sqrt(sumMid * invCnt)) * 1.65F
        Dim rmsHigh As Single = CSng(Math.Sqrt(sumHigh * invCnt)) * 2.85F

        Dim subPk As Single = Math.Max(maxPos, maxNeg)
        Dim visualMicGate As Single = Math.Max(0.0025F, gate * 1.25F)

        If subPk >= visualMicGate Then
            _micGateHoldCount = Math.Min(6, _micGateHoldCount + 1)
        Else
            _micGateHoldCount = Math.Max(0, _micGateHoldCount - 2)
        End If

        If _micGateHoldCount < 2 Then
            refPeak = Math.Max(0.38F, refPeak * 0.995F)
            micCh.PushExactWaveBar(0.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F, True)
        Else
            If subPk > refPeak Then
                refPeak = subPk
            Else
                refPeak = Math.Max(0.38F, refPeak * 0.992F)
            End If

            Dim normPos As Single = Math.Min(0.96F, CSng(Math.Pow(Math.Min(1.0F, maxPos / refPeak), 1.55)))
            Dim normNeg As Single = Math.Min(0.96F, CSng(Math.Pow(Math.Min(1.0F, maxNeg / refPeak), 1.55)))
            Dim envP As Single = Math.Max(normPos, normNeg)

            Dim trHot As Single = Math.Max(0.0F, (subPk - prevSubPk) / refPeak) * 2.4F
            prevSubPk = subPk * 0.72F + prevSubPk * 0.28F
            Dim maxB As Single = Math.Max(0.0001F, Math.Max(rmsLow, Math.Max(rmsMid, rmsHigh)))

            micCh.PushExactWaveBar(
                normPos,
                normNeg,
                (rmsLow / maxB) * envP,
                (rmsMid / maxB) * envP,
                (rmsHigh / maxB) * envP,
                Math.Min(1.0F, trHot),
                True)
        End If

        _micPrevSubPeak = prevSubPk
        _micRefPeak = If(Single.IsNaN(refPeak) OrElse Single.IsInfinity(refPeak), 0.38F, refPeak)

        If Volatile.Read(_micMonitoringEnabled) AndAlso Not micCh.IsEffectivelyMuted Then
            Dim stereoSamples As Integer = frameCount * 2
            SyncLock _asioBufferLock
                If _asioMonitorSamplesReady > 8192 Then
                    _asioMonitorSamplesReady = 0
                End If
                Dim requiredLen As Integer = _asioMonitorSamplesReady + stereoSamples
                If _asioProcessedMonitorBuffer.Length < Math.Max(requiredLen, 8192) Then
                    Array.Resize(_asioProcessedMonitorBuffer, Math.Max(requiredLen * 2, 8192))
                End If

                Dim writeOffset As Integer = _asioMonitorSamplesReady
                Dim chunkBuf(stereoSamples - 1) As Single
                Dim xlrMonMode As Integer = MixerHardwareStateStore.Current.Audio.MicXlrInputMode
                If actualInChannels = 1 Then
                    For f As Integer = 0 To frameCount - 1
                        Dim s As Single = _asioInterleavedBuffer(f)
                        If Single.IsNaN(s) OrElse Single.IsInfinity(s) Then s = 0.0F
                        chunkBuf(f * 2) = s
                        chunkBuf(f * 2 + 1) = s
                    Next
                Else
                    For f As Integer = 0 To frameCount - 1
                        Dim baseIdx As Integer = f * actualInChannels
                        Dim sL As Single = _asioInterleavedBuffer(baseIdx)
                        Dim sR As Single = _asioInterleavedBuffer(baseIdx + 1)
                        If Single.IsNaN(sL) OrElse Single.IsInfinity(sL) Then sL = 0.0F
                        If Single.IsNaN(sR) OrElse Single.IsInfinity(sR) Then sR = 0.0F
                        Select Case xlrMonMode
                            Case 1
                                chunkBuf(f * 2) = sL
                                chunkBuf(f * 2 + 1) = sL
                            Case 2
                                chunkBuf(f * 2) = sR
                                chunkBuf(f * 2 + 1) = sR
                            Case 3
                                Dim m As Single = (sL + sR) * 0.707106769F
                                chunkBuf(f * 2) = m
                                chunkBuf(f * 2 + 1) = m
                            Case Else
                                chunkBuf(f * 2) = sL
                                chunkBuf(f * 2 + 1) = sR
                        End Select
                    Next
                End If

                If _nativeMixer IsNot Nothing AndAlso _nativeMixer.IsAvailable Then
                    Dim audioCfg = MixerHardwareStateStore.Current.Audio
                    Dim micStudioOn As Boolean = audioCfg.MicStudioProcessingEnabled
                    Dim limiterOn As Boolean = audioCfg.LimiterEnabled
                    Dim micIrPreset As Integer = Math.Max(0, Math.Min(7, audioCfg.MicIrPreset))
                    Dim masterVol01 As Single = Math.Max(0.0F, Math.Min(1.0F, Volatile.Read(_volume) / 100.0F))
                    Dim isMasterZero As Boolean = (masterVol01 <= 0.001F)
                    Dim effMicFader As Single = If(micCh.IsEffectivelyMuted OrElse isMasterZero, 0.0F, micCh.Fader01)

                    Dim packedFlags As Integer = If(limiterOn, 1, 0) Or ((micCh.ColorFxMode And 3) << 1) Or ((micIrPreset And 7) << 5)
                    If Not micStudioOn Then packedFlags = packedFlags Or 8
                    If micCh.IsEffectivelyMuted OrElse effMicFader <= 0.001F Then packedFlags = packedFlags Or 16

                    Dim dspParams As New YoumpMixerParams() With {
                        .trimGain = micCh.Trim01 * 2.0F,
                        .eqLowDb = yoump.MediaParameterBroker.ValueToDecibels(micCh.Low01),
                        .eqMidDb = yoump.MediaParameterBroker.ValueToDecibels(micCh.Mid01),
                        .eqHighDb = yoump.MediaParameterBroker.ValueToDecibels(micCh.Hi01),
                        .colorKnob = micCh.Color01,
                        .channelFader = effMicFader,
                        .masterVolume = masterVol01,
                        .balance = -1.0F,
                        .limiterEnabled = packedFlags
                    }
                    Dim outMeters As YoumpMixerMeters = Nothing
                    Try
                        NativeDspMethods.ProcessChannel(0, chunkBuf, frameCount, 2, dspParams, outMeters)
                    Catch
                    End Try
                End If

                Array.Copy(chunkBuf, 0, _asioProcessedMonitorBuffer, writeOffset, stereoSamples)
                _asioMonitorSamplesReady = writeOffset + stereoSamples
            End SyncLock
        Else
            SyncLock _asioBufferLock
                _asioMonitorSamplesReady = 0
            End SyncLock
        End If
    End Sub

    Friend Function ReadAsioMonitorSamples(buffer() As Single, offset As Integer, count As Integer) As Integer
        SyncLock _asioBufferLock
            Dim readyStereoSamples As Integer = _asioMonitorSamplesReady

            If readyStereoSamples > 0 AndAlso Volatile.Read(_micMonitoringEnabled) Then
                Dim outCh As Integer = If(_asioOutputProvider?.WaveFormat IsNot Nothing, _asioOutputProvider.WaveFormat.Channels, 2)
                If outCh = 1 Then
                    Dim availFrames As Integer = Math.Min(count, Math.Min(readyStereoSamples, _asioProcessedMonitorBuffer.Length) \ 2)
                    For f As Integer = 0 To availFrames - 1
                        Dim sL As Single = _asioProcessedMonitorBuffer(f * 2)
                        Dim sR As Single = _asioProcessedMonitorBuffer(f * 2 + 1)
                        buffer(offset + f) = (sL + sR) * 0.5F
                    Next
                    Dim consumedStereo As Integer = availFrames * 2
                    Dim remainingStereo As Integer = readyStereoSamples - consumedStereo
                    If remainingStereo > 0 Then
                        Array.Copy(_asioProcessedMonitorBuffer, consumedStereo, _asioProcessedMonitorBuffer, 0, remainingStereo)
                    End If
                    _asioMonitorSamplesReady = Math.Max(0, remainingStereo)

                    If availFrames < count Then
                        Array.Clear(buffer, offset + availFrames, count - availFrames)
                    End If
                Else
                    Dim toCopy As Integer = Math.Min(count, Math.Min(readyStereoSamples, _asioProcessedMonitorBuffer.Length))
                    Array.Copy(_asioProcessedMonitorBuffer, 0, buffer, offset, toCopy)
                    Dim remainingStereo As Integer = readyStereoSamples - toCopy
                    If remainingStereo > 0 Then
                        Array.Copy(_asioProcessedMonitorBuffer, toCopy, _asioProcessedMonitorBuffer, 0, remainingStereo)
                    End If
                    _asioMonitorSamplesReady = Math.Max(0, remainingStereo)

                    If toCopy < count Then
                        Array.Clear(buffer, offset + toCopy, count - toCopy)
                    End If
                End If
            Else
                _asioMonitorSamplesReady = 0
                Array.Clear(buffer, offset, count)
            End If
        End SyncLock
        Return count
    End Function

    Private Class AsioDirectSampleProvider
        Implements ISampleProvider

        Private ReadOnly _owner As NAudioSyncPlayer
        Private ReadOnly _format As WaveFormat

        Public Sub New(owner As NAudioSyncPlayer, sampleRate As Integer, channels As Integer)
            _owner = owner
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
        End Sub

        Public ReadOnly Property WaveFormat As WaveFormat Implements ISampleProvider.WaveFormat
            Get
                Return _format
            End Get
        End Property

        Public Function Read(buffer() As Single, offset As Integer, count As Integer) As Integer Implements ISampleProvider.Read
            Return _owner.ReadAsioMonitorSamples(buffer, offset, count)
        End Function
    End Class

    Private _lastMicEndpointVol As Single = -1.0F
    Private _micSplitLp250 As Single = 0.0F
    Private _micSplitLp250_2 As Single = 0.0F
    Private _micHpMid_1 As Single = 0.0F
    Private _micSplitLp3500 As Single = 0.0F
    Private _micSplitLp3500_2 As Single = 0.0F
    Private _micPrevSubPeak As Single = 0.0F
    Private _micRefPeak As Single = 0.25F
    Private _micGateHoldCount As Integer = 0

    Private _wasapiMicFloatBuf As Single() = Array.Empty(Of Single)()
    Private _wasapiMicByteBuf As Byte() = Array.Empty(Of Byte)()

    Private Sub OnMicDataAvailable(sender As Object, e As WaveInEventArgs)
        Dim micCap As WasapiCapture = _micCapture
        If e Is Nothing OrElse e.BytesRecorded <= 0 OrElse micCap Is Nothing Then Return
        Dim wf As WaveFormat = micCap.WaveFormat
        If wf Is Nothing Then Return

        Dim chCount As Integer = Math.Max(1, wf.Channels)
        Dim bytesPerSample As Integer = wf.BitsPerSample \ 8
        If bytesPerSample <= 0 Then Return

        Dim frameStride As Integer = bytesPerSample * chCount
        Dim frameCount As Integer = e.BytesRecorded \ frameStride
        If frameCount <= 0 Then Return

        Dim snap As ProgramMixerChannel() = Volatile.Read(_channelsSnapshot)
        Dim micCh As ProgramMixerChannel = Nothing
        If snap IsNot Nothing Then
            For i As Integer = 0 To snap.Length - 1
                If snap(i) IsNot Nothing AndAlso snap(i).ProgramKey.Equals("MIC", StringComparison.OrdinalIgnoreCase) Then
                    micCh = snap(i)
                    Exit For
                End If
            Next
        End If
        If micCh Is Nothing Then Return
        micCh.LastSeenActiveTicks = Environment.TickCount64

        Dim isFloat32 As Boolean = IsWaveFormatIeeeFloat32(wf)

        Dim stereoFloats As Integer = frameCount * 2
        If _wasapiMicFloatBuf.Length < stereoFloats Then
            _wasapiMicFloatBuf = New Single(Math.Max(stereoFloats, 4096) - 1) {}
        End If

        For f As Integer = 0 To frameCount - 1
            Dim frameOffset As Integer = f * frameStride
            Dim sL As Single = 0.0F
            Dim sR As Single = 0.0F

            If isFloat32 Then
                sL = BitConverter.ToSingle(e.Buffer, frameOffset)
                sR = If(chCount >= 2, BitConverter.ToSingle(e.Buffer, frameOffset + 4), sL)
            ElseIf wf.BitsPerSample = 16 Then
                sL = BitConverter.ToInt16(e.Buffer, frameOffset) / 32768.0F
                sR = If(chCount >= 2, BitConverter.ToInt16(e.Buffer, frameOffset + 2) / 32768.0F, sL)
            ElseIf wf.BitsPerSample = 24 Then
                Dim iL As Integer = (CInt(e.Buffer(frameOffset)) Or (CInt(e.Buffer(frameOffset + 1)) << 8) Or (CInt(CSByte(e.Buffer(frameOffset + 2))) << 16))
                sL = CSng(iL / 8388608.0)
                If chCount >= 2 Then
                    Dim rOff As Integer = frameOffset + 3
                    Dim iR As Integer = (CInt(e.Buffer(rOff)) Or (CInt(e.Buffer(rOff + 1)) << 8) Or (CInt(CSByte(e.Buffer(rOff + 2))) << 16))
                    sR = CSng(iR / 8388608.0)
                Else
                    sR = sL
                End If
            ElseIf wf.BitsPerSample = 32 Then
                sL = CSng(BitConverter.ToInt32(e.Buffer, frameOffset) / 2147483648.0)
                sR = If(chCount >= 2, CSng(BitConverter.ToInt32(e.Buffer, frameOffset + 4) / 2147483648.0), sL)
            End If

            If Single.IsNaN(sL) OrElse Single.IsInfinity(sL) Then sL = 0.0F
            If Single.IsNaN(sR) OrElse Single.IsInfinity(sR) Then sR = 0.0F
            _wasapiMicFloatBuf(f * 2) = sL
            _wasapiMicFloatBuf(f * 2 + 1) = sR
        Next

        Dim apoAlreadyProcessed As Boolean = NativeCppAutoBuilderAndInstaller.IsKernelMicApoProcessingAudio()
        micCh.IsApoPostProcessedPeak = apoAlreadyProcessed

        Dim sampleRateHz As Single = CSng(Math.Max(8000, wf.SampleRate))
        Dim alphaLow As Single = CSng(1.0 - Math.Exp(-2.0 * Math.PI * 130.0 / sampleRateHz))
        Dim alphaMid As Single = CSng(1.0 - Math.Exp(-2.0 * Math.PI * 2500.0 / sampleRateHz))
        Dim lpL1 As Single = _micSplitLp250
        Dim lpL2 As Single = _micSplitLp250_2
        Dim hpM1 As Single = _micHpMid_1
        Dim lpM1 As Single = _micSplitLp3500
        Dim lpM2 As Single = _micSplitLp3500_2
        Dim prevSubPk As Single = _micPrevSubPeak
        Dim refPeak As Single = _micRefPeak
        Dim gate As Single = Math.Max(0.0F, MixerHardwareStateStore.Current.Audio.MicNoiseGateThreshold)
        Dim xlrMode As Integer = MixerHardwareStateStore.Current.Audio.MicXlrInputMode

        Dim subWindowFrames As Integer = Math.Max(256, CInt(sampleRateHz * 0.015F))
        Dim fIdx As Integer = 0

        While fIdx < frameCount
            Dim subEnd As Integer = Math.Min(frameCount - 1, fIdx + subWindowFrames - 1)
            Dim subCnt As Integer = (subEnd - fIdx) + 1
            Dim maxPos As Single = 0.0F
            Dim maxNeg As Single = 0.0F
            Dim sumLow As Single = 0.0F
            Dim sumMid As Single = 0.0F
            Dim sumHigh As Single = 0.0F

            For f As Integer = fIdx To subEnd
                Dim sampleL As Single = _wasapiMicFloatBuf(f * 2)
                Dim sampleR As Single = _wasapiMicFloatBuf(f * 2 + 1)

                Dim mono As Single
                If apoAlreadyProcessed Then
                    mono = sampleL
                Else
                    Select Case xlrMode
                        Case 1 : mono = sampleL
                        Case 2 : mono = sampleR
                        Case 3 : mono = (sampleL + sampleR) * 0.707106769F
                        Case Else
                            Dim aL As Single = Math.Abs(sampleL)
                            Dim aR As Single = Math.Abs(sampleR)
                            mono = If(aL >= aR, sampleL, sampleR)
                    End Select
                End If

                Dim posV As Single = Math.Max(Math.Max(0.0F, mono), Math.Abs(mono) * 0.85F)
                Dim negV As Single = Math.Max(Math.Max(0.0F, -mono), Math.Abs(mono) * 0.85F)
                If posV > maxPos Then maxPos = posV
                If negV > maxNeg Then maxNeg = negV

                lpL1 += alphaLow * (mono - lpL1)
                lpL2 += alphaLow * (lpL1 - lpL2)

                Dim hp1 As Single = mono - lpL1
                hpM1 += alphaLow * (hp1 - hpM1)
                Dim cleanNoBass As Single = hp1 - hpM1

                lpM1 += alphaMid * (cleanNoBass - lpM1)
                lpM2 += alphaMid * (lpM1 - lpM2)

                Dim bLow As Single = Math.Abs(lpL2)
                Dim bMid As Single = Math.Abs(lpM2)
                Dim bHigh As Single = Math.Abs(cleanNoBass - lpM1)

                sumLow += bLow * bLow
                sumMid += bMid * bMid
                sumHigh += bHigh * bHigh
            Next

            Dim invCnt As Single = 1.0F / CSng(subCnt)
            Dim rmsLow As Single = CSng(Math.Sqrt(sumLow * invCnt)) * 1.25F
            Dim rmsMid As Single = CSng(Math.Sqrt(sumMid * invCnt)) * 1.65F
            Dim rmsHigh As Single = CSng(Math.Sqrt(sumHigh * invCnt)) * 2.85F

            Dim subPk As Single = Math.Max(maxPos, maxNeg)
            Dim faderGateScale As Single = If(apoAlreadyProcessed, Math.Max(0.05F, micCh.Fader01 * micCh.Fader01), 1.0F)
            Dim visualMicGate As Single = Math.Max(0.0005F, gate * 1.25F * faderGateScale)

            If subPk >= visualMicGate Then
                _micGateHoldCount = Math.Min(6, _micGateHoldCount + 1)
            Else
                _micGateHoldCount = Math.Max(0, _micGateHoldCount - 2)
            End If

            If _micGateHoldCount < 2 Then
                refPeak = Math.Max(0.38F, refPeak * 0.995F)
                micCh.PushExactWaveBar(0.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F, True)
            Else
                If subPk > refPeak Then
                    refPeak = subPk
                Else
                    refPeak = Math.Max(0.38F, refPeak * 0.992F)
                End If

                Dim normPos As Single = Math.Min(0.96F, CSng(Math.Pow(Math.Min(1.0F, maxPos / refPeak), 1.55)))
                Dim normNeg As Single = Math.Min(0.96F, CSng(Math.Pow(Math.Min(1.0F, maxNeg / refPeak), 1.55)))
                Dim envP As Single = Math.Max(normPos, normNeg)

                Dim trHot As Single = Math.Max(0.0F, (subPk - prevSubPk) / refPeak) * 2.4F
                prevSubPk = subPk * 0.72F + prevSubPk * 0.28F
                Dim maxB As Single = Math.Max(0.0001F, Math.Max(rmsLow, Math.Max(rmsMid, rmsHigh)))

                micCh.PushExactWaveBar(
                    normPos,
                    normNeg,
                    (rmsLow / maxB) * envP,
                    (rmsMid / maxB) * envP,
                    (rmsHigh / maxB) * envP,
                    Math.Min(1.0F, trHot),
                    True)
            End If

            fIdx = subEnd + 1
        End While

        _micSplitLp250 = If(Single.IsNaN(lpL1) OrElse Single.IsInfinity(lpL1), 0.0F, lpL1)
        _micSplitLp250_2 = If(Single.IsNaN(lpL2) OrElse Single.IsInfinity(lpL2), 0.0F, lpL2)
        _micHpMid_1 = If(Single.IsNaN(hpM1) OrElse Single.IsInfinity(hpM1), 0.0F, hpM1)
        _micSplitLp3500 = If(Single.IsNaN(lpM1) OrElse Single.IsInfinity(lpM1), 0.0F, lpM1)
        _micSplitLp3500_2 = If(Single.IsNaN(lpM2) OrElse Single.IsInfinity(lpM2), 0.0F, lpM2)
        _micPrevSubPeak = prevSubPk
        _micRefPeak = If(Single.IsNaN(refPeak) OrElse Single.IsInfinity(refPeak), 0.38F, refPeak)

        Dim waveProv As BufferedWaveProvider = _micWaveProvider
        Dim waveOut As WasapiOut = _micWaveOut
        Dim needMonitor As Boolean = (Volatile.Read(_micMonitoringEnabled) AndAlso Not IsAsioActive AndAlso waveProv IsNot Nothing AndAlso Not micCh.IsEffectivelyMuted)

        If needMonitor Then
            Dim audioCfg = MixerHardwareStateStore.Current.Audio
            Dim masterVol01 As Single = Math.Max(0.0F, Math.Min(1.0F, Volatile.Read(_volume) / 100.0F))
            Dim isMasterZero As Boolean = (masterVol01 <= 0.001F)
            Dim effMicFader As Single = If(micCh.IsEffectivelyMuted OrElse isMasterZero, 0.0F, micCh.Fader01)

            If Not apoAlreadyProcessed AndAlso _nativeMixer IsNot Nothing AndAlso _nativeMixer.IsAvailable Then
                Try
                    Dim micStudioOn As Boolean = audioCfg.MicStudioProcessingEnabled
                    Dim limiterOn As Boolean = audioCfg.LimiterEnabled
                    Dim micIrPreset As Integer = Math.Max(0, Math.Min(7, audioCfg.MicIrPreset))

                    Dim packedFlags As Integer = If(limiterOn, 1, 0) Or ((micCh.ColorFxMode And 3) << 1) Or ((micIrPreset And 7) << 5)
                    If Not micStudioOn Then packedFlags = packedFlags Or 8
                    If micCh.IsEffectivelyMuted OrElse effMicFader <= 0.001F Then packedFlags = packedFlags Or 16

                    Dim dspParams As New YoumpMixerParams() With {
                        .trimGain = micCh.Trim01 * 2.0F,
                        .eqLowDb = yoump.MediaParameterBroker.ValueToDecibels(micCh.Low01),
                        .eqMidDb = yoump.MediaParameterBroker.ValueToDecibels(micCh.Mid01),
                        .eqHighDb = yoump.MediaParameterBroker.ValueToDecibels(micCh.Hi01),
                        .colorKnob = micCh.Color01,
                        .channelFader = effMicFader,
                        .masterVolume = masterVol01,
                        .balance = -1.0F,
                        .limiterEnabled = packedFlags
                    }
                    Dim dummyMeters As YoumpMixerMeters = Nothing
                    NativeDspMethods.ProcessChannel(0, _wasapiMicFloatBuf, frameCount, 2, dspParams, dummyMeters)
                Catch
                End Try
            ElseIf apoAlreadyProcessed Then
                ' Если Capture APO уже обработал микрофон в audiodg.exe (включая effMicFader^2),
                ' применяем к мониторингу только уровень мастера без повторного умножения на фейдер микрофона
                Dim monGain As Single = masterVol01 * masterVol01
                For i As Integer = 0 To stereoFloats - 1
                    _wasapiMicFloatBuf(i) *= monGain
                Next
            End If

            Try
                Dim byteLen As Integer = stereoFloats * 4
                If _wasapiMicByteBuf.Length < byteLen Then
                    _wasapiMicByteBuf = New Byte(Math.Max(byteLen, 16384) - 1) {}
                End If
                Buffer.BlockCopy(_wasapiMicFloatBuf, 0, _wasapiMicByteBuf, 0, byteLen)
                waveProv.AddSamples(_wasapiMicByteBuf, 0, byteLen)

                If waveOut IsNot Nothing AndAlso waveOut.PlaybackState <> PlaybackState.Playing Then
                    waveOut.Play()
                End If
            Catch
            End Try
        End If
    End Sub

    Public Sub DecayMetersIfSilent(Optional forceImmediateSync As Boolean = False)
        If _isDisposed Then Return
        If forceImmediateSync Then
            TriggerFastSessionSync(forceVolumeWrite:=False)
        End If
    End Sub

    Private Sub TriggerFastSessionSync(Optional forceVolumeWrite As Boolean = True)
        If _isDisposed Then Return
        SyncNativeKernelDspParameters()
        If forceVolumeWrite Then
            Interlocked.Exchange(_forceImmediateApply, 1)
        End If
        _syncSignal.Set()
    End Sub

    Private Sub SyncNativeKernelDspParameters()
        Try
            RecalculateEffectiveMuteStates()
            Dim snap As ProgramMixerChannel() = Volatile.Read(_channelsSnapshot)
            If snap Is Nothing OrElse snap.Length = 0 Then Return

            Dim audioCfg = MixerHardwareStateStore.Current.Audio
            Dim micStudioOn As Boolean = audioCfg.MicStudioProcessingEnabled
            Dim limiterOn As Boolean = audioCfg.LimiterEnabled
            Dim micIrPreset As Integer = Math.Max(0, Math.Min(7, audioCfg.MicIrPreset))
            Dim micIrMix As Single = Math.Max(0.0F, Math.Min(1.0F, audioCfg.MicIrMix))
            Dim talkoverMode As Integer = audioCfg.MicTalkoverMode
            Dim duckEnv As Single = Volatile.Read(_talkoverDuckEnvelope)
            Dim masterVol01 As Single = Math.Max(0.0F, Math.Min(1.0F, Volatile.Read(_volume) / 100.0F))
            Dim isMasterZero As Boolean = (masterVol01 <= 0.001F)

            NativeCppAutoBuilderAndInstaller.WriteMicConvolutionIrParams(
                micIrPreset,
                micIrMix,
                audioCfg.MicNoiseGateThreshold,
                audioCfg.GetPackedMicExtraFlags())

            Dim count As Integer = Math.Min(16, snap.Length)
            Dim rawPeaks(count - 1) As Single
            Dim attenPeaks(count - 1) As Single
            Dim paramArr(count - 1) As YoumpMixerParams
            Dim hasMicChannel As Boolean = False

            For i As Integer = 0 To count - 1
                Dim c As ProgramMixerChannel = snap(i)
                If c Is Nothing Then Continue For

                Dim isMicCh As Boolean = c.ProgramKey.Equals("MIC", StringComparison.OrdinalIgnoreCase)
                If isMicCh Then hasMicChannel = True

                Dim rPk As Single = c.RawSessionMeterPeak
                Dim effFader As Single = If(c.IsEffectivelyMuted OrElse (Not isMicCh AndAlso isMasterZero), 0.0F, c.Fader01)
                Dim isFaderZero As Boolean = (effFader <= 0.001F)
                Dim appVol As Single = If(c.LastAppliedSessionVolume >= 0.0F, c.LastAppliedSessionVolume, effFader * effFader)
                rawPeaks(i) = rPk
                attenPeaks(i) = rPk * appVol

                Dim irBits As Integer = If(isMicCh, (micIrPreset And 7) << 5, 0)

                Dim packedFlags As Integer = If(limiterOn, 1, 0) Or ((c.ColorFxMode And 3) << 1) Or irBits
                If Not micStudioOn Then packedFlags = packedFlags Or 8
                If isMicCh AndAlso (c.IsEffectivelyMuted OrElse isFaderZero) Then packedFlags = packedFlags Or 16

                Dim chMidDb As Single = yoump.MediaParameterBroker.ValueToDecibels(c.Mid01)
                If Not isMicCh AndAlso talkoverMode = 2 AndAlso duckEnv > 0.01F Then
                    chMidDb = Math.Max(-24.0F, chMidDb - (duckEnv * 9.5F))
                End If

                Dim p As New YoumpMixerParams() With {
                    .trimGain = c.Trim01 * 2.0F,
                    .eqLowDb = yoump.MediaParameterBroker.ValueToDecibels(c.Low01),
                    .eqMidDb = chMidDb,
                    .eqHighDb = yoump.MediaParameterBroker.ValueToDecibels(c.Hi01),
                    .colorKnob = c.Color01,
                    .channelFader = If(isMicCh, effFader, 1.0F),
                    .masterVolume = 1.0F,
                    .balance = If(isMicCh, -1.0F, 0.5F),
                    .limiterEnabled = packedFlags
                }
                paramArr(i) = p
            Next

            NativeCppAutoBuilderAndInstaller.WriteMultiChannelTable(count, rawPeaks, attenPeaks, paramArr)

            If hasMicChannel Then
                If Math.Abs(1.0F - _lastMicEndpointVol) > 0.005F Then
                    _lastMicEndpointVol = 1.0F
                    Try
                        Dim mainDev As MMDevice = _mainCaptureDevice
                        If mainDev?.AudioEndpointVolume IsNot Nothing Then
                            mainDev.AudioEndpointVolume.Mute = False
                            mainDev.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0F
                        End If
                        Dim micDev As MMDevice = _micCaptureDevice
                        If micDev?.AudioEndpointVolume IsNot Nothing Then
                            micDev.AudioEndpointVolume.Mute = False
                            micDev.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0F
                        End If
                    Catch
                    End Try
                End If
            End If

            Dim targetRenderCh As ProgramMixerChannel = Nothing
            If _focusedTrackId <> Guid.Empty Then
                Dim cand As ProgramMixerChannel = GetChannelOrFirst(_focusedTrackId)
                If cand IsNot Nothing AndAlso Not cand.ProgramKey.Equals("MIC", StringComparison.OrdinalIgnoreCase) Then
                    targetRenderCh = cand
                End If
            End If

            If targetRenderCh Is Nothing Then
                For i As Integer = 0 To count - 1
                    If snap(i) IsNot Nothing AndAlso Not snap(i).ProgramKey.Equals("MIC", StringComparison.OrdinalIgnoreCase) Then
                        targetRenderCh = snap(i)
                        Exit For
                    End If
                Next
            End If

            If targetRenderCh IsNot Nothing AndAlso _nativeMixer IsNot Nothing Then
                Dim isTargetFaderZero As Boolean = (targetRenderCh.IsEffectivelyMuted OrElse isMasterZero OrElse targetRenderCh.Fader01 <= 0.001F)
                Dim targetPackedFlags As Integer = If(limiterOn, 1, 0) Or ((targetRenderCh.ColorFxMode And 3) << 1)
                If Not micStudioOn Then targetPackedFlags = targetPackedFlags Or 8
                If isTargetFaderZero Then targetPackedFlags = targetPackedFlags Or 16

                Dim targetMidDb As Single = yoump.MediaParameterBroker.ValueToDecibels(targetRenderCh.Mid01)
                If talkoverMode = 2 AndAlso duckEnv > 0.01F Then
                    targetMidDb = Math.Max(-24.0F, targetMidDb - (duckEnv * 9.5F))
                End If
                _nativeMixer.SetParameters(
                    targetRenderCh.Trim01 * 2.0F,
                    yoump.MediaParameterBroker.ValueToDecibels(targetRenderCh.Low01),
                    targetMidDb,
                    yoump.MediaParameterBroker.ValueToDecibels(targetRenderCh.Hi01),
                    targetRenderCh.Color01,
                    1.0F, 1.0F, 0.5F, limiterOn,
                    targetPackedFlags)
            End If
        Catch
        End Try
    End Sub

    Public Sub InstallKernelApoForWindows11()
        If Interlocked.CompareExchange(_isInstallingApo, 1, 0) <> 0 Then Return

        Task.Run(
            Sub()
                Try
                    ' Останавливаем активные захваты перед перезапуском Audiosrv
                    StopEngine()

                    Dim baseDir As String = AppDomain.CurrentDomain.BaseDirectory
                    Dim rootDir As String = NativeCppAutoBuilderAndInstaller.FindProjectRootDirectory()
                    Dim srcApo As String = Path.Combine(baseDir, "Yoump.APO.dll")
                    If Not File.Exists(srcApo) AndAlso Not String.IsNullOrEmpty(rootDir) Then
                        Dim bApo As String = Path.Combine(rootDir, "build", "Yoump.APO.dll")
                        If File.Exists(bApo) Then srcApo = bApo
                    End If
                    Dim srcDsp As String = Path.Combine(baseDir, "Yoump.DSP.dll")
                    If Not File.Exists(srcDsp) AndAlso Not String.IsNullOrEmpty(rootDir) Then
                        Dim bDsp As String = Path.Combine(rootDir, "build", "Yoump.DSP.dll")
                        If File.Exists(bDsp) Then srcDsp = bDsp
                    End If

                    Dim aCfg = MixerHardwareStateStore.Current.Audio
                    Dim btSet As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
                        System.Text.RegularExpressions.Regex.Escape("Space Travel"),
                        System.Text.RegularExpressions.Regex.Escape("MOONDROP"),
                        System.Text.RegularExpressions.Regex.Escape("LDAC")
                    }
                    If aCfg.BluetoothHeadphoneKeywords IsNot Nothing Then
                        For Each kw In aCfg.BluetoothHeadphoneKeywords
                            If Not String.IsNullOrWhiteSpace(kw) Then
                                Dim cleanKw As String = kw.Trim()
                                ' Не добавляем общие слова "Headphones"/"Наушники", чтобы не затронуть проводные выходы
                                If Not cleanKw.Equals("Headphones", StringComparison.OrdinalIgnoreCase) AndAlso
                                   Not cleanKw.Equals("Наушники", StringComparison.OrdinalIgnoreCase) Then
                                    btSet.Add(System.Text.RegularExpressions.Regex.Escape(cleanKw))
                                End If
                            End If
                        Next
                    End If
                    Dim btRegex As String = String.Join("|", btSet).Replace("'", "''")

                    Dim blockSet As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
                        "3-4",
                        System.Text.RegularExpressions.Regex.Escape("Hands-Free AG"),
                        System.Text.RegularExpressions.Regex.Escape("Головной телефон"),
                        "MAIN"
                    }
                    If aCfg.BlockedOutputKeywords IsNot Nothing Then
                        For Each bkw In aCfg.BlockedOutputKeywords
                            If Not String.IsNullOrWhiteSpace(bkw) Then
                                blockSet.Add(System.Text.RegularExpressions.Regex.Escape(bkw.Trim()))
                            End If
                        Next
                    End If
                    Dim blockRegex As String = String.Join("|", blockSet).Replace("'", "''")

                    Dim hwSet As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {"QUAD-CAPTURE", "UA-55"}
                    If aCfg.HardwareKeywords IsNot Nothing Then
                        For Each hkw In aCfg.HardwareKeywords
                            If Not String.IsNullOrWhiteSpace(hkw) Then
                                hwSet.Add(System.Text.RegularExpressions.Regex.Escape(hkw.Trim()))
                            End If
                        Next
                    End If
                    Dim hwRegex As String = String.Join("|", hwSet).Replace("'", "''")
                    Dim outKwRegex As String = System.Text.RegularExpressions.Regex.Escape(If(String.IsNullOrWhiteSpace(aCfg.PreferredOutputKeyword), "1-2", aCfg.PreferredOutputKeyword.Trim())).Replace("'", "''")
                    Dim micKwRegex As String = System.Text.RegularExpressions.Regex.Escape(If(String.IsNullOrWhiteSpace(aCfg.PreferredMicKeyword), "1-2", aCfg.PreferredMicKeyword.Trim())).Replace("'", "''")
                    Dim prefOutNameEsc As String = If(String.IsNullOrWhiteSpace(aCfg.PreferredOutputDeviceName), "", System.Text.RegularExpressions.Regex.Escape(aCfg.PreferredOutputDeviceName.Trim()).Replace("'", "''"))

                    Dim psScriptPath As String = Path.Combine(Path.GetTempPath(), "yoump_register_apo_win11.ps1")
                    Dim sb As New StringBuilder()

                    sb.AppendLine("$ErrorActionPreference = 'Continue'")
                    sb.AppendLine("$env:LIB = ''")
                    sb.AppendLine("$apo = '{A7D6D67E-4F8D-4A7D-9C10-3A7F6C2D9101}'")
                    sb.AppendLine("$defMode = [string[]]@('{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}')")
                    sb.AppendLine("$apoArr = [string[]]@($apo)")
                    sb.AppendLine($"$SrcApo = '{srcApo.Replace("'", "''")}'")
                    sb.AppendLine($"$SrcDsp = '{srcDsp.Replace("'", "''")}'")
                    sb.AppendLine("$Sys32Apo = Join-Path $env:SystemRoot 'System32\Yoump.APO.dll'")
                    sb.AppendLine("$Sys32Dsp = Join-Path $env:SystemRoot 'System32\Yoump.DSP.dll'")

                    sb.AppendLine("$csharpCode = @'")
                    sb.AppendLine("using System;")
                    sb.AppendLine("using System.Diagnostics;")
                    sb.AppendLine("using System.Runtime.InteropServices;")
                    sb.AppendLine("public static class TokenPriv")
                    sb.AppendLine("{")
                    sb.AppendLine("    [DllImport(""advapi32.dll"", SetLastError = true)]")
                    sb.AppendLine("    static extern bool OpenProcessToken(IntPtr h, uint a, out IntPtr t);")
                    sb.AppendLine("    [DllImport(""advapi32.dll"", SetLastError = true, CharSet = CharSet.Unicode)]")
                    sb.AppendLine("    static extern bool LookupPrivilegeValue(string s, string n, out long l);")
                    sb.AppendLine("    [DllImport(""advapi32.dll"", SetLastError = true)]")
                    sb.AppendLine("    static extern bool AdjustTokenPrivileges(IntPtr t, bool d, ref TP n, int b, IntPtr p, IntPtr r);")
                    sb.AppendLine("    [DllImport(""kernel32.dll"", SetLastError = true)]")
                    sb.AppendLine("    static extern bool CloseHandle(IntPtr h);")
                    sb.AppendLine("    [StructLayout(LayoutKind.Sequential, Pack = 4)]")
                    sb.AppendLine("    struct LUID_ATTR { public long Luid; public uint Attr; }")
                    sb.AppendLine("    [StructLayout(LayoutKind.Sequential, Pack = 4)]")
                    sb.AppendLine("    struct TP { public uint Count; public LUID_ATTR Priv; }")
                    sb.AppendLine("    public static void Enable(string priv)")
                    sb.AppendLine("    {")
                    sb.AppendLine("        IntPtr hTok = IntPtr.Zero;")
                    sb.AppendLine("        if (OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out hTok))")
                    sb.AppendLine("        {")
                    sb.AppendLine("            long luid = 0;")
                    sb.AppendLine("            if (LookupPrivilegeValue(null, priv, out luid))")
                    sb.AppendLine("            {")
                    sb.AppendLine("                TP tp = new TP();")
                    sb.AppendLine("                tp.Count = 1;")
                    sb.AppendLine("                tp.Priv = new LUID_ATTR { Luid = luid, Attr = 2 };")
                    sb.AppendLine("                AdjustTokenPrivileges(hTok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);")
                    sb.AppendLine("            }")
                    sb.AppendLine("            CloseHandle(hTok);")
                    sb.AppendLine("        }")
                    sb.AppendLine("    }")
                    sb.AppendLine("}")
                    sb.AppendLine("'@")
                    sb.AppendLine("if (-not ('TokenPriv' -as [type])) { Add-Type -TypeDefinition $csharpCode -IgnoreWarnings }")
                    sb.AppendLine("[TokenPriv]::Enable('SeTakeOwnershipPrivilege')")
                    sb.AppendLine("[TokenPriv]::Enable('SeRestorePrivilege')")
                    sb.AppendLine("[TokenPriv]::Enable('SeSecurityPrivilege')")
                    sb.AppendLine("[TokenPriv]::Enable('SeBackupPrivilege')")

                    sb.AppendLine("function Unlock-RegKey([string]$subPath) {")
                    sb.AppendLine("    try {")
                    sb.AppendLine("        $adminSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')")
                    sb.AppendLine("        $k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($subPath, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [System.Security.AccessControl.RegistryRights]::TakeOwnership)")
                    sb.AppendLine("        if ($k) {")
                    sb.AppendLine("            $acl = $k.GetAccessControl([System.Security.AccessControl.AccessControlSections]::None)")
                    sb.AppendLine("            $acl.SetOwner($adminSid)")
                    sb.AppendLine("            $k.SetAccessControl($acl)")
                    sb.AppendLine("            $k.Close()")
                    sb.AppendLine("        }")
                    sb.AppendLine("        $k2 = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($subPath, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [System.Security.AccessControl.RegistryRights]::ChangePermissions -bor [System.Security.AccessControl.RegistryRights]::ReadPermissions)")
                    sb.AppendLine("        if ($k2) {")
                    sb.AppendLine("            $acl2 = $k2.GetAccessControl()")
                    sb.AppendLine("            $rule = New-Object System.Security.AccessControl.RegistryAccessRule($adminSid, [System.Security.AccessControl.RegistryRights]::FullControl, [System.Security.AccessControl.InheritanceFlags]::ContainerInherit, [System.Security.AccessControl.PropagationFlags]::None, [System.Security.AccessControl.AccessControlType]::Allow)")
                    sb.AppendLine("            $acl2.AddAccessRule($rule)")
                    sb.AppendLine("            $k2.SetAccessControl($acl2)")
                    sb.AppendLine("            $k2.Close()")
                    sb.AppendLine("        }")
                    sb.AppendLine("    } catch {}")
                    sb.AppendLine("}")

                    sb.AppendLine("try {")
                    sb.AppendLine("    $shmDir = 'C:\ProgramData\Yoump'")
                    sb.AppendLine("    $shmPath = 'C:\ProgramData\Yoump\yoump_dsp_shm_v4.bin'")
                    sb.AppendLine("    if (-not (Test-Path $shmDir)) { New-Item -Path $shmDir -ItemType Directory -Force | Out-Null }")
                    sb.AppendLine("    $fs = [System.IO.File]::Open($shmPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)")
                    sb.AppendLine("    if ($fs.Length -lt 4096) { $fs.SetLength(4096) }")
                    sb.AppendLine("    $br = New-Object System.IO.BinaryReader($fs)")
                    sb.AppendLine("    $bw = New-Object System.IO.BinaryWriter($fs)")
                    sb.AppendLine("    $fs.Position = 0")
                    sb.AppendLine("    $magic = $br.ReadUInt32()")
                    sb.AppendLine("    if ($magic -ne 0x594D5034) {")
                    sb.AppendLine("        $fs.Position = 0")
                    sb.AppendLine("        $bw.Write([uint32]0x594D5034)")
                    sb.AppendLine("        $bw.Write([uint32]0)")
                    sb.AppendLine("        $bw.Write([single]1.0)")
                    sb.AppendLine("        $bw.Write([single]0.0)")
                    sb.AppendLine("        $bw.Write([single]0.0)")
                    sb.AppendLine("        $bw.Write([single]0.0)")
                    sb.AppendLine("        $bw.Write([single]0.5)")
                    sb.AppendLine("        $bw.Write([single]1.0)")
                    sb.AppendLine("        $bw.Write([single]1.0)")
                    sb.AppendLine("        $bw.Write([single]0.5)")
                    sb.AppendLine("        $bw.Write([int32]1)")
                    sb.AppendLine("        $fs.Position = 56")
                    sb.AppendLine("        $bw.Write([single]0.0)")
                    sb.AppendLine("        $bw.Write([single]1.0)")
                    sb.AppendLine("    }")
                    sb.AppendLine("    $fs.Position = 72")
                    sb.AppendLine("    $bw.Write([uint64]0)")
                    sb.AppendLine("    $bw.Write([byte]0)")
                    sb.AppendLine("    $bw.Flush()")
                    sb.AppendLine("    $fs.Close()")
                    sb.AppendLine("    icacls $shmDir /grant '*S-1-1-0:(OI)(CI)F' '*S-1-5-19:(OI)(CI)F' '*S-1-15-2-1:(OI)(CI)F' '*S-1-15-2-2:(OI)(CI)F' /T /C /Q | Out-Null")
                    sb.AppendLine("    icacls $shmPath /grant '*S-1-1-0:F' '*S-1-5-19:F' '*S-1-15-2-1:F' '*S-1-15-2-2:F' /C /Q | Out-Null")
                    sb.AppendLine("} catch {}")

                    sb.AppendLine("Stop-Service -Name 'Audiosrv' -Force -ErrorAction SilentlyContinue")
                    sb.AppendLine("Stop-Service -Name 'AudioEndpointBuilder' -Force -ErrorAction SilentlyContinue")
                    sb.AppendLine("Get-Process -Name 'audiodg' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue")
                    sb.AppendLine("Start-Sleep -Milliseconds 400")
                    sb.AppendLine("if (Test-Path $SrcDsp) {")
                    sb.AppendLine("    Copy-Item -Path $SrcDsp -Destination $Sys32Dsp -Force -ErrorAction SilentlyContinue")
                    sb.AppendLine("    icacls $Sys32Dsp /grant '*S-1-1-0:(RX)' '*S-1-5-19:(RX)' '*S-1-15-2-1:(RX)' '*S-1-15-2-2:(RX)' /C /Q | Out-Null")
                    sb.AppendLine("}")
                    sb.AppendLine("if (Test-Path $SrcApo) {")
                    sb.AppendLine("    Copy-Item -Path $SrcApo -Destination $Sys32Apo -Force -ErrorAction SilentlyContinue")
                    sb.AppendLine("    icacls $Sys32Apo /grant '*S-1-1-0:(RX)' '*S-1-5-19:(RX)' '*S-1-15-2-1:(RX)' '*S-1-15-2-2:(RX)' /C /Q | Out-Null")
                    sb.AppendLine("}")
                    sb.AppendLine("$TargetDll = if (Test-Path $Sys32Apo) { $Sys32Apo } else { $SrcApo }")

                    sb.AppendLine("New-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio' -Force | Out-Null")
                    sb.AppendLine("Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio' -Name 'DisableProtectedAudioDG' -Value 1 -Type DWord -Force")
                    sb.AppendLine("try {")
                    sb.AppendLine("    $dspKey = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey('SOFTWARE\Yoump\DSP', $true)")
                    sb.AppendLine("    if ($dspKey) {")
                    sb.AppendLine("        $dspKey.SetValue('', 'Yoump Kernel DSP', [Microsoft.Win32.RegistryValueKind]::String)")
                    sb.AppendLine("        $acl = $dspKey.GetAccessControl()")
                    sb.AppendLine("        foreach ($sidStr in @('S-1-1-0', 'S-1-5-19', 'S-1-15-2-1', 'S-1-15-2-2')) {")
                    sb.AppendLine("            try {")
                    sb.AppendLine("                $sid = New-Object System.Security.Principal.SecurityIdentifier($sidStr)")
                    sb.AppendLine("                $rule = New-Object System.Security.AccessControl.RegistryAccessRule($sid, [System.Security.AccessControl.RegistryRights]::FullControl, [System.Security.AccessControl.InheritanceFlags]::ContainerInherit, [System.Security.AccessControl.PropagationFlags]::None, [System.Security.AccessControl.AccessControlType]::Allow)")
                    sb.AppendLine("                $acl.AddAccessRule($rule)")
                    sb.AppendLine("            } catch {}")
                    sb.AppendLine("        }")
                    sb.AppendLine("        $dspKey.SetAccessControl($acl)")
                    sb.AppendLine("        $dspKey.Close()")
                    sb.AppendLine("    }")
                    sb.AppendLine("} catch {}")

                    sb.AppendLine("$ClsidKey = ""HKLM:\SOFTWARE\Classes\CLSID\$apo""")
                    sb.AppendLine("New-Item -Path $ClsidKey -Force | Out-Null")
                    sb.AppendLine("Set-ItemProperty -Path $ClsidKey -Name '(Default)' -Value 'Yoump System Audio APO' -Force")
                    sb.AppendLine("$InprocKey = ""$ClsidKey\InprocServer32""")
                    sb.AppendLine("New-Item -Path $InprocKey -Force | Out-Null")
                    sb.AppendLine("Set-ItemProperty -Path $InprocKey -Name '(Default)' -Value $TargetDll -Force")
                    sb.AppendLine("Set-ItemProperty -Path $InprocKey -Name 'ThreadingModel' -Value 'Both' -Force")

                    sb.AppendLine("$ApoRegKey = ""HKLM:\SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\$apo""")
                    sb.AppendLine("New-Item -Path $ApoRegKey -Force | Out-Null")
                    sb.AppendLine("Set-ItemProperty -Path $ApoRegKey -Name 'FriendlyName' -Value 'Yoump System Audio APO' -Type String -Force")
                    sb.AppendLine("Set-ItemProperty -Path $ApoRegKey -Name 'Copyright' -Value 'Yoump' -Type String -Force")
                    sb.AppendLine("Set-ItemProperty -Path $ApoRegKey -Name 'MajorVersion' -Value 1 -Type DWord -Force")
                    sb.AppendLine("Set-ItemProperty -Path $ApoRegKey -Name 'MinorVersion' -Value 0 -Type DWord -Force")
                    sb.AppendLine("Set-ItemProperty -Path $ApoRegKey -Name 'Flags' -Value 15 -Type DWord -Force")
                    sb.AppendLine("Set-ItemProperty -Path $ApoRegKey -Name 'MinInputConnections' -Value 1 -Type DWord -Force")
                    sb.AppendLine("Set-ItemProperty -Path $ApoRegKey -Name 'MaxInputConnections' -Value 1 -Type DWord -Force")
                    sb.AppendLine("Set-ItemProperty -Path $ApoRegKey -Name 'MinOutputConnections' -Value 1 -Type DWord -Force")
                    sb.AppendLine("Set-ItemProperty -Path $ApoRegKey -Name 'MaxOutputConnections' -Value 1 -Type DWord -Force")
                    sb.AppendLine("Set-ItemProperty -Path $ApoRegKey -Name 'MaxInstances' -Value 64 -Type DWord -Force")
                    sb.AppendLine("Set-ItemProperty -Path $ApoRegKey -Name 'NumAPOInterfaces' -Value 2 -Type DWord -Force")
                    sb.AppendLine("Set-ItemProperty -Path $ApoRegKey -Name 'APOInterface0' -Value '{5FA00F27-ADD6-499A-8A9D-6B98521FA75B}' -Type String -Force")
                    sb.AppendLine("Set-ItemProperty -Path $ApoRegKey -Name 'APOInterface1' -Value '{BAFE99D2-7436-44CE-9E0E-4D89AFBFFF56}' -Type String -Force")

                    ' Регистрация Render APO только как SFX (Stream Effect: ключи ,1 и ,13), без дублирования в MFX (,5)
                    sb.AppendLine("$renderRootPath = 'SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'")
                    sb.AppendLine("$renderRoot = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($renderRootPath, $false)")
                    sb.AppendLine("if ($renderRoot) {")
                    sb.AppendLine("    foreach ($sub in $renderRoot.GetSubKeyNames()) {")
                    sb.AppendLine("        try {")
                    sb.AppendLine("            $epPath = ""$renderRootPath\$sub""")
                    sb.AppendLine("            $epKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($epPath, $false)")
                    sb.AppendLine("            if (-not $epKey) { continue }")
                    sb.AppendLine("            $state = $epKey.GetValue('DeviceState', 0)")
                    sb.AppendLine("            if ($state -ne 1) { $epKey.Close(); continue }")
                    sb.AppendLine("            $props = $epKey.OpenSubKey('Properties', $false)")
                    sb.AppendLine("            $isTargetRender = $false")
                    sb.AppendLine("            if ($props) {")
                    sb.AppendLine("                $name = [string]$props.GetValue('{b3f8fa53-0004-438e-9003-51a46e139bfc},6', '')")
                    sb.AppendLine("                $line = [string]$props.GetValue('{a45c254e-df1c-4efd-8020-67d146a850e0},2', '')")
                    sb.AppendLine("                $combined = ""$line $name""")
                    sb.AppendLine($"                if ($combined -notmatch '{blockRegex}') {{")
                    sb.AppendLine($"                    if (($name -match '{outKwRegex}' -or $line -match '{outKwRegex}') -and ($name -match '{hwRegex}' -or $line -match '{hwRegex}')) {{")
                    sb.AppendLine("                        $isTargetRender = $true")
                    sb.AppendLine($"                    }} elseif ('{prefOutNameEsc}' -ne '' -and $combined -match '{prefOutNameEsc}') {{")
                    sb.AppendLine("                        $isTargetRender = $true")
                    sb.AppendLine($"                    }} elseif ('{btRegex}' -ne '' -and $combined -match '{btRegex}') {{")
                    sb.AppendLine("                        $isTargetRender = $true")
                    sb.AppendLine("                    }")
                    sb.AppendLine("                }")
                    sb.AppendLine("                $props.Close()")
                    sb.AppendLine("            }")
                    sb.AppendLine("            $epKey.Close()")
                    sb.AppendLine("            if (-not $isTargetRender) { continue }")
                    sb.AppendLine("            Unlock-RegKey $epPath")
                    sb.AppendLine("            Unlock-RegKey ""$epPath\Properties""")
                    sb.AppendLine("            Unlock-RegKey ""$epPath\FxProperties""")
                    sb.AppendLine("            $propsW = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(""$epPath\Properties"", $true)")
                    sb.AppendLine("            if ($propsW) {")
                    sb.AppendLine("                $propsW.SetValue('{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5', 0, [Microsoft.Win32.RegistryValueKind]::DWord)")
                    sb.AppendLine("                $propsW.Close()")
                    sb.AppendLine("            }")
                    sb.AppendLine("            $fx = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey(""$epPath\FxProperties"", $true)")
                    sb.AppendLine("            if ($fx) {")
                    sb.AppendLine("                $fx.SetValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},0', '{00000000-0000-0000-0000-000000000000}', [Microsoft.Win32.RegistryValueKind]::String)")
                    sb.AppendLine("                $fx.SetValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1', $apo, [Microsoft.Win32.RegistryValueKind]::String)")
                    sb.AppendLine("                $fx.SetValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13', $apoArr, [Microsoft.Win32.RegistryValueKind]::MultiString)")
                    sb.AppendLine("                $fx.SetValue('{d3993a3f-99c2-4402-b5ec-a92a0367664b},5', $defMode, [Microsoft.Win32.RegistryValueKind]::MultiString)")
                    sb.AppendLine("                $fx.SetValue('{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5', 0, [Microsoft.Win32.RegistryValueKind]::DWord)")
                    sb.AppendLine("                $fx.DeleteValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2', $false)")
                    sb.AppendLine("                $fx.DeleteValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5', $false)")
                    sb.AppendLine("                $fx.DeleteValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6', $false)")
                    sb.AppendLine("                $fx.DeleteValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},14', $false)")
                    sb.AppendLine("                $fx.DeleteValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},99', $false)")
                    sb.AppendLine("                $fx.Close()")
                    sb.AppendLine("            }")
                    sb.AppendLine("        } catch {}")
                    sb.AppendLine("    }")
                    sb.AppendLine("    $renderRoot.Close()")
                    sb.AppendLine("}")

                    ' Регистрация Capture APO только как SFX (ключи ,1 и ,13) с маркером микрофона (,99 = 1)
                    sb.AppendLine("$captureRootPath = 'SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture'")
                    sb.AppendLine("$captureRoot = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($captureRootPath, $false)")
                    sb.AppendLine("if ($captureRoot) {")
                    sb.AppendLine("    foreach ($sub in $captureRoot.GetSubKeyNames()) {")
                    sb.AppendLine("        try {")
                    sb.AppendLine("            $epPath = ""$captureRootPath\$sub""")
                    sb.AppendLine("            $epKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($epPath, $false)")
                    sb.AppendLine("            if (-not $epKey) { continue }")
                    sb.AppendLine("            $state = $epKey.GetValue('DeviceState', 0)")
                    sb.AppendLine("            if ($state -ne 1) { $epKey.Close(); continue }")
                    sb.AppendLine("            $props = $epKey.OpenSubKey('Properties', $false)")
                    sb.AppendLine("            $isQuadMic12 = $false")
                    sb.AppendLine("            $isQuadMain = $false")
                    sb.AppendLine("            if ($props) {")
                    sb.AppendLine("                $name = [string]$props.GetValue('{b3f8fa53-0004-438e-9003-51a46e139bfc},6', '')")
                    sb.AppendLine("                $line = [string]$props.GetValue('{a45c254e-df1c-4efd-8020-67d146a850e0},2', '')")
                    sb.AppendLine($"                if ($name -match '{hwRegex}' -or $line -match '{hwRegex}') {{")
                    sb.AppendLine($"                    if (($name -match '{micKwRegex}' -or $line -match '{micKwRegex}') -and ($name -notmatch 'MAIN' -and $line -notmatch 'MAIN')) {{")
                    sb.AppendLine("                        $isQuadMic12 = $true")
                    sb.AppendLine("                    } elseif ($name -match 'MAIN' -or $line -match 'MAIN') {")
                    sb.AppendLine("                        $isQuadMain = $true")
                    sb.AppendLine("                    }")
                    sb.AppendLine("                }")
                    sb.AppendLine("                $props.Close()")
                    sb.AppendLine("            }")
                    sb.AppendLine("            $epKey.Close()")
                    sb.AppendLine("            if (-not $isQuadMic12 -and -not $isQuadMain) { continue }")
                    sb.AppendLine("            Unlock-RegKey $epPath")
                    sb.AppendLine("            Unlock-RegKey ""$epPath\Properties""")
                    sb.AppendLine("            Unlock-RegKey ""$epPath\FxProperties""")
                    sb.AppendLine("            if ($isQuadMain) {")
                    sb.AppendLine("                $propsW = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(""$epPath\Properties"", $true)")
                    sb.AppendLine("                if ($propsW) {")
                    sb.AppendLine("                    $propsW.DeleteValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},99', $false)")
                    sb.AppendLine("                    $propsW.Close()")
                    sb.AppendLine("                }")
                    sb.AppendLine("                $fx = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(""$epPath\FxProperties"", $true)")
                    sb.AppendLine("                if ($fx) {")
                    sb.AppendLine("                    foreach ($vn in @('1','2','5','6','13','14','99')) {")
                    sb.AppendLine("                        $fx.DeleteValue(""{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},$vn"", $false)")
                    sb.AppendLine("                    }")
                    sb.AppendLine("                    $fx.Close()")
                    sb.AppendLine("                }")
                    sb.AppendLine("                continue")
                    sb.AppendLine("            }")
                    sb.AppendLine("            $propsW = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(""$epPath\Properties"", $true)")
                    sb.AppendLine("            if ($propsW) {")
                    sb.AppendLine("                $propsW.SetValue('{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5', 0, [Microsoft.Win32.RegistryValueKind]::DWord)")
                    sb.AppendLine("                $propsW.SetValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},99', 1, [Microsoft.Win32.RegistryValueKind]::DWord)")
                    sb.AppendLine("                $propsW.Close()")
                    sb.AppendLine("            }")
                    sb.AppendLine("            $fx = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey(""$epPath\FxProperties"", $true)")
                    sb.AppendLine("            if ($fx) {")
                    sb.AppendLine("                $fx.SetValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},0', '{00000000-0000-0000-0000-000000000000}', [Microsoft.Win32.RegistryValueKind]::String)")
                    sb.AppendLine("                $fx.SetValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1', $apo, [Microsoft.Win32.RegistryValueKind]::String)")
                    sb.AppendLine("                $fx.SetValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13', $apoArr, [Microsoft.Win32.RegistryValueKind]::MultiString)")
                    sb.AppendLine("                $fx.SetValue('{d3993a3f-99c2-4402-b5ec-a92a0367664b},5', $defMode, [Microsoft.Win32.RegistryValueKind]::MultiString)")
                    sb.AppendLine("                $fx.SetValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},99', 1, [Microsoft.Win32.RegistryValueKind]::DWord)")
                    sb.AppendLine("                $fx.SetValue('{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5', 0, [Microsoft.Win32.RegistryValueKind]::DWord)")
                    sb.AppendLine("                $fx.DeleteValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2', $false)")
                    sb.AppendLine("                $fx.DeleteValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5', $false)")
                    sb.AppendLine("                $fx.DeleteValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6', $false)")
                    sb.AppendLine("                $fx.DeleteValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},14', $false)")
                    sb.AppendLine("                $fx.Close()")
                    sb.AppendLine("            }")
                    sb.AppendLine("        } catch {}")
                    sb.AppendLine("    }")
                    sb.AppendLine("    $captureRoot.Close()")
                    sb.AppendLine("}")

                    sb.AppendLine("Start-Service -Name 'AudioEndpointBuilder' -ErrorAction SilentlyContinue")
                    sb.AppendLine("Start-Service -Name 'Audiosrv' -ErrorAction SilentlyContinue")

                    File.WriteAllText(psScriptPath, sb.ToString(), System.Text.Encoding.UTF8)

                    Dim psi As New ProcessStartInfo() With {
                        .FileName = "powershell.exe",
                        .Arguments = $"-NoProfile -ExecutionPolicy Bypass -File ""{psScriptPath}""",
                        .UseShellExecute = True,
                        .Verb = "runas",
                        .WindowStyle = ProcessWindowStyle.Hidden
                    }
                    Try
                        Using proc As Process = Process.Start(psi)
                            proc?.WaitForExit(15000)
                        End Using
                    Finally
                        Try
                            If File.Exists(psScriptPath) Then File.Delete(psScriptPath)
                        Catch
                        End Try
                    End Try

                    Thread.Sleep(600)
                    Interlocked.Exchange(_isInstallingApo, 0)
                    aCfg = MixerHardwareStateStore.Current.Audio
                    Dim prefOut As String = If(aCfg.PreferBluetoothHeadphonesOutput OrElse String.IsNullOrWhiteSpace(aCfg.PreferredOutputDeviceId),
                                               Nothing,
                                               aCfg.PreferredOutputDeviceId)
                    StartEngine(Nothing, prefOut, False)
                Catch
                Finally
                    Interlocked.Exchange(_isInstallingApo, 0)
                End Try
            End Sub)
    End Sub

    Private Shared Function IsGenericHostProcess(procNameLower As String) As Boolean
        Select Case procNameLower
            Case "", "svchost", "dllhost", "runtimebroker", "applicationframehost", "taskhostw", "backgroundtaskhost", "sihost"
                Return True
            Case Else
                Return False
        End Select
    End Function

    Friend Shared Function TryGetProcessNameFast(pid As Integer) As String
        Return TryGetExecutableNameByPidWin32(pid)
    End Function

    Private Shared Function TryGetExecutableNameByPidWin32(pid As Integer) As String
        If pid <= 4 Then Return ""
        Dim hProc As IntPtr = IntPtr.Zero
        Try
            hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, CUInt(pid))
            If hProc = IntPtr.Zero Then Return ""

            Dim exitCode As UInteger = 0UI
            If Not GetExitCodeProcess(hProc, exitCode) OrElse exitCode <> STILL_ACTIVE Then
                Return ""
            End If

            Dim sb As New StringBuilder(1024)
            Dim size As UInteger = CUInt(sb.Capacity)
            If QueryFullProcessImageNameW(hProc, 0UI, sb, size) AndAlso size > 0UI Then
                Return Path.GetFileNameWithoutExtension(sb.ToString(0, CInt(size))).Trim()
            End If
        Catch
        Finally
            If hProc <> IntPtr.Zero Then
                Try : CloseHandle(hProc) : Catch : End Try
            End If
        End Try
        Return ""
    End Function

    Private ReadOnly _pidCacheTick As New ConcurrentDictionary(Of Integer, Long)()

    Private Function ResolveFriendlyProgramNameCached(pid As Integer,
                                                      sessionDisplayName As String,
                                                      sessionIdentifier As String) As (Key As String, Title As String, RawProcessName As String)
        Dim nowTicks As Long = Environment.TickCount64
        Dim cached As (Key As String, Title As String, RawProcessName As String) = Nothing

        If pid > 4 AndAlso _pidToProgramCache.TryGetValue(pid, cached) AndAlso Not String.IsNullOrEmpty(cached.Key) Then
            Dim lastVerifyTick As Long = 0L
            If _pidCacheTick.TryGetValue(pid, lastVerifyTick) AndAlso (nowTicks - lastVerifyTick) < 3000L Then
                If cached.Key = IgnoredProcessMarker Then
                    Return ("", "", "")
                End If
                Return cached
            End If
        End If

        Dim rawProcName As String = TryGetExecutableNameByPidWin32(pid)
        If pid > 4 AndAlso Not String.IsNullOrEmpty(cached.Key) AndAlso
           (String.IsNullOrEmpty(rawProcName) OrElse String.Equals(cached.RawProcessName, rawProcName, StringComparison.OrdinalIgnoreCase)) Then
            _pidCacheTick(pid) = nowTicks
            If cached.Key = IgnoredProcessMarker Then
                Return ("", "", "")
            End If
            Return cached
        End If

        If String.IsNullOrEmpty(rawProcName) AndAlso pid > 4 Then
            Try
                Using p As Process = Process.GetProcessById(pid)
                    If p IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(p.ProcessName) Then
                        rawProcName = p.ProcessName.Trim()
                    End If
                End Using
            Catch
            End Try
        End If

        Dim candidateName As String = rawProcName.ToLowerInvariant()
        Dim idLower As String = If(sessionIdentifier, "").ToLowerInvariant()

        If IsGenericHostProcess(candidateName) AndAlso Not String.IsNullOrEmpty(idLower) Then
            Dim exeIdx As Integer = idLower.IndexOf(".exe", StringComparison.OrdinalIgnoreCase)
            If exeIdx > 0 Then
                Dim slashIdx As Integer = idLower.LastIndexOfAny(New Char() {"\"c, "/"c, "|"c}, exeIdx)
                Dim startIdx As Integer = If(slashIdx >= 0, slashIdx + 1, 0)
                If exeIdx > startIdx Then
                    Dim extracted As String = idLower.Substring(startIdx, exeIdx - startIdx).Trim()
                    If Not IsGenericHostProcess(extracted) Then
                        candidateName = extracted
                        If String.IsNullOrEmpty(rawProcName) Then rawProcName = extracted
                    End If
                End If
            End If
        End If

        If String.IsNullOrWhiteSpace(candidateName) Then
            Return ("", "", "")
        End If

        If _pidToProgramCache.Count > 512 Then
            _pidToProgramCache.Clear()
            _pidCacheTick.Clear()
        End If
        If _lastRouteAttemptTicks.Count > 512 Then
            _lastRouteAttemptTicks.Clear()
        End If

        Dim rulesCfg = MixerHardwareStateStore.Current.ProcessRules

        If rulesCfg.IgnoredProcesses IsNot Nothing Then
            For i As Integer = 0 To rulesCfg.IgnoredProcesses.Count - 1
                Dim ign As String = rulesCfg.IgnoredProcesses(i)
                If Not String.IsNullOrWhiteSpace(ign) AndAlso candidateName.Equals(ign.Trim(), StringComparison.OrdinalIgnoreCase) Then
                    If pid > 4 Then
                        _pidToProgramCache(pid) = (IgnoredProcessMarker, "", rawProcName)
                        _pidCacheTick(pid) = nowTicks
                    End If
                    Return ("", "", "")
                End If
            Next
        End If

        Dim resolved As (Key As String, Title As String, RawProcessName As String) = ("", "", rawProcName)

        Dim aliasName As String = Nothing
        If rulesCfg.ProcessAliases IsNot Nothing AndAlso rulesCfg.ProcessAliases.TryGetValue(candidateName, aliasName) AndAlso Not String.IsNullOrWhiteSpace(aliasName) Then
            Dim cleanAlias As String = aliasName.Trim().ToUpperInvariant()
            resolved = (cleanAlias, cleanAlias, If(String.IsNullOrEmpty(rawProcName), candidateName, rawProcName))
        ElseIf candidateName.Equals("яндекс музыка", StringComparison.OrdinalIgnoreCase) OrElse
               candidateName.Equals("yandexmusic", StringComparison.OrdinalIgnoreCase) Then
            resolved = ("YA.MUSIC", "YA.MUSIC", If(String.IsNullOrEmpty(rawProcName), candidateName, rawProcName))
        Else
            Dim up As String = candidateName.ToUpperInvariant()
            If up.Length > 9 Then up = up.Substring(0, 9)
            resolved = (up, up, rawProcName)
        End If

        If pid > 4 AndAlso Not String.IsNullOrEmpty(resolved.Key) Then
            _pidToProgramCache(pid) = resolved
            _pidCacheTick(pid) = nowTicks
        End If
        Return resolved
    End Function

    Private Sub ApplyProgramChannelsAndUpdateMeters(workerEnumerator As MMDeviceEnumerator,
                                                    refreshSessions As Boolean,
                                                    forceWriteAll As Boolean)
        Dim curOutputId As String = Volatile.Read(_cachedOutputId)
        If _isDisposed OrElse String.IsNullOrEmpty(curOutputId) Then Return

        RecalculateEffectiveMuteStates()
        Dim channels As ProgramMixerChannel() = Volatile.Read(_channelsSnapshot)
        If channels.Length = 0 Then Return

        Dim currentPid As Integer = Environment.ProcessId
        Dim nowTicks As Long = Environment.TickCount64
        Dim masterVol01 As Single = Math.Max(0.0F, Math.Min(1.0F, Volatile.Read(_volume) / 100.0F))
        Dim curBal01 As Single = Volatile.Read(_balance)
        Dim balGainL As Single = If(curBal01 < 0.5F, 1.0F, (1.0F - curBal01) * 2.0F)
        Dim balGainR As Single = If(curBal01 < 0.5F, curBal01 * 2.0F, 1.0F)
        Dim needApplyBalance As Boolean = _balanceUserInteracted OrElse (Math.Abs(curBal01 - _lastAppliedBalance) > 0.002F)

        Dim channelMaxPeaks As New Dictionary(Of Guid, Single)()
        Dim channelMaxPeaksL As New Dictionary(Of Guid, Single)()
        Dim channelMaxPeaksR As New Dictionary(Of Guid, Single)()
        Dim channelHasActiveState As New Dictionary(Of Guid, Boolean)()
        Dim channelIndexMap As New Dictionary(Of Guid, Integer)()

        For i As Integer = 0 To channels.Length - 1
            Dim tid As Guid = channels(i).TrackId
            channelMaxPeaks(tid) = 0.0F
            channelMaxPeaksL(tid) = 0.0F
            channelMaxPeaksR(tid) = 0.0F
            channelHasActiveState(tid) = False
            channelIndexMap(tid) = i
        Next

        Dim openedDevices As New List(Of (IsPrimaryOutput As Boolean, Dev As MMDevice, Sessions As SessionCollection))()

        Try
            Dim targetDevices As New List(Of MMDevice)()
            If refreshSessions Then
                For Each d In workerEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    targetDevices.Add(d)
                Next
            Else
                Try
                    Dim primaryDev As MMDevice = workerEnumerator.GetDevice(curOutputId)
                    If primaryDev IsNot Nothing Then targetDevices.Add(primaryDev)
                Catch
                End Try
            End If

            For Each dev In targetDevices
                Dim addedToOpened As Boolean = False
                Try
                    If dev Is Nothing Then Continue For
                    Dim isPrimaryOutput As Boolean = dev.ID.Equals(curOutputId, StringComparison.OrdinalIgnoreCase)

                    Dim mgr = dev.AudioSessionManager
                    If mgr Is Nothing Then
                        dev.Dispose()
                        Continue For
                    End If

                    Dim sessions = mgr.Sessions
                    If sessions Is Nothing Then
                        dev.Dispose()
                        Continue For
                    End If

                    openedDevices.Add((isPrimaryOutput, dev, sessions))
                    addedToOpened = True

                    For i As Integer = 0 To sessions.Count - 1
                        Try
                            Using s As AudioSessionControl = sessions(i)
                                If s Is Nothing OrElse s.IsSystemSoundsSession Then Continue For
                                If s.State = AudioSessionState.AudioSessionStateExpired Then Continue For

                                Dim pid As Integer = CInt(s.GetProcessID)
                                If pid = currentPid Then Continue For

                                Dim sessId As String = ""
                                Dim dispName As String = ""
                                If pid <= 4 OrElse Not _pidToProgramCache.ContainsKey(pid) Then
                                    Try : sessId = s.GetSessionIdentifier : Catch : End Try
                                    Try : dispName = s.DisplayName : Catch : End Try
                                End If

                                Dim info = ResolveFriendlyProgramNameCached(pid, dispName, sessId)
                                If String.IsNullOrEmpty(info.Key) Then Continue For

                                Dim matchedCh As ProgramMixerChannel = MatchChannelForProgram(channels, info.Key, pid)
                                If matchedCh Is Nothing Then Continue For

                                matchedCh.LastSeenActiveTicks = nowTicks
                                If pid > 4 Then
                                    matchedCh.AddPid(pid)
                                    If Not isPrimaryOutput AndAlso s.State = AudioSessionState.AudioSessionStateActive Then
                                        Dim lastTry As Long = 0L
                                        If Not _lastRouteAttemptTicks.TryGetValue(pid, lastTry) OrElse (nowTicks - lastTry) >= 3000L Then
                                            _lastRouteAttemptTicks(pid) = nowTicks
                                            WindowsAudioPolicyRouter.TryRouteProcessToEndpoint(pid, curOutputId)
                                        End If
                                    End If
                                End If

                                If s.State = AudioSessionState.AudioSessionStateActive Then
                                    channelHasActiveState(matchedCh.TrackId) = True
                                End If

                                Dim rawPeak As Single = 0.0F
                                Dim rawPeakL As Single = 0.0F
                                Dim rawPeakR As Single = 0.0F
                                Try
                                    rawPeak = s.AudioMeterInformation.MasterPeakValue
                                    Dim chPeaks = s.AudioMeterInformation.PeakValues
                                    If chPeaks IsNot Nothing AndAlso chPeaks.Count >= 2 Then
                                        rawPeakL = chPeaks(0)
                                        rawPeakR = chPeaks(1)
                                    Else
                                        rawPeakL = rawPeak
                                        rawPeakR = rawPeak
                                    End If
                                Catch
                                    rawPeakL = rawPeak
                                    rawPeakR = rawPeak
                                End Try
                                If Single.IsNaN(rawPeak) OrElse Single.IsInfinity(rawPeak) Then rawPeak = 0.0F
                                If Single.IsNaN(rawPeakL) OrElse Single.IsInfinity(rawPeakL) Then rawPeakL = rawPeak
                                If Single.IsNaN(rawPeakR) OrElse Single.IsInfinity(rawPeakR) Then rawPeakR = rawPeak

                                Dim sessionPeak As Single = Math.Max(0.0F, Math.Min(1.0F, rawPeak))
                                Dim sessionPeakL As Single = Math.Max(0.0F, Math.Min(1.0F, rawPeakL))
                                Dim sessionPeakR As Single = Math.Max(0.0F, Math.Min(1.0F, rawPeakR))

                                If sessionPeak > channelMaxPeaks(matchedCh.TrackId) Then
                                    channelMaxPeaks(matchedCh.TrackId) = sessionPeak
                                End If
                                If sessionPeakL > channelMaxPeaksL(matchedCh.TrackId) Then
                                    channelMaxPeaksL(matchedCh.TrackId) = sessionPeakL
                                End If
                                If sessionPeakR > channelMaxPeaksR(matchedCh.TrackId) Then
                                    channelMaxPeaksR(matchedCh.TrackId) = sessionPeakR
                                End If
                            End Using
                        Catch
                        End Try
                    Next
                Catch
                    If Not addedToOpened Then
                        Try : dev?.Dispose() : Catch : End Try
                    End If
                End Try
            Next

            Dim activeRenderChannelsCount As Integer = 0
            For i As Integer = 0 To channels.Length - 1
                If Not channels(i).ProgramKey.Equals("MIC", StringComparison.OrdinalIgnoreCase) AndAlso
                   channelMaxPeaks(channels(i).TrackId) > 0.001F Then
                    activeRenderChannelsCount += 1
                End If
            Next

            Dim talkoverMode As Integer = MixerHardwareStateStore.Current.Audio.MicTalkoverMode
            Dim micTriggerLevel As Single = 0.0F
            For i As Integer = 0 To channels.Length - 1
                If channels(i).ProgramKey.Equals("MIC", StringComparison.OrdinalIgnoreCase) Then
                    If Not channels(i).IsEffectivelyMuted AndAlso channels(i).Fader01 > 0.05F Then
                        Dim micFaderFactor As Single = If(channels(i).IsApoPostProcessedPeak, 1.0F, channels(i).Fader01)
                        micTriggerLevel = channels(i).RawSessionMeterPeak * micFaderFactor
                    End If
                    Exit For
                End If
            Next

            Dim targetDuck As Single = If(talkoverMode > 0 AndAlso micTriggerLevel > 0.032F, 1.0F, 0.0F)
            Dim curDuck As Single = Volatile.Read(_talkoverDuckEnvelope)
            If targetDuck > curDuck Then
                curDuck = curDuck * 0.35F + targetDuck * 0.65F
            Else
                curDuck = curDuck * 0.92F + targetDuck * 0.08F
                If curDuck < 0.005F Then curDuck = 0.0F
            End If
            Volatile.Write(_talkoverDuckEnvelope, curDuck)

            Dim talkoverGainMult As Single = 1.0F
            If talkoverMode = 1 AndAlso curDuck > 0.005F Then
                talkoverGainMult = 1.0F - (curDuck * 0.8F)
            ElseIf talkoverMode = 2 AndAlso curDuck > 0.005F Then
                talkoverGainMult = 1.0F - (curDuck * 0.32F)
            End If

            Const SILENT_FLOOR As Single = 0.0002F
            Dim apoActive As Boolean = NativeCppAutoBuilderAndInstaller.IsKernelApoProcessingAudio()

            For devIdx As Integer = 0 To openedDevices.Count - 1
                Dim entry = openedDevices(devIdx)
                Dim sessions As SessionCollection = entry.Sessions

                For i As Integer = 0 To sessions.Count - 1
                    Try
                        Using s As AudioSessionControl = sessions(i)
                            If s Is Nothing OrElse s.IsSystemSoundsSession Then Continue For
                            If s.State = AudioSessionState.AudioSessionStateExpired Then Continue For

                            Dim pid As Integer = CInt(s.GetProcessID)
                            If pid = currentPid Then Continue For

                            Dim sessId As String = ""
                            Dim dispName As String = ""
                            Try : sessId = s.GetSessionIdentifier : Catch : End Try
                            Try : dispName = s.DisplayName : Catch : End Try

                            Dim info = ResolveFriendlyProgramNameCached(pid, dispName, sessId)
                            If String.IsNullOrEmpty(info.Key) Then Continue For

                            Dim matchedCh As ProgramMixerChannel = MatchChannelForProgram(channels, info.Key, pid)
                            If matchedCh Is Nothing Then Continue For

                            Dim effFader01 As Single = If(matchedCh.IsEffectivelyMuted, 0.0F, matchedCh.Fader01)
                            Dim faderCurve As Single = effFader01 * effFader01
                            Dim masterCurve As Single = masterVol01 * masterVol01
                            Dim baseTargetVol As Single = faderCurve * masterCurve * talkoverGainMult

                            Dim channelWatermark As Single = 1.0F
                            If activeRenderChannelsCount > 1 AndAlso baseTargetVol > 0.05F Then
                                Dim chOrderIdx As Integer = 0
                                If channelIndexMap.TryGetValue(matchedCh.TrackId, chOrderIdx) AndAlso chOrderIdx > 0 Then
                                    channelWatermark = 1.0F - Math.Min(0.045F, (chOrderIdx - 1) * 0.0035F)
                                End If
                                Dim sinceInteract As Long = nowTicks - Interlocked.Read(matchedCh.LastUserInteractTicks)
                                If sinceInteract >= 0 AndAlso sinceInteract < 450 Then
                                    channelWatermark *= 0.985F
                                End If
                            End If

                            Dim safeTargetVol As Single
                            If baseTargetVol <= 0.0001F Then
                                safeTargetVol = 0.0F
                            Else
                                safeTargetVol = Math.Max(SILENT_FLOOR, Math.Min(1.0F, baseTargetVol * channelWatermark))
                            End If

                            Dim isFirstSessionSync As Boolean = (matchedCh.LastAppliedSessionVolume < 0.0F)

                            Try
                                If s.SimpleAudioVolume.Mute Then
                                    s.SimpleAudioVolume.Mute = False
                                End If
                                Dim curVol As Single = s.SimpleAudioVolume.Volume
                                Dim volChanged As Boolean = (Math.Abs(curVol - safeTargetVol) > 0.0015F)

                                If forceWriteAll OrElse volChanged OrElse isFirstSessionSync Then
                                    s.SimpleAudioVolume.Volume = safeTargetVol
                                End If

                                If forceWriteAll OrElse needApplyBalance OrElse isFirstSessionSync Then
                                    SessionChannelVolumeHelper.TrySetSessionChannelVolumes(s, balGainL, balGainR)
                                End If
                            Catch
                            End Try

                            matchedCh.LastAppliedSessionVolume = safeTargetVol
                        End Using
                    Catch
                    End Try
                Next
            Next

            If needApplyBalance Then
                _lastAppliedBalance = curBal01
                _balanceUserInteracted = False
            End If

            SyncNativeKernelDspParameters()
        Catch
        Finally
            For Each entry In openedDevices
                Try : entry.Dev?.Dispose() : Catch : End Try
            Next
        End Try

        Dim maxMasterPostFaderAmp As Single = 0.0F
        Dim postMasterCurve As Single = masterVol01 * masterVol01
        Dim specLow As Single = Volatile.Read(_liveSpecRatioLow)
        Dim specMid As Single = Volatile.Read(_liveSpecRatioMid)
        Dim specHigh As Single = Volatile.Read(_liveSpecRatioHigh)

        For i As Integer = 0 To channels.Length - 1
            Dim ch As ProgramMixerChannel = channels(i)
            If Not ch.ProgramKey.Equals("MIC", StringComparison.OrdinalIgnoreCase) Then
                ch.UpdateMetersFromStereoSessionPeak(
                    channelMaxPeaksL(ch.TrackId),
                    channelMaxPeaksR(ch.TrackId),
                    specLow, specMid, specHigh)
            End If

            Dim effFdr As Single = If(ch.IsEffectivelyMuted, 0.0F, If(ch.IsApoPostProcessedPeak, 1.0F, ch.Fader01))
            Dim fdrCurveVal As Single = effFdr * effFdr
            Dim postAmp As Single = ch.GetPostEqPreFaderLinearAmplitude() * fdrCurveVal * postMasterCurve
            If postAmp > maxMasterPostFaderAmp Then
                maxMasterPostFaderAmp = postAmp
            End If
        Next

        Dim hwPeak As Single = Volatile.Read(_liveOutputHardwarePeak)
        Volatile.Write(_liveOutputHardwarePeak, hwPeak * 0.85F)

        Dim levelSrc As String = If(MixerHardwareStateStore.CurrentMasterLevelSource, "Auto")
        If levelSrc.Equals("Hardware", StringComparison.OrdinalIgnoreCase) Then
            maxMasterPostFaderAmp = Math.Max(maxMasterPostFaderAmp, hwPeak * postMasterCurve)
        ElseIf levelSrc.Equals("Auto", StringComparison.OrdinalIgnoreCase) AndAlso maxMasterPostFaderAmp < 0.0005F AndAlso hwPeak > 0.002F Then
            maxMasterPostFaderAmp = hwPeak * postMasterCurve
        End If

        If _masterEffects IsNot Nothing Then
            Dim maxPflAll As Single = 0.0F
            For i As Integer = 0 To channels.Length - 1
                If channels(i).ChannelPeakPfl > maxPflAll Then
                    maxPflAll = channels(i).ChannelPeakPfl
                End If
            Next

            Dim finalMasterL As Single = ProgramMixerChannel.LinearToPioneerMeter01(maxMasterPostFaderAmp * balGainL)
            Dim finalMasterR As Single = ProgramMixerChannel.LinearToPioneerMeter01(maxMasterPostFaderAmp * balGainR)

            Volatile.Write(_masterEffects.ExternalDirectPeakPfl, maxPflAll)
            Volatile.Write(_masterEffects.ExternalDirectPeakL, finalMasterL)
            Volatile.Write(_masterEffects.ExternalDirectPeakR, finalMasterR)
            Volatile.Write(_masterEffects.CurrentChannelPeak, maxPflAll)
            Volatile.Write(_masterEffects.CurrentPeakL, finalMasterL)
            Volatile.Write(_masterEffects.CurrentPeakR, finalMasterR)
        End If
    End Sub

    Private Shared Function MatchChannelForProgram(channels() As ProgramMixerChannel, progKey As String, pid As Integer) As ProgramMixerChannel
        For cIdx As Integer = 0 To channels.Length - 1
            Dim candidate As ProgramMixerChannel = channels(cIdx)
            If candidate.ProgramKey.Equals(progKey, StringComparison.OrdinalIgnoreCase) OrElse
               (pid > 4 AndAlso candidate.ContainsPid(pid)) Then
                Return candidate
            End If
        Next
        If channels.Length = 1 AndAlso channels(0).ProgramKey = "SYSTEM" Then
            Return channels(0)
        End If
        Return Nothing
    End Function

    Private Function CheckBluetoothHeadphonesHotplug() As Boolean
        If _isDisposed OrElse Volatile.Read(_isInstallingApo) <> 0 Then Return False

        Dim nowTicks As Long = Environment.TickCount64
        If (nowTicks - Interlocked.Read(_lastBtHotplugProbeTicks)) < 2500L Then Return False
        Interlocked.Exchange(_lastBtHotplugProbeTicks, nowTicks)

        Try
            ' 1. Горячее подключение студийного микрофона (работает независимо от настройки Bluetooth)
            If Not IsAsioActive AndAlso (_micCapture Is Nothing OrElse _micCaptureDevice Is Nothing) Then
                Dim probeMic As MMDevice = FindQuadCapture12MicDevice()
                If probeMic IsNot Nothing Then
                    Try : probeMic.Dispose() : Catch : End Try
                    If _uiContext IsNot Nothing AndAlso SynchronizationContext.Current IsNot _uiContext Then
                        _uiContext.Post(
                            Sub(st)
                                SyncLock _engineLock
                                    If Not _isDisposed AndAlso Not IsAsioActive AndAlso (_micCapture Is Nothing OrElse _micCaptureDevice Is Nothing) Then
                                        StartQuadCaptureMicEngine()
                                    End If
                                End SyncLock
                            End Sub, Nothing)
                    ElseIf Monitor.TryEnter(_engineLock, 0) Then
                        Try
                            If Not _isDisposed AndAlso Not IsAsioActive AndAlso (_micCapture Is Nothing OrElse _micCaptureDevice Is Nothing) Then
                                StartQuadCaptureMicEngine()
                            End If
                        Finally
                            Monitor.Exit(_engineLock)
                        End Try
                    End If
                End If
            End If

            ' 2. Горячее переключение Bluetooth Hi-Res / LDAC наушников и возврат при отключении активного выхода
            Dim audioCfg = MixerHardwareStateStore.Current.Audio
            RefreshEndpointsCache()
            Dim curOutId As String = Volatile.Read(_cachedOutputId)

            If audioCfg.PreferBluetoothHeadphonesOutput Then
                Dim btEp As AudioEndpointInfo = Nothing
                Dim btOutId As String = FindHighQualityBluetoothOutputId(btEp)
                If Not String.IsNullOrEmpty(btOutId) AndAlso Not String.Equals(curOutId, btOutId, StringComparison.OrdinalIgnoreCase) Then
                    StartEngine(Nothing, btOutId, False)
                    Return True
                End If
            End If

            If Not String.IsNullOrEmpty(curOutId) Then
                Dim eps As List(Of AudioEndpointInfo) = Volatile.Read(_cachedEndpoints)
                Dim currentStillActive As Boolean = (eps IsNot Nothing AndAlso eps.Any(Function(e) e.Id.Equals(curOutId, StringComparison.OrdinalIgnoreCase)))
                If Not currentStillActive Then
                    StartEngine(Nothing, Nothing, False)
                    Return True
                End If
            End If
        Catch
        End Try
        Return False
    End Function

    Private Function DiscoverAndSyncProgramChannels(workerEnumerator As MMDeviceEnumerator) As Boolean
        If _isDisposed OrElse _model?.Tracks Is Nothing Then Return False
        If CheckBluetoothHeadphonesHotplug() Then Return True

        Dim currentPid As Integer = Environment.ProcessId
        Dim progPids As New Dictionary(Of String, HashSet(Of Integer))(StringComparer.OrdinalIgnoreCase)
        Dim progRawNames As New Dictionary(Of String, HashSet(Of String))(StringComparer.OrdinalIgnoreCase)
        Dim progTitles As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Dim progIsActive As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
        Dim progOrder As New List(Of String)()

        Try
            For Each dev In workerEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                Dim sessions As SessionCollection = Nothing
                Try
                    Dim mgr = dev.AudioSessionManager
                    If mgr Is Nothing Then Continue For
                    sessions = mgr.Sessions
                    If sessions Is Nothing Then Continue For

                    For i As Integer = 0 To sessions.Count - 1
                        Dim s As AudioSessionControl = Nothing
                        Try
                            s = sessions(i)
                            If s Is Nothing OrElse s.IsSystemSoundsSession Then Continue For
                            If s.State = AudioSessionState.AudioSessionStateExpired Then Continue For

                            Dim pid As Integer = CInt(s.GetProcessID)
                            If pid = currentPid Then Continue For

                            Dim sessId As String = ""
                            Dim dispName As String = ""
                            Try : sessId = s.GetSessionIdentifier : Catch : End Try
                            Try : dispName = s.DisplayName : Catch : End Try

                            Dim info = ResolveFriendlyProgramNameCached(pid, dispName, sessId)
                            If String.IsNullOrEmpty(info.Key) Then Continue For

                            Dim isSessActive As Boolean = (s.State = AudioSessionState.AudioSessionStateActive)
                            If Not progPids.ContainsKey(info.Key) Then
                                progPids(info.Key) = New HashSet(Of Integer)()
                                progRawNames(info.Key) = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                                progTitles(info.Key) = info.Title
                                progIsActive(info.Key) = isSessActive
                                progOrder.Add(info.Key)
                            ElseIf isSessActive Then
                                progIsActive(info.Key) = True
                            End If

                            If pid > 4 Then progPids(info.Key).Add(pid)
                            If Not String.IsNullOrWhiteSpace(info.RawProcessName) Then
                                progRawNames(info.Key).Add(info.RawProcessName)
                            End If
                        Catch
                        Finally
                            Try : s?.Dispose() : Catch : End Try
                        End Try
                    Next
                Catch
                Finally
                    Try : dev.Dispose() : Catch : End Try
                End Try
            Next
        Catch
        End Try

        Dim activeProgOrder As New List(Of String)()
        For Each pk In progOrder
            Dim alreadyExists As Boolean = False
            SyncLock _channelsLock
                alreadyExists = _programChannels.Values.Any(Function(c) c.ProgramKey.Equals(pk, StringComparison.OrdinalIgnoreCase))
            End SyncLock
            If progIsActive(pk) OrElse alreadyExists Then
                activeProgOrder.Add(pk)
            End If
        Next
        If activeProgOrder.Count = 0 AndAlso progOrder.Count > 0 Then
            activeProgOrder.Add(progOrder(0))
        End If

        Dim modelChanged As Boolean = False

        SyncLock _channelsLock
            SyncLock _model.SyncRoot
                If activeProgOrder.Count > 0 AndAlso _model.Tracks.Count >= 1 Then
                    For tIdx As Integer = 0 To _model.Tracks.Count - 1
                        Dim candTrack As TrackModel = _model.Tracks(tIdx)
                        Dim candCh As ProgramMixerChannel = Nothing
                        If _programChannels.TryGetValue(candTrack.Id, candCh) AndAlso candCh IsNot Nothing Then
                            If (candCh.ProgramKey = "SYSTEM" OrElse candTrack.Name = "SYS MIX") AndAlso candCh.ProgramKey <> "MIC" AndAlso
                               Not _programChannels.Values.Any(Function(c) c.ProgramKey.Equals(activeProgOrder(0), StringComparison.OrdinalIgnoreCase)) Then
                                Dim firstKey As String = activeProgOrder(0)
                                Dim savedProf As ChannelProfileConfig = MixerHardwareStateStore.GetOrCreateChannelProfile(firstKey)

                                candCh.ProgramKey = firstKey
                                candCh.DisplayName = progTitles(firstKey)
                                candTrack.Name = progTitles(firstKey)
                                candTrack.Trim = savedProf.Trim
                                candTrack.EqHi = savedProf.EqHi
                                candTrack.EqMid = savedProf.EqMid
                                candTrack.EqLow = savedProf.EqLow
                                candTrack.ColorFx = savedProf.ColorFx
                                candTrack.ColorFxMode = savedProf.ColorFxMode
                                candTrack.WaveformZoom = savedProf.WaveformZoom
                                candTrack.IsMuted = savedProf.IsMuted
                                candTrack.IsSolo = savedProf.IsSolo
                                If candTrack.Clips.Count > 0 Then
                                    candTrack.Clips(0).Volume = savedProf.Fader
                                End If

                                candCh.SetChannelFader(savedProf.Fader, userInitiated:=False)
                                candCh.SetEqAndTrim(
                                    yoump.MediaParameterBroker.ValueToDecibels(savedProf.EqLow),
                                    yoump.MediaParameterBroker.ValueToDecibels(savedProf.EqMid),
                                    yoump.MediaParameterBroker.ValueToDecibels(savedProf.EqHi),
                                    savedProf.Trim * 2.0F,
                                    userInitiated:=False)
                                candCh.SetColorFilter(savedProf.ColorFx, userInitiated:=False)
                                candCh.ColorFxMode = savedProf.ColorFxMode
                                candCh.IsMuted = savedProf.IsMuted
                                candCh.IsSolo = savedProf.IsSolo

                                modelChanged = True
                                Exit For
                            End If
                        End If
                    Next
                End If

                For Each progKey In activeProgOrder
                    Dim existingCh As ProgramMixerChannel = _programChannels.Values.FirstOrDefault(
                        Function(c) c.ProgramKey.Equals(progKey, StringComparison.OrdinalIgnoreCase))

                    If existingCh Is Nothing Then
                        Dim savedProf As ChannelProfileConfig = MixerHardwareStateStore.GetOrCreateChannelProfile(progKey)

                        Dim newTrack As New TrackModel() With {
                            .Name = progTitles(progKey),
                            .Type = TargetFormatType.Audio,
                            .Trim = savedProf.Trim,
                            .EqHi = savedProf.EqHi,
                            .EqMid = savedProf.EqMid,
                            .EqLow = savedProf.EqLow,
                            .ColorFx = savedProf.ColorFx,
                            .ColorFxMode = savedProf.ColorFxMode,
                            .WaveformZoom = savedProf.WaveformZoom,
                            .IsMuted = savedProf.IsMuted,
                            .IsSolo = savedProf.IsSolo
                        }
                        newTrack.Clips.Add(New ClipModel() With {
                            .TimelineStart = TimeSpan.Zero,
                            .TimelineEnd = TimeSpan.FromSeconds(60),
                            .Volume = savedProf.Fader
                        })
                        _model.Tracks.Add(newTrack)

                        existingCh = New ProgramMixerChannel(newTrack.Id, progKey, progTitles(progKey))
                        existingCh.SetChannelFader(savedProf.Fader, userInitiated:=False)
                        existingCh.SetEqAndTrim(
                            yoump.MediaParameterBroker.ValueToDecibels(savedProf.EqLow),
                            yoump.MediaParameterBroker.ValueToDecibels(savedProf.EqMid),
                            yoump.MediaParameterBroker.ValueToDecibels(savedProf.EqHi),
                            savedProf.Trim * 2.0F,
                            userInitiated:=False)
                        existingCh.SetColorFilter(savedProf.ColorFx, userInitiated:=False)
                        existingCh.ColorFxMode = savedProf.ColorFxMode
                        existingCh.IsMuted = savedProf.IsMuted
                        existingCh.IsSolo = savedProf.IsSolo
                        _programChannels(newTrack.Id) = existingCh
                        modelChanged = True
                    End If

                    existingCh.SyncProcessIds(progPids(progKey), progRawNames(progKey))
                Next

                If _model.Tracks.Count > 1 Then
                    For idx As Integer = _model.Tracks.Count - 1 To 0 Step -1
                        If _model.Tracks.Count <= 1 Then Exit For
                        Dim tr As TrackModel = _model.Tracks(idx)
                        Dim ch As ProgramMixerChannel = Nothing
                        If _programChannels.TryGetValue(tr.Id, ch) AndAlso ch IsNot Nothing Then
                            If Not progPids.ContainsKey(ch.ProgramKey) Then
                                Dim isProcAlive As Boolean = ch.IsAnyProcessAliveNow()

                                If Not isProcAlive AndAlso ch.IsSolo AndAlso ch.ProgramKey <> "MIC" AndAlso ch.ProgramKey <> "SYSTEM" Then
                                    ch.IsSolo = False
                                    tr.IsSolo = False
                                    modelChanged = True
                                End If

                                If Not ch.IsProgramStillRunningInWindows(isProcAlive) Then
                                    _programChannels.Remove(tr.Id)
                                    _model.Tracks.RemoveAt(idx)
                                    modelChanged = True
                                End If
                            End If
                        End If
                    Next
                End If

                If _model.Tracks.Count > 1 Then
                    Dim sortedTracks = _model.Tracks.OrderBy(Function(t) MixerHardwareStateStore.GetPinnedSortOrder(t.Name)).ToList()
                    Dim orderDiffers As Boolean = False
                    For i As Integer = 0 To sortedTracks.Count - 1
                        If Not Object.ReferenceEquals(_model.Tracks(i), sortedTracks(i)) Then
                            orderDiffers = True
                            Exit For
                        End If
                    Next
                    If orderDiffers Then
                        _model.Tracks.Clear()
                        _model.Tracks.AddRange(sortedTracks)
                        modelChanged = True
                    End If
                End If

                Dim orderedChannels As New List(Of ProgramMixerChannel)(_model.Tracks.Count)
                For idx As Integer = 0 To _model.Tracks.Count - 1
                    Dim ch As ProgramMixerChannel = Nothing
                    If _programChannels.TryGetValue(_model.Tracks(idx).Id, ch) AndAlso ch IsNot Nothing Then
                        orderedChannels.Add(ch)
                    End If
                Next
                Volatile.Write(_channelsSnapshot, orderedChannels.ToArray())
                RecalculateEffectiveMuteStates()
            End SyncLock
        End SyncLock

        If modelChanged Then
            MixerHardwareStateStore.Save(_model, Me)
            If _uiContext IsNot Nothing Then
                _uiContext.Post(Sub(st) _model.NotifyStateChanged(), Nothing)
            Else
                _model.NotifyStateChanged()
            End If
        End If
        Return False
    End Function

    Private Function GetChannelOrFirst(trackId As Guid) As ProgramMixerChannel
        SyncLock _channelsLock
            Dim ch As ProgramMixerChannel = Nothing
            If trackId <> Guid.Empty AndAlso _programChannels.TryGetValue(trackId, ch) Then
                Return ch
            End If
            Dim snap As ProgramMixerChannel() = Volatile.Read(_channelsSnapshot)
            If snap.Length > 0 Then Return snap(0)
            Return _programChannels.Values.FirstOrDefault()
        End SyncLock
    End Function

    Private Function GetChannelByIndex(trackIndex As Integer) As ProgramMixerChannel
        Dim snap As ProgramMixerChannel() = Volatile.Read(_channelsSnapshot)
        If trackIndex >= 0 AndAlso trackIndex < snap.Length Then
            Return snap(trackIndex)
        End If
        If snap.Length > 0 Then Return snap(0)
        Return GetChannelOrFirst(Guid.Empty)
    End Function

    Public Sub SetChannelFader(fader01 As Single)
        Dim f As Single = If(fader01 > 1.001F, fader01 / 100.0F, fader01)
        f = Math.Max(0.0F, Math.Min(1.0F, f))
        GetChannelOrFirst(Guid.Empty)?.SetChannelFader(f, userInitiated:=True)
        TriggerFastSessionSync(forceVolumeWrite:=True)
    End Sub

    Public Sub SetTrackVolume(trackId As Guid, newVol As Single)
        If trackId <> Guid.Empty Then _focusedTrackId = trackId
        Dim f As Single = If(newVol > 1.001F, newVol / 100.0F, newVol)
        f = Math.Max(0.0F, Math.Min(1.0F, f))
        GetChannelOrFirst(trackId)?.SetChannelFader(f, userInitiated:=True)
        TriggerFastSessionSync(forceVolumeWrite:=True)
    End Sub

    Public Sub SetTrackVolume(volume As Single)
        SetChannelFader(volume)
    End Sub

    Public Sub SetTrackVolume(trackIndex As Integer, volume As Single)
        Dim f As Single = If(volume > 1.001F, volume / 100.0F, volume)
        f = Math.Max(0.0F, Math.Min(1.0F, f))
        Dim ch As ProgramMixerChannel = GetChannelByIndex(trackIndex)
        If ch IsNot Nothing Then
            _focusedTrackId = ch.TrackId
            ch.SetChannelFader(f, userInitiated:=True)
        End If
        TriggerFastSessionSync(forceVolumeWrite:=True)
    End Sub

    Public Sub SetTrackEQAndTrim(lowDb As Single, midDb As Single, hiDb As Single, trimGain0To2 As Single)
        GetChannelOrFirst(_focusedTrackId)?.SetEqAndTrim(lowDb, midDb, hiDb, trimGain0To2, userInitiated:=True)
        TriggerFastSessionSync(forceVolumeWrite:=False)
    End Sub

    Public Sub SetTrackEQAndTrim(trackId As Guid, lowDb As Single, midDb As Single, hiDb As Single, trimGain0To2 As Single)
        If trackId <> Guid.Empty Then _focusedTrackId = trackId
        GetChannelOrFirst(trackId)?.SetEqAndTrim(lowDb, midDb, hiDb, trimGain0To2, userInitiated:=True)
        TriggerFastSessionSync(forceVolumeWrite:=False)
    End Sub

    Public Sub SetTrackEQAndTrim(trackIndex As Integer, lowDb As Single, midDb As Single, hiDb As Single, trimGain0To2 As Single)
        Dim ch As ProgramMixerChannel = GetChannelByIndex(trackIndex)
        If ch IsNot Nothing Then
            _focusedTrackId = ch.TrackId
            ch.SetEqAndTrim(lowDb, midDb, hiDb, trimGain0To2, userInitiated:=True)
        End If
        TriggerFastSessionSync(forceVolumeWrite:=False)
    End Sub

    Public Sub SetColorKnob(knobValue01 As Single)
        GetChannelOrFirst(_focusedTrackId)?.SetColorFilter(knobValue01, userInitiated:=True)
        TriggerFastSessionSync(forceVolumeWrite:=False)
    End Sub

    Public Sub SetTrackColorFilter(trackId As Guid, colorFx As Single)
        If trackId <> Guid.Empty Then _focusedTrackId = trackId
        GetChannelOrFirst(trackId)?.SetColorFilter(colorFx, userInitiated:=True)
        TriggerFastSessionSync(forceVolumeWrite:=False)
    End Sub

    Public Sub SetTrackColorFilter(colorFx As Single)
        SetColorKnob(colorFx)
    End Sub

    Public Sub SetTrackColorFilter(trackIndex As Integer, colorFx As Single)
        Dim ch As ProgramMixerChannel = GetChannelByIndex(trackIndex)
        If ch IsNot Nothing Then
            _focusedTrackId = ch.TrackId
            ch.SetColorFilter(colorFx, userInitiated:=True)
        End If
        TriggerFastSessionSync(forceVolumeWrite:=False)
    End Sub

    Public Sub SetTrackColorFxMode(trackId As Guid, mode As Integer)
        If trackId <> Guid.Empty Then _focusedTrackId = trackId
        Dim ch As ProgramMixerChannel = GetChannelOrFirst(trackId)
        If ch IsNot Nothing Then
            ch.ColorFxMode = mode
        End If
        TriggerFastSessionSync(forceVolumeWrite:=False)
    End Sub

    Public Sub SetTrackMute(trackId As Guid, isMuted As Boolean)
        If trackId <> Guid.Empty Then _focusedTrackId = trackId
        Dim ch As ProgramMixerChannel = GetChannelOrFirst(trackId)
        If ch IsNot Nothing Then
            ch.IsMuted = isMuted
        End If
        RecalculateEffectiveMuteStates()
        TriggerFastSessionSync(forceVolumeWrite:=True)
    End Sub

    Public Sub SetTrackSolo(trackId As Guid, isSolo As Boolean)
        If trackId <> Guid.Empty Then _focusedTrackId = trackId
        Dim ch As ProgramMixerChannel = GetChannelOrFirst(trackId)
        If ch IsNot Nothing Then
            ch.IsSolo = isSolo
        End If
        RecalculateEffectiveMuteStates()
        TriggerFastSessionSync(forceVolumeWrite:=True)
    End Sub

    Public Sub SetMasterBalance(balance01 As Single)
        Dim b As Single = Math.Max(0.0F, Math.Min(1.0F, balance01))
        Dim prev As Single = Volatile.Read(_balance)
        Volatile.Write(_balance, b)
        _balanceEffect.Balance = b
        If Math.Abs(b - prev) > 0.002F Then
            _balanceUserInteracted = True
            TriggerFastSessionSync(forceVolumeWrite:=True)
        End If
    End Sub

    Public Sub SetBalance(balFloat As Single)
        SetMasterBalance(balFloat)
    End Sub

    Public Function GetChannelPreFaderLevel() As Single
        Dim ch As ProgramMixerChannel = GetChannelOrFirst(Guid.Empty)
        Return If(ch IsNot Nothing, ch.ChannelPeakPfl, 0.0F)
    End Function

    Public Function GetChannelPreFaderLevel(trackId As Guid) As Single
        Dim ch As ProgramMixerChannel = GetChannelOrFirst(trackId)
        Return If(ch IsNot Nothing, ch.ChannelPeakPfl, 0.0F)
    End Function

    Public Function GetChannelPostFaderLevel(trackId As Guid) As Single
        Dim ch As ProgramMixerChannel = GetChannelOrFirst(trackId)
        If ch Is Nothing OrElse ch.IsEffectivelyMuted Then Return 0.0F

        Dim faderCurve As Single = If(ch.IsApoPostProcessedPeak, 1.0F, ch.Fader01 * ch.Fader01)
        Dim postFaderAmp As Single = ch.GetPostEqPreFaderLinearAmplitude() * faderCurve
        Return ProgramMixerChannel.LinearToPioneerMeter01(postFaderAmp)
    End Function

    Public Function GetWaveformSnapshot() As Single()
        Dim ch As ProgramMixerChannel = GetChannelOrFirst(Guid.Empty)
        If ch IsNot Nothing Then Return ch.GetWaveSnapshot()
        Return New Single(199) {}
    End Function

    Public Function GetLiveWaveSnapshot(Optional count As Integer = 200) As Single()
        Return GetWaveformSnapshot()
    End Function

    Public Function GetLiveWaveSnapshot(trackId As Guid) As Single()
        Dim ch As ProgramMixerChannel = GetChannelOrFirst(trackId)
        If ch IsNot Nothing Then Return ch.GetWaveSnapshot()
        Return New Single(199) {}
    End Function

    Public Function TryGetThreeBandLiveWaveSnapshot(trackId As Guid,
                                                    ByRef lowPeaks As Single(),
                                                    ByRef midPeaks As Single(),
                                                    ByRef highPeaks As Single(),
                                                    Optional ByRef lastPushTicks As Long = 0L,
                                                    Optional ByRef peakTop As Single() = Nothing,
                                                    Optional ByRef peakBot As Single() = Nothing,
                                                    Optional ByRef transients As Single() = Nothing) As Boolean
        Dim ch As ProgramMixerChannel = GetChannelOrFirst(trackId)
        If ch IsNot Nothing Then
            ch.GetThreeBandWaveSnapshot(lowPeaks, midPeaks, highPeaks, peakTop, peakBot, transients)
            lastPushTicks = ch.LastWaveAdvanceTicks
            Return (lowPeaks IsNot Nothing AndAlso lowPeaks.Length > 0)
        End If
        lowPeaks = Array.Empty(Of Single)()
        midPeaks = Array.Empty(Of Single)()
        highPeaks = Array.Empty(Of Single)()
        peakTop = Array.Empty(Of Single)()
        peakBot = Array.Empty(Of Single)()
        transients = Array.Empty(Of Single)()
        lastPushTicks = 0L
        Return False
    End Function

    Private Sub RestoreWindowsSessionsOnShutdown()
        Try
            Dim currentPid As Integer = Environment.ProcessId
            For Each dev In _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                Try
                    Dim sessions = dev.AudioSessionManager?.Sessions
                    If sessions Is Nothing Then Continue For
                    For i As Integer = 0 To sessions.Count - 1
                        Try
                            Using s As AudioSessionControl = sessions(i)
                                If s Is Nothing OrElse s.IsSystemSoundsSession Then Continue For
                                Dim pid As Integer = CInt(s.GetProcessID)
                                If pid <= 4 OrElse pid = currentPid Then Continue For
                                If s.SimpleAudioVolume.Mute Then
                                    s.SimpleAudioVolume.Mute = False
                                End If
                                s.SimpleAudioVolume.Volume = 1.0F
                                SessionChannelVolumeHelper.TrySetSessionChannelVolumes(s, 1.0F, 1.0F)
                            End Using
                        Catch
                        End Try
                    Next
                Catch
                Finally
                    Try : dev.Dispose() : Catch : End Try
                End Try
            Next
        Catch
        End Try
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _isDisposed Then Return
        _isDisposed = True

        Try : _syncSignal.Set() : Catch : End Try
        Dim worker As Thread = _sessionWorkerThread
        If worker IsNot Nothing AndAlso worker.IsAlive AndAlso Not Object.ReferenceEquals(worker, Thread.CurrentThread) Then
            Try : worker.Join(1500) : Catch : End Try
        End If

        RestoreWindowsSessionsOnShutdown()
        NativeCppAutoBuilderAndInstaller.ResetSharedMemoryToNeutral()
        StopEngine()

        SyncLock _channelsLock
            _programChannels.Clear()
            Volatile.Write(_channelsSnapshot, Array.Empty(Of ProgramMixerChannel)())
        End SyncLock

        Try : _enumerator.Dispose() : Catch : End Try
        Try : _syncSignal.Dispose() : Catch : End Try
    End Sub

    Private Class DummySilentProvider
        Implements ISampleProvider

        Private ReadOnly _format As WaveFormat

        Public Sub New(sampleRate As Integer)
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)
        End Sub

        Public ReadOnly Property WaveFormat As WaveFormat Implements ISampleProvider.WaveFormat
            Get
                Return _format
            End Get
        End Property

        Public Function Read(buffer() As Single, offset As Integer, count As Integer) As Integer Implements ISampleProvider.Read
            Array.Clear(buffer, offset, count)
            Return count
        End Function
    End Class
End Class

Public Class DdjSb3MidiController
    Implements IDisposable

    Private ReadOnly _model As ProjectModel
    Private ReadOnly _player As NAudioSyncPlayer
    Private ReadOnly _masterBus As MasterBusRenderer
    Private ReadOnly _broker As yoump.MediaParameterBroker

    Private _midiIn As MidiIn
    Private _connectedDeviceIndex As Integer = -1
    Private _connectedDeviceName As String = "Не подключен"
    Private _connectTimestampTicks As Long = 0
    Private _cachedMidiDevices As New List(Of (Index As Integer, Name As String))()
    Private ReadOnly _syncLock As New Object()
    Private _isDisposed As Boolean = False

    Private ReadOnly _ccMsb(15, 31) As Integer
    Private ReadOnly _ccIs14Bit(15, 31) As Boolean
    Private ReadOnly _shiftHeld(3) As Boolean
    Private _activeLeftDeck As Integer = 0
    Private _activeRightDeck As Integer = 1

    Public Property CrossfaderControlsBalance As Boolean = False

    Public Event ConnectionChanged(deviceName As String, isConnected As Boolean)

    Public Sub New(model As ProjectModel,
                   player As NAudioSyncPlayer,
                   masterBus As MasterBusRenderer,
                   broker As yoump.MediaParameterBroker,
                   Optional autoStart As Boolean = False)
        _model = model
        _player = player
        _masterBus = masterBus
        _broker = broker

        Me.CrossfaderControlsBalance = MixerHardwareStateStore.Current.Midi.CrossfaderControlsBalance
        ResetMidiStateCache()

        If autoStart Then
            TryAutoConnect()
        End If
    End Sub

    Private Sub ResetMidiStateCache()
        For ch As Integer = 0 To 15
            For cc As Integer = 0 To 31
                Volatile.Write(_ccMsb(ch, cc), -1)
                Volatile.Write(_ccIs14Bit(ch, cc), False)
            Next
        Next
        For d As Integer = 0 To 3
            Volatile.Write(_shiftHeld(d), False)
        Next
        Volatile.Write(_activeLeftDeck, 0)
        Volatile.Write(_activeRightDeck, 1)
    End Sub

    Public ReadOnly Property IsConnected As Boolean
        Get
            Return _midiIn IsNot Nothing AndAlso Volatile.Read(_connectedDeviceIndex) >= 0
        End Get
    End Property

    Public ReadOnly Property ConnectedDeviceName As String
        Get
            Return _connectedDeviceName
        End Get
    End Property

    Public ReadOnly Property ConnectedDeviceIndex As Integer
        Get
            Return Volatile.Read(_connectedDeviceIndex)
        End Get
    End Property

    Public Function GetCachedMidiInDevices() As List(Of (Index As Integer, Name As String))
        Return Volatile.Read(_cachedMidiDevices)
    End Function

    Public Function TryAutoConnect() As Boolean
        SyncLock _syncLock
            If _isDisposed Then Return False
            If _midiIn IsNot Nothing AndAlso Volatile.Read(_connectedDeviceIndex) >= 0 Then Return True

            Try
                Dim count As Integer = MidiIn.NumberOfDevices
                Dim list As New List(Of (Index As Integer, Name As String))()
                For i As Integer = 0 To count - 1
                    Try
                        list.Add((i, MidiIn.DeviceInfo(i).ProductName))
                    Catch
                    End Try
                Next
                Volatile.Write(_cachedMidiDevices, list)

                If list.Count <= 0 Then
                    DisconnectInternal()
                    Return False
                End If

                Dim midiCfg = MixerHardwareStateStore.Current.Midi
                Dim bestIdx As Integer = -1

                ' 1. Сначала ищем приоритетные DJ-контроллеры (DDJ-SB3, Pioneer), игнорируя заблокированные MIDI-порты
                If midiCfg.PreferredControllerNames IsNot Nothing Then
                    For Each prefName In midiCfg.PreferredControllerNames
                        If String.IsNullOrWhiteSpace(prefName) Then Continue For
                        For Each item In list
                            If Not midiCfg.IsBlockedMidiDevice(item.Name) AndAlso
                               item.Name.IndexOf(prefName.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 Then
                                bestIdx = item.Index
                                Exit For
                            End If
                        Next
                        If bestIdx >= 0 Then Exit For
                    Next
                End If

                ' 2. Если не нашли, проверяем последнее подключенное устройство
                If bestIdx < 0 AndAlso Not String.IsNullOrWhiteSpace(midiCfg.LastConnectedDeviceName) AndAlso
                   Not midiCfg.IsBlockedMidiDevice(midiCfg.LastConnectedDeviceName) Then
                    For Each item In list
                        If item.Name.Equals(midiCfg.LastConnectedDeviceName, StringComparison.OrdinalIgnoreCase) Then
                            bestIdx = item.Index
                            Exit For
                        End If
                    Next
                End If

                ' 3. Fallback: берем первое доступное MIDI-устройство не из блок-листа
                If bestIdx < 0 Then
                    For Each item In list
                        If Not midiCfg.IsBlockedMidiDevice(item.Name) Then
                            bestIdx = item.Index
                            Exit For
                        End If
                    Next
                End If

                If bestIdx >= 0 Then
                    Return ConnectDevice(bestIdx)
                End If
            Catch
            End Try
            Return False
        End SyncLock
    End Function

    Public Function ConnectDevice(deviceIndex As Integer, Optional forceReconnect As Boolean = False) As Boolean
        SyncLock _syncLock
            If _isDisposed Then Return False
            If Not forceReconnect AndAlso _midiIn IsNot Nothing AndAlso Volatile.Read(_connectedDeviceIndex) = deviceIndex Then
                Return True
            End If

            Dim hadActivePort As Boolean = (_midiIn IsNot Nothing)
            DisconnectInternal()
            ResetMidiStateCache()
            If hadActivePort Then
                Thread.Sleep(25)
            End If

            Try
                Dim prodName As String = ""
                Dim cachedList = Volatile.Read(_cachedMidiDevices)
                If cachedList IsNot Nothing Then
                    For Each cached In cachedList
                        If cached.Index = deviceIndex Then
                            prodName = cached.Name
                            Exit For
                        End If
                    Next
                End If

                If String.IsNullOrWhiteSpace(prodName) Then
                    Try
                        If deviceIndex >= 0 AndAlso deviceIndex < MidiIn.NumberOfDevices Then
                            prodName = MidiIn.DeviceInfo(deviceIndex).ProductName
                        End If
                    Catch
                    End Try
                End If
                If String.IsNullOrWhiteSpace(prodName) Then
                    prodName = $"MIDI {deviceIndex}"
                End If

                ' Предварительно включаем 14-битный режим MSB+LSB только для аппаратных контроллеров серии DDJ-SB/SR/SX/SZ,
                ' а для любых других контроллеров 14-битный режим активируется автоматически при приходе первого LSB (CC 32..63)
                Dim isStrict14BitDdj As Boolean = (prodName.IndexOf("DDJ-SB", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                                   prodName.IndexOf("DDJ-SR", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                                   prodName.IndexOf("DDJ-SX", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                                   prodName.IndexOf("DDJ-SZ", StringComparison.OrdinalIgnoreCase) >= 0)
                If isStrict14BitDdj Then
                    For ch As Integer = 0 To 15
                        For cc As Integer = 0 To 31
                            If IsSupported14BitMsbCc(cc) Then
                                Volatile.Write(_ccIs14Bit(ch, cc), True)
                            End If
                        Next
                    Next
                End If

                _midiIn = New MidiIn(deviceIndex)
                AddHandler _midiIn.MessageReceived, AddressOf OnMidiMessageReceived
                AddHandler _midiIn.ErrorReceived, AddressOf OnMidiErrorReceived
                _midiIn.Start()

                Volatile.Write(_connectedDeviceIndex, deviceIndex)
                _connectedDeviceName = prodName

                If MixerHardwareStateStore.Current.Midi.SendSeratoStartupSysEx Then
                    Interlocked.Exchange(_connectTimestampTicks, Environment.TickCount64)
                    Dim targetName As String = prodName
                    Task.Run(Sub()
                                 Try
                                     Thread.Sleep(50)
                                     If _isDisposed Then Return

                                     Dim midiCfg = MixerHardwareStateStore.Current.Midi
                                     Dim outCount As Integer = MidiOut.NumberOfDevices
                                     Dim targetOutIdx As Integer = -1
                                     For i As Integer = 0 To outCount - 1
                                         Dim outName As String = MidiOut.DeviceInfo(i).ProductName
                                         If Not midiCfg.IsBlockedMidiDevice(outName) AndAlso
                                            (outName.Equals(targetName, StringComparison.OrdinalIgnoreCase) OrElse
                                             outName.IndexOf("DDJ-SB3", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                             outName.IndexOf("DDJ", StringComparison.OrdinalIgnoreCase) >= 0) Then
                                             targetOutIdx = i
                                             Exit For
                                         End If
                                     Next

                                     If targetOutIdx >= 0 AndAlso Not _isDisposed Then
                                         Interlocked.Exchange(_connectTimestampTicks, Environment.TickCount64)
                                         Dim seratoDumpReq As Byte() = {&HF0, &H0, &H20, &H7F, &H3, &H1, &HF7}
                                         ReliableWinMmSysExSender.SendSysExMessages(targetOutIdx, seratoDumpReq)
                                         Interlocked.Exchange(_connectTimestampTicks, Environment.TickCount64)
                                     End If
                                 Catch
                                 End Try
                             End Sub)
                Else
                    Interlocked.Exchange(_connectTimestampTicks, 0L)
                End If

                RaiseEvent ConnectionChanged(_connectedDeviceName, True)
                Return True
            Catch
                DisconnectInternal()
                Return False
            End Try
        End SyncLock
    End Function

    Private Sub DisconnectInternal()
        If _midiIn IsNot Nothing Then
            Dim m As MidiIn = _midiIn
            _midiIn = Nothing
            Try
                RemoveHandler m.MessageReceived, AddressOf OnMidiMessageReceived
                RemoveHandler m.ErrorReceived, AddressOf OnMidiErrorReceived
            Catch
            End Try
            Try : m.Stop() : Catch : End Try
            Try : m.Reset() : Catch : End Try
            Try : m.Dispose() : Catch : End Try
        End If

        Dim wasConnected As Boolean = (Volatile.Read(_connectedDeviceIndex) >= 0)
        Volatile.Write(_connectedDeviceIndex, -1)
        _connectedDeviceName = "Не подключен"
        If wasConnected Then
            RaiseEvent ConnectionChanged(_connectedDeviceName, False)
        End If
    End Sub

    Private Sub OnMidiErrorReceived(sender As Object, e As MidiInMessageEventArgs)
    End Sub

    Private Sub OnMidiMessageReceived(sender As Object, e As MidiInMessageEventArgs)
        If e Is Nothing OrElse _isDisposed Then Return

        Try
            Dim raw As Integer = e.RawMessage
            Dim status As Integer = raw And &HFF
            Dim msgType As Integer = status And &HF0
            Dim channel As Integer = status And &HF
            Dim data1 As Integer = (raw >> 8) And &H7F
            Dim data2 As Integer = (raw >> 16) And &H7F

            If msgType = &H90 OrElse msgType = &H80 Then
                Dim isPressed As Boolean = (msgType = &H90 AndAlso data2 > 0)
                HandleNoteMessage(channel, data1, isPressed)
            ElseIf msgType = &HB0 Then
                HandleControlChange(channel, data1, data2)
            End If
        Catch
        End Try
    End Sub

    Private Function IsAnyShiftHeld() As Boolean
        Return Volatile.Read(_shiftHeld(0)) OrElse
               Volatile.Read(_shiftHeld(1)) OrElse
               Volatile.Read(_shiftHeld(2)) OrElse
               Volatile.Read(_shiftHeld(3))
    End Function

    Private Sub HandleNoteMessage(channel As Integer, note As Integer, isPressed As Boolean)
        If channel >= 0 AndAlso channel <= 3 AndAlso note = &H3F Then
            Volatile.Write(_shiftHeld(channel), isPressed)
            Return
        End If

        Dim connectTick As Long = Interlocked.Read(_connectTimestampTicks)
        Dim dumpWinMs As Integer = Math.Max(100, MixerHardwareStateStore.Current.Midi.InitialDumpWindowMs)
        Dim isInitialDump As Boolean = (connectTick > 0L AndAlso (Environment.TickCount64 - connectTick) < dumpWinMs)
        If isInitialDump Then Return

        If Not isPressed Then Return

        ' Фиксируем активную левую (Deck 1/3) или правую (Deck 2/4) деку при нажатии кнопок деки
        If channel = 0 OrElse channel = 2 Then
            Volatile.Write(_activeLeftDeck, channel)
        ElseIf channel = 1 OrElse channel = 3 Then
            Volatile.Write(_activeRightDeck, channel)
        End If

        Dim isShiftActive As Boolean = IsAnyShiftHeld()

        If channel >= 0 AndAlso channel <= 3 AndAlso note = &HC Then
            Dim t As TrackModel = GetAudioTrack(ResolveTargetAudioIndex(channel))
            If t IsNot Nothing Then
                If isShiftActive Then
                    _broker?.SetTrackSolo(t.Id, Not t.IsSolo, Me)
                Else
                    _broker?.SetTrackMute(t.Id, Not t.IsMuted, Me)
                End If
            End If
            Return
        End If

        If channel >= 0 AndAlso channel <= 3 AndAlso note = &H54 Then
            Dim t As TrackModel = GetAudioTrack(ResolveTargetAudioIndex(channel))
            If t IsNot Nothing Then
                If isShiftActive Then
                    _broker?.SetTrackEQAndTrim(t.Id, 0.5F, 0.5F, 0.5F, t.Trim, Me)
                    _broker?.SetTrackColorFilter(t.Id, 0.5F, Me)
                    _broker?.SetMasterBalance01(0.5F, Me)
                Else
                    _broker?.SetTrackSolo(t.Id, Not t.IsSolo, Me)
                End If
            End If
            Return
        End If

        If (channel = 4 OrElse channel = 5) AndAlso (note >= &H47 AndAlso note <= &H49) Then
            Dim deckIdx As Integer = If(channel = 4, Volatile.Read(_activeLeftDeck), Volatile.Read(_activeRightDeck))
            Dim targetAudioIdx As Integer = ResolveTargetAudioIndex(deckIdx)
            If targetAudioIdx < 0 Then
                targetAudioIdx = ResolveTargetAudioIndex(If(channel = 4, 0, 1))
            End If
            Dim t As TrackModel = GetAudioTrack(targetAudioIdx)
            If t IsNot Nothing Then
                Dim nextMode As Integer = (t.ColorFxMode + 1) And 3
                _broker?.SetTrackColorFxMode(t.Id, nextMode, Me)
            End If
            Return
        End If

        If channel = 6 AndAlso note = &H41 Then
            Dim t As TrackModel = GetAudioTrack(ResolveTargetAudioIndex(0))
            If t IsNot Nothing Then
                _broker?.SetTrackEQAndTrim(t.Id, 0.5F, 0.5F, 0.5F, t.Trim, Me)
                _broker?.SetTrackColorFilter(t.Id, 0.5F, Me)
            End If
            _broker?.SetMasterBalance01(0.5F, Me)
        End If
    End Sub

    Private Shared Function IsSupported14BitMsbCc(cc As Integer) As Boolean
        Return cc = &H4 OrElse cc = &H7 OrElse cc = &HB OrElse cc = &HF OrElse
               cc = &H13 OrElse cc = &H16 OrElse cc = &H17 OrElse cc = &H18 OrElse cc = &H1F
    End Function

    Private Function ResolveTargetAudioIndex(requestedDeck As Integer) As Integer
        Dim total As Integer = GetAudioTrackCount()
        If total <= 0 OrElse requestedDeck < 0 OrElse requestedDeck >= total Then
            Return -1
        End If
        Return requestedDeck
    End Function

    Private Sub HandleControlChange(channel As Integer, cc As Integer, value As Integer)
        If channel = 6 AndAlso cc = &H40 Then
            Dim stepDelta As Integer = If(value < 64, value, value - 128)
            Dim curMaster01 As Single = If(_player IsNot Nothing, _player.Volume / 100.0F, 1.0F)
            Dim newMaster01 As Single = Math.Max(0.0F, Math.Min(1.0F, curMaster01 + stepDelta * 0.015F))
            _masterBus?.SetVolume(CInt(Math.Round(newMaster01 * 100.0F)))
            _broker?.SetMasterVolume01(newMaster01, Me)
            Return
        End If

        Dim baseCc As Integer = cc
        Dim norm01 As Single = 0.0F

        If cc >= 0 AndAlso cc < 32 Then
            If Not IsSupported14BitMsbCc(cc) Then Return
            Volatile.Write(_ccMsb(channel, cc), value)
            If Volatile.Read(_ccIs14Bit(channel, cc)) Then
                Return
            Else
                norm01 = CSng(value) / 127.0F
            End If
        ElseIf cc >= 32 AndAlso cc < 64 Then
            baseCc = cc - 32
            If Not IsSupported14BitMsbCc(baseCc) Then Return
            Volatile.Write(_ccIs14Bit(channel, baseCc), True)
            Dim msb As Integer = Volatile.Read(_ccMsb(channel, baseCc))
            If msb < 0 Then Return
            Dim combined14 As Integer = (msb << 7) Or value
            norm01 = CSng(combined14) / 16383.0F
        Else
            Return
        End If

        norm01 = Math.Max(0.0F, Math.Min(1.0F, norm01))
        Dim connectTick As Long = Interlocked.Read(_connectTimestampTicks)
        Dim dumpWinMs As Integer = Math.Max(100, MixerHardwareStateStore.Current.Midi.InitialDumpWindowMs)
        Dim isInitialDump As Boolean = (connectTick > 0L AndAlso (Environment.TickCount64 - connectTick) < dumpWinMs)

        If isInitialDump Then Return

        Dim isShiftActive As Boolean = IsAnyShiftHeld()

        Dim isPostDumpSettled As Boolean = (connectTick <= 0L OrElse (Environment.TickCount64 - connectTick) > (dumpWinMs + 600))
        If channel = 0 OrElse (channel = 2 AndAlso isPostDumpSettled AndAlso ResolveTargetAudioIndex(2) >= 0) Then
            Volatile.Write(_activeLeftDeck, channel)
        ElseIf channel = 1 OrElse (channel = 3 AndAlso isPostDumpSettled AndAlso ResolveTargetAudioIndex(3) >= 0) Then
            Volatile.Write(_activeRightDeck, channel)
        End If

        If channel = 6 Then
            Select Case baseCc
                Case &H17
                    Dim leftDeck As Integer = If(Volatile.Read(_shiftHeld(2)) OrElse Volatile.Read(_activeLeftDeck) = 2, 2, 0)
                    Dim targetIdx As Integer = ResolveTargetAudioIndex(leftDeck)
                    If targetIdx < 0 AndAlso leftDeck = 2 Then targetIdx = ResolveTargetAudioIndex(0)
                    ApplyTrackColorFx(targetIdx, SnapCenter(norm01))

                Case &H18
                    If isShiftActive Then
                        _broker?.SetMasterBalance01(SnapCenter(norm01), Me)
                    Else
                        Dim rightDeck As Integer = If(Volatile.Read(_shiftHeld(3)) OrElse Volatile.Read(_activeRightDeck) = 3, 3, 1)
                        Dim targetIdx As Integer = ResolveTargetAudioIndex(rightDeck)
                        If targetIdx < 0 AndAlso rightDeck = 3 Then targetIdx = ResolveTargetAudioIndex(1)
                        ApplyTrackColorFx(targetIdx, SnapCenter(norm01))
                    End If

                Case &H1F
                    If CrossfaderControlsBalance Then
                        _broker?.SetMasterBalance01(SnapCenter(norm01), Me)
                    End If
            End Select
            Return
        End If

        If channel >= 0 AndAlso channel <= 3 Then
            Dim targetIdx As Integer = ResolveTargetAudioIndex(channel)
            If targetIdx < 0 Then Return
            Select Case baseCc
                Case &H4 : ApplyTrackEqOrTrim(targetIdx, trim:=SnapCenter(norm01))
                Case &H7 : ApplyTrackEqOrTrim(targetIdx, hi:=SnapCenter(norm01))
                Case &HB : ApplyTrackEqOrTrim(targetIdx, mid:=SnapCenter(norm01))
                Case &HF : ApplyTrackEqOrTrim(targetIdx, low:=SnapCenter(norm01))
                Case &H17, &H18, &H16 : ApplyTrackColorFx(targetIdx, SnapCenter(norm01))
                Case &H13 : ApplyTrackFader(targetIdx, norm01)
            End Select
        End If
    End Sub

    Private Shared Function SnapCenter(val01 As Single) As Single
        Dim deadzone As Single = Math.Max(0.001F, Math.Min(0.08F, MixerHardwareStateStore.Current.Midi.CenterSnapDeadzone))
        If Math.Abs(val01 - 0.5F) < deadzone Then Return 0.5F
        Return val01
    End Function

    Private Function GetAudioTrackCount() As Integer
        If _model?.Tracks Is Nothing Then Return 0
        SyncLock _model.SyncRoot
            Dim count As Integer = 0
            For i As Integer = 0 To _model.Tracks.Count - 1
                If _model.Tracks(i).Type = TargetFormatType.Audio Then count += 1
            Next
            Return count
        End SyncLock
    End Function

    Private Function GetAudioTrack(audioIndex As Integer) As TrackModel
        If audioIndex < 0 OrElse _model?.Tracks Is Nothing Then Return Nothing
        SyncLock _model.SyncRoot
            Dim cur As Integer = 0
            For i As Integer = 0 To _model.Tracks.Count - 1
                Dim t As TrackModel = _model.Tracks(i)
                If t.Type = TargetFormatType.Audio Then
                    If cur = audioIndex Then Return t
                    cur += 1
                End If
            Next
            Return Nothing
        End SyncLock
    End Function

    Private Sub ApplyTrackFader(audioIndex As Integer, fader01 As Single)
        Dim t As TrackModel = GetAudioTrack(audioIndex)
        If t Is Nothing Then Return
        If _broker IsNot Nothing Then
            _broker.SetTrackVolume(t.Id, fader01, Me)
        Else
            _player?.SetTrackVolume(t.Id, fader01)
        End If
    End Sub

    Private Sub ApplyTrackColorFx(audioIndex As Integer, color01 As Single)
        Dim t As TrackModel = GetAudioTrack(audioIndex)
        If t Is Nothing Then Return
        If _broker IsNot Nothing Then
            _broker.SetTrackColorFilter(t.Id, color01, Me)
        Else
            _player?.SetTrackColorFilter(t.Id, color01)
        End If
    End Sub

    Private Sub ApplyTrackEqOrTrim(audioIndex As Integer,
                                   Optional low As Single = -1.0F,
                                   Optional mid As Single = -1.0F,
                                   Optional hi As Single = -1.0F,
                                   Optional trim As Single = -1.0F)
        Dim t As TrackModel = GetAudioTrack(audioIndex)
        If t Is Nothing Then Return

        Dim newLow As Single = If(low >= 0.0F, low, t.EqLow)
        Dim newMid As Single = If(mid >= 0.0F, mid, t.EqMid)
        Dim newHi As Single = If(hi >= 0.0F, hi, t.EqHi)
        Dim newTrim As Single = If(trim >= 0.0F, trim, t.Trim)

        If _broker IsNot Nothing Then
            _broker.SetTrackEQAndTrim(t.Id, newLow, newMid, newHi, newTrim, Me)
        Else
            t.EqLow = newLow
            t.EqMid = newMid
            t.EqHi = newHi
            t.Trim = newTrim
            _player?.SetTrackEQAndTrim(
                t.Id,
                yoump.MediaParameterBroker.ValueToDecibels(newLow),
                yoump.MediaParameterBroker.ValueToDecibels(newMid),
                yoump.MediaParameterBroker.ValueToDecibels(newHi),
                newTrim * 2.0F
            )
            MixerHardwareStateStore.Save(_model, _player)
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _isDisposed Then Return
        _isDisposed = True
        SyncLock _syncLock
            DisconnectInternal()
        End SyncLock
    End Sub
End Class
' --- END OF FILE SystemAudioEngine.vb ---

