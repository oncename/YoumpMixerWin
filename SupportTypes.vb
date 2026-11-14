' --- START OF FILE SupportTypes.vb ---
Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.IO.MemoryMappedFiles
Imports System.Runtime.InteropServices
Imports System.Text.Encodings.Web
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading

Namespace yoump.IServices
    Public Interface IPlaybackController
        ReadOnly Property IsPlaying As Boolean
        ReadOnly Property CurrentVirtualTime As TimeSpan
    End Interface
End Namespace

Namespace yoump.Native
    <StructLayout(LayoutKind.Sequential, Pack:=1)>
    Public Structure NativePeakMinMax
        Public MinL As SByte
        Public MaxL As SByte
        Public MinR As SByte
        Public MaxR As SByte
    End Structure

    Public Structure WaveformCacheHeader
        Public Magic As UInteger
        Public SamplesPerPeak As Integer
        Public MipCount As Integer
        Public SampleRate As Integer
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Public Structure ThreeBandPeakBar
        Public LowTop As Single
        Public LowBot As Single
        Public MidTop As Single
        Public MidBot As Single
        Public HighTop As Single
        Public HighBot As Single
        Public CrestHot As Single
    End Structure

    Public Class PersistentWaveformCache
        Implements IDisposable

        Private Const CacheMagic As UInteger = &H57504B31UI
        Private Const HeaderSizeBytes As Integer = 64
        Private Const MaxMipLevels As Integer = 6

        Public Header As WaveformCacheHeader

        Private _fs As FileStream
        Private _mmf As MemoryMappedFile
        Private _accessor As MemoryMappedViewAccessor
        Private _fileLength As Long
        Private ReadOnly _mipOffsets(MaxMipLevels - 1) As Long
        Private ReadOnly _mipCounts(MaxMipLevels - 1) As Integer
        Private ReadOnly _readLock As New Object()
        Private _isDisposed As Boolean = False

        Private Sub New(fs As FileStream,
                        mmf As MemoryMappedFile,
                        acc As MemoryMappedViewAccessor,
                        fileLen As Long,
                        hdr As WaveformCacheHeader,
                        offsets() As Long,
                        counts() As Integer)
            _fs = fs
            _mmf = mmf
            _accessor = acc
            _fileLength = fileLen
            Header = hdr
            Array.Copy(offsets, _mipOffsets, MaxMipLevels)
            Array.Copy(counts, _mipCounts, MaxMipLevels)
        End Sub

        Public ReadOnly Property EffectiveSampleRate As Integer
            Get
                Return If(Header.SampleRate >= 8000 AndAlso Header.SampleRate <= 384000, Header.SampleRate, 48000)
            End Get
        End Property

        Public Shared Function GetCacheFilePath(sourceFilePath As String, cacheDir As String) As String
            Dim fi As New FileInfo(sourceFilePath)
            Dim stamp As String = $"{fi.Name}_{fi.Length}_{fi.LastWriteTimeUtc.Ticks:X}"
            For Each badChar As Char In Path.GetInvalidFileNameChars()
                stamp = stamp.Replace(badChar, "_"c)
            Next
            Return Path.Combine(cacheDir, stamp & ".wpk")
        End Function

        Public Shared Function TryOpenExisting(filePath As String, cacheDir As String) As PersistentWaveformCache
            If String.IsNullOrWhiteSpace(filePath) OrElse String.IsNullOrWhiteSpace(cacheDir) Then Return Nothing
            If Not File.Exists(filePath) Then Return Nothing

            Dim cacheFile As String = GetCacheFilePath(filePath, cacheDir)
            If Not File.Exists(cacheFile) Then Return Nothing

            Dim fs As FileStream = Nothing
            Dim mmf As MemoryMappedFile = Nothing
            Dim acc As MemoryMappedViewAccessor = Nothing
            Try
                Dim fi As New FileInfo(cacheFile)
                If fi.Length < HeaderSizeBytes Then Return Nothing

                fs = New FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete)
                mmf = MemoryMappedFile.CreateFromFile(fs, Nothing, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, False)
                acc = mmf.CreateViewAccessor(0, fi.Length, MemoryMappedFileAccess.Read)

                Dim magic As UInteger = acc.ReadUInt32(0)
                Dim spp As Integer = acc.ReadInt32(4)
                Dim mipCount As Integer = Math.Min(MaxMipLevels, Math.Max(0, acc.ReadInt32(8)))
                Dim sampleRateVal As Integer = acc.ReadInt32(12)
                If sampleRateVal < 8000 OrElse sampleRateVal > 384000 Then sampleRateVal = 48000

                If magic <> CacheMagic OrElse spp <= 0 OrElse mipCount <= 0 Then
                    Try : acc.Dispose() : Catch : End Try
                    Try : mmf.Dispose() : Catch : End Try
                    Try : fs.Dispose() : Catch : End Try
                    Return Nothing
                End If

                Dim offsets(MaxMipLevels - 1) As Long
                Dim counts(MaxMipLevels - 1) As Integer
                For m As Integer = 0 To mipCount - 1
                    Dim tablePos As Integer = 16 + (m * 8)
                    offsets(m) = CLng(acc.ReadUInt32(tablePos))
                    counts(m) = Math.Max(0, acc.ReadInt32(tablePos + 4))
                Next

                Dim hdr As New WaveformCacheHeader() With {
                    .Magic = magic,
                    .SamplesPerPeak = spp,
                    .MipCount = mipCount,
                    .SampleRate = sampleRateVal
                }
                Return New PersistentWaveformCache(fs, mmf, acc, fi.Length, hdr, offsets, counts)
            Catch
                Try : acc?.Dispose() : Catch : End Try
                Try : mmf?.Dispose() : Catch : End Try
                Try : fs?.Dispose() : Catch : End Try
                Return Nothing
            End Try
        End Function

        Public Function SelectOptimalMipLevel(clipDurationSec As Double, targetPixelWidth As Single) As Integer
            If Header.MipCount <= 1 OrElse Header.SamplesPerPeak <= 0 OrElse clipDurationSec <= 0.01 Then Return 0
            Dim desiredPeaks As Double = Math.Max(48.0, CDbl(targetPixelWidth) * 4.0)
            Dim sampleRateHz As Double = CDbl(EffectiveSampleRate)
            Dim bestMip As Integer = 0

            For m As Integer = 0 To Header.MipCount - 1
                Dim pps As Double = CDbl(Header.SamplesPerPeak) * Math.Pow(4.0, m)
                Dim availPeaks As Double = (clipDurationSec * sampleRateHz) / pps
                If availPeaks >= desiredPeaks Then
                    bestMip = m
                Else
                    Exit For
                End If
            Next
            Return Math.Max(0, Math.Min(Header.MipCount - 1, bestMip))
        End Function

        Public Function ReadPeaks(mipLevel As Integer, startIndex As Integer, count As Integer) As NativePeakMinMax()
            If _isDisposed OrElse _accessor Is Nothing OrElse count <= 0 OrElse Header.MipCount <= 0 Then
                Return Array.Empty(Of NativePeakMinMax)()
            End If

            SyncLock _readLock
                If _isDisposed OrElse _accessor Is Nothing Then Return Array.Empty(Of NativePeakMinMax)()

                Dim safeMip As Integer = Math.Max(0, Math.Min(Header.MipCount - 1, mipLevel))
                Dim totalInMip As Integer = _mipCounts(safeMip)
                Dim safeStart As Integer = Math.Max(0, startIndex)
                If totalInMip <= 0 OrElse safeStart >= totalInMip Then
                    Return Array.Empty(Of NativePeakMinMax)()
                End If

                Dim availCount As Integer = Math.Min(count, totalInMip - safeStart)
                Dim byteOffset As Long = _mipOffsets(safeMip) + CLng(safeStart) * 4L
                Dim byteLen As Long = CLng(availCount) * 4L
                If byteOffset < HeaderSizeBytes OrElse (byteOffset + byteLen) > _fileLength Then
                    Return Array.Empty(Of NativePeakMinMax)()
                End If

                Dim result(availCount - 1) As NativePeakMinMax
                Try
                    _accessor.ReadArray(Of NativePeakMinMax)(byteOffset, result, 0, availCount)
                    Return result
                Catch
                    Return Array.Empty(Of NativePeakMinMax)()
                End Try
            End SyncLock
        End Function

        Public Shared Function ExtractThreeBandBars(peaks() As NativePeakMinMax, targetBarCount As Integer) As ThreeBandPeakBar()
            If peaks Is Nothing OrElse peaks.Length = 0 OrElse targetBarCount <= 0 Then
                Return Array.Empty(Of ThreeBandPeakBar)()
            End If

            Dim barCount As Integer = Math.Min(peaks.Length, Math.Max(1, targetBarCount))
            Dim bars(barCount - 1) As ThreeBandPeakBar
            Dim rawTop(barCount - 1) As Single
            Dim rawBot(barCount - 1) As Single
            Dim avgTopArr(barCount - 1) As Single
            Dim avgBotArr(barCount - 1) As Single
            Dim fluxTopArr(barCount - 1) As Single
            Dim fluxBotArr(barCount - 1) As Single
            Dim sideArr(barCount - 1) As Single

            Dim stepSize As Double = CDbl(peaks.Length) / CDbl(barCount)
            Dim prevL As Single = 0.0F
            Dim prevR As Single = 0.0F

            For b As Integer = 0 To barCount - 1
                Dim startIdx As Integer = CInt(Math.Floor(b * stepSize))
                Dim endIdx As Integer = Math.Min(peaks.Length - 1, CInt(Math.Floor((b + 1) * stepSize)))
                If endIdx < startIdx Then endIdx = startIdx

                Dim maxL As Single = 0.0F
                Dim maxR As Single = 0.0F
                Dim sumL As Single = 0.0F
                Dim sumR As Single = 0.0F
                Dim sumFluxL As Single = 0.0F
                Dim sumFluxR As Single = 0.0F
                Dim sumSide As Single = 0.0F
                Dim cnt As Integer = (endIdx - startIdx) + 1

                For j As Integer = startIdx To endIdx
                    Dim p As NativePeakMinMax = peaks(j)
                    Dim aL As Single = Math.Max(Math.Abs(CInt(p.MaxL)), Math.Abs(CInt(p.MinL))) / 128.0F
                    Dim aR As Single = Math.Max(Math.Abs(CInt(p.MaxR)), Math.Abs(CInt(p.MinR))) / 128.0F
                    If aR < 0.004F AndAlso aL > 0.01F Then aR = aL

                    If aL > maxL Then maxL = aL
                    If aR > maxR Then maxR = aR

                    Dim p2pL As Single = (CInt(p.MaxL) - CInt(p.MinL)) / 255.0F
                    Dim p2pR As Single = (CInt(p.MaxR) - CInt(p.MinR)) / 255.0F
                    If p2pR < 0.004F AndAlso p2pL > 0.01F Then p2pR = p2pL

                    sumL += p2pL
                    sumR += p2pR
                    sumFluxL += Math.Abs(aL - prevL)
                    sumFluxR += Math.Abs(aR - prevR)
                    sumSide += Math.Abs(aL - aR)

                    prevL = aL
                    prevR = aR
                Next

                Dim invCnt As Single = 1.0F / CSng(cnt)
                rawTop(b) = maxL
                rawBot(b) = maxR
                avgTopArr(b) = sumL * invCnt
                avgBotArr(b) = sumR * invCnt
                fluxTopArr(b) = sumFluxL * invCnt
                fluxBotArr(b) = sumFluxR * invCnt
                sideArr(b) = sumSide * invCnt
            Next

            Dim bassTop(barCount - 1) As Single
            Dim bassBot(barCount - 1) As Single
            Dim envT As Single = avgTopArr(0)
            Dim envB As Single = avgBotArr(0)

            For b As Integer = 0 To barCount - 1
                envT = envT * 0.65F + avgTopArr(b) * 0.35F
                envB = envB * 0.65F + avgBotArr(b) * 0.35F
                bassTop(b) = envT
                bassBot(b) = envB
            Next

            envT = bassTop(barCount - 1)
            envB = bassBot(barCount - 1)
            For b As Integer = barCount - 1 To 0 Step -1
                envT = envT * 0.65F + bassTop(b) * 0.35F
                envB = envB * 0.65F + bassBot(b) * 0.35F
                bassTop(b) = envT
                bassBot(b) = envB
            Next

            For b As Integer = 0 To barCount - 1
                Dim pkT As Single = rawTop(b)
                Dim pkB As Single = rawBot(b)
                Dim avT As Single = avgTopArr(b)
                Dim avB As Single = avgBotArr(b)
                Dim flT As Single = fluxTopArr(b)
                Dim flB As Single = fluxBotArr(b)
                Dim sd As Single = sideArr(b)

                Dim crestT As Single = Math.Max(0.0F, pkT - avT)
                Dim crestB As Single = Math.Max(0.0F, pkB - avB)

                Dim lTop As Single = Math.Min(1.0F, Math.Max(pkT * 0.88F, bassTop(b) * 1.18F))
                Dim lBot As Single = Math.Min(1.0F, Math.Max(pkB * 0.88F, bassBot(b) * 1.18F))

                Dim mTop As Single = Math.Min(1.0F, (avT * 0.78F) + (pkT * 0.26F))
                Dim mBot As Single = Math.Min(1.0F, (avB * 0.78F) + (pkB * 0.26F))

                Dim hTop As Single = Math.Min(1.0F, (flT * 1.9F) + (crestT * 1.25F) + (sd * 0.85F) + (pkT * 0.2F))
                Dim hBot As Single = Math.Min(1.0F, (flB * 1.9F) + (crestB * 1.25F) + (sd * 0.85F) + (pkB * 0.2F))

                Dim hot As Single = Math.Min(1.0F, Math.Max(flT, flB) * 2.2F + Math.Max(crestT, crestB) * 1.4F)

                bars(b) = New ThreeBandPeakBar() With {
                    .LowTop = lTop,
                    .LowBot = lBot,
                    .MidTop = mTop,
                    .MidBot = mBot,
                    .HighTop = hTop,
                    .HighBot = hBot,
                    .CrestHot = hot
                }
            Next

            Return bars
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _isDisposed Then Return
            _isDisposed = True
            SyncLock _readLock
                Try : _accessor?.Dispose() : Catch : End Try
                Try : _mmf?.Dispose() : Catch : End Try
                Try : _fs?.Dispose() : Catch : End Try
                _accessor = Nothing
                _mmf = Nothing
                _fs = Nothing
            End SyncLock
        End Sub
    End Class
