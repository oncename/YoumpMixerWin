' --- START OF FILE AudioMixerControl.vb ---
Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.IO
Imports System.Linq
Imports System.Numerics
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Windows.Forms
Imports Vortice
Imports Vortice.DCommon
Imports Vortice.Direct2D1
Imports Vortice.Direct3D11
Imports Vortice.DirectWrite
Imports Vortice.DXGI
Imports Vortice.Mathematics
Imports yoump.Audio
Imports yoump.Audio.Native
Imports yoump.IServices
Imports yoump.Native
Imports D2D1 = Vortice.Direct2D1.D2D1
Imports DWrite = Vortice.DirectWrite.DWrite
Imports PixelFormat = Vortice.DCommon.PixelFormat
Imports AlphaMode = Vortice.DCommon.AlphaMode

Public Class AudioMixerControl
    Inherits Control

    <DllImport("user32.dll", ExactSpelling:=True)>
    Private Shared Function IsWindow(hWnd As IntPtr) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    Private Shared Function IsDeviceLostException(ex As Exception) As Boolean
        Dim hr As Integer = ex.HResult
        Return hr = -2005270523 OrElse
               hr = -2005270521 OrElse
               hr = -2003119092 OrElse
               hr = -2003238911 OrElse
               ex.Message.Contains("DEVICE_REMOVED") OrElse
               ex.Message.Contains("WRONG_STATE")
    End Function

    Private Shared Sub SafeRelease(Of T As Class)(ByRef comObj As T)
        Dim objToDispose As T = Interlocked.Exchange(comObj, Nothing)
        If objToDispose IsNot Nothing AndAlso TypeOf objToDispose Is IDisposable Then
            DirectCast(objToDispose, IDisposable).Dispose()
        End If
    End Sub

    Private Shared ReadOnly Property CurrentProjectFps As Double
        Get
            Try
                Dim fps As Double = SettingsService.Instance.Current.ProjectFps
                If fps >= 10.0 AndAlso fps <= 1000.0 Then Return fps
                Return 60.0
            Catch
                Return 60.0
            End Try
        End Get
    End Property

    Private Shared ReadOnly Property TargetFrameIntervalMs As Integer
        Get
            Dim interval = CInt(Math.Floor(1000.0 / CurrentProjectFps))
            Return Math.Max(1, interval)
        End Get
    End Property

    Public Class AudioChannelState
        Public Trim As Single = 0.5F
        Public Hi As Single = 0.5F
        Public Mid As Single = 0.5F
        Public Low As Single = 0.5F
        Public ColorFx As Single = 0.5F
        Public ColorFxMode As Integer = 0
        Public Fader As Single = 1.0F
        Public WaveformZoom As Single = 1.0F
        Public IsMuted As Boolean = False
        Public IsSolo As Boolean = False
        Public Peak As Single = 0.0F
        Public PeakHold As Single = 0.0F
        Public PeakHoldTicks As Long = 0
    End Class

    Private Class CachedMixerPeakSlice
        Public MipLevel As Integer
        Public StartIndex As Integer
        Public Count As Integer
        Public TargetBarCount As Integer
        Public Peaks As NativePeakMinMax()
        Public ThreeBandBars As ThreeBandPeakBar()
    End Class

    Private ReadOnly myModel As ProjectModel
    Private ReadOnly myPlayback As IPlaybackController
    Private _audioPlayer As NAudioSyncPlayer
    Private _masterBusRenderer As MasterBusRenderer
    Private _broker As yoump.MediaParameterBroker
    Private _dispatcher As TimelineCommandDispatcher
    Private _invalidatePending As Integer = 0

    ' Ресурсы Direct3D 11 + DXGI Flip SwapChain + Direct2D 1.1 DeviceContext
    Private factory2D As ID2D1Factory1
    Private factoryDW As IDWriteFactory
    Private d3dDevice As ID3D11Device
    Private d3dContext As ID3D11DeviceContext
    Private dxgiSwapChain As IDXGISwapChain1
    Private d2dDevice As ID2D1Device
    Private d2dTargetBitmap As ID2D1Bitmap1
    Private d2dContext As ID2D1DeviceContext
    Private renderTarget As ID2D1RenderTarget

    Private brushBgEq, brushBgColor, brushBgFader, brushSeparator As ID2D1SolidColorBrush
    Private brushTextWhite, brushTextGray, brushTextDark, brushBigNumber As ID2D1SolidColorBrush
    Private brushKnobWhiteRing, brushKnobBlack, brushKnobGray, brushKnobSilver, brushKnobIndicator, brushKnobBlueIndicator As ID2D1SolidColorBrush
    Private brushFaderSlot, brushFaderThumbBorder, brushFaderThumbBg As ID2D1SolidColorBrush
    Private brushLedTrench, brushLedOff, brushLedGreen, brushLedYellowGreen, brushLedYellow, brushLedOrange, brushLedRed As ID2D1SolidColorBrush
    Private brushWaveBg, brushWaveform, brushWavePlayhead, brushWaveGrid, brushWaveClipBg As ID2D1SolidColorBrush
    Private brushWaveLowShadow, brushWaveLow, brushWaveHigh, brushWaveCore, brushWaveEcho, brushWavePlayedDim As ID2D1SolidColorBrush
    Private brushOsdBg, brushOsdBorder As ID2D1SolidColorBrush

    Private fontNormalCenter, fontSmallCenter As IDWriteTextFormat
    Private fontTinyCenter, fontTinyLeft, fontTinyRight As IDWriteTextFormat
    Private fontLedCenter, fontLedLeft, fontBadge As IDWriteTextFormat

    Private draggedProperty As String = ""
    Private dragStartMouseX, dragStartMouseY As Integer
    Private ReadOnly audioStates As New Dictionary(Of Guid, AudioChannelState)()

    Private _fxPopupTrackId As Guid = Guid.Empty
    Private _fxPopupHoverMode As Integer = -1

    Private _masterHoldL As Single = 0.0F
    Private _masterHoldTicksL As Long = 0
    Private _masterHoldR As Single = 0.0F
    Private _masterHoldTicksR As Long = 0
    Private _osdText As String = ""
    Private _osdTrackId As Guid = Guid.Empty
    Private _osdExpireTicks As Long = 0
    Private masterVolume As Single = 1.0F
    Private masterBalance As Single = 0.5F

    Private ReadOnly _cacheDir As String
    Private ReadOnly _mmfCache As New Dictionary(Of String, PersistentWaveformCache)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _mixerPeakSlices As New Dictionary(Of Guid, CachedMixerPeakSlice)()
    Private ReadOnly _lastMmfProbeTicks As New Dictionary(Of String, Long)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _invalidateAction As Action

    Private _isRenderingTimer As Integer = 0
    Private _isRenderingFrame As Integer = 0
    Private _pendingResize As Integer = 0
    Private _swapChainWidth As Integer = 0
    Private _swapChainHeight As Integer = 0

    <Browsable(False)>
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property MasterPeakL As Single = 0.0F

    <Browsable(False)>
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property MasterPeakR As Single = 0.0F

    <Browsable(False)>
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property AudioPlayer As NAudioSyncPlayer
        Get
            Return _audioPlayer
        End Get
        Set(value As NAudioSyncPlayer)
            _audioPlayer = value
            If _audioPlayer IsNot Nothing Then
                masterVolume = CSng(_audioPlayer.Volume / 100.0F)
                If Single.IsNaN(masterVolume) OrElse Single.IsInfinity(masterVolume) Then masterVolume = 1.0F

                If _audioPlayer.MasterBalanceEffect IsNot Nothing Then
                    masterBalance = _audioPlayer.MasterBalanceEffect.Balance
                    If Single.IsNaN(masterBalance) OrElse Single.IsInfinity(masterBalance) Then masterBalance = 0.5F
                End If
            End If
        End Set
    End Property

    <Browsable(False)>
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property MasterBus As MasterBusRenderer
        Get
            Return _masterBusRenderer
        End Get
        Set(value As MasterBusRenderer)
            _masterBusRenderer = value
            If _masterBusRenderer IsNot Nothing Then
                masterVolume = CSng(_masterBusRenderer.CurrentVolume / 100.0F)
                If Single.IsNaN(masterVolume) OrElse Single.IsInfinity(masterVolume) Then masterVolume = 1.0F
            End If
        End Set
    End Property

    <Browsable(False)>
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property CommandDispatcher As TimelineCommandDispatcher
        Get
            Return _dispatcher
        End Get
        Set(value As TimelineCommandDispatcher)
            _dispatcher = value
        End Set
    End Property

    Private isDeviceValid As Boolean = False
    Private isDisposedState As Boolean = False
    Private mixerScrollX As Single = 0.0F

    Private WithEvents RenderTimer As New System.Windows.Forms.Timer()

    Public Const TRACK_WIDTH As Single = 92.0F
    Public Const MASTER_WIDTH As Single = 88.0F
    Private Const VIRTUAL_HEIGHT As Single = 490.0F

    Private Const WAVE_SECTION_H As Single = 85.0F
    Private Const EQ_SECTION_H As Single = 215.0F
    Private Const COL_SECTION_H As Single = 55.0F

    Private Const Y_TRIM As Single = 127.0F
    Private Const Y_HI As Single = 173.0F
    Private Const Y_MID As Single = 219.0F
    Private Const Y_LOW As Single = 265.0F

    Private Const FX_POPUP_ITEM_H As Single = 15.5F
    Private Const FX_POPUP_COUNT As Integer = 4

    Private currentScale As Single = 1.0F
    Private currentOffsetX As Single = 0.0F

    Private _waveformAmpScale As Single = 1.0F
    Private Const WAVE_AMP_MIN As Single = 0.25F
    Private Const WAVE_AMP_MAX As Single = 4.0F
    Private Const WAVE_AMP_STEP As Single = 0.25F

    Public Sub New(model As ProjectModel,
                   playback As IPlaybackController,
                   Optional audioPlayer As NAudioSyncPlayer = Nothing,
                   Optional masterBus As MasterBusRenderer = Nothing,
                   Optional broker As yoump.MediaParameterBroker = Nothing,
                   Optional dispatcher As TimelineCommandDispatcher = Nothing)

        _invalidateAction = New Action(Sub()
                                           Interlocked.Exchange(_invalidatePending, 0)
                                           If Not isDisposedState AndAlso Not Me.IsDisposed AndAlso Me.IsHandleCreated Then
                                               SyncFromModel()
                                               SyncMasterFromPlayer()
                                               Me.Invalidate()
                                           End If
                                       End Sub)
        Me.SetStyle(ControlStyles.AllPaintingInWmPaint Or ControlStyles.UserPaint Or ControlStyles.Opaque, True)
        Me.DoubleBuffered = False

        myModel = model
        myPlayback = playback
        _audioPlayer = audioPlayer
        _masterBusRenderer = masterBus
        _broker = broker
        _dispatcher = dispatcher

        _waveformAmpScale = Math.Max(WAVE_AMP_MIN, Math.Min(WAVE_AMP_MAX, MixerHardwareStateStore.Current.Ui.WaveformAmpScale))

        If myModel IsNot Nothing Then
            AddHandler myModel.StateChanged, AddressOf OnModelStateChanged
        End If

        If _broker IsNot Nothing Then
            SetBroker(_broker)
        End If

        If _masterBusRenderer IsNot Nothing Then
            masterVolume = CSng(_masterBusRenderer.CurrentVolume / 100.0F)
            If Single.IsNaN(masterVolume) OrElse Single.IsInfinity(masterVolume) Then masterVolume = 1.0F
        ElseIf _audioPlayer IsNot Nothing Then
            masterVolume = CSng(_audioPlayer.Volume / 100.0F)
            If Single.IsNaN(masterVolume) OrElse Single.IsInfinity(masterVolume) Then masterVolume = 1.0F
        End If

        If _audioPlayer IsNot Nothing AndAlso _audioPlayer.MasterBalanceEffect IsNot Nothing Then
            masterBalance = _audioPlayer.MasterBalanceEffect.Balance
            If Single.IsNaN(masterBalance) OrElse Single.IsInfinity(masterBalance) Then masterBalance = 0.5F
        End If

        Dim baseCacheDir As String = SettingsService.Instance.Current.CacheDirectory
        If String.IsNullOrWhiteSpace(baseCacheDir) Then
            baseCacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "yoump")
        End If
        _cacheDir = Path.Combine(baseCacheDir, "PeakCache")
        Try
            If Not Directory.Exists(_cacheDir) Then
                Directory.CreateDirectory(_cacheDir)
            End If
        Catch
        End Try

        Try
            factory2D = D2D1.D2D1CreateFactory(Of ID2D1Factory1)(Direct2D1.FactoryType.MultiThreaded)
            factoryDW = DWrite.DWriteCreateFactory(Of IDWriteFactory)()

            fontNormalCenter = factoryDW.CreateTextFormat("Segoe UI", FontWeight.Bold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 7.0F)
            fontNormalCenter.TextAlignment = TextAlignment.Center
            fontNormalCenter.ParagraphAlignment = ParagraphAlignment.Center

            fontSmallCenter = factoryDW.CreateTextFormat("Segoe UI", FontWeight.Bold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 5.8F)
            fontSmallCenter.TextAlignment = TextAlignment.Center
            fontSmallCenter.ParagraphAlignment = ParagraphAlignment.Center

            fontTinyCenter = factoryDW.CreateTextFormat("Segoe UI", FontWeight.Bold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 4.5F)
            fontTinyCenter.TextAlignment = TextAlignment.Center
            fontTinyCenter.ParagraphAlignment = ParagraphAlignment.Center

            fontTinyLeft = factoryDW.CreateTextFormat("Segoe UI", FontWeight.Bold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 4.5F)
            fontTinyLeft.TextAlignment = TextAlignment.Leading
            fontTinyLeft.ParagraphAlignment = ParagraphAlignment.Center

            fontTinyRight = factoryDW.CreateTextFormat("Segoe UI", FontWeight.Bold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 4.5F)
            fontTinyRight.TextAlignment = TextAlignment.Trailing
            fontTinyRight.ParagraphAlignment = ParagraphAlignment.Center

            fontLedCenter = factoryDW.CreateTextFormat("Segoe UI", FontWeight.Bold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 4.8F)
            fontLedCenter.TextAlignment = TextAlignment.Center
            fontLedCenter.ParagraphAlignment = ParagraphAlignment.Center

            fontLedLeft = factoryDW.CreateTextFormat("Segoe UI", FontWeight.Bold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 4.8F)
            fontLedLeft.TextAlignment = TextAlignment.Leading
            fontLedLeft.ParagraphAlignment = ParagraphAlignment.Center

            fontBadge = factoryDW.CreateTextFormat("Segoe UI", FontWeight.Bold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 6.5F)
            fontBadge.TextAlignment = TextAlignment.Center
            fontBadge.ParagraphAlignment = ParagraphAlignment.Center
        Catch
        End Try

        SyncFromModel()

        RenderTimer.Interval = TargetFrameIntervalMs
        AddHandler SettingsService.ProjectFpsChanged, AddressOf OnProjectFpsChanged
    End Sub

    Public Shared Function GetColorFxModeName(mode As Integer) As String
        Select Case mode And 3
            Case 1 : Return "SPACE"
            Case 2 : Return "CRUSH"
            Case 3 : Return "LOUD"
            Case Else : Return "FILTER"
        End Select
    End Function

    Private Shared Function GetColorFxPopupOptionTitle(mode As Integer) As String
        Select Case mode And 3
            Case 1 : Return "SPACE (ECHO)"
            Case 2 : Return "CRUSH (DRV/BIT)"
            Case 3 : Return "LOUD (VOC/SUB)"
            Case Else : Return "FILTER (LPF/HPF)"
        End Select
    End Function

    Public Sub ShowExternalBanner(trackId As Guid, text As String)
        ShowOsd(trackId, text)
        SafeInvalidate()
    End Sub

    Public Sub ReloadSettingsFromConfig()
        _waveformAmpScale = Math.Max(WAVE_AMP_MIN, Math.Min(WAVE_AMP_MAX, MixerHardwareStateStore.Current.Ui.WaveformAmpScale))
        RenderTimer.Interval = TargetFrameIntervalMs
        SyncFromModel()
        SyncMasterFromPlayer()
        SafeInvalidate()
    End Sub

    Private Function GetAudioTracksSnapshot() As List(Of TrackModel)
        Dim list As New List(Of TrackModel)()
        If myModel?.Tracks Is Nothing Then Return list
        SyncLock myModel.SyncRoot
            For i As Integer = 0 To myModel.Tracks.Count - 1
                Dim t As TrackModel = myModel.Tracks(i)
                If t IsNot Nothing AndAlso t.Type = TargetFormatType.Audio Then
                    list.Add(t)
                End If
            Next
        End SyncLock
        Return list
    End Function

    Public Sub SetBroker(broker As yoump.MediaParameterBroker)
        If _broker IsNot Nothing Then
            RemoveHandler _broker.TrackParamChanged, AddressOf OnExternalTrackParamChanged
            RemoveHandler _broker.MasterParamChanged, AddressOf OnExternalMasterParamChanged
        End If
        _broker = broker
        If _broker IsNot Nothing Then
            AddHandler _broker.TrackParamChanged, AddressOf OnExternalTrackParamChanged
            AddHandler _broker.MasterParamChanged, AddressOf OnExternalMasterParamChanged
        End If
    End Sub

    Private Sub OnExternalTrackParamChanged(trackId As Guid, origin As Object)
        If Object.ReferenceEquals(origin, Me) Then Return
        If isDisposedState OrElse Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
        If Me.InvokeRequired Then
            SafeInvalidate()
        Else
            SyncFromModel()
            SafeInvalidate()
        End If
    End Sub

    Private Sub OnExternalMasterParamChanged(origin As Object)
        If Object.ReferenceEquals(origin, Me) Then Return
        If isDisposedState OrElse Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
        If Me.InvokeRequired Then
            SafeInvalidate()
        Else
            SyncMasterFromPlayer()
            SafeInvalidate()
        End If
    End Sub

    Private _osdChannelTitle As String = "MASTER"
    Private _osdParamName As String = "READY"
    Private _osdValueText As String = "0.0 dB"

    Private Sub ShowOsd(trackId As Guid, text As String)
        _osdTrackId = trackId
        _osdText = text
        Dim osdMs As Integer = Math.Max(500, MixerHardwareStateStore.Current.Ui.OsdDurationMs)
        _osdExpireTicks = Environment.TickCount64 + osdMs

        If trackId = Guid.Empty Then
            _osdChannelTitle = "MASTER"
        Else
            Dim audioTracks As List(Of TrackModel) = GetAudioTracksSnapshot()
            For idx As Integer = 0 To audioTracks.Count - 1
                Dim t As TrackModel = audioTracks(idx)
                If t.Id = trackId Then
                    _osdChannelTitle = If(Not String.IsNullOrWhiteSpace(t.Name), $"CH{idx + 1}: {t.Name}", $"CH {idx + 1}")
                    Exit For
                End If
            Next
        End If

        Dim parts As String() = text.Split(":"c)
        If parts.Length >= 2 Then
            _osdParamName = parts(0).Trim()
            _osdValueText = parts(1).Trim()
        Else
            _osdParamName = "PARAM"
            _osdValueText = text
        End If
    End Sub

    Private Shared Function FormatEqBandOsd(bandName As String, val01 As Single) As String
        If val01 <= 0.012F Then Return $"{bandName}: KILL"
        If Math.Abs(val01 - 0.5F) <= 0.01F Then Return $"{bandName}: 0.0 dB"
        If val01 > 0.5F Then
            Dim boostDb As Single = ((val01 - 0.5F) / 0.5F) * 6.0F
            Return $"{bandName}: +{boostDb:F1} dB"
        Else
            Dim cutDb As Single = (val01 - 0.5F) * 24.0F
            Return $"{bandName}: {cutDb:F1} dB"
        End If
    End Function

    Private Shared Function FormatTrimOsd(val01 As Single) As String
        If val01 <= 0.015F Then Return "TRIM: -INF dB"
        If Math.Abs(val01 - 0.5F) <= 0.01F Then Return "TRIM: 0.0 dB"
        If val01 > 0.5F Then
            Dim boostDb As Single = ((val01 - 0.5F) / 0.5F) * 9.0F
            Return $"TRIM: +{boostDb:F1} dB"
        Else
            Dim norm As Single = Math.Max(0.001F, (val01 - 0.015F) / 0.485F)
            Dim db As Single = CSng(20.0 * Math.Log10(norm * norm))
            Return $"TRIM: {db:F1} dB"
        End If
    End Function

    Private Shared Function FormatColorFxOsd(val01 As Single, Optional mode As Integer = 0) As String
        Dim m As Integer = mode And 3
        If Math.Abs(val01 - 0.5F) <= 0.012F Then
            Return $"{GetColorFxModeName(m)}: BYPASS"
        End If

        Dim pct As Integer = CInt(Math.Round(Math.Abs(val01 - 0.5F) * 200.0F))
        Select Case m
            Case 1
                Return If(val01 < 0.5F, $"DUB ECHO: {pct}%", $"SPACE: {pct}%")
            Case 2
                Return If(val01 < 0.5F, $"DRIVE: {pct}%", $"CRUSH: {pct}%")
            Case 3
                Return If(val01 < 0.5F, $"VOCAL: {pct}%", $"BASS+: {pct}%")
            Case Else
                If val01 < 0.5F Then
                    Dim t As Single = Math.Max(0.0F, Math.Min(1.0F, val01 / 0.48F))
                    Dim freq As Double = 30.0 * Math.Pow(18000.0 / 30.0, t)
                    If freq >= 1000.0 Then
                        Return $"LPF: {(freq / 1000.0):F2} kHz"
                    Else
                        Return $"LPF: {freq:F0} Hz"
                    End If
                Else
                    Dim t As Single = Math.Max(0.0F, Math.Min(1.0F, (val01 - 0.52F) / 0.48F))
                    Dim freq As Double = 25.0 * Math.Pow(15000.0 / 25.0, t)
                    If freq >= 1000.0 Then
                        Return $"HPF: {(freq / 1000.0):F2} kHz"
                    Else
                        Return $"HPF: {freq:F0} Hz"
                    End If
                End If
        End Select
    End Function

    Private Sub SyncMasterFromPlayer()
        If _masterBusRenderer IsNot Nothing AndAlso draggedProperty <> "MixerMasterVol" Then
            Dim newVol As Single = CSng(_masterBusRenderer.CurrentVolume / 100.0F)
            If Single.IsNaN(newVol) OrElse Single.IsInfinity(newVol) Then newVol = 1.0F
            If Math.Abs(newVol - masterVolume) > 0.005F Then
                ShowOsd(Guid.Empty, $"MASTER: {CInt(Math.Round(newVol * 100.0F))}%")
            End If
            masterVolume = newVol
        ElseIf _audioPlayer IsNot Nothing AndAlso draggedProperty <> "MixerMasterVol" Then
            Dim newVol As Single = CSng(_audioPlayer.Volume / 100.0F)
            If Single.IsNaN(newVol) OrElse Single.IsInfinity(newVol) Then newVol = 1.0F
            If Math.Abs(newVol - masterVolume) > 0.005F Then
                ShowOsd(Guid.Empty, $"MASTER: {CInt(Math.Round(newVol * 100.0F))}%")
            End If
            masterVolume = newVol
        End If

        If _audioPlayer?.MasterBalanceEffect IsNot Nothing AndAlso draggedProperty <> "MixerMasterBal" Then
            Dim newBal As Single = _audioPlayer.MasterBalanceEffect.Balance
            If Single.IsNaN(newBal) OrElse Single.IsInfinity(newBal) Then newBal = 0.5F
            If Math.Abs(newBal - masterBalance) > 0.005F Then
                Dim balTxt As String = If(Math.Abs(newBal - 0.5F) <= 0.015F, "BAL: CENTER",
                                       If(newBal < 0.5F, $"BAL: L {CInt(Math.Round((0.5F - newBal) * 200.0F))}%",
                                                         $"BAL: R {CInt(Math.Round((newBal - 0.5F) * 200.0F))}%"))
                ShowOsd(Guid.Empty, balTxt)
            End If
            masterBalance = newBal
        End If
    End Sub

    Private Sub OnModelStateChanged()
        If isDisposedState OrElse Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
        If Me.InvokeRequired Then
            SafeInvalidate()
        Else
            SyncFromModel()
            SafeInvalidate()
        End If
    End Sub

    Private Sub SyncFromModel()
        If myModel?.Tracks Is Nothing Then Return
        Dim tracksSnap As List(Of TrackModel) = GetAudioTracksSnapshot()
        Dim activeIds As New HashSet(Of Guid)()

        For Each track In tracksSnap
            activeIds.Add(track.Id)

            Dim st As AudioChannelState = Nothing
            Dim isNew As Boolean = False
            If Not audioStates.TryGetValue(track.Id, st) Then
                Dim savedProf As ChannelProfileConfig = MixerHardwareStateStore.GetOrCreateChannelProfile(track.Name)
                If savedProf IsNot Nothing AndAlso savedProf.WaveformZoom > 0.05F Then
                    track.WaveformZoom = savedProf.WaveformZoom
                End If
                st = New AudioChannelState() With {
                    .WaveformZoom = Math.Max(WAVE_AMP_MIN, Math.Min(WAVE_AMP_MAX, If(track.WaveformZoom <= 0.05F, 1.0F, track.WaveformZoom)))
                }
                audioStates(track.Id) = st
                isNew = True
            End If

            Dim idSuffix As String = track.Id.ToString()
            Dim faderVal As Single = If(track.Clips IsNot Nothing AndAlso track.Clips.Count > 0, track.Clips(0).Volume, 1.0F)
            If Not isNew Then
                If draggedProperty <> $"MixerTrim_{idSuffix}" AndAlso Math.Abs(st.Trim - track.Trim) > 0.004F Then
                    ShowOsd(track.Id, FormatTrimOsd(track.Trim))
                ElseIf draggedProperty <> $"MixerHi_{idSuffix}" AndAlso Math.Abs(st.Hi - track.EqHi) > 0.004F Then
                    ShowOsd(track.Id, FormatEqBandOsd("HI", track.EqHi))
                ElseIf draggedProperty <> $"MixerMid_{idSuffix}" AndAlso Math.Abs(st.Mid - track.EqMid) > 0.004F Then
                    ShowOsd(track.Id, FormatEqBandOsd("MID", track.EqMid))
                ElseIf draggedProperty <> $"MixerLow_{idSuffix}" AndAlso Math.Abs(st.Low - track.EqLow) > 0.004F Then
                    ShowOsd(track.Id, FormatEqBandOsd("LOW", track.EqLow))
                ElseIf draggedProperty <> $"MixerColor_{idSuffix}" AndAlso Math.Abs(st.ColorFx - track.ColorFx) > 0.004F Then
                    ShowOsd(track.Id, FormatColorFxOsd(track.ColorFx, track.ColorFxMode))
                ElseIf st.ColorFxMode <> track.ColorFxMode Then
                    ShowOsd(track.Id, $"FX MODE: {GetColorFxModeName(track.ColorFxMode)}")
                ElseIf st.IsMuted <> track.IsMuted Then
                    ShowOsd(track.Id, If(track.IsMuted, "MUTE: ON", "MUTE: OFF"))
                ElseIf st.IsSolo <> track.IsSolo Then
                    ShowOsd(track.Id, If(track.IsSolo, "SOLO: ON", "SOLO: OFF"))
                ElseIf draggedProperty <> $"MixerVol_{idSuffix}" AndAlso Math.Abs(st.Fader - faderVal) > 0.005F Then
                    ShowOsd(track.Id, $"FADER: {(faderVal * 10.0F):F1}")
                End If
            End If

            If draggedProperty <> $"MixerTrim_{idSuffix}" Then st.Trim = track.Trim
            If draggedProperty <> $"MixerHi_{idSuffix}" Then st.Hi = track.EqHi
            If draggedProperty <> $"MixerMid_{idSuffix}" Then st.Mid = track.EqMid
            If draggedProperty <> $"MixerLow_{idSuffix}" Then st.Low = track.EqLow
            If draggedProperty <> $"MixerColor_{idSuffix}" Then st.ColorFx = track.ColorFx
            st.ColorFxMode = track.ColorFxMode
            st.IsMuted = track.IsMuted
            st.IsSolo = track.IsSolo
            If track.WaveformZoom > 0.05F Then
                st.WaveformZoom = Math.Max(WAVE_AMP_MIN, Math.Min(WAVE_AMP_MAX, track.WaveformZoom))
            End If
            If draggedProperty <> $"MixerVol_{idSuffix}" Then st.Fader = faderVal
        Next

        If _fxPopupTrackId <> Guid.Empty AndAlso Not activeIds.Contains(_fxPopupTrackId) Then
            _fxPopupTrackId = Guid.Empty
            _fxPopupHoverMode = -1
        End If

        If audioStates.Count > activeIds.Count Then
            Dim staleIds As New List(Of Guid)()
            For Each k In audioStates.Keys
                If Not activeIds.Contains(k) Then
                    staleIds.Add(k)
                End If
            Next
            For Each staleId In staleIds
                audioStates.Remove(staleId)
            Next
        End If
    End Sub

    Private Sub OnProjectFpsChanged(newFps As Double)
        If Me.InvokeRequired Then
            Try : Me.BeginInvoke(New Action(Sub() OnProjectFpsChanged(newFps))) : Catch : End Try
            Return
        End If
        Dim interval = CInt(Math.Floor(1000.0 / newFps))
        RenderTimer.Interval = Math.Max(1, interval)
    End Sub

    Protected Overrides Sub OnPaintBackground(pevent As PaintEventArgs)
    End Sub

    Protected Overrides Sub WndProc(ByRef m As Message)
        Const WM_ERASEBKGND As Integer = &H14
        If m.Msg = WM_ERASEBKGND Then
            m.Result = New IntPtr(1)
            Return
        End If
        MyBase.WndProc(m)
    End Sub

    Protected Overrides Sub OnHandleCreated(e As EventArgs)
        MyBase.OnHandleCreated(e)
        CreateResources()
        RenderTimer.Interval = TargetFrameIntervalMs
        RenderTimer.Start()

        Try
            RemoveHandler ThemeManager.ThemeChanged, AddressOf OnThemeChanged
            AddHandler ThemeManager.ThemeChanged, AddressOf OnThemeChanged
        Catch
        End Try
    End Sub

    Private Sub OnThemeChanged(sender As Object, e As EventArgs)
        If isDisposedState OrElse Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
        If Me.InvokeRequired Then
            Try : Me.BeginInvoke(New Action(Sub() OnThemeChanged(sender, e))) : Catch : End Try
            Return
        End If

        UpdateBrushes()
        SafeInvalidate()
    End Sub

    Protected Overrides Sub OnMouseCaptureChanged(e As EventArgs)
        MyBase.OnMouseCaptureChanged(e)
        If Not Me.Capture AndAlso draggedProperty <> "" Then
            draggedProperty = ""
            myModel?.NotifyPropertiesChanged()
            SafeInvalidate()
        End If
    End Sub

    Private Sub RenderTimer_Tick(sender As Object, e As EventArgs) Handles RenderTimer.Tick
        If Interlocked.Exchange(_isRenderingTimer, 1) <> 0 Then Return
        Try
            If isDisposedState OrElse Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return

            Dim isPlaying As Boolean = (_audioPlayer IsNot Nothing) OrElse (myPlayback IsNot Nothing AndAlso myPlayback.IsPlaying)
            If Not isPlaying AndAlso String.IsNullOrEmpty(draggedProperty) Then
                If MasterPeakL > 0.0F OrElse MasterPeakR > 0.0F Then
                    MasterPeakL = 0.0F
                    MasterPeakR = 0.0F
                    SafeInvalidate()
                End If
                Return
            End If

            SyncFromModel()
            SyncMasterFromPlayer()

            If _audioPlayer?.MasterEffects IsNot Nothing Then
                Dim rawMasterL As Single = Volatile.Read(_audioPlayer.MasterEffects.CurrentPeakL)
                Dim rawMasterR As Single = Volatile.Read(_audioPlayer.MasterEffects.CurrentPeakR)
                If Single.IsNaN(rawMasterL) OrElse Single.IsInfinity(rawMasterL) Then rawMasterL = 0.0F
                If Single.IsNaN(rawMasterR) OrElse Single.IsInfinity(rawMasterR) Then rawMasterR = 0.0F

                Dim nowTicks As Long = Environment.TickCount64
                Dim uiCfg = MixerHardwareStateStore.Current.Ui
                Dim decayFactor As Single = Math.Max(0.5F, Math.Min(0.98F, uiCfg.MeterDecayFactor))
                Dim holdMs As Integer = Math.Max(100, uiCfg.PeakHoldDurationMs)

                MasterPeakL = Math.Max(rawMasterL, MasterPeakL * decayFactor)
                MasterPeakR = Math.Max(rawMasterR, MasterPeakR * decayFactor)

                If MasterPeakL >= _masterHoldL Then
                    _masterHoldL = MasterPeakL
                    _masterHoldTicksL = nowTicks
                ElseIf (nowTicks - _masterHoldTicksL) > holdMs Then
                    _masterHoldL = Math.Max(MasterPeakL, _masterHoldL * 0.86F)
                End If

                If MasterPeakR >= _masterHoldR Then
                    _masterHoldR = MasterPeakR
                    _masterHoldTicksR = nowTicks
                ElseIf (nowTicks - _masterHoldTicksR) > holdMs Then
                    _masterHoldR = Math.Max(MasterPeakR, _masterHoldR * 0.86F)
                End If

                Dim anySolo As Boolean = False
                For Each kvp In audioStates
                    If kvp.Value.IsSolo Then
                        anySolo = True
                        Exit For
                    End If
                Next

                For Each kvp In audioStates
                    Dim effMuted As Boolean = kvp.Value.IsMuted OrElse (anySolo AndAlso Not kvp.Value.IsSolo)
                    If kvp.Value.Fader <= 0.001F OrElse effMuted Then
                        kvp.Value.Peak = 0.0F
                        kvp.Value.PeakHold = 0.0F
                        kvp.Value.PeakHoldTicks = nowTicks
                        Continue For
                    End If

                    Dim chPostFader As Single = If(_audioPlayer IsNot Nothing, _audioPlayer.GetChannelPostFaderLevel(kvp.Key), 0.0F)
                    If Single.IsNaN(chPostFader) OrElse Single.IsInfinity(chPostFader) Then chPostFader = 0.0F
                    Dim chPeak As Single = Math.Max(chPostFader, kvp.Value.Peak * decayFactor)
                    kvp.Value.Peak = Math.Max(0.0F, Math.Min(1.0F, chPeak))

                    If kvp.Value.Peak >= kvp.Value.PeakHold Then
                        kvp.Value.PeakHold = kvp.Value.Peak
                        kvp.Value.PeakHoldTicks = nowTicks
                    ElseIf (nowTicks - kvp.Value.PeakHoldTicks) > holdMs Then
                        kvp.Value.PeakHold = Math.Max(kvp.Value.Peak, kvp.Value.PeakHold * 0.86F)
                    End If
                Next
            End If

            SafeInvalidate()
        Catch ex As Exception
            DiagnosticsClient.RegisterSilentException(ex)
        Finally
            Interlocked.Exchange(_isRenderingTimer, 0)
        End Try
    End Sub

    Private Sub DiscardDeviceResources()
        DiscardBrushes()
        If d2dContext IsNot Nothing Then
            Try : d2dContext.Target = Nothing : Catch : End Try
        End If
        SafeRelease(d2dTargetBitmap)
        SafeRelease(d2dContext)
        renderTarget = Nothing
        SafeRelease(d2dDevice)
        SafeRelease(dxgiSwapChain)
        SafeRelease(d3dContext)
        SafeRelease(d3dDevice)
        _swapChainWidth = 0
        _swapChainHeight = 0
    End Sub

    Private Sub AttachSwapChainBackBufferToD2D()
        If d2dContext Is Nothing OrElse dxgiSwapChain Is Nothing Then Return
        d2dContext.Target = Nothing
        SafeRelease(d2dTargetBitmap)

        Using backBuffer As IDXGISurface = dxgiSwapChain.GetBuffer(Of IDXGISurface)(0UI)
            Dim bmpProps As New BitmapProperties1(
                New PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
                96.0F, 96.0F,
                BitmapOptions.Target Or BitmapOptions.CannotDraw)
            d2dTargetBitmap = d2dContext.CreateBitmapFromDxgiSurface(backBuffer, bmpProps)
            d2dContext.Target = d2dTargetBitmap
        End Using
    End Sub

    Private Function TryResizeSwapChainBuffersUnlocked(targetW As Integer, targetH As Integer) As Boolean
        If targetW <= 0 OrElse targetH <= 0 OrElse dxgiSwapChain Is Nothing OrElse d2dContext Is Nothing Then Return False
        Try
            d2dContext.Target = Nothing
            SafeRelease(d2dTargetBitmap)
            Dim hr = dxgiSwapChain.ResizeBuffers(0UI, CUInt(targetW), CUInt(targetH), Format.B8G8R8A8_UNorm, SwapChainFlags.None)
            If hr.Failure Then Return False
            AttachSwapChainBackBufferToD2D()
            _swapChainWidth = targetW
            _swapChainHeight = targetH
            Interlocked.Exchange(_pendingResize, 0)
            Return True
        Catch
            Return False
        End Try
    End Function

    Private Sub CreateResources()
        If factory2D Is Nothing OrElse Me.Width <= 0 OrElse Me.Height <= 0 OrElse Not Me.IsHandleCreated Then Return
        Dim hwnd As IntPtr = Me.Handle
        If hwnd = IntPtr.Zero OrElse Not IsWindow(hwnd) Then Return

        Try
            DiscardDeviceResources()

            Dim featureLevels As Vortice.Direct3D.FeatureLevel() = {
                Vortice.Direct3D.FeatureLevel.Level_11_1,
                Vortice.Direct3D.FeatureLevel.Level_11_0,
                Vortice.Direct3D.FeatureLevel.Level_10_1,
                Vortice.Direct3D.FeatureLevel.Level_10_0
            }

            Dim nullAdapter As IDXGIAdapter = Nothing
            Dim hr = D3D11.D3D11CreateDevice(
                nullAdapter,
                Vortice.Direct3D.DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                d3dDevice,
                d3dContext)

            If hr.Failure OrElse d3dDevice Is Nothing Then
                D3D11.D3D11CreateDevice(
                    nullAdapter,
                    Vortice.Direct3D.DriverType.Warp,
                    DeviceCreationFlags.BgraSupport,
                    featureLevels,
                    d3dDevice,
                    d3dContext)
            End If

            Dim initW As Integer = Math.Max(1, Me.Width)
            Dim initH As Integer = Math.Max(1, Me.Height)

            Using dxgiDevice As IDXGIDevice = d3dDevice.QueryInterface(Of IDXGIDevice)()
                Using dxgiFactory As IDXGIFactory2 = DXGI.CreateDXGIFactory1(Of IDXGIFactory2)()
                    Dim swapDesc As New SwapChainDescription1() With {
                        .Width = CUInt(initW),
                        .Height = CUInt(initH),
                        .Format = Format.B8G8R8A8_UNorm,
                        .Stereo = False,
                        .SampleDescription = SampleDescription.Default,
                        .BufferUsage = Usage.RenderTargetOutput,
                        .BufferCount = 2UI,
                        .Scaling = Scaling.Stretch,
                        .SwapEffect = SwapEffect.FlipSequential,
                        .AlphaMode = Vortice.DXGI.AlphaMode.Ignore
                    }

                    Try
                        dxgiSwapChain = dxgiFactory.CreateSwapChainForHwnd(d3dDevice, hwnd, swapDesc)
                    Catch
                        swapDesc.SwapEffect = SwapEffect.Discard
                        swapDesc.BufferCount = 1UI
                        dxgiSwapChain = dxgiFactory.CreateSwapChainForHwnd(d3dDevice, hwnd, swapDesc)
                    End Try

                    dxgiFactory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter)
                End Using

                d2dDevice = factory2D.CreateDevice(dxgiDevice)
                d2dContext = d2dDevice.CreateDeviceContext(DeviceContextOptions.EnableMultithreadedOptimizations)
                renderTarget = d2dContext
            End Using

            AttachSwapChainBackBufferToD2D()
            _swapChainWidth = initW
            _swapChainHeight = initH
            Interlocked.Exchange(_pendingResize, 0)
            renderTarget.AntialiasMode = AntialiasMode.PerPrimitive
            UpdateBrushes()
            isDeviceValid = True
        Catch ex As Exception
            isDeviceValid = False
            DiagnosticsClient.RegisterSilentException(ex)
        End Try
    End Sub

    Private Sub DiscardBrushes()
        SafeRelease(brushBgEq)
        SafeRelease(brushBgColor)
        SafeRelease(brushBgFader)
        SafeRelease(brushSeparator)

        SafeRelease(brushTextWhite)
        SafeRelease(brushTextGray)
        SafeRelease(brushTextDark)
        SafeRelease(brushBigNumber)

        SafeRelease(brushKnobWhiteRing)
        SafeRelease(brushKnobBlack)
        SafeRelease(brushKnobGray)
        SafeRelease(brushKnobSilver)
        SafeRelease(brushKnobIndicator)
        SafeRelease(brushKnobBlueIndicator)

        SafeRelease(brushFaderSlot)
        SafeRelease(brushFaderThumbBorder)
        SafeRelease(brushFaderThumbBg)

        SafeRelease(brushLedTrench)
        SafeRelease(brushLedOff)
        SafeRelease(brushLedGreen)
        SafeRelease(brushLedYellowGreen)
        SafeRelease(brushLedYellow)
        SafeRelease(brushLedOrange)
        SafeRelease(brushLedRed)

        SafeRelease(brushWaveBg)
        SafeRelease(brushWaveform)
        SafeRelease(brushWavePlayhead)
        SafeRelease(brushWaveGrid)
        SafeRelease(brushWaveClipBg)
        SafeRelease(brushWaveLowShadow)
        SafeRelease(brushWaveLow)
        SafeRelease(brushWaveHigh)
        SafeRelease(brushWaveCore)
        SafeRelease(brushWaveEcho)
        SafeRelease(brushWavePlayedDim)
        SafeRelease(brushOsdBg)
        SafeRelease(brushOsdBorder)
    End Sub

    Private Sub UpdateBrushes()
        If renderTarget Is Nothing OrElse renderTarget.NativePointer = IntPtr.Zero Then Return
        Try
            DiscardBrushes()

            brushBgEq = renderTarget.CreateSolidColorBrush(New Color4(20 / 255.0F, 20 / 255.0F, 22 / 255.0F, 1.0F))
            brushBgColor = renderTarget.CreateSolidColorBrush(New Color4(28 / 255.0F, 28 / 255.0F, 30 / 255.0F, 1.0F))
            brushBgFader = renderTarget.CreateSolidColorBrush(New Color4(14 / 255.0F, 14 / 255.0F, 16 / 255.0F, 1.0F))
            brushSeparator = renderTarget.CreateSolidColorBrush(New Color4(8 / 255.0F, 8 / 255.0F, 10 / 255.0F, 1.0F))

            brushTextWhite = renderTarget.CreateSolidColorBrush(New Color4(240 / 255.0F, 240 / 255.0F, 240 / 255.0F, 1.0F))
            brushTextGray = renderTarget.CreateSolidColorBrush(New Color4(130 / 255.0F, 130 / 255.0F, 135 / 255.0F, 1.0F))
            brushTextDark = renderTarget.CreateSolidColorBrush(New Color4(60 / 255.0F, 60 / 255.0F, 65 / 255.0F, 1.0F))
            brushBigNumber = renderTarget.CreateSolidColorBrush(New Color4(255 / 255.0F, 255 / 255.0F, 255 / 255.0F, 0.15F))

            brushKnobWhiteRing = renderTarget.CreateSolidColorBrush(New Color4(220 / 255.0F, 220 / 255.0F, 225 / 255.0F, 1.0F))
            brushKnobBlack = renderTarget.CreateSolidColorBrush(New Color4(24 / 255.0F, 24 / 255.0F, 26 / 255.0F, 1.0F))
            brushKnobGray = renderTarget.CreateSolidColorBrush(New Color4(140 / 255.0F, 140 / 255.0F, 145 / 255.0F, 1.0F))
            brushKnobSilver = renderTarget.CreateSolidColorBrush(New Color4(190 / 255.0F, 190 / 255.0F, 195 / 255.0F, 1.0F))
            brushKnobIndicator = renderTarget.CreateSolidColorBrush(New Color4(255 / 255.0F, 255 / 255.0F, 255 / 255.0F, 1.0F))
            brushKnobBlueIndicator = renderTarget.CreateSolidColorBrush(New Color4(0 / 255.0F, 180 / 255.0F, 255 / 255.0F, 1.0F))

            brushFaderSlot = renderTarget.CreateSolidColorBrush(New Color4(5 / 255.0F, 5 / 255.0F, 5 / 255.0F, 1.0F))
            brushFaderThumbBorder = renderTarget.CreateSolidColorBrush(New Color4(50 / 255.0F, 50 / 255.0F, 55 / 255.0F, 1.0F))
            brushFaderThumbBg = renderTarget.CreateSolidColorBrush(New Color4(20 / 255.0F, 20 / 255.0F, 20 / 255.0F, 1.0F))

            brushLedTrench = renderTarget.CreateSolidColorBrush(New Color4(6 / 255.0F, 6 / 255.0F, 8 / 255.0F, 1.0F))
            brushLedOff = renderTarget.CreateSolidColorBrush(New Color4(30 / 255.0F, 35 / 255.0F, 25 / 255.0F, 1.0F))
            brushLedGreen = renderTarget.CreateSolidColorBrush(New Color4(0.4F, 1.0F, 0.2F, 1.0F))
            brushLedYellowGreen = renderTarget.CreateSolidColorBrush(New Color4(0.7F, 1.0F, 0.1F, 1.0F))
            brushLedYellow = renderTarget.CreateSolidColorBrush(New Color4(1.0F, 0.85F, 0.0F, 1.0F))
            brushLedOrange = renderTarget.CreateSolidColorBrush(New Color4(1.0F, 0.45F, 0.0F, 1.0F))
            brushLedRed = renderTarget.CreateSolidColorBrush(New Color4(1.0F, 0.15F, 0.15F, 1.0F))

            brushWaveBg = renderTarget.CreateSolidColorBrush(New Color4(2 / 255.0F, 2 / 255.0F, 4 / 255.0F, 1.0F))
            brushWaveClipBg = renderTarget.CreateSolidColorBrush(New Color4(4 / 255.0F, 5 / 255.0F, 8 / 255.0F, 0.55F))
            brushWaveform = renderTarget.CreateSolidColorBrush(New Color4(0 / 255.0F, 175 / 255.0F, 255 / 255.0F, 0.95F))
            brushWavePlayhead = renderTarget.CreateSolidColorBrush(New Color4(255 / 255.0F, 45 / 255.0F, 45 / 255.0F, 1.0F))
            brushWaveGrid = renderTarget.CreateSolidColorBrush(New Color4(255 / 255.0F, 255 / 255.0F, 255 / 255.0F, 0.08F))

            brushWaveLowShadow = renderTarget.CreateSolidColorBrush(New Color4(0.42F, 0.04F, 0.02F, 0.76F))
            brushWaveLow = renderTarget.CreateSolidColorBrush(New Color4(0.98F, 0.14F, 0.08F, 0.96F))
            brushWaveHigh = renderTarget.CreateSolidColorBrush(New Color4(0.0F, 0.72F, 1.0F, 0.98F))
            brushWaveCore = renderTarget.CreateSolidColorBrush(New Color4(0.88F, 0.98F, 1.0F, 0.95F))
            brushWaveEcho = renderTarget.CreateSolidColorBrush(New Color4(0.0F, 0.64F, 1.0F, 0.32F))
            brushWavePlayedDim = renderTarget.CreateSolidColorBrush(New Color4(0.02F, 0.03F, 0.05F, 0.42F))

            brushOsdBg = renderTarget.CreateSolidColorBrush(New Color4(0.03F, 0.04F, 0.06F, 0.96F))
            brushOsdBorder = renderTarget.CreateSolidColorBrush(New Color4(0.0F, 0.72F, 1.0F, 0.9F))
        Catch ex As Exception
        End Try
    End Sub

    Protected Overrides Sub OnResize(e As EventArgs)
        MyBase.OnResize(e)
        If Me.Width <= 0 OrElse Me.Height <= 0 Then Return

        If isDeviceValid AndAlso dxgiSwapChain IsNot Nothing AndAlso d2dContext IsNot Nothing Then
            If Interlocked.Exchange(_isRenderingFrame, 1) = 0 Then
                Dim resizedOk As Boolean = TryResizeSwapChainBuffersUnlocked(Me.Width, Me.Height)
                Interlocked.Exchange(_isRenderingFrame, 0)

                If resizedOk Then
                    Render()
                Else
                    CreateResources()
                End If
            Else
                Interlocked.Exchange(_pendingResize, 1)
                SafeInvalidate()
            End If
        Else
            CreateResources()
        End If
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        If Not isDeviceValid OrElse renderTarget Is Nothing OrElse renderTarget.NativePointer = IntPtr.Zero Then
            e.Graphics.Clear(ThemeManager.BackColor)
            Return
        End If
        Render()
    End Sub

    Public Sub SafeInvalidate()
        If isDisposedState OrElse Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
        If Me.InvokeRequired Then
            If Interlocked.CompareExchange(_invalidatePending, 1, 0) = 0 Then
                Try
                    Me.BeginInvoke(_invalidateAction)
                Catch
                    Interlocked.Exchange(_invalidatePending, 0)
                End Try
            End If
        Else
            Interlocked.Exchange(_invalidatePending, 0)
            Me.Invalidate()
        End If
    End Sub

    Public Sub AdjustWaveformScale(delta As Single, Optional targetTrack As TrackModel = Nothing)
        If targetTrack IsNot Nothing Then
            Dim curZoom As Single = If(targetTrack.WaveformZoom <= 0.05F, 1.0F, targetTrack.WaveformZoom)
            Dim newZoom As Single = Math.Max(WAVE_AMP_MIN, Math.Min(WAVE_AMP_MAX, curZoom + delta))
            targetTrack.WaveformZoom = newZoom

            Dim st As AudioChannelState = Nothing
            If audioStates.TryGetValue(targetTrack.Id, st) AndAlso st IsNot Nothing Then
                st.WaveformZoom = newZoom
            End If

            MixerHardwareStateStore.Save(myModel, _audioPlayer)
            ShowOsd(targetTrack.Id, $"WAVE ZOOM: {CInt(Math.Round(newZoom * 100.0F))}%")
        Else
            _waveformAmpScale = Math.Max(WAVE_AMP_MIN, Math.Min(WAVE_AMP_MAX, _waveformAmpScale + delta))
            MixerHardwareStateStore.Current.Ui.WaveformAmpScale = _waveformAmpScale
            MixerHardwareStateStore.RequestSaveDeferred()
            ShowOsd(Guid.Empty, $"WAVE ZOOM: {CInt(Math.Round(_waveformAmpScale * 100.0F))}%")
        End If
    End Sub

    Private Sub Render()
        If Interlocked.Exchange(_isRenderingFrame, 1) <> 0 Then Return
        Try
            If Not isDeviceValid OrElse renderTarget Is Nothing OrElse renderTarget.NativePointer = IntPtr.Zero OrElse dxgiSwapChain Is Nothing Then Return
            If Me.IsDisposed OrElse Not Me.IsHandleCreated OrElse Me.Width <= 0 OrElse Me.Height <= 0 Then Return
            Dim hwnd As IntPtr = Me.Handle
            If hwnd = IntPtr.Zero OrElse Not IsWindow(hwnd) Then Return

            ' Если во время предыдущего кадра пришёл OnResize, обновляем размер буфера SwapChain перед началом отрисовки
            If Interlocked.Exchange(_pendingResize, 0) <> 0 OrElse _swapChainWidth <> Me.Width OrElse _swapChainHeight <> Me.Height Then
                If Not TryResizeSwapChainBuffersUnlocked(Me.Width, Me.Height) Then
                    Interlocked.Exchange(_isRenderingFrame, 0)
                    CreateResources()
                    Return
                End If
            End If

            currentScale = CSng(Me.Height) / VIRTUAL_HEIGHT
            If Single.IsNaN(currentScale) OrElse Single.IsInfinity(currentScale) OrElse currentScale <= 0.001F Then currentScale = 1.0F

            Dim audioTracks As List(Of TrackModel) = GetAudioTracksSnapshot()
            Dim audioTracksCount As Integer = audioTracks.Count

            Dim virtualWidth As Single = (Math.Max(1, audioTracksCount) * TRACK_WIDTH) + MASTER_WIDTH
            Dim scaledMixerWidth As Single = virtualWidth * currentScale

            If scaledMixerWidth < Me.Width Then
                currentOffsetX = (Me.Width - scaledMixerWidth) / 2.0F
            Else
                currentOffsetX = 0.0F
            End If

            If Single.IsNaN(currentOffsetX) OrElse Single.IsInfinity(currentOffsetX) Then currentOffsetX = 0.0F

            Dim viewportWidth As Single = Me.Width / currentScale
            Dim maxScrollX As Single = Math.Max(0.0F, virtualWidth - viewportWidth)
            If mixerScrollX > maxScrollX Then mixerScrollX = maxScrollX
            If mixerScrollX < 0.0F Then mixerScrollX = 0.0F

            Dim needRecreate As Boolean = False
            renderTarget.BeginDraw()
            Try
                renderTarget.Clear(If(ThemeManager.IsDarkTheme, New Color4(0.04F, 0.04F, 0.05F, 1.0F), New Color4(0.87F, 0.87F, 0.87F, 1.0F)))

                renderTarget.Transform = New Matrix3x2(currentScale, 0, 0, currentScale, currentOffsetX, 0)
                RenderAudioMixer(audioTracks)
                renderTarget.Transform = Matrix3x2.Identity
            Finally
                Dim endRes = renderTarget.EndDraw()
                Dim presRes = dxgiSwapChain.Present(0UI, PresentFlags.None)
                If endRes.Failure OrElse presRes.Failure Then
                    needRecreate = True
                End If
            End Try

            If needRecreate Then
                Interlocked.Exchange(_isRenderingFrame, 0)
                CreateResources()
                Return
            End If
        Catch ex As Exception
            If IsDeviceLostException(ex) Then
                Interlocked.Exchange(_isRenderingFrame, 0)
                CreateResources()
                Return
            Else
                DiagnosticsClient.RegisterSilentException(ex)
            End If
        Finally
            Interlocked.Exchange(_isRenderingFrame, 0)
        End Try
    End Sub

    Private Sub DrawDJMKnob(cx As Single, cy As Single, radius As Single, value As Single, type As Integer)
        If Single.IsNaN(value) OrElse Single.IsInfinity(value) Then value = 0.5F
        value = Math.Max(0.0F, Math.Min(1.0F, value))

        If type = 0 OrElse type = 1 Then
            Dim dotDist As Single = radius + If(type = 1, 5.0F, 3.5F)
            For i As Integer = 0 To 10
                Dim a As Single = -135.0F + (i * 27.0F)
                Dim radA As Single = CSng(a * Math.PI / 180.0)
                Dim px As Single = cx + CSng(Math.Sin(radA)) * dotDist
                Dim py As Single = cy - CSng(Math.Cos(radA)) * dotDist
                renderTarget.FillEllipse(New Ellipse(New Vector2(px, py), 0.8F, 0.8F), brushTextWhite)
            Next
        End If

        If type = 1 Then
            renderTarget.DrawEllipse(New Ellipse(New Vector2(cx, cy), radius + 1.5F, radius + 1.5F), brushKnobWhiteRing, 2.0F)
        End If

        Dim bodyBrush As ID2D1SolidColorBrush = brushKnobBlack
        If type = 0 Then bodyBrush = brushKnobGray
        If type = 2 Then bodyBrush = brushKnobSilver

        renderTarget.FillEllipse(New Ellipse(New Vector2(cx, cy), radius, radius), bodyBrush)

        Dim angle As Single = -135.0F + (value * 270.0F)
        Dim radInd As Single = CSng(angle * Math.PI / 180.0)
        Dim iLen As Single = radius
        Dim pxI As Single = cx + CSng(Math.Sin(radInd)) * iLen
        Dim pyI As Single = cy - CSng(Math.Cos(radInd)) * iLen

        Dim indBrush = If(type = 1 AndAlso Math.Abs(value - 0.5F) > 0.01F, brushKnobBlueIndicator, brushKnobIndicator)
        renderTarget.DrawLine(New Vector2(cx, cy), New Vector2(pxI, pyI), indBrush, 2.0F)
    End Sub

    Private Sub DrawDJMMeter(x As Single, y As Single, w As Single, h As Single, peakValue As Single, Optional holdValue As Single = 0.0F)
        If Single.IsNaN(peakValue) OrElse Single.IsInfinity(peakValue) Then peakValue = 0.0F
        If Single.IsNaN(holdValue) OrElse Single.IsInfinity(holdValue) Then holdValue = 0.0F
        If h <= 0.0F Then h = 1.0F

        Dim trenchRect As New RawRectF(x - 1.2F, y - 1.2F, x + w + 1.2F, y + h + 1.2F)
        renderTarget.FillRectangle(trenchRect, brushLedTrench)
        renderTarget.DrawRectangle(trenchRect, brushSeparator, 1.0F)

        Dim ledCount As Integer = 15
        Dim spacing As Single = 1.6F
        Dim ledH As Single = (h - (ledCount - 1) * spacing) / ledCount
        Dim minimumMeterLevel As Single = 0.15F / ledCount
        Dim holdIdx As Integer = If(holdValue >= minimumMeterLevel,
                                    Math.Max(0, Math.Min(ledCount - 1, CInt(Math.Floor(holdValue * ledCount - 0.15F)))),
                                    -1)

        For i As Integer = 0 To ledCount - 1
            Dim ledY As Single = y + h - (i + 1) * ledH - i * spacing
            Dim ledCx As Single = x + w / 2.0F
            Dim ledCy As Single = ledY + ledH / 2.0F
            Dim ledEllipse As New Ellipse(New Vector2(ledCx, ledCy), w / 2.0F, ledH / 2.0F)

            Dim threshold As Single = (i + 0.15F) / CSng(ledCount)
            Dim isOn As Boolean = (peakValue >= threshold) OrElse (i = holdIdx)

            Dim clrBrush As ID2D1SolidColorBrush = brushLedGreen
            If i >= 8 AndAlso i <= 9 Then clrBrush = brushLedYellowGreen
            If i >= 10 AndAlso i <= 11 Then clrBrush = brushLedYellow
            If i = 12 Then clrBrush = brushLedOrange
            If i >= 13 Then clrBrush = brushLedRed

            renderTarget.FillEllipse(ledEllipse, If(isOn, clrBrush, brushLedOff))
        Next
    End Sub

    Private Shared Function ExtractSeratoThreeBandBarsFromWpk(peaks() As NativePeakMinMax, targetBarCount As Integer) As ThreeBandPeakBar()
        Return PersistentWaveformCache.ExtractThreeBandBars(peaks, targetBarCount)
    End Function

    Private Shared Sub ComputeRealtimeBandFactors(track As TrackModel,
                                                  isEffectivelyMuted As Boolean,
                                                  ByRef outLowGain As Single,
                                                  ByRef outMidGain As Single,
                                                  ByRef outHighGain As Single,
                                                  ByRef outEchoMix As Single,
                                                  ByRef outCrushSteps As Single,
                                                  ByRef outDriveAmount As Single)
        If track Is Nothing Then
            outLowGain = 1.0F
            outMidGain = 1.0F
            outHighGain = 1.0F
            outEchoMix = 0.0F
            outCrushSteps = 0.0F
            outDriveAmount = 0.0F
            Return
        End If

        Dim faderVal As Single = If(track.Clips IsNot Nothing AndAlso track.Clips.Count > 0, track.Clips(0).Volume, 1.0F)
        faderVal = Math.Max(0.0F, Math.Min(1.0F, faderVal))
        Dim faderFactor As Single = If(isEffectivelyMuted, 0.16F, faderVal)

        ' Если для MAIN MIC активен Capture APO в ядре, сигнал из микрофона уже ослаблен фейдером в audiodg.exe
        Dim isMicTrack As Boolean = (track.Name = "MAIN MIC" OrElse track.Name = "MIC" OrElse track.Name = "MIC 1-2")
        If isMicTrack AndAlso Not isEffectivelyMuted AndAlso NativeCppAutoBuilderAndInstaller.IsKernelMicApoProcessingAudio() Then
            faderFactor = Math.Max(0.55F, CSng(Math.Sqrt(faderVal)))
        End If

        Dim trimLin As Single = BasicEqualizerEffect.CalculatePioneerTrimLinear(track.Trim * 2.0F)
        Dim lowIso As Single = BasicEqualizerEffect.DbToIsolatorLinear(yoump.MediaParameterBroker.ValueToDecibels(track.EqLow))
        Dim midIso As Single = BasicEqualizerEffect.DbToIsolatorLinear(yoump.MediaParameterBroker.ValueToDecibels(track.EqMid))
        Dim hiIso As Single = BasicEqualizerEffect.DbToIsolatorLinear(yoump.MediaParameterBroker.ValueToDecibels(track.EqHi))

        Dim c As Single = Math.Max(0.0F, Math.Min(1.0F, track.ColorFx))
        Dim colMode As Integer = track.ColorFxMode And 3
        Dim distFromCenter As Single = Math.Abs(c - 0.5F)
        Dim normDist As Single = Math.Min(1.0F, distFromCenter * 2.0F)

        outEchoMix = 0.0F
        outCrushSteps = 0.0F
        outDriveAmount = 0.0F

        If distFromCenter > 0.012F Then
            Select Case colMode
                Case 0 ' FILTER (Cytomic SVF LPF / HPF с моделированием резонансного пика Q=1.45)
                    If c < 0.5F Then
                        Dim t As Single = Math.Max(0.0F, Math.Min(1.0F, c / 0.48F))
                        Dim edgeAtten As Single = If(c < 0.025F, c / 0.025F, 1.0F)
                        Dim midResPeak As Single = 1.0F + 0.34F * CSng(Math.Exp(-Math.Pow((t - 0.58F) / 0.18F, 2.0)))
                        Dim lowResPeak As Single = 1.0F + 0.28F * CSng(Math.Exp(-Math.Pow((t - 0.22F) / 0.14F, 2.0)))

                        hiIso *= Math.Max(0.0F, Math.Min(1.3F, CSng(Math.Pow(t, 2.7)) * 1.18F)) * edgeAtten
                        midIso *= Math.Max(0.0F, Math.Min(1.35F, CSng(Math.Pow(t, 1.25)) * midResPeak)) * edgeAtten
                        lowIso *= Math.Max(0.0F, (0.25F + 0.75F * t) * lowResPeak) * edgeAtten
                    Else
                        Dim t As Single = Math.Max(0.0F, Math.Min(1.0F, (c - 0.52F) / 0.48F))
                        Dim invT As Single = 1.0F - t
                        Dim edgeAtten As Single = If(c > 0.975F, (1.0F - c) / 0.025F, 1.0F)
                        Dim midResPeak As Single = 1.0F + 0.34F * CSng(Math.Exp(-Math.Pow((invT - 0.58F) / 0.18F, 2.0)))
                        Dim hiResPeak As Single = 1.0F + 0.28F * CSng(Math.Exp(-Math.Pow((invT - 0.22F) / 0.14F, 2.0)))

                        lowIso *= Math.Max(0.0F, Math.Min(1.3F, CSng(Math.Pow(invT, 2.7)) * 1.18F)) * edgeAtten
                        midIso *= Math.Max(0.0F, Math.Min(1.35F, CSng(Math.Pow(invT, 1.25)) * midResPeak)) * edgeAtten
                        hiIso *= Math.Max(0.0F, (0.25F + 0.75F * invT) * hiResPeak) * edgeAtten
                    End If

                Case 1 ' SPACE (Dub Echo)
                    outEchoMix = 0.68F * normDist
                    If c < 0.5F Then
                        midIso *= (1.0F + 0.25F * normDist)
                    Else
                        hiIso *= (1.0F + 0.35F * normDist)
                    End If

                Case 2 ' CRUSH (Drive / Bitcrusher)
                    If c < 0.5F Then
                        outDriveAmount = normDist * 2.4F
                        lowIso *= (1.0F + 0.26F * normDist)
                        midIso *= (1.0F + 0.3F * normDist)
                    Else
                        outCrushSteps = Math.Max(3.0F, 14.0F * (1.0F - normDist * 0.82F))
                        hiIso *= (1.0F + 0.4F * normDist)
                    End If

                Case 3 ' LOUDNESS (Vocal Focus / Sub+Air Boost)
                    If c < 0.5F Then
                        lowIso *= (1.0F - 0.65F * normDist)
                        midIso *= (1.0F + 0.65F * normDist)
                        hiIso *= (1.0F - 0.35F * normDist)
                    Else
                        lowIso *= (1.0F + 0.55F * normDist)
                        hiIso *= (1.0F + 0.45F * normDist)
                    End If
            End Select
        End If

        Dim baseScale As Single = trimLin * faderFactor
        outLowGain = lowIso * baseScale
        outMidGain = midIso * baseScale
        outHighGain = hiIso * baseScale
    End Sub

    Private Shared Function ApplyFxShape(val As Single, driveAmount As Single, crushSteps As Single) As Single
        If val <= 0.0005F Then Return 0.0F
        If driveAmount > 0.01F Then
            Dim drv As Single = 1.0F + driveAmount
            Dim norm As Single = CSng(Math.Tanh(drv))
            If norm > 0.001F Then
                Return CSng(Math.Tanh(val * drv)) / norm
            End If
        ElseIf crushSteps >= 2.0F Then
            Return CSng(Math.Round(val * crushSteps)) / crushSteps
        End If
        Return val
    End Function

    Private Sub RenderSeratoRekordboxColumn(bx As Single,
                                            midY As Single,
                                            maxHalfH As Single,
                                            ByRef bar As ThreeBandPeakBar,
                                            lowGain As Single,
                                            midGain As Single,
                                            highGain As Single,
                                            echoTop As Single,
                                            echoBot As Single,
                                            crushSteps As Single,
                                            driveAmount As Single,
                                            barStep As Single)
        Dim maxH As Single = Math.Max(1.0F, maxHalfH - 1.0F)

        Dim lTop As Single = ApplyFxShape(bar.LowTop * lowGain * _waveformAmpScale, driveAmount, crushSteps)
        Dim lBot As Single = ApplyFxShape(bar.LowBot * lowGain * _waveformAmpScale, driveAmount, crushSteps)
        Dim mTop As Single = ApplyFxShape(bar.MidTop * midGain * _waveformAmpScale, driveAmount, crushSteps)
        Dim mBot As Single = ApplyFxShape(bar.MidBot * midGain * _waveformAmpScale, driveAmount, crushSteps)
        Dim hTop As Single = ApplyFxShape(bar.HighTop * highGain * _waveformAmpScale, driveAmount, crushSteps)
        Dim hBot As Single = ApplyFxShape(bar.HighBot * highGain * _waveformAmpScale, driveAmount, crushSteps)

        Dim leftX As Single = CSng(Math.Floor(bx))
        Dim barWidthPx As Single = Math.Max(1.0F, CSng(Math.Floor(barStep)) - 1.0F)
        If barStep < 2.0F Then barWidthPx = 1.0F
        Dim rightX As Single = leftX + barWidthPx

        If echoTop > 0.015F OrElse echoBot > 0.015F Then
            Dim eT As Single = CSng(Math.Round(Math.Min(maxH, echoTop * maxHalfH * 0.85F * _waveformAmpScale)))
            Dim eB As Single = CSng(Math.Round(Math.Min(maxH, echoBot * maxHalfH * 0.85F * _waveformAmpScale)))
            If eT >= 1.0F OrElse eB >= 1.0F Then
                renderTarget.FillRectangle(New RawRectF(leftX, midY - eT, rightX, midY + eB), brushWaveEcho)
            End If
        End If

        Dim peakTopNorm As Single = Math.Max(lTop, Math.Max(mTop, hTop))
        Dim peakBotNorm As Single = Math.Max(lBot, Math.Max(mBot, hBot))
        If peakTopNorm <= 0.012F AndAlso peakBotNorm <= 0.012F Then Return

        Dim exactPeakT As Single = Math.Min(maxH, peakTopNorm * maxHalfH * 0.95F)
        Dim exactPeakB As Single = Math.Min(maxH, peakBotNorm * maxHalfH * 0.95F)
        Dim pxPeakT As Single = CSng(Math.Floor(exactPeakT))
        Dim pxPeakB As Single = CSng(Math.Floor(exactPeakB))
        If pxPeakT < 1.0F AndAlso pxPeakB < 1.0F Then Return

        Dim bandL As Single = (lTop + lBot) * 0.5F
        Dim bandM As Single = (mTop + mBot) * 0.5F
        Dim bandH As Single = (hTop + hBot) * 0.5F

        Dim minCommon As Single = Math.Min(bandL, Math.Min(bandM, bandH)) * 0.65F
        Dim dL As Single = Math.Max(0.0001F, bandL - minCommon)
        Dim dM As Single = Math.Max(0.0001F, bandM - minCommon)
        Dim dH As Single = Math.Max(0.0001F, bandH - minCommon)
        Dim maxDom As Single = Math.Max(0.0001F, Math.Max(dL, Math.Max(dM, dH)))

        Dim wL As Single = CSng(Math.Pow(dL / maxDom, 1.55F))
        Dim wM As Single = CSng(Math.Pow(dM / maxDom, 1.55F))
        Dim wH As Single = CSng(Math.Pow(dH / maxDom, 1.55F))

        Dim orangeMix As Single = Math.Max(0.0F, Math.Min(wL, wM) - wH * 0.4F)
        Dim cyanMix As Single = Math.Max(0.0F, Math.Min(wM, wH) - wL * 0.4F)
        Dim purpleMix As Single = Math.Max(0.0F, Math.Min(wL, wH) - wM * 0.6F)

        Dim wSum As Single = Math.Max(0.0001F, wL + wM + wH)
        wL /= wSum
        wM /= wSum
        wH /= wSum

        Dim rOuter As Single = (wL * 1.0F) + (wM * 0.12F) + (wH * 0.08F) + (orangeMix * 0.55F) + (purpleMix * 0.35F)
        Dim gOuter As Single = (wL * 0.1F) + (wM * 0.94F) + (wH * 0.45F) + (orangeMix * 0.38F) + (cyanMix * 0.35F) - (purpleMix * 0.15F)
        Dim bOuter As Single = (wL * 0.06F) + (wM * 0.16F) + (wH * 1.0F) - (orangeMix * 0.25F) + (cyanMix * 0.35F) + (purpleMix * 0.45F)

        rOuter = Math.Max(0.04F, rOuter)
        gOuter = Math.Max(0.04F, gOuter)
        bOuter = Math.Max(0.04F, bOuter)
        Dim maxOutRgb As Single = Math.Max(0.01F, Math.Max(rOuter, Math.Max(gOuter, bOuter)))
        rOuter /= maxOutRgb
        gOuter /= maxOutRgb
        bOuter /= maxOutRgb

        Dim fracT As Single = (exactPeakT - pxPeakT) * 0.65F
        If fracT > 0.12F AndAlso pxPeakT + 1.0F <= maxH Then
            brushWaveLow.Color = New Color4(rOuter, gOuter, bOuter, fracT)
            renderTarget.FillRectangle(New RawRectF(leftX, midY - pxPeakT - 1.0F, rightX, midY - pxPeakT), brushWaveLow)
        End If
        Dim fracB As Single = (exactPeakB - pxPeakB) * 0.65F
        If fracB > 0.12F AndAlso pxPeakB + 1.0F <= maxH Then
            brushWaveLow.Color = New Color4(rOuter, gOuter, bOuter, fracB)
            renderTarget.FillRectangle(New RawRectF(leftX, midY + pxPeakB, rightX, midY + pxPeakB + 1.0F), brushWaveLow)
        End If

        brushWaveLow.Color = New Color4(rOuter, gOuter, bOuter, 0.96F)
        renderTarget.FillRectangle(New RawRectF(leftX, midY - pxPeakT, rightX, midY + pxPeakB), brushWaveLow)

        If barStep >= 2.0F Then
            Dim bridgeT As Single = CSng(Math.Round(pxPeakT * 0.86F))
            Dim bridgeB As Single = CSng(Math.Round(pxPeakB * 0.86F))
            If bridgeT >= 1.0F OrElse bridgeB >= 1.0F Then
                brushWaveLow.Color = New Color4(rOuter * 0.85F, gOuter * 0.85F, bOuter * 0.85F, 0.36F)
                renderTarget.FillRectangle(New RawRectF(rightX, midY - bridgeT, rightX + 1.0F, midY + bridgeB), brushWaveLow)
            End If
        End If

        Dim midLayerT As Single = CSng(Math.Round(pxPeakT * 0.55F))
        Dim midLayerB As Single = CSng(Math.Round(pxPeakB * 0.55F))
        If midLayerT >= 1.0F OrElse midLayerB >= 1.0F Then
            Dim rMid As Single = Math.Min(1.0F, rOuter * 0.86F + 0.18F)
            Dim gMid As Single = Math.Min(1.0F, gOuter * 0.86F + 0.18F)
            Dim bMid As Single = Math.Min(1.0F, bOuter * 0.86F + 0.16F)
            brushWaveLowShadow.Color = New Color4(rMid, gMid, bMid, 0.94F)
            renderTarget.FillRectangle(New RawRectF(leftX, midY - midLayerT, rightX, midY + midLayerB), brushWaveLowShadow)
        End If

        Dim coreTop As Single = CSng(Math.Round(pxPeakT * 0.22F))
        Dim coreBot As Single = CSng(Math.Round(pxPeakB * 0.22F))
        If coreTop >= 1.0F OrElse coreBot >= 1.0F Then
            Dim coreR As Single = Math.Min(1.0F, rOuter * 0.6F + 0.42F)
            Dim coreG As Single = Math.Min(1.0F, gOuter * 0.6F + 0.4F)
            Dim coreB As Single = Math.Min(1.0F, bOuter * 0.6F + 0.38F)
            brushWaveCore.Color = New Color4(coreR, coreG, coreB, 0.96F)
            renderTarget.FillRectangle(New RawRectF(leftX, midY - coreTop, rightX, midY + coreBot), brushWaveCore)
        End If
    End Sub

    Private Sub DrawPlayheadWithCaps(playheadX As Single, topY As Single, botY As Single)
        renderTarget.DrawLine(New Vector2(playheadX, topY + 1.0F), New Vector2(playheadX, botY - 1.0F), brushWavePlayhead, 1.5F)
        renderTarget.FillRectangle(New RawRectF(playheadX - 2.2F, topY + 0.5F, playheadX + 2.2F, topY + 3.0F), brushWavePlayhead)
        renderTarget.FillRectangle(New RawRectF(playheadX - 2.2F, botY - 3.0F, playheadX + 2.2F, botY - 0.5F), brushWavePlayhead)
    End Sub

    Private Sub DrawChannelWaveformScreen(x As Single, y As Single, w As Single, h As Single, track As TrackModel, currentVirtTime As TimeSpan)
        Dim waveBox As New RawRectF(x + 4.0F, y + 4.0F, x + w - 4.0F, y + h - 4.0F)
        renderTarget.FillRectangle(waveBox, brushWaveBg)
        renderTarget.DrawRectangle(waveBox, brushSeparator, 1.0F)

        Dim waveWidth As Single = waveBox.Right - waveBox.Left
        Dim usableTop As Single = waveBox.Top + 15.0F
        Dim usableBot As Single = waveBox.Bottom - 2.0F
        Dim waveHeight As Single = (usableBot - usableTop) / 2.0F
        Dim midY As Single = usableTop + waveHeight

        Dim halfGridOffset As Single = waveHeight * 0.5F
        renderTarget.DrawLine(New Vector2(waveBox.Left, midY - halfGridOffset), New Vector2(waveBox.Right, midY - halfGridOffset), brushWaveGrid, 0.6F)
        renderTarget.DrawLine(New Vector2(waveBox.Left, midY + halfGridOffset), New Vector2(waveBox.Right, midY + halfGridOffset), brushWaveGrid, 0.6F)
        For gIdx As Integer = 1 To 3
            Dim gx As Single = waveBox.Left + (waveWidth * gIdx * 0.25F)
            renderTarget.DrawLine(New Vector2(gx, usableTop), New Vector2(gx, usableBot), brushWaveGrid, 0.6F)
        Next

        Dim totalProjDuration As TimeSpan = If(myModel IsNot Nothing, myModel.TotalDuration, TimeSpan.Zero)
        Dim totalProjSec As Double = totalProjDuration.TotalSeconds
        If Double.IsNaN(totalProjSec) OrElse Double.IsInfinity(totalProjSec) OrElse totalProjSec <= 0.05 Then Return

        Dim anySolo As Boolean = False
        For Each kvp In audioStates
            If kvp.Value.IsSolo Then
                anySolo = True
                Exit For
            End If
        Next
        Dim isEffectivelyMuted As Boolean = (track IsNot Nothing AndAlso (track.IsMuted OrElse (anySolo AndAlso Not track.IsSolo)))

        Dim lowGain As Single = 1.0F
        Dim midGain As Single = 1.0F
        Dim highGain As Single = 1.0F
        Dim echoMix As Single = 0.0F
        Dim crushSteps As Single = 0.0F
        Dim driveAmount As Single = 0.0F
        ComputeRealtimeBandFactors(track, isEffectivelyMuted, lowGain, midGain, highGain, echoMix, crushSteps, driveAmount)

        Dim trackZoom As Single = 1.0F
        If track IsNot Nothing Then
            Dim stZoom As AudioChannelState = Nothing
            If audioStates.TryGetValue(track.Id, stZoom) AndAlso stZoom IsNot Nothing AndAlso stZoom.WaveformZoom > 0.05F Then
                trackZoom = stZoom.WaveformZoom
            ElseIf track.WaveformZoom > 0.05F Then
                trackZoom = track.WaveformZoom
            End If
        End If
        lowGain *= trackZoom
        midGain *= trackZoom
        highGain *= trackZoom

        Dim renderedAnyFileClip As Boolean = False

        If track IsNot Nothing AndAlso track.Clips IsNot Nothing AndAlso track.Clips.Count > 0 Then
            For cIndex As Integer = 0 To track.Clips.Count - 1
                Dim clip = track.Clips(cIndex)
                Dim clipStartSec As Double = clip.TimelineStart.TotalSeconds
                Dim clipEndSec As Double = clip.TimelineEnd.TotalSeconds
                Dim clipDurSec As Double = clipEndSec - clipStartSec

                If Double.IsNaN(clipDurSec) OrElse Double.IsInfinity(clipDurSec) OrElse clipDurSec <= 0.01 Then Continue For

                Dim clipLeftRatio As Single = CSng(Math.Max(0.0, Math.Min(1.0, clipStartSec / totalProjSec)))
                Dim clipRightRatio As Single = CSng(Math.Max(0.0, Math.Min(1.0, clipEndSec / totalProjSec)))

                If Single.IsNaN(clipLeftRatio) OrElse Single.IsInfinity(clipLeftRatio) Then clipLeftRatio = 0.0F
                If Single.IsNaN(clipRightRatio) OrElse Single.IsInfinity(clipRightRatio) Then clipRightRatio = 1.0F

                Dim clipX As Single = waveBox.Left + (clipLeftRatio * waveWidth)
                Dim clipW As Single = Math.Max(2.0F, (clipRightRatio - clipLeftRatio) * waveWidth)
                If Single.IsNaN(clipW) OrElse Single.IsInfinity(clipW) Then clipW = 2.0F

                Dim clipRect As New RawRectF(clipX, usableTop, clipX + clipW, usableBot)
                renderTarget.FillRectangle(clipRect, brushWaveClipBg)

                Dim cache As PersistentWaveformCache = Nothing
                If Not String.IsNullOrWhiteSpace(clip.FilePath) Then
                    If Not _mmfCache.TryGetValue(clip.FilePath, cache) Then
                        Dim nowProbeTicks As Long = Environment.TickCount64
                        Dim lastProbe As Long = 0
                        If Not _lastMmfProbeTicks.TryGetValue(clip.FilePath, lastProbe) OrElse (nowProbeTicks - lastProbe) >= 500 Then
                            _lastMmfProbeTicks(clip.FilePath) = nowProbeTicks
                            cache = PersistentWaveformCache.TryOpenExisting(clip.FilePath, _cacheDir)
                            If cache IsNot Nothing Then
                                If _mmfCache.Count >= 32 Then
                                    For Each oldCache In _mmfCache.Values
                                        oldCache?.Dispose()
                                    Next
                                    _mmfCache.Clear()
                                End If
                                _mmfCache(clip.FilePath) = cache
                                _lastMmfProbeTicks.Remove(clip.FilePath)
                            End If
                        End If
                    End If
                End If

                Dim physLeft As Single = CSng(Math.Floor((clipX * currentScale) + currentOffsetX)) + 1.0F
                Dim physRight As Single = CSng(Math.Floor(((clipX + clipW) * currentScale) + currentOffsetX)) - 3.0F
                Dim physWidth As Single = Math.Max(16.0F, physRight - physLeft)
                Dim physTop As Single = CSng(Math.Floor(usableTop * currentScale))
                Dim physBot As Single = CSng(Math.Floor(usableBot * currentScale))
                Dim physHalfH As Single = Math.Max(4.0F, CSng(Math.Floor((physBot - physTop) * 0.5F)))
                Dim physMidY As Single = physTop + physHalfH

                Const PIXELS_PER_BAR As Integer = 2
                Dim exactBarCount As Integer = Math.Max(16, Math.Min(200, CInt(Math.Floor(physWidth / CSng(PIXELS_PER_BAR)))))
                Dim barStepPx As Single = Math.Max(CSng(PIXELS_PER_BAR), physWidth / CSng(exactBarCount))

                If cache IsNot Nothing AndAlso cache.Header.SamplesPerPeak > 0 Then
                    renderedAnyFileClip = True
                    Dim sampleRateHz As Double = CDbl(cache.EffectiveSampleRate)
                    Dim mipLevel As Integer = cache.SelectOptimalMipLevel(clipDurSec, physWidth)
                    Dim pps As Double = CDbl(cache.Header.SamplesPerPeak) * Math.Pow(4.0, mipLevel)
                    Dim srcStartSec As Double = clip.SourceIn.TotalSeconds

                    Dim startIndex As Integer = CInt(Math.Floor(srcStartSec * sampleRateHz / pps))
                    Dim count As Integer = CInt(Math.Ceiling(clipDurSec * sampleRateHz / pps))

                    Dim threeBandBars As ThreeBandPeakBar() = Nothing
                    Dim cachedSlice As CachedMixerPeakSlice = Nothing
                    If _mixerPeakSlices.TryGetValue(clip.Id, cachedSlice) AndAlso cachedSlice IsNot Nothing AndAlso
                       cachedSlice.MipLevel = mipLevel AndAlso cachedSlice.StartIndex = startIndex AndAlso
                       cachedSlice.Count = count AndAlso cachedSlice.TargetBarCount = exactBarCount AndAlso
                       cachedSlice.ThreeBandBars IsNot Nothing Then
                        threeBandBars = cachedSlice.ThreeBandBars
                    Else
                        If _mixerPeakSlices.Count > 64 Then _mixerPeakSlices.Clear()
                        Dim rawPeaks As NativePeakMinMax() = cache.ReadPeaks(mipLevel, startIndex, count)
                        If rawPeaks IsNot Nothing AndAlso rawPeaks.Length > 0 Then
                            threeBandBars = ExtractSeratoThreeBandBarsFromWpk(rawPeaks, exactBarCount)
                            _mixerPeakSlices(clip.Id) = New CachedMixerPeakSlice With {
                                .MipLevel = mipLevel,
                                .StartIndex = startIndex,
                                .Count = count,
                                .TargetBarCount = exactBarCount,
                                .Peaks = rawPeaks,
                                .ThreeBandBars = threeBandBars
                            }
                        End If
                    End If

                    If threeBandBars IsNot Nothing AndAlso threeBandBars.Length > 0 Then
                        Dim prevTransform As Matrix3x2 = renderTarget.Transform
                        Dim prevAa As AntialiasMode = renderTarget.AntialiasMode
                        renderTarget.Transform = Matrix3x2.Identity
                        renderTarget.AntialiasMode = AntialiasMode.Aliased
                        Try
                            For i As Integer = 0 To threeBandBars.Length - 1
                                Dim bx As Single = physLeft + (i * barStepPx)
                                Dim bBar As ThreeBandPeakBar = threeBandBars(i)
                                Dim echoT As Single = 0.0F
                                Dim echoB As Single = 0.0F
                                If echoMix > 0.01F AndAlso i >= 3 Then
                                    Dim pBar As ThreeBandPeakBar = threeBandBars(i - 3)
                                    echoT = Math.Max(pBar.LowTop * lowGain, Math.Max(pBar.MidTop * midGain, pBar.HighTop * highGain)) * echoMix
                                    echoB = Math.Max(pBar.LowBot * lowGain, Math.Max(pBar.MidBot * midGain, pBar.HighBot * highGain)) * echoMix
                                End If

                                RenderSeratoRekordboxColumn(
                                    bx, physMidY, physHalfH, bBar,
                                    lowGain, midGain, highGain,
                                    echoT, echoB, crushSteps, driveAmount, barStepPx)
                            Next
                            renderTarget.DrawLine(New Vector2(physLeft, physMidY), New Vector2(physRight, physMidY), brushSeparator, 1.0F)
                        Finally
                            renderTarget.AntialiasMode = prevAa
                            renderTarget.Transform = prevTransform
                        End Try
                    End If
                Else
                    Dim lowPeaks As Single() = Nothing
                    Dim midPeaks As Single() = Nothing
                    Dim highPeaks As Single() = Nothing
                    Dim peakTopArr As Single() = Nothing
                    Dim peakBotArr As Single() = Nothing
                    Dim transArr As Single() = Nothing
                    Dim lastPushTicks As Long = 0L
                    Dim hasThreeBand As Boolean = (track IsNot Nothing AndAlso _audioPlayer IsNot Nothing AndAlso
                                                   _audioPlayer.TryGetThreeBandLiveWaveSnapshot(
                                                       track.Id, lowPeaks, midPeaks, highPeaks, lastPushTicks, peakTopArr, peakBotArr, transArr))

                    If hasThreeBand AndAlso lowPeaks IsNot Nothing AndAlso lowPeaks.Length > 0 Then
                        Dim totalLen As Integer = lowPeaks.Length
                        Dim countBars As Integer = Math.Min(exactBarCount, totalLen)
                        Dim startIdx As Integer = totalLen - countBars
                        Dim liveBarStepPx As Single = Math.Max(CSng(PIXELS_PER_BAR), physWidth / CSng(Math.Max(1, countBars)))

                        Dim prevTransform As Matrix3x2 = renderTarget.Transform
                        Dim prevAa As AntialiasMode = renderTarget.AntialiasMode
                        renderTarget.Transform = Matrix3x2.Identity
                        renderTarget.AntialiasMode = AntialiasMode.Aliased
                        Try
                            For vIdx As Integer = 0 To countBars - 1
                                Dim sIdx As Integer = startIdx + vIdx
                                Dim prevIdx As Integer = Math.Max(startIdx, sIdx - 1)
                                Dim nextIdx As Integer = Math.Min(totalLen - 1, sIdx + 1)
                                Dim bx As Single = physLeft + (vIdx * liveBarStepPx)

                                Dim lVal As Single = lowPeaks(prevIdx) * 0.2F + lowPeaks(sIdx) * 0.6F + lowPeaks(nextIdx) * 0.2F
                                Dim mVal As Single = midPeaks(prevIdx) * 0.2F + midPeaks(sIdx) * 0.6F + midPeaks(nextIdx) * 0.2F
                                Dim hVal As Single = highPeaks(prevIdx) * 0.2F + highPeaks(sIdx) * 0.6F + highPeaks(nextIdx) * 0.2F
                                Dim maxBand As Single = Math.Max(0.0001F, Math.Max(lVal, Math.Max(mVal, hVal)))

                                Dim pkT As Single = maxBand
                                If peakTopArr IsNot Nothing AndAlso nextIdx < peakTopArr.Length Then
                                    pkT = peakTopArr(prevIdx) * 0.22F + peakTopArr(sIdx) * 0.56F + peakTopArr(nextIdx) * 0.22F
                                End If

                                Dim pkB As Single = maxBand
                                If peakBotArr IsNot Nothing AndAlso nextIdx < peakBotArr.Length Then
                                    pkB = peakBotArr(prevIdx) * 0.22F + peakBotArr(sIdx) * 0.56F + peakBotArr(nextIdx) * 0.22F
                                End If

                                Dim topRatio As Single = pkT / maxBand
                                Dim botRatio As Single = pkB / maxBand
                                Dim hotVal As Single = If(transArr IsNot Nothing AndAlso sIdx < transArr.Length, transArr(sIdx), 0.0F)

                                Dim bBar As New ThreeBandPeakBar() With {
                                    .LowTop = lVal * topRatio,
                                    .LowBot = lVal * botRatio,
                                    .MidTop = mVal * topRatio,
                                    .MidBot = mVal * botRatio,
                                    .HighTop = hVal * topRatio,
                                    .HighBot = hVal * botRatio,
                                    .CrestHot = hotVal
                                }

                                Dim echoVal As Single = 0.0F
                                If echoMix > 0.01F AndAlso sIdx >= 4 Then
                                    echoVal = Math.Max(lowPeaks(sIdx - 4) * lowGain, Math.Max(midPeaks(sIdx - 4) * midGain, highPeaks(sIdx - 4) * highGain)) * echoMix
                                End If

                                RenderSeratoRekordboxColumn(
                                    bx, physMidY, physHalfH, bBar,
                                    lowGain, midGain, highGain,
                                    echoVal, echoVal, crushSteps, driveAmount, liveBarStepPx)
                            Next

                            renderTarget.DrawLine(New Vector2(physLeft, physMidY), New Vector2(physRight, physMidY), brushSeparator, 1.0F)
                        Finally
                            renderTarget.AntialiasMode = prevAa
                            renderTarget.Transform = prevTransform
                        End Try

                        Dim liveHeadX As Single = clipX + clipW - 1.5F
                        DrawPlayheadWithCaps(liveHeadX, usableTop, usableBot)
                    Else
                        renderTarget.DrawLine(New Vector2(clipX, midY), New Vector2(clipX + clipW, midY), brushWaveform, 1.5F)
                    End If
                End If
            Next

            If renderedAnyFileClip AndAlso currentVirtTime.TotalSeconds >= 0.0 AndAlso currentVirtTime.TotalSeconds <= totalProjSec Then
                Dim playheadRatio As Single = CSng(currentVirtTime.TotalSeconds / totalProjSec)
                If Not Single.IsNaN(playheadRatio) AndAlso Not Single.IsInfinity(playheadRatio) Then
                    Dim playheadX As Single = waveBox.Left + (playheadRatio * waveWidth)
                    If playheadX > waveBox.Left + 2.0F Then
                        Dim playedRect As New RawRectF(waveBox.Left + 1.0F, usableTop, playheadX, usableBot)
                        renderTarget.FillRectangle(playedRect, brushWavePlayedDim)
                    End If
                    DrawPlayheadWithCaps(playheadX, usableTop, usableBot)
                End If
            End If
        End If
    End Sub

    Private Sub DrawFxPopupMenu(colX As Single, w As Single, colTop As Single, activeMode As Integer)
        Dim popLeft As Single = colX + 3.0F
        Dim popRight As Single = colX + w - 3.0F
        Dim popTop As Single = colTop + 14.0F
        Dim popHeight As Single = (FX_POPUP_COUNT * FX_POPUP_ITEM_H) + 4.0F
        Dim popRect As New RawRectF(popLeft, popTop, popRight, popTop + popHeight)

        renderTarget.FillRectangle(popRect, brushOsdBg)
        renderTarget.DrawRectangle(popRect, brushOsdBorder, 1.3F)

        For m As Integer = 0 To FX_POPUP_COUNT - 1
            Dim itemTop As Single = popTop + 2.0F + (m * FX_POPUP_ITEM_H)
            Dim itemBot As Single = itemTop + FX_POPUP_ITEM_H - 1.0F
            Dim itemRect As New RawRectF(popLeft + 2.0F, itemTop, popRight - 2.0F, itemBot)

            Dim isSelected As Boolean = ((activeMode And 3) = m)
            Dim isHovered As Boolean = (_fxPopupHoverMode = m)

            If isSelected Then
                renderTarget.FillRectangle(itemRect, brushWaveClipBg)
                renderTarget.DrawRectangle(itemRect, brushKnobBlueIndicator, 1.1F)
            ElseIf isHovered Then
                renderTarget.FillRectangle(itemRect, brushBgColor)
                renderTarget.DrawRectangle(itemRect, brushTextGray, 0.8F)
            Else
                renderTarget.FillRectangle(itemRect, brushBgFader)
            End If

            Dim prefix As String = If(isSelected, "► ", "")
            Dim lblBrush As ID2D1SolidColorBrush = If(isSelected, brushKnobBlueIndicator, If(isHovered, brushTextWhite, brushTextGray))
            renderTarget.DrawText(prefix & GetColorFxPopupOptionTitle(m), fontSmallCenter, itemRect, lblBrush)
        Next
    End Sub

    Private Sub RenderAudioMixer(audioTracks As List(Of TrackModel))
        Dim channelCols As Integer = Math.Max(1, audioTracks.Count)
        Dim totalCols As Integer = channelCols + 1
        Dim currentX As Single = -mixerScrollX
        Dim H As Single = VIRTUAL_HEIGHT

        Dim waveTop As Single = 0.0F
        Dim eqTop As Single = WAVE_SECTION_H
        Dim colTop As Single = eqTop + EQ_SECTION_H
        Dim faderTop As Single = colTop + COL_SECTION_H

        Dim viewportWidth As Single = Me.Width / currentScale
        Dim currentVirtTime As TimeSpan = If(myPlayback IsNot Nothing, myPlayback.CurrentVirtualTime, TimeSpan.Zero)

        Dim popupColX As Single = -1.0F
        Dim popupColW As Single = TRACK_WIDTH
        Dim popupActiveMode As Integer = 0

        For col As Integer = 0 To totalCols - 1
            Dim isMaster As Boolean = (col = totalCols - 1)
            Dim w As Single = If(isMaster, MASTER_WIDTH, TRACK_WIDTH)

            If currentX + w > 0 AndAlso currentX < viewportWidth Then
                If Not isMaster Then
                    Dim track = If(col < audioTracks.Count, audioTracks(col), Nothing)
                    Dim state As AudioChannelState = Nothing

                    If track IsNot Nothing Then
                        If Not audioStates.TryGetValue(track.Id, state) Then
                            state = New AudioChannelState() With {
                             .Trim = track.Trim,
                             .Hi = track.EqHi,
                             .Mid = track.EqMid,
                             .Low = track.EqLow,
                             .ColorFx = track.ColorFx,
                             .ColorFxMode = track.ColorFxMode,
                             .WaveformZoom = If(track.WaveformZoom <= 0.05F, 1.0F, track.WaveformZoom),
                             .IsMuted = track.IsMuted,
                             .IsSolo = track.IsSolo,
                             .Fader = If(track.Clips IsNot Nothing AndAlso track.Clips.Count > 0, track.Clips(0).Volume, 1.0F)
                         }
                            audioStates(track.Id) = state
                        End If
                    Else
                        state = New AudioChannelState()
                    End If

                    If track IsNot Nothing AndAlso track.Id = _fxPopupTrackId Then
                        popupColX = currentX
                        popupColW = w
                        popupActiveMode = state.ColorFxMode
                    End If

                    renderTarget.FillRectangle(New RawRectF(currentX, 0, currentX + w, eqTop), brushBgFader)
                    renderTarget.FillRectangle(New RawRectF(currentX, eqTop, currentX + w, colTop), brushBgEq)
                    renderTarget.FillRectangle(New RawRectF(currentX, colTop, currentX + w, faderTop), brushBgColor)
                    renderTarget.FillRectangle(New RawRectF(currentX, faderTop, currentX + w, H), brushBgFader)
                    renderTarget.DrawLine(New Vector2(currentX + w, 0), New Vector2(currentX + w, H), brushSeparator, 1.0F)

                    Dim cx As Single = currentX + w * 0.65F
                    Dim fLineX As Single = currentX + w / 2.0F
                    Dim knobRad As Single = 10.0F

                    DrawChannelWaveformScreen(currentX, waveTop, w, WAVE_SECTION_H, track, currentVirtTime)

                    Dim badgeRect As New RawRectF(currentX + 6.0F, waveTop + 6.0F, currentX + w - 6.0F, waveTop + 18.0F)
                    renderTarget.FillRectangle(badgeRect, brushBgColor)
                    Dim isFocusedTrack As Boolean = (track IsNot Nothing AndAlso _audioPlayer IsNot Nothing AndAlso _audioPlayer.FocusedTrackId = track.Id)
                    renderTarget.DrawRectangle(badgeRect, If(isFocusedTrack, brushKnobBlueIndicator, brushSeparator), If(isFocusedTrack, 1.4F, 1.0F))
                    Dim chTitle As String = If(track IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(track.Name), $"CH{col + 1}: {track.Name}", $"CH {col + 1}")
                    renderTarget.DrawText(chTitle, fontBadge, badgeRect, brushTextWhite)

                    Dim btnMuteRect As New RawRectF(currentX + 6.0F, eqTop + 3.0F, fLineX - 2.0F, eqTop + 15.0F)
                    renderTarget.FillRectangle(btnMuteRect, If(state.IsMuted, brushLedRed, brushBgColor))
                    renderTarget.DrawRectangle(btnMuteRect, If(state.IsMuted, brushTextWhite, brushSeparator), 1.0F)
                    renderTarget.DrawText("MUTE", fontTinyCenter, btnMuteRect, If(state.IsMuted, brushTextWhite, brushTextGray))

                    Dim btnSoloRect As New RawRectF(fLineX + 2.0F, eqTop + 3.0F, currentX + w - 6.0F, eqTop + 15.0F)
                    renderTarget.FillRectangle(btnSoloRect, If(state.IsSolo, brushLedOrange, brushBgColor))
                    renderTarget.DrawRectangle(btnSoloRect, If(state.IsSolo, brushTextWhite, brushSeparator), 1.0F)
                    renderTarget.DrawText("SOLO", fontTinyCenter, btnSoloRect, If(state.IsSolo, brushTextWhite, brushTextGray))

                    Dim meterX As Single = currentX + 5.0F
                    Dim meterW As Single = 4.2F
                    Dim meterTop As Single = Y_TRIM - 8.0F
                    Dim meterH As Single = (Y_LOW - Y_TRIM) + knobRad * 2.0F

                    DrawDJMMeter(meterX, meterTop, meterW, meterH, state.Peak, state.PeakHold)

                    Dim ledCount As Integer = 15
                    Dim spacing As Single = 1.6F
                    Dim ledH As Single = (meterH - (ledCount - 1) * spacing) / ledCount
                    Dim ledLbls() As String = {"OVER", "10", "7", "4", "2", "1", "0", "-1", "-2", "-3", "-5", "-7", "-10", "-15", "-24"}

                    For i As Integer = 0 To ledCount - 1
                        Dim lblIdx As Integer = (ledCount - 1) - i
                        Dim ledY As Single = meterTop + meterH - (i + 1) * ledH - i * spacing
                        Dim clr = If(lblIdx <= 1, brushTextWhite, brushTextGray)
                        Dim textRect As New RawRectF(meterX + meterW + 2.0F, ledY - 1.5F, cx - 14.0F, ledY + ledH + 1.5F)
                        renderTarget.DrawText(ledLbls(lblIdx), fontLedLeft, textRect, clr)
                    Next

                    renderTarget.DrawText("TRIM", fontSmallCenter, New RawRectF(cx - 18.0F, Y_TRIM - knobRad - 13.5F, cx + 18.0F, Y_TRIM - knobRad - 5.0F), brushTextWhite)
                    DrawDJMKnob(cx, Y_TRIM, knobRad, state.Trim, 1)

                    renderTarget.DrawText("HI", fontSmallCenter, New RawRectF(cx - 18.0F, Y_HI - knobRad - 13.5F, cx + 18.0F, Y_HI - knobRad - 5.0F), brushTextWhite)
                    DrawDJMKnob(cx, Y_HI, knobRad, state.Hi, 1)

                    renderTarget.DrawText("MID", fontSmallCenter, New RawRectF(cx - 18.0F, Y_MID - knobRad - 13.5F, cx + 18.0F, Y_MID - knobRad - 5.0F), brushTextWhite)
                    DrawDJMKnob(cx, Y_MID, knobRad, state.Mid, 1)

                    renderTarget.DrawText("LOW", fontSmallCenter, New RawRectF(cx - 18.0F, Y_LOW - knobRad - 13.5F, cx + 18.0F, Y_LOW - knobRad - 5.0F), brushTextWhite)
                    DrawDJMKnob(cx, Y_LOW, knobRad, state.Low, 1)

                    Dim eqYs() As Single = {Y_TRIM, Y_HI, Y_MID, Y_LOW}
                    For Each yK In eqYs
                        Dim lblY As Single = yK + knobRad + 3.5F
                        renderTarget.DrawText("MIN", fontTinyRight, New RawRectF(cx - 28.0F, lblY, cx - 9.0F, lblY + 7.0F), brushTextGray)
                        renderTarget.DrawText("MAX", fontTinyLeft, New RawRectF(cx + 9.0F, lblY, cx + 28.0F, lblY + 7.0F), brushTextGray)
                    Next

                    Dim colCy As Single = colTop + (COL_SECTION_H / 2.0F) + 3.5F
                    Dim isPopupOpenForTrack As Boolean = (track IsNot Nothing AndAlso track.Id = _fxPopupTrackId)
                    Dim fxModeRect As New RawRectF(currentX + 6.0F, colTop + 2.0F, currentX + w - 6.0F, colTop + 13.5F)
                    renderTarget.FillRectangle(fxModeRect, If(isPopupOpenForTrack, brushWaveClipBg, brushBgFader))
                    renderTarget.DrawRectangle(fxModeRect, If(isPopupOpenForTrack OrElse state.ColorFxMode <> 0, brushKnobBlueIndicator, brushSeparator), 1.0F)
                    Dim arrowSym As String = If(isPopupOpenForTrack, " ▲", " ▾")
                    renderTarget.DrawText($"FX: {GetColorFxModeName(state.ColorFxMode)}{arrowSym}", fontSmallCenter, fxModeRect, If(isPopupOpenForTrack OrElse state.ColorFxMode <> 0, brushKnobBlueIndicator, brushTextWhite))

                    DrawDJMKnob(cx, colCy, knobRad * 1.02F, state.ColorFx, 1)

                    Dim leftFxLbl As String = "LPF"
                    Dim rightFxLbl As String = "HPF"
                    Select Case state.ColorFxMode And 3
                        Case 1 : leftFxLbl = "DUB" : rightFxLbl = "SPC"
                        Case 2 : leftFxLbl = "DRV" : rightFxLbl = "CRS"
                        Case 3 : leftFxLbl = "VOC" : rightFxLbl = "SUB"
                    End Select
                    renderTarget.DrawText(leftFxLbl, fontTinyRight, New RawRectF(cx - 26.0F, colCy + knobRad + 2.0F, cx - 8.0F, colCy + knobRad + 9.5F), brushTextWhite)
                    renderTarget.DrawText(rightFxLbl, fontTinyLeft, New RawRectF(cx + 8.0F, colCy + knobRad + 2.0F, cx + 26.0F, colCy + knobRad + 9.5F), brushTextWhite)

                    Dim fTop As Single = faderTop + 12.0F
                    Dim fBot As Single = H - 12.0F
                    If fBot <= fTop Then fBot = fTop + 30.0F

                    Dim fdLabels() As String = {"10", "9", "8", "7", "6", "5", "4", "3", "2", "1", "0"}
                    For j As Integer = 0 To fdLabels.Length - 1
                        Dim ly As Single = fTop + (j / CSng(fdLabels.Length - 1)) * (fBot - fTop)
                        renderTarget.DrawLine(New Vector2(currentX + 16.0F, ly), New Vector2(fLineX - 6.0F, ly), brushTextWhite, 1.0F)
                        renderTarget.DrawLine(New Vector2(fLineX + 6.0F, ly), New Vector2(currentX + w - 16.0F, ly), brushTextWhite, 1.0F)
                        renderTarget.DrawText(fdLabels(j), fontTinyRight, New RawRectF(currentX + 2.0F, ly - 3.0F, currentX + 14.0F, ly + 3.0F), brushTextWhite)
                        renderTarget.DrawText(fdLabels(j), fontTinyLeft, New RawRectF(currentX + w - 14.0F, ly - 3.0F, currentX + w - 2.0F, ly + 3.0F), brushTextWhite)
                    Next

                    renderTarget.FillRectangle(New RawRectF(fLineX - 2.2F, fTop - 2.0F, fLineX + 2.2F, fBot + 2.0F), brushFaderSlot)

                    Dim thumbY As Single = fTop + (1.0F - state.Fader) * (fBot - fTop)
                    Dim tRect As New RawRectF(fLineX - 13.0F, thumbY - 7.0F, fLineX + 13.0F, thumbY + 7.0F)
                    renderTarget.FillRectangle(tRect, brushFaderThumbBg)
                    renderTarget.DrawRectangle(tRect, brushFaderThumbBorder, 1.0F)
                    renderTarget.DrawLine(New Vector2(fLineX - 10.0F, thumbY), New Vector2(fLineX + 10.0F, thumbY), brushTextWhite, 2.0F)
                Else
                    Dim cx As Single = currentX + w / 2.0F

                    renderTarget.FillRectangle(New RawRectF(currentX, 0, currentX + w, H), brushBgEq)
                    renderTarget.DrawLine(New Vector2(currentX, 0), New Vector2(currentX, H), brushSeparator, 2.0F)

                    renderTarget.FillRectangle(New RawRectF(currentX, 0, currentX + w, 22.0F), brushBgColor)
                    renderTarget.DrawText("MASTER", fontNormalCenter, New RawRectF(currentX, 2.0F, currentX + w, 20.0F), brushTextWhite)

                    Dim mstBox As New RawRectF(currentX + 4.0F, 26.0F, currentX + w - 4.0F, 92.0F)
                    renderTarget.FillRectangle(mstBox, brushWaveBg)
                    Dim isOsdActive As Boolean = (Environment.TickCount64 < _osdExpireTicks)
                    renderTarget.DrawRectangle(mstBox, If(isOsdActive, brushOsdBorder, brushSeparator), If(isOsdActive, 1.2F, 1.0F))

                    Dim hdrBox As New RawRectF(mstBox.Left + 2.0F, mstBox.Top + 2.0F, mstBox.Right - 2.0F, mstBox.Top + 16.0F)
                    renderTarget.FillRectangle(hdrBox, brushBgColor)
                    renderTarget.DrawText(_osdChannelTitle, fontSmallCenter, hdrBox, If(isOsdActive, brushKnobBlueIndicator, brushTextGray))

                    Dim paramBox As New RawRectF(mstBox.Left + 2.0F, mstBox.Top + 19.0F, mstBox.Right - 2.0F, mstBox.Top + 37.0F)
                    renderTarget.DrawText(_osdParamName, fontNormalCenter, paramBox, brushTextWhite)

                    Dim valBox As New RawRectF(mstBox.Left + 3.0F, mstBox.Top + 39.0F, mstBox.Right - 3.0F, mstBox.Bottom - 4.0F)
                    renderTarget.FillRectangle(valBox, brushOsdBg)
                    renderTarget.DrawRectangle(valBox, brushSeparator, 0.8F)
                    renderTarget.DrawText(_osdValueText, fontNormalCenter, valBox, If(isOsdActive, brushWaveHigh, brushTextGray))

                    Dim mLvlY As Single = Y_TRIM
                    renderTarget.DrawText("LEVEL", fontSmallCenter, New RawRectF(currentX, mLvlY - 29.0F, currentX + w, mLvlY - 19.0F), brushTextWhite)
                    DrawDJMKnob(cx, mLvlY, 10.8F, masterVolume, 1)
                    renderTarget.DrawText("0", fontTinyRight, New RawRectF(cx - 28.0F, mLvlY + 12.0F, cx - 8.0F, mLvlY + 20.0F), brushTextWhite)
                    renderTarget.DrawText("100", fontTinyLeft, New RawRectF(cx + 8.0F, mLvlY + 12.0F, cx + 28.0F, mLvlY + 20.0F), brushTextWhite)

                    Dim mMeterTop As Single = Y_HI - 8.0F
                    Dim mMeterBot As Single = H - 69.0F
                    Dim meterW As Single = 4.2F

                    DrawDJMMeter(cx - 14.0F, mMeterTop, meterW, mMeterBot - mMeterTop, MasterPeakL, _masterHoldL)
                    DrawDJMMeter(cx + 10.0F, mMeterTop, meterW, mMeterBot - mMeterTop, MasterPeakR, _masterHoldR)

                    Dim ledLbls() As String = {"OVER", "10", "7", "4", "2", "1", "0", "-1", "-2", "-3", "-5", "-7", "-10", "-15", "-24"}
                    Dim ledCount As Integer = 15
                    Dim spacing As Single = 1.6F
                    Dim ledH As Single = ((mMeterBot - mMeterTop) - (ledCount - 1) * spacing) / ledCount

                    For i As Integer = 0 To ledCount - 1
                        Dim lblIdx As Integer = (ledCount - 1) - i
                        Dim ledY As Single = mMeterTop + (mMeterBot - mMeterTop) - (i + 1) * ledH - i * spacing
                        Dim clr = If(lblIdx <= 1, brushTextWhite, brushTextGray)
                        renderTarget.DrawText(ledLbls(lblIdx), fontLedCenter, New RawRectF(cx - 8.0F, ledY - 1.5F, cx + 8.0F, ledY + ledH + 1.5F), clr)
                    Next

                    renderTarget.DrawText("L", fontNormalCenter, New RawRectF(cx - 18.0F, mMeterBot + 2.0F, cx - 6.0F, mMeterBot + 14.0F), brushTextWhite)
                    renderTarget.DrawText("dB", fontLedCenter, New RawRectF(cx - 8.0F, mMeterBot + 3.0F, cx + 8.0F, mMeterBot + 14.0F), brushTextGray)
                    renderTarget.DrawText("R", fontNormalCenter, New RawRectF(cx + 6.0F, mMeterBot + 2.0F, cx + 18.0F, mMeterBot + 14.0F), brushTextWhite)

                    Dim mBalY As Single = H - 28.0F
                    renderTarget.DrawText("BALANCE", fontSmallCenter, New RawRectF(currentX, mBalY - 25.0F, currentX + w, mBalY - 15.0F), brushTextWhite)
                    DrawDJMKnob(cx, mBalY, 9.2F, masterBalance, 1)
                    renderTarget.DrawText("L", fontTinyRight, New RawRectF(cx - 22.0F, mBalY + 10.0F, cx - 6.0F, mBalY + 18.0F), brushTextWhite)
                    renderTarget.DrawText("R", fontTinyLeft, New RawRectF(cx + 6.0F, mBalY + 10.0F, cx + 22.0F, mBalY + 18.0F), brushTextWhite)
                End If
            End If
            currentX += w
        Next

        If _fxPopupTrackId <> Guid.Empty AndAlso popupColX >= -popupColW Then
            DrawFxPopupMenu(popupColX, popupColW, colTop, popupActiveMode)
        End If
    End Sub

    Protected Overrides Sub OnMouseWheel(e As MouseEventArgs)
        MyBase.OnMouseWheel(e)

        If e.Y >= 0 AndAlso e.Y <= WAVE_SECTION_H * currentScale Then
            If e.Delta <> 0 Then
                Dim vx As Single = GetVirtualX(e.X) + mixerScrollX
                Dim colIdx As Integer = CInt(Math.Floor(vx / TRACK_WIDTH))
                Dim audioTracks As List(Of TrackModel) = GetAudioTracksSnapshot()
                Dim hoveredTrack As TrackModel = If(colIdx >= 0 AndAlso colIdx < audioTracks.Count, audioTracks(colIdx), Nothing)

                AdjustWaveformScale(CSng(e.Delta / 120.0) * WAVE_AMP_STEP, hoveredTrack)
                SafeInvalidate()
            End If
            Return
        End If

        If currentScale <= 0.001F Then currentScale = 1.0F

        Dim audioTracksCount As Integer = GetAudioTracksSnapshot().Count

        Dim totalMixerWidth As Single = (Math.Max(1, audioTracksCount) * TRACK_WIDTH) + MASTER_WIDTH
        Dim viewportWidth As Single = Me.Width / currentScale
        Dim maxScrollX As Single = Math.Max(0.0F, totalMixerWidth - viewportWidth)

        mixerScrollX -= CSng((e.Delta * 0.5) / currentScale)
        mixerScrollX = Math.Max(0.0F, Math.Min(maxScrollX, mixerScrollX))
        SafeInvalidate()
    End Sub

    Private Function GetVirtualX(screenX As Integer) As Single
        If currentScale <= 0.001F Then Return 0.0F
        Return (screenX - currentOffsetX) / currentScale
    End Function

    Private Function GetVirtualY(screenY As Integer) As Single
        If currentScale <= 0.001F Then Return 0.0F
        Return screenY / currentScale
    End Function

    Private Function GetHitElement(screenX As Integer, screenY As Integer) As String
        Dim x As Single = GetVirtualX(screenX)
        Dim y As Single = GetVirtualY(screenY)

        Dim audioTracks As List(Of TrackModel) = GetAudioTracksSnapshot()
        If audioTracks.Count = 0 AndAlso myModel?.Tracks Is Nothing Then Return ""

        Dim totalCols As Integer = Math.Max(1, audioTracks.Count) + 1
        Dim currentX As Single = -mixerScrollX
        Dim H As Single = VIRTUAL_HEIGHT

        Dim eqTop As Single = WAVE_SECTION_H
        Dim colTop As Single = eqTop + EQ_SECTION_H
        Dim faderTop As Single = colTop + COL_SECTION_H

        If _fxPopupTrackId <> Guid.Empty Then
            Dim popX As Single = -mixerScrollX
            For col As Integer = 0 To audioTracks.Count - 1
                Dim tr = audioTracks(col)
                If tr.Id = _fxPopupTrackId Then
                    Dim popLeft As Single = popX + 3.0F
                    Dim popRight As Single = popX + TRACK_WIDTH - 3.0F
                    Dim popTop As Single = colTop + 14.0F
                    Dim popBot As Single = popTop + (FX_POPUP_COUNT * FX_POPUP_ITEM_H) + 4.0F
                    If x >= popLeft AndAlso x <= popRight AndAlso y >= popTop AndAlso y <= popBot Then
                        Dim idx As Integer = CInt(Math.Floor((y - (popTop + 2.0F)) / FX_POPUP_ITEM_H))
                        idx = Math.Max(0, Math.Min(FX_POPUP_COUNT - 1, idx))
                        Return $"MixerColorSelect_{tr.Id}_{idx}"
                    End If
                    Exit For
                End If
                popX += TRACK_WIDTH
            Next
        End If

        For col As Integer = 0 To totalCols - 1
            Dim isMaster As Boolean = (col = totalCols - 1)
            Dim w As Single = If(isMaster, MASTER_WIDTH, TRACK_WIDTH)

            If Not isMaster AndAlso col < audioTracks.Count Then
                Dim track = audioTracks(col)
                Dim cx As Single = currentX + w * 0.65F
                Dim fLineX As Single = currentX + w / 2.0F

                If y >= eqTop + 2.5F AndAlso y <= eqTop + 15.5F Then
                    If x >= currentX + 5.0F AndAlso x <= fLineX - 1.0F Then Return $"MixerMute_{track.Id}"
                    If x >= fLineX + 1.0F AndAlso x <= currentX + w - 5.0F Then Return $"MixerSolo_{track.Id}"
                End If

                If Math.Abs(x - cx) < 15.0F AndAlso Math.Abs(y - Y_TRIM) < 15.0F Then Return $"MixerTrim_{track.Id}"
                If Math.Abs(x - cx) < 15.0F AndAlso Math.Abs(y - Y_HI) < 15.0F Then Return $"MixerHi_{track.Id}"
                If Math.Abs(x - cx) < 15.0F AndAlso Math.Abs(y - Y_MID) < 15.0F Then Return $"MixerMid_{track.Id}"
                If Math.Abs(x - cx) < 15.0F AndAlso Math.Abs(y - Y_LOW) < 15.0F Then Return $"MixerLow_{track.Id}"

                If x >= currentX + 6.0F AndAlso x <= currentX + w - 6.0F AndAlso y >= colTop + 2.0F AndAlso y <= colTop + 13.5F Then
                    Return $"MixerColorMode_{track.Id}"
                End If

                Dim colCy As Single = colTop + (COL_SECTION_H / 2.0F) + 3.5F
                If Math.Abs(x - cx) < 15.0F AndAlso Math.Abs(y - colCy) < 15.0F Then Return $"MixerColor_{track.Id}"

                If Math.Abs(x - fLineX) < 18.0F AndAlso y >= faderTop + 6.0F AndAlso y <= H Then Return $"MixerVol_{track.Id}"
            ElseIf isMaster Then
                Dim cx As Single = currentX + w / 2.0F
                If Math.Abs(x - cx) < 18.0F AndAlso Math.Abs(y - Y_TRIM) < 18.0F Then Return "MixerMasterVol"
                If Math.Abs(x - cx) < 15.0F AndAlso Math.Abs(y - (H - 28.0F)) < 15.0F Then Return "MixerMasterBal"
            End If

            currentX += w
        Next
        Return ""
    End Function

    Protected Overrides Sub OnMouseMove(e As MouseEventArgs)
        MyBase.OnMouseMove(e)

        If draggedProperty <> "" Then
            Me.Cursor = Cursors.SizeNS

            Dim vY As Single = GetVirtualY(e.Y)
            Dim vStartY As Single = GetVirtualY(dragStartMouseY)
            Dim vStartX As Single = GetVirtualX(dragStartMouseX)
            Dim deltaY As Single = vStartY - vY
            Dim deltaX As Single = GetVirtualX(e.X) - vStartX

            dragStartMouseY = e.Y
            dragStartMouseX = e.X

            Dim H As Single = VIRTUAL_HEIGHT
            Dim faderTop As Single = WAVE_SECTION_H + EQ_SECTION_H + COL_SECTION_H

            If draggedProperty.StartsWith("MixerVol_") Then
                Dim tId As Guid = Guid.Parse(draggedProperty.Replace("MixerVol_", ""))

                Dim fTop As Single = faderTop + 12.0F
                Dim fBot As Single = H - 12.0F
                If fBot <= fTop Then fBot = fTop + 30.0F

                Dim newVol As Single = 1.0F - ((vY - fTop) / (fBot - fTop))
                newVol = Math.Max(0.0F, Math.Min(1.0F, newVol))

                Dim st As AudioChannelState = Nothing
                If audioStates.TryGetValue(tId, st) AndAlso st IsNot Nothing Then
                    st.Fader = newVol
                    ShowOsd(tId, $"FADER: {(newVol * 10.0F):F1}")
                End If

                If _broker IsNot Nothing Then
                    _broker.SetTrackVolume(tId, newVol, Me)
                Else
                    ApplyVolumeToTrackVisualOnly(tId, newVol)
                    NotifyTimelineRedraw()
                End If

            ElseIf draggedProperty = "MixerMasterVol" Then
                Dim sensitivity As Single = 0.005F
                masterVolume = Math.Max(0.0F, Math.Min(1.0F, masterVolume + deltaY * sensitivity))
                SyncMasterVolume(masterVolume)
                ShowOsd(Guid.Empty, $"MASTER: {CInt(Math.Round(masterVolume * 100.0F))}%")

            ElseIf draggedProperty = "MixerMasterBal" Then
                Dim sensitivity As Single = 0.005F
                Dim deltaBal As Single = If(Math.Abs(deltaX) > Math.Abs(deltaY), deltaX, deltaY)
                masterBalance = Math.Max(0.0F, Math.Min(1.0F, masterBalance + deltaBal * sensitivity))
                SyncMasterBalance(masterBalance)
                Dim balTxt As String = If(Math.Abs(masterBalance - 0.5F) <= 0.015F,
                                          "BAL: CENTER",
                                          If(masterBalance < 0.5F,
                                             $"BAL: L {CInt(Math.Round((0.5F - masterBalance) * 200.0F))}%",
                                             $"BAL: R {CInt(Math.Round((masterBalance - 0.5F) * 200.0F))}%"))
                ShowOsd(Guid.Empty, balTxt)

            Else
                Dim sensitivity As Single = 0.005F
                Dim tId As Guid = Guid.Empty
                Dim propType As String = ""

                If draggedProperty.StartsWith("MixerTrim_") Then
                    tId = Guid.Parse(draggedProperty.Replace("MixerTrim_", ""))
                    propType = "Trim"
                ElseIf draggedProperty.StartsWith("MixerHi_") Then
                    tId = Guid.Parse(draggedProperty.Replace("MixerHi_", ""))
                    propType = "Hi"
                ElseIf draggedProperty.StartsWith("MixerMid_") Then
                    tId = Guid.Parse(draggedProperty.Replace("MixerMid_", ""))
                    propType = "Mid"
                ElseIf draggedProperty.StartsWith("MixerLow_") Then
                    tId = Guid.Parse(draggedProperty.Replace("MixerLow_", ""))
                    propType = "Low"
                ElseIf draggedProperty.StartsWith("MixerColor_") Then
                    tId = Guid.Parse(draggedProperty.Replace("MixerColor_", ""))
                    propType = "Color"
                End If

                If tId <> Guid.Empty Then
                    Dim st As AudioChannelState = Nothing
                    If audioStates.TryGetValue(tId, st) AndAlso st IsNot Nothing Then
                        Select Case propType
                            Case "Trim"
                                st.Trim = Math.Max(0.0F, Math.Min(1.0F, st.Trim + deltaY * sensitivity))
                                ShowOsd(tId, FormatTrimOsd(st.Trim))
                            Case "Hi"
                                st.Hi = Math.Max(0.0F, Math.Min(1.0F, st.Hi + deltaY * sensitivity))
                                ShowOsd(tId, FormatEqBandOsd("HI", st.Hi))
                            Case "Mid"
                                st.Mid = Math.Max(0.0F, Math.Min(1.0F, st.Mid + deltaY * sensitivity))
                                ShowOsd(tId, FormatEqBandOsd("MID", st.Mid))
                            Case "Low"
                                st.Low = Math.Max(0.0F, Math.Min(1.0F, st.Low + deltaY * sensitivity))
                                ShowOsd(tId, FormatEqBandOsd("LOW", st.Low))
                            Case "Color"
                                st.ColorFx = Math.Max(0.0F, Math.Min(1.0F, st.ColorFx + deltaY * sensitivity))
                                ShowOsd(tId, FormatColorFxOsd(st.ColorFx, st.ColorFxMode))
                        End Select

                        If _broker IsNot Nothing Then
                            Select Case propType
                                Case "Trim", "Hi", "Mid", "Low"
                                    _broker.SetTrackEQAndTrim(tId, st.Low, st.Mid, st.Hi, st.Trim, Me)
                                Case "Color"
                                    _broker.SetTrackColorFilter(tId, st.ColorFx, Me)
                            End Select
                        Else
                            Select Case propType
                                Case "Trim", "Hi", "Mid", "Low"
                                    SyncTrackEQ(tId, st)
                                Case "Color"
                                    If myModel?.Tracks IsNot Nothing Then
                                        SyncLock myModel.SyncRoot
                                            Dim track = myModel.Tracks.FirstOrDefault(Function(t) t.Id = tId)
                                            If track IsNot Nothing Then track.ColorFx = st.ColorFx
                                        End SyncLock
                                    End If
                                    _audioPlayer?.SetTrackColorFilter(tId, st.ColorFx)
                                    MixerHardwareStateStore.Save(myModel, _audioPlayer)
                                    myModel?.RefreshSnapshotOnly()
                            End Select
                            NotifyTimelineRedraw()
                        End If
                    End If
                End If
            End If
            SafeInvalidate()
            Return
        End If

        Dim hit = GetHitElement(e.X, e.Y)

        Dim newHoverMode As Integer = -1
        If hit.StartsWith("MixerColorSelect_") Then
            Dim parts As String() = hit.Split("_"c)
            If parts.Length >= 3 Then
                Integer.TryParse(parts(2), newHoverMode)
            End If
        End If
        If newHoverMode <> _fxPopupHoverMode Then
            _fxPopupHoverMode = newHoverMode
            SafeInvalidate()
        End If

        If hit.StartsWith("MixerMute_") OrElse
           hit.StartsWith("MixerSolo_") OrElse
           hit.StartsWith("MixerColorMode_") OrElse
           hit.StartsWith("MixerColorSelect_") Then
            Me.Cursor = Cursors.Hand
        Else
            Me.Cursor = If(hit <> "", Cursors.SizeNS, Cursors.Default)
        End If
    End Sub

    Private Sub NotifyTimelineRedraw()
        _dispatcher?.RequestVisualOnlyRedraw()
    End Sub

    Private Sub SyncTrackEQ(trackId As Guid, state As AudioChannelState)
        If _audioPlayer IsNot Nothing Then
            Dim lowDb = yoump.MediaParameterBroker.ValueToDecibels(state.Low)
            Dim midDb = yoump.MediaParameterBroker.ValueToDecibels(state.Mid)
            Dim hiDb = yoump.MediaParameterBroker.ValueToDecibels(state.Hi)
            Dim trimGain = state.Trim * 2.0F

            _audioPlayer.SetTrackEQAndTrim(trackId, lowDb, midDb, hiDb, trimGain)
        End If

        If myModel?.Tracks IsNot Nothing Then
            SyncLock myModel.SyncRoot
                Dim track = myModel.Tracks.FirstOrDefault(Function(t) t.Id = trackId)
                If track IsNot Nothing Then
                    track.Trim = state.Trim
                    track.EqHi = state.Hi
                    track.EqMid = state.Mid
                    track.EqLow = state.Low
                    track.ColorFx = state.ColorFx
                    track.ColorFxMode = state.ColorFxMode
                    track.IsMuted = state.IsMuted
                    track.IsSolo = state.IsSolo
                End If
            End SyncLock
        End If

        MixerHardwareStateStore.Save(myModel, _audioPlayer)
        myModel?.RefreshSnapshotOnly()
    End Sub

    Private Sub SyncMasterBalance(balFloat As Single)
        If Single.IsNaN(balFloat) OrElse Single.IsInfinity(balFloat) Then balFloat = 0.5F
        Dim clamped As Single = Math.Max(0.0F, Math.Min(1.0F, balFloat))
        If _broker IsNot Nothing Then
            _broker.SetMasterBalance01(clamped, Me)
        Else
            _audioPlayer?.SetBalance(clamped)
            MixerHardwareStateStore.Save(myModel, _audioPlayer)
        End If
    End Sub

    Private Sub SyncMasterVolume(volFloat As Single)
        If Single.IsNaN(volFloat) OrElse Single.IsInfinity(volFloat) Then volFloat = 1.0F
        Dim clamped As Single = Math.Max(0.0F, Math.Min(1.0F, volFloat))
        If _broker IsNot Nothing Then
            _broker.SetMasterVolume01(clamped, Me)
        ElseIf _audioPlayer IsNot Nothing Then
            _audioPlayer.Volume = clamped * 100.0F
            MixerHardwareStateStore.Save(myModel, _audioPlayer)
        Else
            _masterBusRenderer?.SetVolume(CInt(Math.Round(clamped * 100.0F)))
            MixerHardwareStateStore.Save(myModel, _audioPlayer)
        End If
    End Sub

    Private Sub ApplyVolumeToTrackVisualOnly(trackId As Guid, faderValue As Single)
        If myModel?.Tracks Is Nothing Then Return

        Dim finalVolume As Single = Math.Max(0.0F, Math.Min(1.0F, faderValue))
        Dim changed As Boolean = False

        SyncLock myModel.SyncRoot
            Dim track = myModel.Tracks.FirstOrDefault(Function(t) t.Id = trackId)
            If track IsNot Nothing AndAlso track.Clips IsNot Nothing Then
                For Each clip In track.Clips
                    If Math.Abs(clip.Volume - finalVolume) > 0.001F Then
                        clip.Volume = finalVolume
                        changed = True
                    End If
                Next
            End If
        End SyncLock

        If changed Then
            _audioPlayer?.SetTrackVolume(trackId, finalVolume)
            MixerHardwareStateStore.Save(myModel, _audioPlayer)
            myModel.RefreshSnapshotOnly()
        End If
    End Sub

    Protected Overrides Sub OnMouseDown(e As MouseEventArgs)
        MyBase.OnMouseDown(e)
        Try
            If _audioPlayer IsNot Nothing Then
                Dim vx As Single = GetVirtualX(e.X) + mixerScrollX
                Dim colIdx As Integer = CInt(Math.Floor(vx / TRACK_WIDTH))
                Dim audioTracks As List(Of TrackModel) = GetAudioTracksSnapshot()
                If colIdx >= 0 AndAlso colIdx < audioTracks.Count Then
                    _audioPlayer.FocusedTrackId = audioTracks(colIdx).Id
                    SafeInvalidate()
                End If
            End If
        Catch
        End Try

        Dim hit = GetHitElement(e.X, e.Y)

        If hit.StartsWith("MixerColorSelect_") Then
            Dim parts As String() = hit.Split("_"c)
            If parts.Length >= 3 Then
                Dim tId As Guid = Guid.Parse(parts(1))
                Dim selectedMode As Integer = 0
                If Integer.TryParse(parts(2), selectedMode) Then
                    selectedMode = selectedMode And 3
                    Dim st As AudioChannelState = Nothing
                    If audioStates.TryGetValue(tId, st) AndAlso st IsNot Nothing Then
                        st.ColorFxMode = selectedMode
                        ShowOsd(tId, $"FX MODE: {GetColorFxModeName(selectedMode)}")
                        If _broker IsNot Nothing Then
                            _broker.SetTrackColorFxMode(tId, selectedMode, Me)
                        Else
                            If myModel?.Tracks IsNot Nothing Then
                                SyncLock myModel.SyncRoot
                                    Dim track = myModel.Tracks.FirstOrDefault(Function(t) t.Id = tId)
                                    If track IsNot Nothing Then track.ColorFxMode = selectedMode
                                End SyncLock
                            End If
                            _audioPlayer?.SetTrackColorFxMode(tId, selectedMode)
                            MixerHardwareStateStore.Save(myModel, _audioPlayer)
                        End If
                    End If
                End If
            End If
            _fxPopupTrackId = Guid.Empty
            _fxPopupHoverMode = -1
            SafeInvalidate()
            Return
        End If

        If hit.StartsWith("MixerColorMode_") Then
            Dim tId As Guid = Guid.Parse(hit.Replace("MixerColorMode_", ""))
            If _fxPopupTrackId = tId Then
                _fxPopupTrackId = Guid.Empty
            Else
                _fxPopupTrackId = tId
            End If
            _fxPopupHoverMode = -1
            SafeInvalidate()
            Return
        End If

        If _fxPopupTrackId <> Guid.Empty Then
            _fxPopupTrackId = Guid.Empty
            _fxPopupHoverMode = -1
            SafeInvalidate()
        End If

        If e.Button = MouseButtons.Left AndAlso hit <> "" Then
            If hit.StartsWith("MixerMute_") Then
                Dim tId As Guid = Guid.Parse(hit.Replace("MixerMute_", ""))
                Dim st As AudioChannelState = Nothing
                If audioStates.TryGetValue(tId, st) AndAlso st IsNot Nothing Then
                    st.IsMuted = Not st.IsMuted
                    ShowOsd(tId, If(st.IsMuted, "MUTE: ON", "MUTE: OFF"))
                    If _broker IsNot Nothing Then
                        _broker.SetTrackMute(tId, st.IsMuted, Me)
                    Else
                        _audioPlayer?.SetTrackMute(tId, st.IsMuted)
                    End If
                    SafeInvalidate()
                End If
                Return
            ElseIf hit.StartsWith("MixerSolo_") Then
                Dim tId As Guid = Guid.Parse(hit.Replace("MixerSolo_", ""))
                Dim st As AudioChannelState = Nothing
                If audioStates.TryGetValue(tId, st) AndAlso st IsNot Nothing Then
                    st.IsSolo = Not st.IsSolo
                    ShowOsd(tId, If(st.IsSolo, "SOLO: ON", "SOLO: OFF"))
                    If _broker IsNot Nothing Then
                        _broker.SetTrackSolo(tId, st.IsSolo, Me)
                    Else
                        _audioPlayer?.SetTrackSolo(tId, st.IsSolo)
                    End If
                    SafeInvalidate()
                End If
                Return
            End If
        End If

        If e.Button = MouseButtons.Right AndAlso hit = "" Then
            Dim parentMixerForm As AudioMixerForm = TryCast(Me.FindForm(), AudioMixerForm)
            parentMixerForm?.ShowDeviceContextMenu(Me, e.Location)
            Return
        End If

        If e.Button = MouseButtons.Right AndAlso hit <> "" Then
            If hit = "MixerMasterVol" Then
                masterVolume = 1.0F
                SyncMasterVolume(masterVolume)
                ShowOsd(Guid.Empty, $"MASTER: {CInt(Math.Round(masterVolume * 100.0F))}%")
            ElseIf hit = "MixerMasterBal" Then
                masterBalance = 0.5F
                SyncMasterBalance(masterBalance)
                ShowOsd(Guid.Empty, "BAL: CENTER")
            ElseIf hit.StartsWith("MixerVol_") Then
                Dim tId As Guid = Guid.Parse(hit.Replace("MixerVol_", ""))
                Dim st As AudioChannelState = Nothing
                If audioStates.TryGetValue(tId, st) AndAlso st IsNot Nothing Then
                    st.Fader = 1.0F
                    ShowOsd(tId, "FADER: 10.0")
                End If
                If _broker IsNot Nothing Then
                    _broker.SetTrackVolume(tId, 1.0F, Me)
                Else
                    ApplyVolumeToTrackVisualOnly(tId, 1.0F)
                End If
                NotifyTimelineRedraw()
            Else
                Dim tId As Guid = Guid.Empty
                Dim propType As String = ""

                If hit.StartsWith("MixerTrim_") Then
                    tId = Guid.Parse(hit.Replace("MixerTrim_", ""))
                    propType = "Trim"
                ElseIf hit.StartsWith("MixerHi_") Then
                    tId = Guid.Parse(hit.Replace("MixerHi_", ""))
                    propType = "Hi"
                ElseIf hit.StartsWith("MixerMid_") Then
                    tId = Guid.Parse(hit.Replace("MixerMid_", ""))
                    propType = "Mid"
                ElseIf hit.StartsWith("MixerLow_") Then
                    tId = Guid.Parse(hit.Replace("MixerLow_", ""))
                    propType = "Low"
                ElseIf hit.StartsWith("MixerColor_") Then
                    tId = Guid.Parse(hit.Replace("MixerColor_", ""))
                    propType = "Color"
                End If

                If tId <> Guid.Empty Then
                    Dim st As AudioChannelState = Nothing
                    If audioStates.TryGetValue(tId, st) AndAlso st IsNot Nothing Then
                        Select Case propType
                            Case "Trim" : st.Trim = 0.5F : ShowOsd(tId, "TRIM: 0.0 dB")
                            Case "Hi" : st.Hi = 0.5F : ShowOsd(tId, "HI: 0.0 dB")
                            Case "Mid" : st.Mid = 0.5F : ShowOsd(tId, "MID: 0.0 dB")
                            Case "Low" : st.Low = 0.5F : ShowOsd(tId, "LOW: 0.0 dB")
                            Case "Color" : st.ColorFx = 0.5F : ShowOsd(tId, $"{GetColorFxModeName(st.ColorFxMode)}: BYPASS")
                        End Select

                        If _broker IsNot Nothing Then
                            Select Case propType
                                Case "Trim", "Hi", "Mid", "Low"
                                    _broker.SetTrackEQAndTrim(tId, st.Low, st.Mid, st.Hi, st.Trim, Me)
                                Case "Color"
                                    _broker.SetTrackColorFilter(tId, st.ColorFx, Me)
                            End Select
                        Else
                            Select Case propType
                                Case "Trim", "Hi", "Mid", "Low"
                                    SyncTrackEQ(tId, st)
                                Case "Color"
                                    If myModel?.Tracks IsNot Nothing Then
                                        SyncLock myModel.SyncRoot
                                            Dim track = myModel.Tracks.FirstOrDefault(Function(t) t.Id = tId)
                                            If track IsNot Nothing Then track.ColorFx = st.ColorFx
                                        End SyncLock
                                    End If
                                    _audioPlayer?.SetTrackColorFilter(tId, st.ColorFx)
                                    MixerHardwareStateStore.Save(myModel, _audioPlayer)
                                    myModel?.RefreshSnapshotOnly()
                            End Select
                        End If

                        NotifyTimelineRedraw()
                    End If
                End If
            End If
            SafeInvalidate()
            Return
        End If

        If e.Button = MouseButtons.Left AndAlso hit <> "" Then
            draggedProperty = hit
            dragStartMouseY = e.Y
            dragStartMouseX = e.X
            Me.Capture = True
        End If
    End Sub

    Protected Overrides Sub OnMouseUp(e As MouseEventArgs)
        MyBase.OnMouseUp(e)
        If draggedProperty <> "" Then
            draggedProperty = ""
            Me.Capture = False
            myModel?.NotifyPropertiesChanged()
            NotifyTimelineRedraw()
        End If
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If Not isDisposedState Then
            If disposing Then
                If _broker IsNot Nothing Then
                    RemoveHandler _broker.TrackParamChanged, AddressOf OnExternalTrackParamChanged
                    RemoveHandler _broker.MasterParamChanged, AddressOf OnExternalMasterParamChanged
                    _broker = Nothing
                End If

                If myModel IsNot Nothing Then
                    RemoveHandler myModel.StateChanged, AddressOf OnModelStateChanged
                End If

                Try : RemoveHandler ThemeManager.ThemeChanged, AddressOf OnThemeChanged : Catch : End Try
                Try : RemoveHandler SettingsService.ProjectFpsChanged, AddressOf OnProjectFpsChanged : Catch : End Try

                RenderTimer.Stop()
                RenderTimer.Dispose()

                For Each c In _mmfCache.Values
                    c?.Dispose()
                Next
                _mmfCache.Clear()
                _mixerPeakSlices.Clear()

                DiscardDeviceResources()
                SafeRelease(factory2D)
                SafeRelease(factoryDW)

                SafeRelease(fontNormalCenter)
                SafeRelease(fontSmallCenter)
                SafeRelease(fontTinyCenter)
                SafeRelease(fontTinyLeft)
                SafeRelease(fontTinyRight)
                SafeRelease(fontLedCenter)
                SafeRelease(fontLedLeft)
                SafeRelease(fontBadge)

                _audioPlayer = Nothing
                _masterBusRenderer = Nothing
                _dispatcher = Nothing
            End If
            isDisposedState = True
        End If

        MyBase.Dispose(disposing)
    End Sub

End Class
' --- END OF FILE AudioMixerControl.vb ---
