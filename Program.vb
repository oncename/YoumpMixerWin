' --- START OF FILE Program.vb ---
Option Strict On
Option Explicit On

Imports System
Imports System.Diagnostics
Imports System.Threading
Imports System.Windows.Forms
Imports yoump

Module Program
    Private Const ShutdownEventName As String = "Local\YoumpMixerGracefulShutdownEvent"
    Private Const SingleInstanceMutexName As String = "Local\YoumpMixerSingleInstanceLifecycleMutex"

    <STAThread>
    Sub Main()
        ' Сигнализируем предыдущему экземпляру (включая скрытый в трее), чтобы он штатно закрыл MIDI и сохранил config.json
        Try
            Using ev As EventWaitHandle = New EventWaitHandle(False, EventResetMode.ManualReset, ShutdownEventName)
                ev.Set()
            End Using
        Catch
        End Try

        Dim instanceMutex As Mutex = Nothing
        Dim ownsMutex As Boolean = False
        Try
            instanceMutex = New Mutex(False, SingleInstanceMutexName)
            Try
                ownsMutex = instanceMutex.WaitOne(3000)
            Catch ex As AbandonedMutexException
                ownsMutex = True
            End Try
        Catch
        End Try

        If Not ownsMutex Then
            Try
                Using curProc As Process = Process.GetCurrentProcess()
                    For Each p As Process In Process.GetProcessesByName(curProc.ProcessName)
                        If p.Id <> curProc.Id Then
                            Try
                                If p.CloseMainWindow() Then
                                    p.WaitForExit(800)
                                End If
                            Catch
                            End Try
                            Try
                                If Not p.HasExited Then
                                    p.Kill()
                                    p.WaitForExit(500)
                                End If
                            Catch
                            End Try
                        End If
                        p.Dispose()
                    Next
                End Using
            Catch
            End Try

            If instanceMutex IsNot Nothing Then
                Try
                    ownsMutex = instanceMutex.WaitOne(1000)
                Catch ex As AbandonedMutexException
                    ownsMutex = True
                Catch
                End Try
            End If
        End If

        Dim shutdownEvent As EventWaitHandle = Nothing
        Try
            shutdownEvent = New EventWaitHandle(False, EventResetMode.ManualReset, ShutdownEventName)
            shutdownEvent.Reset()
        Catch
        End Try

        Try
            Application.SetHighDpiMode(HighDpiMode.SystemAware)
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)

        ' Загружаем (или создаём при первом запуске) config.json рядом с программой
        Dim savedConfig As MixerHardwareStateStore = MixerHardwareStateStore.Load()
        Dim micProfile As ChannelProfileConfig = MixerHardwareStateStore.GetOrCreateChannelProfile("MAIN MIC")

        Dim model As New ProjectModel() With {
            .TotalDuration = TimeSpan.FromSeconds(60)
        }

        Dim sysTrack As New TrackModel() With {
            .Name = "MAIN MIC",
            .Type = TargetFormatType.Audio,
            .Trim = micProfile.Trim,
            .EqHi = micProfile.EqHi,
            .EqMid = micProfile.EqMid,
            .EqLow = micProfile.EqLow,
            .ColorFx = micProfile.ColorFx,
            .ColorFxMode = micProfile.ColorFxMode,
            .WaveformZoom = micProfile.WaveformZoom,
            .IsMuted = micProfile.IsMuted,
            .IsSolo = micProfile.IsSolo
        }
        sysTrack.Clips.Add(New ClipModel() With {
            .TimelineStart = TimeSpan.Zero,
            .TimelineEnd = TimeSpan.FromSeconds(60),
            .Volume = micProfile.Fader
        })
        model.Tracks.Add(sysTrack)

        Dim playback As New SystemPlaybackController()
        Dim player As New NAudioSyncPlayer(autoStart:=False, model:=model)
        player.ApplyInitialSavedState(savedConfig)

        Dim masterBus As New MasterBusRenderer(player)
        Dim broker As New MediaParameterBroker(model, player)
        Dim dispatcher As New TimelineCommandDispatcher()
        Dim midiController As New DdjSb3MidiController(model, player, masterBus, broker, autoStart:=False) With {
            .CrossfaderControlsBalance = savedConfig.Midi.CrossfaderControlsBalance
        }

        Dim regWaitHandle As RegisteredWaitHandle = Nothing
        Using mainForm As New AudioMixerForm(model, playback, player, masterBus, broker, dispatcher, midiController)
            If shutdownEvent IsNot Nothing Then
                regWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                    shutdownEvent,
                    Sub(state, timedOut)
                        If Not timedOut Then
                            Try
                                If Not mainForm.IsDisposed AndAlso mainForm.IsHandleCreated Then
                                    mainForm.BeginInvoke(New Action(Sub() mainForm.Close()))
                                Else
                                    Application.Exit()
                                End If
                            Catch
                                Try : Application.Exit() : Catch : End Try
                            End Try
                        End If
                    End Sub,
                    Nothing,
                    -1,
                    executeOnlyOnce:=True)
            End If

            Application.Run(mainForm)
        End Using

            MixerHardwareStateStore.FlushNow()
            Try : regWaitHandle?.Unregister(Nothing) : Catch : End Try
        Finally
            Try : shutdownEvent?.Dispose() : Catch : End Try
            If instanceMutex IsNot Nothing Then
                If ownsMutex Then
                    Try : instanceMutex.ReleaseMutex() : Catch : End Try
                End If
                Try : instanceMutex.Dispose() : Catch : End Try
            End If
        End Try
    End Sub
End Module
' --- END OF FILE Program.vb ---