End Namespace

Public Class ChannelProfileConfig
    Public Property Trim As Single = 0.5F
    Public Property EqHi As Single = 0.5F
    Public Property EqMid As Single = 0.5F
    Public Property EqLow As Single = 0.5F
    Public Property ColorFx As Single = 0.5F
    Public Property ColorFxMode As Integer = 0 ' 0 = FILTER, 1 = SPACE, 2 = CRUSH, 3 = LOUD
    Public Property Fader As Single = 1.0F
    Public Property WaveformZoom As Single = 1.0F
    Public Property IsMuted As Boolean = False
    Public Property IsSolo As Boolean = False

    Private Shared Function SafeClamp(val As Single, minVal As Single, maxVal As Single, defVal As Single) As Single
        If Single.IsNaN(val) OrElse Single.IsInfinity(val) Then Return defVal
        Return Math.Max(minVal, Math.Min(maxVal, val))
    End Function

    Public Function Clone() As ChannelProfileConfig
        Dim zoomVal As Single = If(Single.IsNaN(Me.WaveformZoom) OrElse Single.IsInfinity(Me.WaveformZoom) OrElse Me.WaveformZoom <= 0.01F, 1.0F, Me.WaveformZoom)
        Return New ChannelProfileConfig() With {
            .Trim = SafeClamp(Me.Trim, 0.0F, 1.0F, 0.5F),
            .EqHi = SafeClamp(Me.EqHi, 0.0F, 1.0F, 0.5F),
            .EqMid = SafeClamp(Me.EqMid, 0.0F, 1.0F, 0.5F),
            .EqLow = SafeClamp(Me.EqLow, 0.0F, 1.0F, 0.5F),
            .ColorFx = SafeClamp(Me.ColorFx, 0.0F, 1.0F, 0.5F),
            .ColorFxMode = Math.Max(0, Math.Min(3, Me.ColorFxMode)),
            .Fader = SafeClamp(Me.Fader, 0.0F, 1.0F, 1.0F),
            .WaveformZoom = Math.Max(0.25F, Math.Min(4.0F, zoomVal)),
            .IsMuted = Me.IsMuted,
            .IsSolo = Me.IsSolo
        }
    End Function
