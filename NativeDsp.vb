' --- START OF FILE NativeDsp.vb ---
Option Strict On
Option Explicit On

Imports System
Imports System.IO
Imports System.IO.MemoryMappedFiles
Imports System.Reflection
Imports System.Runtime.InteropServices
Imports System.Security.AccessControl
Imports System.Security.Principal
Imports System.Threading
Imports Microsoft.Win32
Imports NAudio.Wave
Imports yoump.Audio

Namespace yoump.Audio.Native

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Public Structure YoumpMixerParams
        Public trimGain As Single
        Public eqLowDb As Single
        Public eqMidDb As Single
        Public eqHighDb As Single
        Public colorKnob As Single
        Public channelFader As Single
        Public masterVolume As Single
        Public balance As Single
        Public limiterEnabled As Integer

        Public Shared Function Sanitize(ByRef p As YoumpMixerParams) As YoumpMixerParams
            Dim s As YoumpMixerParams = p
            If Single.IsNaN(s.trimGain) OrElse Single.IsInfinity(s.trimGain) Then s.trimGain = 1.0F
            If Single.IsNaN(s.eqLowDb) OrElse Single.IsInfinity(s.eqLowDb) Then s.eqLowDb = 0.0F
            If Single.IsNaN(s.eqMidDb) OrElse Single.IsInfinity(s.eqMidDb) Then s.eqMidDb = 0.0F
            If Single.IsNaN(s.eqHighDb) OrElse Single.IsInfinity(s.eqHighDb) Then s.eqHighDb = 0.0F
            If Single.IsNaN(s.colorKnob) OrElse Single.IsInfinity(s.colorKnob) Then s.colorKnob = 0.5F
            If Single.IsNaN(s.channelFader) OrElse Single.IsInfinity(s.channelFader) Then s.channelFader = 1.0F
            If Single.IsNaN(s.masterVolume) OrElse Single.IsInfinity(s.masterVolume) Then s.masterVolume = 1.0F
            If Single.IsNaN(s.balance) OrElse Single.IsInfinity(s.balance) Then s.balance = 0.5F
            Return s
        End Function

        Public Shared Function AreEqual(ByRef a As YoumpMixerParams, ByRef b As YoumpMixerParams) As Boolean
            Return Math.Abs(a.trimGain - b.trimGain) < 0.0001F AndAlso
                   Math.Abs(a.eqLowDb - b.eqLowDb) < 0.001F AndAlso
                   Math.Abs(a.eqMidDb - b.eqMidDb) < 0.001F AndAlso
                   Math.Abs(a.eqHighDb - b.eqHighDb) < 0.001F AndAlso
                   Math.Abs(a.colorKnob - b.colorKnob) < 0.0001F AndAlso
                   Math.Abs(a.channelFader - b.channelFader) < 0.0001F AndAlso
                   Math.Abs(a.masterVolume - b.masterVolume) < 0.0001F AndAlso
                   Math.Abs(a.balance - b.balance) < 0.0001F AndAlso
                   a.limiterEnabled = b.limiterEnabled
        End Function
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Public Structure YoumpMixerMeters
        Public channelPeakPfl As Single
        Public masterPeakL As Single
        Public masterPeakR As Single
    End Structure

    Public NotInheritable Class NativeCppAutoBuilderAndInstaller
        Private Const ShmMagic As UInteger = &H594D5034UI
        Private Const ShmViewSize As Integer = 4096
        Private Const ShmCustomIrMaxTaps As Integer = 512
        Private Shared ReadOnly _shmDir As String = "C:\ProgramData\Yoump"
        Private Shared ReadOnly _shmFilePath As String = "C:\ProgramData\Yoump\yoump_dsp_shm_v4.bin"

        Private Shared _shmFileStream As FileStream = Nothing
        Private Shared _mmf As MemoryMappedFile = Nothing
        Private Shared _accessor As MemoryMappedViewAccessor = Nothing
        Private Shared ReadOnly _shmLock As New Object()
        Private Shared _autoBuildStarted As Integer = 0
        Private Shared _lastObservedHeartbeat As UInteger = 0UI
        Private Shared _lastHeartbeatChangeTicks As Long = 0L
        Private Shared _lastObservedMicHeartbeat As Integer = 0
        Private Shared _lastMicHeartbeatChangeTicks As Long = 0L
        Private Shared _customIrVersionCounter As UInteger = 1UI

        <DllImport("kernel32.dll", ExactSpelling:=True)>
        Private Shared Function GetTickCount64() As ULong
        End Function

        Private Sub New()
        End Sub

        Public Shared Sub ResetSharedMemoryToNeutral()
            EnsureSharedMemoryInitialized()
            SyncLock _shmLock
                Dim acc As MemoryMappedViewAccessor = _accessor
                If acc Is Nothing Then Return
                Try
                    acc.Write(8, 1.0F)   ' trimGain = 1.0 (0 dB)
                    acc.Write(12, 0.0F)  ' eqLowDb = 0.0
                    acc.Write(16, 0.0F)  ' eqMidDb = 0.0
                    acc.Write(20, 0.0F)  ' eqHighDb = 0.0
                    acc.Write(24, 0.5F)  ' colorKnob = 0.5 (Bypass)
                    acc.Write(28, 1.0F)  ' channelFader = 1.0
                    acc.Write(32, 1.0F)  ' masterVolume = 1.0
                    acc.Write(36, 0.5F)  ' balance = 0.5
                    acc.Write(40, 1)     ' limiterEnabled = 1 (без бита Mute 0x10)
                    acc.Write(72, 0UL)   ' lastApoProcessTick = 0 (штатное отключение приложения)
                    acc.Write(80, CByte(0))
                    acc.Write(128, 0)    ' channelCount = 0
                    acc.Write(840, 0)    ' micIrPreset = 0
                    acc.Write(844, 1.0F) ' micIrMix = 1.0
                    acc.Write(2908, 3)   ' micExtraFlags = 3

                    For c As Integer = 0 To 15
                        acc.Write(136 + (c * 4), 0.0F)
                        acc.Write(200 + (c * 4), 0.0F)
                        Dim baseOff As Integer = 264 + (c * 36)
                        acc.Write(baseOff + 0, 1.0F)
                        acc.Write(baseOff + 4, 0.0F)
                        acc.Write(baseOff + 8, 0.0F)
                        acc.Write(baseOff + 12, 0.0F)
                        acc.Write(baseOff + 16, 0.5F)
                        acc.Write(baseOff + 20, 1.0F)
                        acc.Write(baseOff + 24, 1.0F)
                        acc.Write(baseOff + 28, 0.5F)
                        acc.Write(baseOff + 32, 1)
                    Next
                    Thread.MemoryBarrier()
                    acc.Write(0, ShmMagic)
                    acc.Flush()
                    _hasWrittenParamsOnce = False
                Catch
                End Try
            End SyncLock
        End Sub

        Private Shared Sub GrantLpacFileAccess(path As String, isDirectory As Boolean)
            Try
                Dim sids As String() = {"S-1-1-0", "S-1-5-19", "S-1-15-2-1", "S-1-15-2-2"}
                If isDirectory Then
                    Dim dirInfo As New DirectoryInfo(path)
                    Dim dirSec As DirectorySecurity = dirInfo.GetAccessControl()
                    For Each sidStr As String In sids
                        Try
                            Dim sid As New SecurityIdentifier(sidStr)
                            dirSec.AddAccessRule(New FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit Or InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow))
                        Catch
                        End Try
                    Next
                    dirInfo.SetAccessControl(dirSec)
                Else
                    Dim fileInfo As New FileInfo(path)
                    Dim fileSec As FileSecurity = fileInfo.GetAccessControl()
                    For Each sidStr As String In sids
                        Try
                            Dim sid As New SecurityIdentifier(sidStr)
                            fileSec.AddAccessRule(New FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow))
                        Catch
                        End Try
                    Next
                    fileInfo.SetAccessControl(fileSec)
                End If
            Catch
            End Try
        End Sub

        Public Shared Sub EnsureSharedMemoryInitialized()
            If _accessor IsNot Nothing Then Return
            SyncLock _shmLock
                If _accessor IsNot Nothing Then Return
                Dim fs As FileStream = Nothing
                Try
                    If Not Directory.Exists(_shmDir) Then
                        Directory.CreateDirectory(_shmDir)
                    End If
                    GrantLpacFileAccess(_shmDir, isDirectory:=True)

                    fs = New FileStream(_shmFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite Or FileShare.Delete)
                    If fs.Length < ShmViewSize Then
                        fs.SetLength(ShmViewSize)
                    End If
                    GrantLpacFileAccess(_shmFilePath, isDirectory:=False)

                    Try
                        _mmf = MemoryMappedFile.CreateFromFile(fs, "Local\YoumpDspSharedMemV4", ShmViewSize, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, True)
                    Catch
                        If fs Is Nothing OrElse Not fs.CanRead Then
                            fs = New FileStream(_shmFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite Or FileShare.Delete)
                        End If
                        _mmf = MemoryMappedFile.CreateFromFile(fs, Nothing, ShmViewSize, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, True)
                    End Try

                    _shmFileStream = fs
                    _accessor = _mmf.CreateViewAccessor(0, ShmViewSize, MemoryMappedFileAccess.ReadWrite)

                    Dim curMagic As UInteger = _accessor.ReadUInt32(0)
                    If curMagic <> ShmMagic Then
                        _accessor.Write(4, 0UI)
                        _accessor.Write(8, 1.0F)
                        _accessor.Write(12, 0.0F)
                        _accessor.Write(16, 0.0F)
                        _accessor.Write(20, 0.0F)
                        _accessor.Write(24, 0.5F)
                        _accessor.Write(28, 1.0F)
                        _accessor.Write(32, 1.0F)
                        _accessor.Write(36, 0.5F)
                        _accessor.Write(40, 1)
                        _accessor.Write(56, 0.0F)
                        _accessor.Write(60, 1.0F)
                        _accessor.Write(72, GetTickCount64())
                        _accessor.Write(80, CByte(0))
                        _accessor.Write(840, 0)
                        _accessor.Write(844, 1.0F)
                        _accessor.Write(848, 0UI)
                        _accessor.Write(852, 0)
                        _accessor.Write(2904, 0.0045F)
                        _accessor.Write(2908, 3)
                        Thread.MemoryBarrier()
                        _accessor.Write(0, ShmMagic)
                        _accessor.Flush()
                    Else
                        _accessor.Write(72, GetTickCount64())
                    End If
                Catch
                    If _mmf Is Nothing AndAlso fs IsNot Nothing Then
                        Try : fs.Dispose() : Catch : End Try
                    End If
                End Try
            End SyncLock
        End Sub

        Public Shared Sub WriteMicConvolutionIrParams(presetId As Integer,
                                                      mix01 As Single,
                                                      Optional gateThreshold As Single = -1.0F,
                                                      Optional micExtraFlags As Integer = -1)
            EnsureSharedMemoryInitialized()
            SyncLock _shmLock
                Dim acc As MemoryMappedViewAccessor = _accessor
                If acc Is Nothing Then Return
                Try
                    Dim audioCfg = MixerHardwareStateStore.Current.Audio
                    Dim safePreset As Integer = Math.Max(0, Math.Min(7, presetId))
                    Dim safeMix As Single = If(Single.IsNaN(mix01) OrElse Single.IsInfinity(mix01), 1.0F, Math.Max(0.0F, Math.Min(1.0F, mix01)))
                    Dim rawGate As Single = If(gateThreshold >= 0.0F, gateThreshold, audioCfg.MicNoiseGateThreshold)
                    If Single.IsNaN(rawGate) OrElse Single.IsInfinity(rawGate) Then rawGate = 0.0045F
                    Dim safeGate As Single = Math.Max(0.0F, Math.Min(0.2F, rawGate))
                    Dim safeExtra As Integer = If(micExtraFlags >= 0,
                                                  micExtraFlags,
                                                  audioCfg.GetPackedMicExtraFlags())
                    acc.Write(72, GetTickCount64())
                    acc.Write(840, safePreset)
                    acc.Write(844, safeMix)
                    acc.Write(2904, safeGate)
                    acc.Write(2908, safeExtra)
                Catch
                End Try
            End SyncLock
        End Sub

        Public Shared Function LoadAndUploadCustomWavIr(wavFilePath As String, Optional targetSampleRate As Integer = 48000) As Boolean
            If String.IsNullOrWhiteSpace(wavFilePath) OrElse Not File.Exists(wavFilePath) Then Return False
            EnsureSharedMemoryInitialized()

            Try
                Dim safeTargetSr As Integer = If(targetSampleRate >= 8000, targetSampleRate, 48000)
                Dim taps(ShmCustomIrMaxTaps - 1) As Single
                Dim readTaps As Integer = 0

                Using reader As New AudioFileReader(wavFilePath)
                    Dim provider As ISampleProvider = reader
                    If reader.WaveFormat.SampleRate <> safeTargetSr Then
                        provider = New NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(reader, safeTargetSr)
                    End If

                    Dim chCount As Integer = Math.Max(1, provider.WaveFormat.Channels)
                    Dim tempBuf(chCount * 1024 - 1) As Single
                    Dim totalSamplesRead As Integer = 0

                    While totalSamplesRead < tempBuf.Length
                        Dim chunk As Integer = provider.Read(tempBuf, totalSamplesRead, tempBuf.Length - totalSamplesRead)
                        If chunk <= 0 Then Exit While
                        totalSamplesRead += chunk
                    End While

                    Dim totalFrames As Integer = totalSamplesRead \ chCount
                    readTaps = Math.Min(ShmCustomIrMaxTaps, totalFrames)

                    For i As Integer = 0 To readTaps - 1
                        Dim mono As Single = tempBuf(i * chCount)
                        If chCount >= 2 Then
                            mono = (mono + tempBuf(i * chCount + 1)) * 0.5F
                        End If
                        If Single.IsNaN(mono) OrElse Single.IsInfinity(mono) Then mono = 0.0F
                        taps(i) = mono
                    Next
                End Using

                If readTaps <= 0 Then Return False

                Dim sumSq As Double = 0.0
                For i As Integer = 0 To readTaps - 1
                    sumSq += CDbl(taps(i)) * CDbl(taps(i))
                Next
                Dim normFactor As Single = If(sumSq > 0.000001, CSng(1.0 / Math.Sqrt(sumSq)), 1.0F)
                normFactor = Math.Max(0.05F, Math.Min(8.0F, normFactor))

                SyncLock _shmLock
                    Dim acc As MemoryMappedViewAccessor = _accessor
                    If acc Is Nothing Then Return False

                    ' Двухфазная запись SeqLock: сначала меняем версию на промежуточную и сбрасываем длину в 0,
                    ' чтобы C++ поток в audiodg.exe не прочитал частично записанный массив коэффициентов
                    Dim shmCurVer As UInteger = acc.ReadUInt32(848)
                    Dim writeVer As UInteger = Math.Max(_customIrVersionCounter + 1UI, shmCurVer + 1UI)
                    If (writeVer And 1UI) = 0UI Then writeVer += 1UI

                    acc.Write(852, 0)
                    acc.Write(848, writeVer)
                    Thread.MemoryBarrier()

                    For i As Integer = 0 To ShmCustomIrMaxTaps - 1
                        Dim v As Single = If(i < readTaps, taps(i) * normFactor, 0.0F)
                        If Single.IsNaN(v) OrElse Single.IsInfinity(v) Then v = 0.0F
                        acc.Write(856 + (i * 4), v)
                    Next

                    Thread.MemoryBarrier()
                    acc.Write(852, readTaps)
                    Thread.MemoryBarrier()

                    Dim finalVer As UInteger = writeVer + 1UI
                    If finalVer = 0UI Then finalVer = 2UI
                    _customIrVersionCounter = finalVer

                    acc.Write(848, finalVer)
                    acc.Write(840, 7)
                    acc.Write(72, GetTickCount64())
                    acc.Flush()
                End SyncLock
                Return True
            Catch
                Return False
            End Try
        End Function

        Private Shared _lastWrittenParams As YoumpMixerParams
        Private Shared _hasWrittenParamsOnce As Boolean = False
        Private Shared _pendingRegParams As YoumpMixerParams
        Private Shared _regSyncScheduled As Integer = 0
        Private Shared _hklmWriteAllowed As Integer = 1
        Private Shared ReadOnly _regSyncLock As New Object()

        Public Shared Function WriteSharedMixerParams(ByRef p As YoumpMixerParams) As Boolean
            Dim safeParams As YoumpMixerParams = YoumpMixerParams.Sanitize(p)
            safeParams.channelFader = 1.0F
            safeParams.masterVolume = 1.0F
            If safeParams.balance >= -0.5F Then
                safeParams.balance = 0.5F
            End If

            EnsureSharedMemoryInitialized()

            SyncLock _shmLock
                Dim acc As MemoryMappedViewAccessor = _accessor
                If acc IsNot Nothing Then
                    Try
                        acc.Write(72, GetTickCount64())
                    Catch
                    End Try
                End If

                If _hasWrittenParamsOnce AndAlso YoumpMixerParams.AreEqual(safeParams, _lastWrittenParams) Then
                    Return False
                End If
                _lastWrittenParams = safeParams
                _hasWrittenParamsOnce = True

                If acc IsNot Nothing Then
                    Try
                        acc.Write(0, ShmMagic)
                        acc.Write(8, safeParams.trimGain)
                        acc.Write(12, safeParams.eqLowDb)
                        acc.Write(16, safeParams.eqMidDb)
                        acc.Write(20, safeParams.eqHighDb)
                        acc.Write(24, safeParams.colorKnob)
                        acc.Write(28, safeParams.channelFader)
                        acc.Write(32, safeParams.masterVolume)
                        acc.Write(36, safeParams.balance)
                        acc.Write(40, safeParams.limiterEnabled)
                        acc.Write(80, CByte(0))
                    Catch
                    End Try
                End If
            End SyncLock

            SyncLock _regSyncLock
                _pendingRegParams = safeParams
            End SyncLock

            If Interlocked.CompareExchange(_regSyncScheduled, 1, 0) = 0 Then
                ThreadPool.QueueUserWorkItem(AddressOf FlushDeferredRegistrySync)
            End If
            Return True
        End Function

        Private Shared Sub FlushDeferredRegistrySync(state As Object)
            Try
                Do
                    Thread.Sleep(150)
                    Dim snap As YoumpMixerParams
                    SyncLock _regSyncLock
                        snap = _pendingRegParams
                    End SyncLock

                    Dim bytes(35) As Byte
                    Dim floats As Single() = {
                        snap.trimGain, snap.eqLowDb, snap.eqMidDb, snap.eqHighDb,
                        snap.colorKnob, snap.channelFader, snap.masterVolume, snap.balance
                    }
                    Buffer.BlockCopy(floats, 0, bytes, 0, 32)
                    Buffer.BlockCopy(BitConverter.GetBytes(snap.limiterEnabled), 0, bytes, 32, 4)

                    Dim wroteToHklm As Boolean = False
                    If Volatile.Read(_hklmWriteAllowed) = 1 Then
                        Try
                            Using k As RegistryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\Yoump\DSP", writable:=True)
                                Dim targetKey As RegistryKey = If(k, Registry.LocalMachine.CreateSubKey("SOFTWARE\Yoump\DSP", writable:=True))
                                If targetKey IsNot Nothing Then
                                    Using targetKey
                                        targetKey.SetValue("MixerParams", bytes, RegistryValueKind.Binary)
                                        wroteToHklm = True
                                    End Using
                                End If
                            End Using
                        Catch ex As UnauthorizedAccessException
                            Interlocked.Exchange(_hklmWriteAllowed, 0)
                        Catch ex As System.Security.SecurityException
                            Interlocked.Exchange(_hklmWriteAllowed, 0)
                        Catch
                        End Try
                    End If

                    If Not wroteToHklm Then
                        Try
                            Using kCu As RegistryKey = Registry.CurrentUser.CreateSubKey("SOFTWARE\Yoump\DSP", writable:=True)
                                kCu?.SetValue("MixerParams", bytes, RegistryValueKind.Binary)
                            End Using
                        Catch
                        End Try
                    End If

                    SyncLock _regSyncLock
                        If YoumpMixerParams.AreEqual(snap, _pendingRegParams) Then
                            Exit Do
                        End If
                    End SyncLock
                Loop
            Finally
                Interlocked.Exchange(_regSyncScheduled, 0)
            End Try
        End Sub

        Public Shared Sub WriteMultiChannelTable(channelCount As Integer,
                                                 rawPeaks() As Single,
                                                 attenPeaks() As Single,
                                                 channelParams() As YoumpMixerParams)
            EnsureSharedMemoryInitialized()
            SyncLock _shmLock
                Dim acc As MemoryMappedViewAccessor = _accessor
                If acc Is Nothing Then Return
                Try
                    Dim safeCount As Integer = Math.Max(0, Math.Min(16, channelCount))
                    Dim prevCount As Integer = Math.Max(0, Math.Min(16, acc.ReadInt32(128)))

                    If safeCount < prevCount Then
                        acc.Write(128, safeCount)
                        Thread.MemoryBarrier()
                    End If

                    For c As Integer = 0 To safeCount - 1
                        Dim rPk As Single = If(rawPeaks IsNot Nothing AndAlso c < rawPeaks.Length, rawPeaks(c), 0.0F)
                        Dim aPk As Single = If(attenPeaks IsNot Nothing AndAlso c < attenPeaks.Length, attenPeaks(c), 0.0F)
                        If Single.IsNaN(rPk) OrElse Single.IsInfinity(rPk) Then rPk = 0.0F
                        If Single.IsNaN(aPk) OrElse Single.IsInfinity(aPk) Then aPk = 0.0F

                        acc.Write(136 + (c * 4), rPk)
                        acc.Write(200 + (c * 4), aPk)

                        If channelParams IsNot Nothing AndAlso c < channelParams.Length Then
                            Dim baseOff As Integer = 264 + (c * 36)
                            Dim cp As YoumpMixerParams = YoumpMixerParams.Sanitize(channelParams(c))
                            acc.Write(baseOff + 0, cp.trimGain)
                            acc.Write(baseOff + 4, cp.eqLowDb)
                            acc.Write(baseOff + 8, cp.eqMidDb)
                            acc.Write(baseOff + 12, cp.eqHighDb)
                            acc.Write(baseOff + 16, cp.colorKnob)
                            acc.Write(baseOff + 20, cp.channelFader)
                            acc.Write(baseOff + 24, cp.masterVolume)
                            acc.Write(baseOff + 28, cp.balance)
                            acc.Write(baseOff + 32, cp.limiterEnabled)
                        End If
                    Next

                    Thread.MemoryBarrier()
                    acc.Write(128, safeCount)
                    acc.Write(72, GetTickCount64())
                    acc.Write(80, CByte(0))
                Catch
                End Try
            End SyncLock
        End Sub

        Public Shared Function IsKernelApoProcessingAudio() As Boolean
            EnsureSharedMemoryInitialized()
            SyncLock _shmLock
                Dim acc As MemoryMappedViewAccessor = _accessor
                If acc Is Nothing Then Return False
                Try
                    Dim hb As UInteger = acc.ReadUInt32(4)
                    Dim nowTicks As Long = Environment.TickCount64
                    If hb <> _lastObservedHeartbeat AndAlso hb > 0UI Then
                        _lastObservedHeartbeat = hb
                        _lastHeartbeatChangeTicks = nowTicks
                        Return True
                    End If
                    Return (_lastHeartbeatChangeTicks > 0L AndAlso (nowTicks - _lastHeartbeatChangeTicks) <= 1200L)
                Catch
                    Return False
                End Try
            End SyncLock
        End Function

        Public Shared Sub TouchMicHeartbeat()
            EnsureSharedMemoryInitialized()
            SyncLock _shmLock
                Dim acc As MemoryMappedViewAccessor = _accessor
                If acc Is Nothing Then Return
                Try
                    Dim cur As Integer = acc.ReadInt32(132)
                    acc.Write(132, cur + 1)
                Catch
                End Try
            End SyncLock
        End Sub

        Public Shared Function IsKernelMicApoProcessingAudio() As Boolean
            EnsureSharedMemoryInitialized()
            SyncLock _shmLock
                Dim acc As MemoryMappedViewAccessor = _accessor
                If acc Is Nothing Then Return False
                Try
                    Dim hb As Integer = acc.ReadInt32(132)
                    Dim nowTicks As Long = Environment.TickCount64
                    If hb <> _lastObservedMicHeartbeat AndAlso hb <> 0 Then
                        _lastObservedMicHeartbeat = hb
                        _lastMicHeartbeatChangeTicks = nowTicks
                        Return True
                    End If
                    Return (_lastMicHeartbeatChangeTicks > 0L AndAlso (nowTicks - _lastMicHeartbeatChangeTicks) <= 1200L)
                Catch
                    Return False
                End Try
            End SyncLock
        End Function

        Public Shared Function FindProjectRootDirectory() As String
            Dim customRoot As String = MixerHardwareStateStore.GetCustomParam("NativeRootDirectory", "")
            If Not String.IsNullOrWhiteSpace(customRoot) AndAlso Directory.Exists(customRoot) Then
                Return customRoot
            End If

            Dim searchDir As DirectoryInfo = New DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory)
            For depth As Integer = 0 To 6
                If searchDir Is Nothing Then Exit For
                If Directory.Exists(Path.Combine(searchDir.FullName, "Yoump.DSP")) AndAlso
                   Directory.Exists(Path.Combine(searchDir.FullName, "Yoump.APO")) Then
                    Return searchDir.FullName
                End If
                searchDir = searchDir.Parent
            Next
            Return AppDomain.CurrentDomain.BaseDirectory
        End Function

        Public Shared Sub EnsureAutoCompiledAsync()
            EnsureSharedMemoryInitialized()
            If Interlocked.CompareExchange(_autoBuildStarted, 1, 0) <> 0 Then Return

            ThreadPool.QueueUserWorkItem(
                Sub()
                    Try
                        Dim baseDir As String = AppDomain.CurrentDomain.BaseDirectory
                        Dim rootDir As String = FindProjectRootDirectory()

                        Dim localDsp As String = Path.Combine(baseDir, "Yoump.DSP.dll")
                        Dim localApo As String = Path.Combine(baseDir, "Yoump.APO.dll")

                        If Not String.IsNullOrEmpty(rootDir) Then
                            Dim builtDsp As String = Path.Combine(rootDir, "build", "Yoump.DSP.dll")
                            Dim builtApo As String = Path.Combine(rootDir, "build", "Yoump.APO.dll")

                            If File.Exists(builtDsp) AndAlso Not String.Equals(builtDsp, localDsp, StringComparison.OrdinalIgnoreCase) Then
                                Try : File.Copy(builtDsp, localDsp, True) : Catch : End Try
                            End If
                            If File.Exists(builtApo) AndAlso Not String.Equals(builtApo, localApo, StringComparison.OrdinalIgnoreCase) Then
                                Try : File.Copy(builtApo, localApo, True) : Catch : End Try
                            End If
                        End If
                    Catch
                    End Try
                End Sub)
        End Sub
    End Class

    Public Module NativeDspMethods
        Private Const DspDllName As String = "Yoump.DSP.dll"

        Sub New()
            Try
                NativeCppAutoBuilderAndInstaller.EnsureSharedMemoryInitialized()
                NativeLibrary.SetDllImportResolver(GetType(NativeDspMethods).Assembly, AddressOf ResolveYoumpDspLibrary)
            Catch
            End Try
        End Sub

        Private Function ResolveYoumpDspLibrary(libraryName As String, assembly As Assembly, searchPath As DllImportSearchPath?) As IntPtr
            If String.Equals(libraryName, DspDllName, StringComparison.OrdinalIgnoreCase) Then
                Try
                    Dim baseDir As String = AppDomain.CurrentDomain.BaseDirectory
                    Dim localCandidate As String = Path.Combine(baseDir, "Yoump.DSP.dll")
                    If File.Exists(localCandidate) Then
                        Dim hLocal As IntPtr = IntPtr.Zero
                        If NativeLibrary.TryLoad(localCandidate, hLocal) Then Return hLocal
                    End If

                    Dim rootDir As String = NativeCppAutoBuilderAndInstaller.FindProjectRootDirectory()
                    Dim buildCand As String = Path.Combine(rootDir, "build", "Yoump.DSP.dll")
                    If File.Exists(buildCand) Then
                        Dim hBuild As IntPtr = IntPtr.Zero
                        If NativeLibrary.TryLoad(buildCand, hBuild) Then Return hBuild
                    End If

                    Dim sys32Path As String = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Yoump.DSP.dll")
                    If File.Exists(sys32Path) Then
                        Dim hSys As IntPtr = IntPtr.Zero
                        If NativeLibrary.TryLoad(sys32Path, hSys) Then Return hSys
                    End If
                Catch
                End Try
            End If
            Return IntPtr.Zero
        End Function

        <DllImport(DspDllName, CallingConvention:=CallingConvention.Cdecl, EntryPoint:="YoumpDsp_GetVersion")>
        Public Function GetVersion() As Integer
        End Function

        <DllImport(DspDllName, CallingConvention:=CallingConvention.Cdecl, EntryPoint:="YoumpDsp_SetGainDb")>
        Public Sub SetGainDb(gainDb As Single)
        End Sub

        <DllImport(DspDllName, CallingConvention:=CallingConvention.Cdecl, EntryPoint:="YoumpDsp_ProcessCurrentGain")>
        Public Function ProcessCurrentGain(
            <[In], Out> samples As Single(),
            frameCount As Integer,
            channels As Integer
        ) As Integer
        End Function

        <DllImport(DspDllName, CallingConvention:=CallingConvention.Cdecl, EntryPoint:="YoumpDsp_InitMixer")>
        Public Sub InitMixer(sampleRate As Integer)
        End Sub

        <DllImport(DspDllName, CallingConvention:=CallingConvention.Cdecl, EntryPoint:="YoumpDsp_SetMixerParams")>
        Public Sub SetMixerParams(ByRef params As YoumpMixerParams)
        End Sub

        <DllImport(DspDllName, CallingConvention:=CallingConvention.Cdecl, EntryPoint:="YoumpDsp_GetMixerMeters")>
        Public Sub GetMixerMeters(ByRef meters As YoumpMixerMeters)
        End Sub

        <DllImport(DspDllName, CallingConvention:=CallingConvention.Cdecl, EntryPoint:="YoumpDsp_ProcessMixer")>
        Public Function ProcessMixer(
            <[In], Out> samples As Single(),
            frameCount As Integer,
            channels As Integer
        ) As Integer
        End Function

        <DllImport(DspDllName, CallingConvention:=CallingConvention.Cdecl, EntryPoint:="YoumpDsp_ProcessChannel")>
        Public Function ProcessChannel(
            channelIndex As Integer,
            <[In], Out> samples As Single(),
            frameCount As Integer,
            channels As Integer,
            ByRef params As YoumpMixerParams,
            ByRef outMeters As YoumpMixerMeters
        ) As Integer
        End Function

        <DllImport(DspDllName, CallingConvention:=CallingConvention.Cdecl, EntryPoint:="YoumpDsp_InstanceHasActiveOutput")>
        Public Function InstanceHasActiveOutput(instanceId As Integer) As Integer
        End Function
    End Module

    Public Class NativeMixerEffect
        Implements IAudioEffect

        Public ReadOnly Property Name As String Implements IAudioEffect.Name
            Get
                Return "Yoump Native C++ DJ Mixer Engine"
            End Get
        End Property

        Public Property IsEnabled As Boolean = True Implements IAudioEffect.IsEnabled

        Private _isAvailable As Boolean = False
        Private _version As Integer = 0
        Private _sampleRate As Integer = 48000
        Private _channels As Integer = 2
        Private _params As YoumpMixerParams
        Private _scratchBuffer As Single() = Array.Empty(Of Single)()

        Public Sub New()
            _params.trimGain = 1.0F
            _params.eqLowDb = 0.0F
            _params.eqMidDb = 0.0F
            _params.eqHighDb = 0.0F
            _params.colorKnob = 0.5F
            _params.channelFader = 1.0F
            _params.masterVolume = 1.0F
            _params.balance = 0.5F
            _params.limiterEnabled = 1

            NativeCppAutoBuilderAndInstaller.EnsureAutoCompiledAsync()

            Try
                _version = NativeDspMethods.GetVersion()
                _isAvailable = (_version >= 2)
            Catch
                _isAvailable = False
            End Try
        End Sub

        Public ReadOnly Property IsAvailable As Boolean
            Get
                Return _isAvailable
            End Get
        End Property

        Public ReadOnly Property SupportsMultiChannel As Boolean
            Get
                Return (_version >= 3)
            End Get
        End Property

        Public ReadOnly Property IsKernelApoActive As Boolean
            Get
                Return NativeCppAutoBuilderAndInstaller.IsKernelApoProcessingAudio()
            End Get
        End Property

        Private _lastFullParams As YoumpMixerParams
        Private _hasSetFullParamsOnce As Boolean = False

        Public Sub SetParameters(trim As Single, lowDb As Single, midDb As Single, hiDb As Single,
                                 colorKnob As Single, fader As Single, masterVol As Single,
                                 balance As Single, limiter As Boolean,
                                 Optional packedFlagsOverride As Integer = -1)
            _params.trimGain = trim
            _params.eqLowDb = lowDb
            _params.eqMidDb = midDb
            _params.eqHighDb = hiDb
            _params.colorKnob = colorKnob
            _params.channelFader = fader
            _params.masterVolume = masterVol
            _params.balance = balance
            _params.limiterEnabled = If(packedFlagsOverride >= 0, packedFlagsOverride, If(limiter, 1, 0))
            _params = YoumpMixerParams.Sanitize(_params)

            Dim fullChanged As Boolean = (Not _hasSetFullParamsOnce) OrElse (Not YoumpMixerParams.AreEqual(_params, _lastFullParams))
            _lastFullParams = _params
            _hasSetFullParamsOnce = True

            Dim shmChanged As Boolean = NativeCppAutoBuilderAndInstaller.WriteSharedMixerParams(_params)

            If (fullChanged OrElse shmChanged) AndAlso _isAvailable AndAlso Not IsKernelApoActive Then
                Try : NativeDspMethods.SetMixerParams(_params) : Catch : End Try
            End If
        End Sub

        Public Function GetMeters() As YoumpMixerMeters
            Dim m As YoumpMixerMeters = Nothing
            If _isAvailable Then
                Try : NativeDspMethods.GetMixerMeters(m) : Catch : End Try
            End If
            Return m
        End Function

        Public Sub Init(sampleRate As Integer, channels As Integer) Implements IAudioEffect.Init
            _sampleRate = sampleRate
            _channels = channels
            NativeCppAutoBuilderAndInstaller.WriteSharedMixerParams(_params)
            If _isAvailable Then
                Try
                    NativeDspMethods.InitMixer(sampleRate)
                    NativeDspMethods.SetMixerParams(_params)
                Catch
                End Try
            End If
        End Sub

        Public Function Process(buffer() As Single, offset As Integer, count As Integer) As Integer Implements IAudioEffect.Process
            If Not _isAvailable OrElse Not IsEnabled OrElse buffer Is Nothing OrElse count <= 0 Then
                Return count
            End If

            If _channels < 1 OrElse _channels > 8 Then
                Return count
            End If

            Dim frameCount As Integer = count \ _channels
            If frameCount <= 0 Then Return count

            If offset = 0 Then
                NativeDspMethods.ProcessMixer(buffer, frameCount, _channels)
            Else
                If _scratchBuffer.Length < count Then
                    _scratchBuffer = New Single(Math.Max(count, 4096) - 1) {}
                End If
                Array.Copy(buffer, offset, _scratchBuffer, 0, count)
                NativeDspMethods.ProcessMixer(_scratchBuffer, frameCount, _channels)
                Array.Copy(_scratchBuffer, 0, buffer, offset, count)
            End If

            Return count
        End Function
    End Class

    Public Class NativeGainEffect
        Implements IAudioEffect

        Public ReadOnly Property Name As String Implements IAudioEffect.Name
            Get
                Return "Yoump Native DSP Gain"
            End Get
        End Property

        Public Property IsEnabled As Boolean = True Implements IAudioEffect.IsEnabled

        Private _gainDb As Single = 0.0F
        Private _sampleRate As Integer = 48000
        Private _channels As Integer = 2
        Private _isAvailable As Boolean = False
        Private _scratchBuffer As Single() = Array.Empty(Of Single)()

        Public Sub New()
            Try
                Dim ver As Integer = NativeDspMethods.GetVersion()
                _isAvailable = (ver > 0)
            Catch
                _isAvailable = False
            End Try
        End Sub

        Public ReadOnly Property IsAvailable As Boolean
            Get
                Return _isAvailable
            End Get
        End Property

        Public Property GainDb As Single
            Get
                Return _gainDb
            End Get
            Set(value As Single)
                If Single.IsNaN(value) OrElse Single.IsInfinity(value) Then value = 0.0F
                _gainDb = value
                If _isAvailable Then
                    NativeDspMethods.SetGainDb(value)
                End If
            End Set
        End Property

        Public Sub Init(sampleRate As Integer, channels As Integer) Implements IAudioEffect.Init
            _sampleRate = sampleRate
            _channels = channels
            If _isAvailable Then
                NativeDspMethods.SetGainDb(_gainDb)
            End If
        End Sub

        Public Function Process(buffer() As Single, offset As Integer, count As Integer) As Integer Implements IAudioEffect.Process
            If Not _isAvailable OrElse Not IsEnabled OrElse buffer Is Nothing OrElse count <= 0 Then
                Return count
            End If

            If _channels < 1 OrElse _channels > 8 Then
                Return count
            End If

            Dim frameCount As Integer = count \ _channels
            If frameCount <= 0 Then Return count

            If offset = 0 Then
                NativeDspMethods.ProcessCurrentGain(buffer, frameCount, _channels)
            Else
                If _scratchBuffer.Length < count Then
                    _scratchBuffer = New Single(Math.Max(count, 4096) - 1) {}
                End If
                Array.Copy(buffer, offset, _scratchBuffer, 0, count)
                NativeDspMethods.ProcessCurrentGain(_scratchBuffer, frameCount, _channels)
                Array.Copy(_scratchBuffer, 0, buffer, offset, count)
            End If

            Return count
        End Function
    End Class

End Namespace
' --- END OF FILE NativeDsp.vb ---