End Class

Public Class AudioRoutingConfig
    Public Property PreferredOutputDeviceId As String = ""
    Public Property PreferredOutputDeviceName As String = ""
    Public Property PreferredOutputKeyword As String = "1-2"
    Public Property HardwareKeywords As String() = {"QUAD-CAPTURE", "UA-55"}
    Public Property BlockedOutputKeywords As String() = {"3-4", "Hands-Free AG", "Головной телефон"}

    Public Property PreferBluetoothHeadphonesOutput As Boolean = True
    Public Property BluetoothHeadphoneKeywords As String() = {
        "Space Travel 2 Ultra", "Space Travel", "MOONDROP", "LDAC", "Headphones", "Наушники"
    }
    Public Property UseBluetoothMic As Boolean = True
    Public Property BluetoothMicHighQualityOnly As Boolean = True
    Public Property MinBluetoothMicSampleRateHz As Integer = 32000
    Public Property PreferredMicKeyword As String = "1-2"
    Public Property PreferredMainCaptureKeyword As String = "MAIN"
    Public Property MicMonitoringEnabled As Boolean = False
    Public Property MicStudioProcessingEnabled As Boolean = True
    Public Property MicDeEsserEnabled As Boolean = True
    Public Property MicPlosiveFilter24dB As Boolean = True
    Public Property MicAntiFeedbackEnabled As Boolean = False
    Public Property MicTalkoverMode As Integer = 0 ' 0 = OFF, 1 = STANDARD (-14 dB Ducking), 2 = ADVANCED (Mid-Band -9.5 dB Ducking)
    Public Property MicXlrInputMode As Integer = 0 ' 0 = Auto-Latch XLR 1/2, 1 = XLR 1 Only (L), 2 = XLR 2 Only (R), 3 = Dual Mono (XLR 1+2)
    Public Property LimiterEnabled As Boolean = True
    Public Property MicNoiseGateThreshold As Single = 0.0045F
    Public Property MicCaptureLatencyMs As Integer = 10
    Public Property MicOutputLatencyMs As Integer = 15
    Public Property MicBufferDurationMs As Integer = 120
    Public Property AutoRouteAppsToPrimaryOutput As Boolean = True
    Public Property SanitizeMediaPlayerRegistry As Boolean = True

    Public Property MicIrPreset As Integer = 0
    Public Property MicIrMix As Single = 1.0F
    Public Property MicCustomIrFilePath As String = ""

    Public Property UseAsio As Boolean = False
    Public Property PreferredAsioDriverName As String = "QUAD-CAPTURE"
    Public Property AsioInputChannelOffset As Integer = 0
    Public Property AsioOutputChannelOffset As Integer = 0

    Public Function GetPackedMicExtraFlags() As Integer
        Dim flags As Integer = 0
        If MicDeEsserEnabled Then flags = flags Or 1
        If MicPlosiveFilter24dB Then flags = flags Or 2
        If MicAntiFeedbackEnabled Then flags = flags Or 4
        Dim safeXlr As Integer = Math.Max(0, Math.Min(3, MicXlrInputMode))
        flags = flags Or ((safeXlr And 3) << 3)
        Return flags
    End Function

    Public Shared Function GetMicIrPresetName(presetId As Integer) As String
        Select Case presetId
            Case 1 : Return "Shure SM7B (Broadcast)"
            Case 2 : Return "Neumann U87 Ai (Studio)"
            Case 3 : Return "Electro-Voice RE20 (Radio)"
            Case 4 : Return "Sony C800G (Tube Vocal)"
            Case 5 : Return "Lexicon 480L (Vocal Plate)"
            Case 6 : Return "Pioneer DJM Club (Space IR)"
            Case 7 : Return "Custom WAV IR (.wav)"
            Case Else : Return "OFF (Без ИК-свёртки)"
        End Select
    End Function

    Public Shared Function GetMicTalkoverModeName(mode As Integer) As String
        Select Case mode
            Case 1 : Return "STANDARD (-14 dB Ducking)"
            Case 2 : Return "ADVANCED (Mid-Band Ducking)"
            Case Else : Return "OFF (Выкл)"
        End Select
    End Function

    Public Shared Function GetMicXlrInputModeName(mode As Integer) As String
        Select Case mode
            Case 1 : Return "XLR 1 Only (Left -> Mono)"
            Case 2 : Return "XLR 2 Only (Right -> Mono)"
            Case 3 : Return "Dual Mono (XLR 1 + XLR 2)"
            Case Else : Return "Auto-Latch (Умный выбор без шума)"
        End Select
    End Function
End Class

Public Class MidiControllerConfig
    Public Property PreferredControllerNames As String() = {"DDJ-SB3", "DDJ", "Pioneer"}
    Public Property BlockedControllerKeywords As String() = {"QUAD-CAPTURE", "UA-55", "Roland", "Microsoft GS Wavetable"}
    Public Property LastConnectedDeviceName As String = ""
    Public Property ReconnectIntervalMs As Integer = 2500
    Public Property CrossfaderControlsBalance As Boolean = False
    Public Property CenterSnapDeadzone As Single = 0.012F
    Public Property InitialDumpWindowMs As Integer = 900
    Public Property SendSeratoStartupSysEx As Boolean = True

    Public Function IsBlockedMidiDevice(deviceName As String) As Boolean
        If String.IsNullOrWhiteSpace(deviceName) Then Return True
        Dim list As String() = If(BlockedControllerKeywords, {"QUAD-CAPTURE", "UA-55", "Roland", "Microsoft GS Wavetable"})
        For Each kw As String In list
            If Not String.IsNullOrWhiteSpace(kw) AndAlso
               deviceName.IndexOf(kw.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 Then
                Return True
            End If
        Next
        Return False
    End Function
End Class

Public Class ProcessRulesConfig
    Public Property SessionPollIntervalMs As Integer = 20
    Public Property DiscoveryIntervalMs As Integer = 250
    Public Property InactiveChannelTimeoutMs As Integer = 12000

    Public Property PinnedChannels As New List(Of String) From {
        "MAIN MIC", "MPC-HC", "AIMP", "POTPLAYER", "VLC", "SPOTIFY", "CHROME", "YANDEX", "EDGE", "FIREFOX", "TELEGRAM", "YA.MUSIC"
    }

    Public Property IgnoredProcesses As New List(Of String) From {
        "audiodg", "svchost", "system", "idle", "explorer", "msedgewebview2",
        "searchhost", "searchapp", "widgets", "widgetservice", "shellexperiencehost",
        "startmenuexperiencehost", "runtimebroker", "applicationframehost", "sihost", "taskhostw"
    }

    Public Property ProcessAliases As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
        {"chrome", "CHROME"},
        {"browser", "YANDEX"},
        {"yandex", "YANDEX"},
        {"msedge", "EDGE"},
        {"firefox", "FIREFOX"},
        {"opera", "OPERA"},
        {"mpc-hc", "MPC-HC"},
        {"mpc-hc64", "MPC-HC"},
        {"mpc-be", "MPC-HC"},
        {"mpc-be64", "MPC-HC"},
        {"aimp", "AIMP"},
        {"aimp2", "AIMP"},
        {"aimp3", "AIMP"},
        {"aimp4", "AIMP"},
        {"aimp64", "AIMP"},
        {"potplayer", "POTPLAYER"},
        {"potplayermini", "POTPLAYER"},
        {"potplayermini64", "POTPLAYER"},
        {"vlc", "VLC"},
        {"spotify", "SPOTIFY"},
        {"telegram", "TELEGRAM"},
        {"яндекс музыка", "YA.MUSIC"},
        {"yandexmusic", "YA.MUSIC"}
    }
End Class

Public Class UiPreferencesConfig
    Public Property IsDarkTheme As Boolean = True
    Public Property AlwaysOnTop As Boolean = False
    Public Property StartMinimizedToTray As Boolean = False
    Public Property HotReloadConfigFile As Boolean = True
    Public Property ProjectFps As Double = 60.0
    Public Property WindowHeight As Integer = 390
    Public Property WindowX As Integer? = Nothing
    Public Property WindowY As Integer? = Nothing
    Public Property WaveformAmpScale As Single = 1.0F
    Public Property OsdDurationMs As Integer = 2500
    Public Property PeakHoldDurationMs As Integer = 600
    Public Property MeterDecayFactor As Single = 0.82F
    Public Property CacheDirectory As String = ""
End Class

Public NotInheritable Class MixerHardwareStateStore
    Public Property MasterVolume01 As Single = 1.0F
    Public Property MasterBalance01 As Single = 0.5F
    Public Property MasterLevelSource As String = "Auto"

    Public Property Audio As New AudioRoutingConfig()
    Public Property Midi As New MidiControllerConfig()
    Public Property ProcessRules As New ProcessRulesConfig()
    Public Property Ui As New UiPreferencesConfig()

    Public Property ChannelProfiles As New Dictionary(Of String, ChannelProfileConfig)(StringComparer.OrdinalIgnoreCase)
    Public Property Presets As New Dictionary(Of String, ChannelProfileConfig)(StringComparer.OrdinalIgnoreCase)
    Public Property CustomParameters As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    <JsonExtensionData>
    Public Property ExtraJsonFields As Dictionary(Of String, JsonElement)

    <JsonIgnore>
    Public Property Trim As Single
        Get
            Return GetMainMicProfile().Trim
        End Get
        Set(value As Single)
            GetMainMicProfile().Trim = Math.Max(0.0F, Math.Min(1.0F, value))
        End Set
    End Property

    <JsonIgnore>
    Public Property EqHi As Single
        Get
            Return GetMainMicProfile().EqHi
        End Get
        Set(value As Single)
            GetMainMicProfile().EqHi = Math.Max(0.0F, Math.Min(1.0F, value))
        End Set
    End Property

    <JsonIgnore>
    Public Property EqMid As Single
        Get
            Return GetMainMicProfile().EqMid
        End Get
        Set(value As Single)
            GetMainMicProfile().EqMid = Math.Max(0.0F, Math.Min(1.0F, value))
        End Set
    End Property

    <JsonIgnore>
    Public Property EqLow As Single
        Get
            Return GetMainMicProfile().EqLow
        End Get
        Set(value As Single)
            GetMainMicProfile().EqLow = Math.Max(0.0F, Math.Min(1.0F, value))
        End Set
    End Property

    <JsonIgnore>
    Public Property ColorFx As Single
        Get
            Return GetMainMicProfile().ColorFx
        End Get
        Set(value As Single)
            GetMainMicProfile().ColorFx = Math.Max(0.0F, Math.Min(1.0F, value))
        End Set
    End Property

    <JsonIgnore>
    Public Property ChannelFader As Single
        Get
            Return GetMainMicProfile().Fader
        End Get
        Set(value As Single)
            GetMainMicProfile().Fader = Math.Max(0.0F, Math.Min(1.0F, value))
        End Set
    End Property

    Public Shared Property CurrentMasterLevelSource As String = "Auto"
    Public Shared LastFlushTick As Long = 0

    Private Shared ReadOnly _fileLock As New Object()
    Private Shared ReadOnly _jsonOptions As New JsonSerializerOptions() With {
        .WriteIndented = True,
        .ReadCommentHandling = JsonCommentHandling.Skip,
        .AllowTrailingCommas = True,
        .PropertyNameCaseInsensitive = True,
        .Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    }

    Private Shared _current As New MixerHardwareStateStore()
    Private Shared _lastModelRef As ProjectModel = Nothing
    Private Shared _lastPlayerRef As NAudioSyncPlayer = Nothing
    Private Shared _lastSavedJson As String = ""
    Private Shared _saveScheduled As Integer = 0
    Private Shared _isDirty As Integer = 0

    Public Shared ReadOnly Property SyncRoot As Object
        Get
            Return _fileLock
        End Get
    End Property

    Public Shared ReadOnly Property Current As MixerHardwareStateStore
        Get
            Return _current
        End Get
    End Property

    Public Shared Function GetConfigFilePath() As String
        Dim baseDir As String = AppDomain.CurrentDomain.BaseDirectory
        Return Path.Combine(baseDir, "config.json")
    End Function

    Private Shared Function GetLegacyStateFilePath() As String
        Dim dir As String = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "yoump")
        Return Path.Combine(dir, "ddj_sb3_state.cfg")
    End Function

    Private Function GetMainMicProfile() As ChannelProfileConfig
        SyncLock _fileLock
            If ChannelProfiles Is Nothing Then
                ChannelProfiles = New Dictionary(Of String, ChannelProfileConfig)(StringComparer.OrdinalIgnoreCase)
            End If
            Dim prof As ChannelProfileConfig = Nothing
            If Not ChannelProfiles.TryGetValue("MAIN MIC", prof) OrElse prof Is Nothing Then
                If ChannelProfiles.TryGetValue("MIC", prof) AndAlso prof IsNot Nothing Then
                    ChannelProfiles("MAIN MIC") = prof
                Else
                    prof = New ChannelProfileConfig()
                    ChannelProfiles("MAIN MIC") = prof
                End If
            End If
            Return prof
        End SyncLock
    End Function

    Public Shared Function GetOrCreateChannelProfile(channelNameOrKey As String) As ChannelProfileConfig
        Dim key As String = If(String.IsNullOrWhiteSpace(channelNameOrKey), "MAIN MIC", channelNameOrKey.Trim().ToUpperInvariant())
        If key = "MIC" OrElse key = "MIC 1-2" Then key = "MAIN MIC"

        SyncLock _fileLock
            If _current.ChannelProfiles Is Nothing Then
                _current.ChannelProfiles = New Dictionary(Of String, ChannelProfileConfig)(StringComparer.OrdinalIgnoreCase)
            End If

            Dim prof As ChannelProfileConfig = Nothing
            If Not _current.ChannelProfiles.TryGetValue(key, prof) OrElse prof Is Nothing Then
                prof = New ChannelProfileConfig()
                _current.ChannelProfiles(key) = prof
            End If
            Return prof.Clone()
        End SyncLock
    End Function

    Public Shared Function GetPinnedSortOrder(channelNameOrKey As String) As Integer
        If String.IsNullOrWhiteSpace(channelNameOrKey) Then Return 9999
        Dim key As String = channelNameOrKey.Trim()
        If key.Equals("MIC", StringComparison.OrdinalIgnoreCase) Then key = "MAIN MIC"

        SyncLock _fileLock
            Dim pinned = _current.ProcessRules?.PinnedChannels
            If pinned IsNot Nothing Then
                For i As Integer = 0 To pinned.Count - 1
                    If Not String.IsNullOrWhiteSpace(pinned(i)) AndAlso pinned(i).Trim().Equals(key, StringComparison.OrdinalIgnoreCase) Then
                        Return i
                    End If
                Next
            End If
        End SyncLock
        Return 1000
    End Function

    Public Shared Function GetCustomParam(key As String, Optional defaultValue As String = "") As String
        If String.IsNullOrWhiteSpace(key) Then Return defaultValue
        SyncLock _fileLock
            If _current.CustomParameters IsNot Nothing Then
                Dim val As String = Nothing
                If _current.CustomParameters.TryGetValue(key, val) AndAlso val IsNot Nothing Then
                    Return val
                End If
            End If
        End SyncLock
        Return defaultValue
    End Function

    Public Shared Function GetCustomFloat(key As String, Optional defaultValue As Single = 0.0F) As Single
        Dim raw As String = GetCustomParam(key, "")
        Dim parsed As Single = 0.0F
        If Not String.IsNullOrWhiteSpace(raw) AndAlso Single.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, parsed) Then
            If Not Single.IsNaN(parsed) AndAlso Not Single.IsInfinity(parsed) Then
                Return parsed
            End If
        End If
        Return defaultValue
    End Function

    Public Shared Sub SetCustomParam(key As String, value As String, Optional saveImmediately As Boolean = False)
        If String.IsNullOrWhiteSpace(key) Then Return
        SyncLock _fileLock
            If _current.CustomParameters Is Nothing Then
                _current.CustomParameters = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            End If
            _current.CustomParameters(key) = If(value, "")
        End SyncLock
        If saveImmediately Then
            FlushNow()
        Else
            RequestSaveDeferred()
        End If
    End Sub

    Private Sub EnsureDefaultPresets()
        If Presets Is Nothing Then
            Presets = New Dictionary(Of String, ChannelProfileConfig)(StringComparer.OrdinalIgnoreCase)
        Else
            Dim ci As New Dictionary(Of String, ChannelProfileConfig)(StringComparer.OrdinalIgnoreCase)
            For Each kvp In Presets
                If Not String.IsNullOrWhiteSpace(kvp.Key) AndAlso kvp.Value IsNot Nothing Then
                    ci(kvp.Key.Trim()) = kvp.Value.Clone()
                End If
            Next
            Presets = ci
        End If

        If Presets.Count = 0 Then
            Presets("DJ / Flat (0 dB)") = New ChannelProfileConfig() With {.Trim = 0.5F, .EqHi = 0.5F, .EqMid = 0.5F, .EqLow = 0.5F, .ColorFx = 0.5F, .ColorFxMode = 0, .Fader = 1.0F}
            Presets("Кино / Голос (Voice Boost)") = New ChannelProfileConfig() With {.Trim = 0.55F, .EqHi = 0.58F, .EqMid = 0.72F, .EqLow = 0.38F, .ColorFx = 0.5F, .ColorFxMode = 0, .Fader = 1.0F}
            Presets("Club / Bass Boost") = New ChannelProfileConfig() With {.Trim = 0.5F, .EqHi = 0.65F, .EqMid = 0.48F, .EqLow = 0.74F, .ColorFx = 0.68F, .ColorFxMode = 3, .Fader = 1.0F}
            Presets("Ночной режим (Low Cut)") = New ChannelProfileConfig() With {.Trim = 0.48F, .EqHi = 0.5F, .EqMid = 0.58F, .EqLow = 0.18F, .ColorFx = 0.58F, .ColorFxMode = 0, .Fader = 0.85F}
        End If
    End Sub

    Private Sub NormalizeAfterLoad()
        If Single.IsNaN(MasterVolume01) OrElse Single.IsInfinity(MasterVolume01) Then MasterVolume01 = 1.0F
        If Single.IsNaN(MasterBalance01) OrElse Single.IsInfinity(MasterBalance01) Then MasterBalance01 = 0.5F
        MasterVolume01 = Math.Max(0.0F, Math.Min(1.0F, MasterVolume01))
        MasterBalance01 = Math.Max(0.0F, Math.Min(1.0F, MasterBalance01))
        If String.IsNullOrWhiteSpace(MasterLevelSource) Then MasterLevelSource = "Auto"

        If Audio Is Nothing Then Audio = New AudioRoutingConfig()
        If Midi Is Nothing Then Midi = New MidiControllerConfig()
        If Midi.BlockedControllerKeywords Is Nothing OrElse Midi.BlockedControllerKeywords.Length = 0 Then
            Midi.BlockedControllerKeywords = {"QUAD-CAPTURE", "UA-55", "Roland", "Microsoft GS Wavetable"}
        End If
        If Not String.IsNullOrWhiteSpace(Midi.LastConnectedDeviceName) AndAlso Midi.IsBlockedMidiDevice(Midi.LastConnectedDeviceName) Then
            Midi.LastConnectedDeviceName = ""
        End If
        If ProcessRules Is Nothing Then ProcessRules = New ProcessRulesConfig()
        If Ui Is Nothing Then Ui = New UiPreferencesConfig()
        If ChannelProfiles Is Nothing Then
            ChannelProfiles = New Dictionary(Of String, ChannelProfileConfig)(StringComparer.OrdinalIgnoreCase)
        Else
            Dim caseInsensitive As New Dictionary(Of String, ChannelProfileConfig)(StringComparer.OrdinalIgnoreCase)
            For Each kvp In ChannelProfiles
                If Not String.IsNullOrWhiteSpace(kvp.Key) AndAlso kvp.Value IsNot Nothing Then
                    caseInsensitive(kvp.Key.Trim().ToUpperInvariant()) = kvp.Value.Clone()
                End If
            Next
            ChannelProfiles = caseInsensitive
        End If

        EnsureDefaultPresets()

        If CustomParameters Is Nothing Then
            CustomParameters = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        End If

        If ProcessRules.ProcessAliases IsNot Nothing Then
            Dim aliasesCi As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            For Each kvp In ProcessRules.ProcessAliases
                If Not String.IsNullOrWhiteSpace(kvp.Key) AndAlso Not String.IsNullOrWhiteSpace(kvp.Value) Then
                    aliasesCi(kvp.Key.Trim()) = kvp.Value.Trim()
                End If
            Next
            ProcessRules.ProcessAliases = aliasesCi
        End If

        If Audio.BluetoothHeadphoneKeywords Is Nothing OrElse Audio.BluetoothHeadphoneKeywords.Length = 0 Then
            Audio.BluetoothHeadphoneKeywords = {"Space Travel 2 Ultra", "Space Travel", "MOONDROP", "LDAC", "Headphones", "Наушники"}
        End If
        Audio.MinBluetoothMicSampleRateHz = Math.Max(16000, Math.Min(192000, Audio.MinBluetoothMicSampleRateHz))
        Audio.MicTalkoverMode = Math.Max(0, Math.Min(2, Audio.MicTalkoverMode))
        Audio.MicXlrInputMode = Math.Max(0, Math.Min(3, Audio.MicXlrInputMode))
        If Single.IsNaN(Audio.MicNoiseGateThreshold) OrElse Single.IsInfinity(Audio.MicNoiseGateThreshold) Then Audio.MicNoiseGateThreshold = 0.0045F
        Audio.MicNoiseGateThreshold = Math.Max(0.0F, Math.Min(0.2F, Audio.MicNoiseGateThreshold))
        If Single.IsNaN(Audio.MicIrMix) OrElse Single.IsInfinity(Audio.MicIrMix) Then Audio.MicIrMix = 1.0F
        Audio.MicIrMix = Math.Max(0.0F, Math.Min(1.0F, Audio.MicIrMix))

        GetMainMicProfile()
        If Single.IsNaN(Ui.WaveformAmpScale) OrElse Single.IsInfinity(Ui.WaveformAmpScale) Then Ui.WaveformAmpScale = 1.0F
        Ui.WaveformAmpScale = Math.Max(0.25F, Math.Min(4.0F, Ui.WaveformAmpScale))
        If Double.IsNaN(Ui.ProjectFps) OrElse Double.IsInfinity(Ui.ProjectFps) Then Ui.ProjectFps = 60.0
        Ui.ProjectFps = Math.Max(15.0, Math.Min(240.0, Ui.ProjectFps))
        Ui.WindowHeight = Math.Max(390, Ui.WindowHeight)
    End Sub

    Public Shared Function Load() As MixerHardwareStateStore
        SyncLock _fileLock
            Try
                Dim configPath As String = GetConfigFilePath()
                If File.Exists(configPath) Then
                    Dim json As String = ""
                    Using fs As New FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete)
                        Using sr As New StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks:=True)
                            json = sr.ReadToEnd()
                        End Using
                    End Using

                    If Not String.IsNullOrWhiteSpace(json) Then
                        Dim loaded As MixerHardwareStateStore = JsonSerializer.Deserialize(Of MixerHardwareStateStore)(json, _jsonOptions)
                        If loaded IsNot Nothing Then
                            loaded.NormalizeAfterLoad()
                            _current = loaded
                            CurrentMasterLevelSource = _current.MasterLevelSource
                            _lastSavedJson = json
                            Return _current
                        End If
                    End If
                End If

                Dim legacyPath As String = GetLegacyStateFilePath()
                Dim st As New MixerHardwareStateStore()
                st.NormalizeAfterLoad()

                If File.Exists(legacyPath) Then
                    For Each line As String In File.ReadAllLines(legacyPath)
                        If String.IsNullOrWhiteSpace(line) Then Continue For
                        Dim parts() As String = line.Split("="c)
                        If parts.Length <> 2 Then Continue For
                        Dim key As String = parts(0).Trim()
                        Dim rawVal As String = parts(1).Trim()
                        If key = "MasterLevelSource" Then
                            st.MasterLevelSource = rawVal
                            Continue For
                        End If
                        Dim val As Single = 0.0F
                        If Single.TryParse(rawVal, NumberStyles.Float, CultureInfo.InvariantCulture, val) Then
                            val = Math.Max(0.0F, Math.Min(1.0F, val))
                            Select Case key
                                Case "Trim" : st.Trim = val
                                Case "EqHi" : st.EqHi = val
                                Case "EqMid" : st.EqMid = val
                                Case "EqLow" : st.EqLow = val
                                Case "ColorFx" : st.ColorFx = val
                                Case "ChannelFader" : st.ChannelFader = val
                                Case "MasterVolume01" : st.MasterVolume01 = val
                                Case "MasterBalance01" : st.MasterBalance01 = val
                            End Select
                        End If
                    Next
                End If

                _current = st
                CurrentMasterLevelSource = _current.MasterLevelSource
                FlushNow()
            Catch
            End Try
            Return _current
        End SyncLock
    End Function

    Public Shared Sub SyncFromModelAndPlayer(model As ProjectModel, player As NAudioSyncPlayer)
        If model IsNot Nothing Then Volatile.Write(_lastModelRef, model)
        If player IsNot Nothing Then Volatile.Write(_lastPlayerRef, player)

        Dim trackSnapshots As New List(Of (Key As String, Prof As ChannelProfileConfig))()
        Try
            If model?.Tracks IsNot Nothing Then
                SyncLock model.SyncRoot
                    For i As Integer = 0 To model.Tracks.Count - 1
                        Dim t As TrackModel = model.Tracks(i)
                        If t Is Nothing OrElse t.Type <> TargetFormatType.Audio Then Continue For

                        Dim key As String = If(String.IsNullOrWhiteSpace(t.Name), "MAIN MIC", t.Name.Trim().ToUpperInvariant())
                        If key = "MIC" OrElse key = "MIC 1-2" Then key = "MAIN MIC"

                        Dim fader As Single = If(t.Clips IsNot Nothing AndAlso t.Clips.Count > 0, t.Clips(0).Volume, 1.0F)
                        Dim snapProf As New ChannelProfileConfig() With {
                            .Trim = Math.Max(0.0F, Math.Min(1.0F, t.Trim)),
                            .EqHi = Math.Max(0.0F, Math.Min(1.0F, t.EqHi)),
                            .EqMid = Math.Max(0.0F, Math.Min(1.0F, t.EqMid)),
                            .EqLow = Math.Max(0.0F, Math.Min(1.0F, t.EqLow)),
                            .ColorFx = Math.Max(0.0F, Math.Min(1.0F, t.ColorFx)),
                            .ColorFxMode = Math.Max(0, Math.Min(3, t.ColorFxMode)),
                            .Fader = Math.Max(0.0F, Math.Min(1.0F, fader)),
                            .WaveformZoom = Math.Max(0.25F, Math.Min(4.0F, If(t.WaveformZoom <= 0.01F, 1.0F, t.WaveformZoom))),
                            .IsMuted = t.IsMuted,
                            .IsSolo = t.IsSolo
                        }
                        trackSnapshots.Add((key, snapProf))
                    Next
                End SyncLock
            End If
        Catch
        End Try

        SyncLock _fileLock
            Try
                If _current.ChannelProfiles Is Nothing Then
                    _current.ChannelProfiles = New Dictionary(Of String, ChannelProfileConfig)(StringComparer.OrdinalIgnoreCase)
                End If
                For Each item In trackSnapshots
                    _current.ChannelProfiles(item.Key) = item.Prof
                Next

                If player IsNot Nothing AndAlso Not player.IsDisposed Then
                    _current.MasterVolume01 = Math.Max(0.0F, Math.Min(1.0F, player.Volume / 100.0F))
                    If player.MasterBalanceEffect IsNot Nothing Then
                        _current.MasterBalance01 = Math.Max(0.0F, Math.Min(1.0F, player.MasterBalanceEffect.Balance))
                    End If
                    _current.Audio.MicMonitoringEnabled = player.MicMonitoringEnabled
                End If

                _current.MasterLevelSource = CurrentMasterLevelSource
            Catch
            End Try
        End SyncLock
    End Sub

    Public Shared Sub Save(model As ProjectModel, player As NAudioSyncPlayer)
        If model IsNot Nothing Then Volatile.Write(_lastModelRef, model)
        If player IsNot Nothing Then Volatile.Write(_lastPlayerRef, player)
        RequestSaveDeferred()
    End Sub

    Public Shared Sub RequestSaveDeferred()
        Interlocked.Exchange(_isDirty, 1)
        If Interlocked.CompareExchange(_saveScheduled, 1, 0) = 0 Then
            ThreadPool.QueueUserWorkItem(
                Sub()
                    Try
                        Do
                            Thread.Sleep(400)
                            If Interlocked.Exchange(_isDirty, 0) = 1 Then
                                Dim mRef As ProjectModel = Volatile.Read(_lastModelRef)
                                Dim pRef As NAudioSyncPlayer = Volatile.Read(_lastPlayerRef)
                                If mRef IsNot Nothing OrElse pRef IsNot Nothing Then
                                    SyncFromModelAndPlayer(mRef, pRef)
                                End If
                                FlushNow()
                            End If
                        Loop While Volatile.Read(_isDirty) = 1
                    Catch
                    Finally
                        Interlocked.Exchange(_saveScheduled, 0)
                    End Try
                End Sub)
        End If
    End Sub

    Public Shared Sub FlushNow()
        SyncLock _fileLock
            Try
                Interlocked.Exchange(_isDirty, 0)
                Dim json As String = JsonSerializer.Serialize(_current, _jsonOptions)
                If json = _lastSavedJson AndAlso File.Exists(GetConfigFilePath()) Then Return

                Interlocked.Exchange(LastFlushTick, Environment.TickCount64)
                Dim configPath As String = GetConfigFilePath()
                Dim tempPath As String = configPath & ".tmp"
                File.WriteAllText(tempPath, json)
                File.Move(tempPath, configPath, overwrite:=True)
                Interlocked.Exchange(LastFlushTick, Environment.TickCount64)
                _lastSavedJson = json
            Catch
            End Try
        End SyncLock
    End Sub
End Class

Namespace yoump
    Public Class MediaParameterBroker
        Public Event TrackParamChanged(trackId As Guid, origin As Object)
        Public Event MasterParamChanged(origin As Object)

        Private Const HalfKnobRange As Single = 0.5F
        Private Const MaxEqBoostDb As Single = 6.0F
        Private Const MaxEqCutDb As Single = 12.0F

        Private ReadOnly _model As ProjectModel
        Private ReadOnly _player As NAudioSyncPlayer

        Public Sub New(model As ProjectModel, player As NAudioSyncPlayer)
            _model = model
            _player = player
        End Sub

        Public Shared Function ValueToDecibels(knobVal As Single) As Single
            If Single.IsNaN(knobVal) OrElse Single.IsInfinity(knobVal) Then Return 0.0F
            Dim clamped As Single = Math.Max(0.0F, Math.Min(1.0F, knobVal))
            If clamped >= HalfKnobRange Then
                Return ((clamped - HalfKnobRange) / HalfKnobRange) * MaxEqBoostDb
            Else
                Return ((clamped - HalfKnobRange) / HalfKnobRange) * MaxEqCutDb
            End If
        End Function

        Public Shared Function DecibelsToValue01(db As Single) As Single
            If Single.IsNaN(db) OrElse Single.IsInfinity(db) Then Return HalfKnobRange
            If db >= 0.0F Then
                Dim normBoost As Single = (db / MaxEqBoostDb) * HalfKnobRange + HalfKnobRange
                Return Math.Max(HalfKnobRange, Math.Min(1.0F, normBoost))
            Else
                Dim normCut As Single = (db / MaxEqCutDb) * HalfKnobRange + HalfKnobRange
                Return Math.Max(0.0F, Math.Min(HalfKnobRange, normCut))
            End If
        End Function

        Public Sub SetTrackVolume(trackId As Guid, newVol As Single, origin As Object)
            If Single.IsNaN(newVol) OrElse Single.IsInfinity(newVol) Then newVol = 1.0F
            Dim clamped As Single = Math.Max(0.0F, Math.Min(1.0F, newVol))
            If _model?.Tracks IsNot Nothing Then
                SyncLock _model.SyncRoot
                    For i As Integer = 0 To _model.Tracks.Count - 1
                        Dim t As TrackModel = _model.Tracks(i)
                        If t.Id = trackId Then
                            For cIdx As Integer = 0 To t.Clips.Count - 1
                                t.Clips(cIdx).Volume = clamped
                            Next
                        End If
                    Next
                End SyncLock
            End If
            _player?.SetTrackVolume(trackId, clamped)
            MixerHardwareStateStore.Save(_model, _player)
            RaiseEvent TrackParamChanged(trackId, origin)
        End Sub

        Public Sub SetTrackEQAndTrim(trackId As Guid, low As Single, mid As Single, hi As Single, trim As Single, origin As Object)
            If Single.IsNaN(low) OrElse Single.IsInfinity(low) Then low = 0.5F
            If Single.IsNaN(mid) OrElse Single.IsInfinity(mid) Then mid = 0.5F
            If Single.IsNaN(hi) OrElse Single.IsInfinity(hi) Then hi = 0.5F
            If Single.IsNaN(trim) OrElse Single.IsInfinity(trim) Then trim = 0.5F

            Dim cLow As Single = Math.Max(0.0F, Math.Min(1.0F, low))
            Dim cMid As Single = Math.Max(0.0F, Math.Min(1.0F, mid))
            Dim cHi As Single = Math.Max(0.0F, Math.Min(1.0F, hi))
            Dim cTrim As Single = Math.Max(0.0F, Math.Min(1.0F, trim))

            If _model?.Tracks IsNot Nothing Then
                SyncLock _model.SyncRoot
                    For i As Integer = 0 To _model.Tracks.Count - 1
                        Dim t As TrackModel = _model.Tracks(i)
                        If t.Id = trackId Then
                            t.EqLow = cLow
                            t.EqMid = cMid
                            t.EqHi = cHi
                            t.Trim = cTrim
                        End If
                    Next
                End SyncLock
            End If
            If _player IsNot Nothing Then
                _player.SetTrackEQAndTrim(trackId, ValueToDecibels(cLow), ValueToDecibels(cMid), ValueToDecibels(cHi), cTrim * 2.0F)
            End If
            MixerHardwareStateStore.Save(_model, _player)
            RaiseEvent TrackParamChanged(trackId, origin)
        End Sub

        Public Sub SetTrackColorFilter(trackId As Guid, colorFx As Single, origin As Object)
            If Single.IsNaN(colorFx) OrElse Single.IsInfinity(colorFx) Then colorFx = 0.5F
            Dim clamped As Single = Math.Max(0.0F, Math.Min(1.0F, colorFx))
            If _model?.Tracks IsNot Nothing Then
                SyncLock _model.SyncRoot
                    For i As Integer = 0 To _model.Tracks.Count - 1
                        Dim t As TrackModel = _model.Tracks(i)
                        If t.Id = trackId Then
                            t.ColorFx = clamped
                        End If
                    Next
                End SyncLock
            End If
            _player?.SetTrackColorFilter(trackId, clamped)
            MixerHardwareStateStore.Save(_model, _player)
            RaiseEvent TrackParamChanged(trackId, origin)
        End Sub

        Public Sub SetTrackColorFxMode(trackId As Guid, mode As Integer, origin As Object)
            Dim safeMode As Integer = Math.Max(0, Math.Min(3, mode))
            If _model?.Tracks IsNot Nothing Then
                SyncLock _model.SyncRoot
                    For i As Integer = 0 To _model.Tracks.Count - 1
                        Dim t As TrackModel = _model.Tracks(i)
                        If t.Id = trackId Then
                            t.ColorFxMode = safeMode
                        End If
                    Next
                End SyncLock
            End If
            _player?.SetTrackColorFxMode(trackId, safeMode)
            MixerHardwareStateStore.Save(_model, _player)
            RaiseEvent TrackParamChanged(trackId, origin)
        End Sub

        Public Sub SetTrackMute(trackId As Guid, isMuted As Boolean, origin As Object)
            If _model?.Tracks IsNot Nothing Then
                SyncLock _model.SyncRoot
                    For i As Integer = 0 To _model.Tracks.Count - 1
                        Dim t As TrackModel = _model.Tracks(i)
                        If t.Id = trackId Then
                            t.IsMuted = isMuted
                        End If
                    Next
                End SyncLock
            End If
            _player?.SetTrackMute(trackId, isMuted)
            MixerHardwareStateStore.Save(_model, _player)
            RaiseEvent TrackParamChanged(trackId, origin)
        End Sub

        Public Sub SetTrackSolo(trackId As Guid, isSolo As Boolean, origin As Object)
            If _model?.Tracks IsNot Nothing Then
                SyncLock _model.SyncRoot
                    For i As Integer = 0 To _model.Tracks.Count - 1
                        Dim t As TrackModel = _model.Tracks(i)
                        If t.Id = trackId Then
                            t.IsSolo = isSolo
                        End If
                    Next
                End SyncLock
            End If
            _player?.SetTrackSolo(trackId, isSolo)
            MixerHardwareStateStore.Save(_model, _player)
            RaiseEvent TrackParamChanged(trackId, origin)
        End Sub

        Public Sub ApplyPresetToTrack(trackId As Guid, preset As ChannelProfileConfig, origin As Object)
            If preset Is Nothing Then Return
            SetTrackEQAndTrim(trackId, preset.EqLow, preset.EqMid, preset.EqHi, preset.Trim, origin)
            SetTrackColorFxMode(trackId, preset.ColorFxMode, origin)
            SetTrackColorFilter(trackId, preset.ColorFx, origin)
            SetTrackVolume(trackId, preset.Fader, origin)
        End Sub

        Public Sub SetMasterVolume01(masterVol01 As Single, origin As Object)
            If Single.IsNaN(masterVol01) OrElse Single.IsInfinity(masterVol01) Then masterVol01 = 1.0F
            Dim clamped As Single = Math.Max(0.0F, Math.Min(1.0F, masterVol01))
            If _player IsNot Nothing Then
                _player.Volume = clamped * 100.0F
            End If
            MixerHardwareStateStore.Save(_model, _player)
            RaiseEvent MasterParamChanged(origin)
        End Sub

        Public Sub SetMasterBalance01(balance01 As Single, origin As Object)
            If Single.IsNaN(balance01) OrElse Single.IsInfinity(balance01) Then balance01 = 0.5F
            Dim clamped As Single = Math.Max(0.0F, Math.Min(1.0F, balance01))
            If _player IsNot Nothing Then
                _player.SetBalance(clamped)
            End If
            MixerHardwareStateStore.Save(_model, _player)
            RaiseEvent MasterParamChanged(origin)
        End Sub
    End Class
End Namespace

Public Enum TargetFormatType
    Video = 0
    Audio = 1
End Enum

Public Class ClipModel
    Public Property Id As Guid = Guid.NewGuid()
    Public Property FilePath As String = ""
    Public Property TimelineStart As TimeSpan = TimeSpan.Zero
    Public Property TimelineEnd As TimeSpan = TimeSpan.FromSeconds(60)
    Public Property SourceIn As TimeSpan = TimeSpan.Zero
    Public Property Volume As Single = 1.0F
End Class

Public Class TrackModel
    Public Property Id As Guid = Guid.NewGuid()
    Public Property Name As String = "SYSTEM"
    Public Property Type As TargetFormatType = TargetFormatType.Audio
    Public Property IsMuted As Boolean = False
    Public Property IsSolo As Boolean = False
    Public Property Trim As Single = 0.5F
    Public Property EqHi As Single = 0.5F
    Public Property EqMid As Single = 0.5F
    Public Property EqLow As Single = 0.5F
    Public Property ColorFx As Single = 0.5F
    Public Property ColorFxMode As Integer = 0
    Public Property WaveformZoom As Single = 1.0F
    Public Property Clips As New List(Of ClipModel)()
End Class

Public Class ProjectModel
    Public ReadOnly SyncRoot As New Object()
    Public Property Tracks As New List(Of TrackModel)()
    Public Property TotalDuration As TimeSpan = TimeSpan.FromSeconds(60)

    Public Event StateChanged()
    Public Event PropertiesChanged()

    Public Sub NotifyStateChanged()
        RaiseEvent StateChanged()
    End Sub

    Public Sub NotifyPropertiesChanged()
        RaiseEvent PropertiesChanged()
    End Sub

    Public Sub RefreshSnapshotOnly()
    End Sub
End Class

Public Class AppSettings
    Public Property ProjectFps As Double
        Get
            Return MixerHardwareStateStore.Current.Ui.ProjectFps
        End Get
        Set(value As Double)
            MixerHardwareStateStore.Current.Ui.ProjectFps = value
            MixerHardwareStateStore.RequestSaveDeferred()
        End Set
    End Property

    Public Property CacheDirectory As String
        Get
            Return MixerHardwareStateStore.Current.Ui.CacheDirectory
        End Get
        Set(value As String)
            MixerHardwareStateStore.Current.Ui.CacheDirectory = If(value, "")
            MixerHardwareStateStore.RequestSaveDeferred()
        End Set
    End Property
End Class

Public Class SettingsService
    Private Shared ReadOnly _instance As New SettingsService()
    Public Shared ReadOnly Property Instance As SettingsService
        Get
            Return _instance
        End Get
    End Property

    Public Property Current As New AppSettings()
    Public Shared Event ProjectFpsChanged(newFps As Double)

    Public Shared Sub RaiseFpsChanged(newFps As Double)
        Instance.Current.ProjectFps = newFps
        RaiseEvent ProjectFpsChanged(newFps)
    End Sub
End Class

Public Class ThemeChangedEventArgs
    Inherits EventArgs
End Class

Public Class ThemeManager
    Public Shared Property IsDarkTheme As Boolean
        Get
            Return MixerHardwareStateStore.Current.Ui.IsDarkTheme
        End Get
        Set(value As Boolean)
            If MixerHardwareStateStore.Current.Ui.IsDarkTheme <> value Then
                MixerHardwareStateStore.Current.Ui.IsDarkTheme = value
                MixerHardwareStateStore.RequestSaveDeferred()
                RaiseEvent ThemeChanged(Nothing, New ThemeChangedEventArgs())
            End If
        End Set
    End Property

    Public Shared ReadOnly Property BackColor As Color
        Get
            Return If(IsDarkTheme, Color.FromArgb(14, 14, 16), Color.FromArgb(222, 222, 222))
        End Get
    End Property

    Public Shared ReadOnly Property ForeColor As Color
        Get
            Return If(IsDarkTheme, Color.FromArgb(240, 240, 240), Color.FromArgb(20, 20, 22))
        End Get
    End Property

    Public Shared Event ThemeChanged As EventHandler(Of ThemeChangedEventArgs)

    <DllImport("dwmapi.dll", PreserveSig:=True)>
    Private Shared Function DwmSetWindowAttribute(hwnd As IntPtr, attr As Integer, ByRef attrValue As Integer, attrSize As Integer) As Integer
    End Function

    Public Shared Sub ApplyDwm(hwnd As IntPtr)
        If hwnd = IntPtr.Zero Then Return
        Try
            Dim useDark As Integer = If(IsDarkTheme, 1, 0)
            DwmSetWindowAttribute(hwnd, 20, useDark, Marshal.SizeOf(GetType(Integer)))
        Catch
        End Try
    End Sub
End Class

Public Class DiagnosticsClient
    Public Shared Sub RegisterSilentException(ex As Exception)
    End Sub
End Class

Public Class TimelineCommandDispatcher
    Public Sub RequestVisualOnlyRedraw()
    End Sub
End Class
' --- END OF FILE SupportTypes.vb ---