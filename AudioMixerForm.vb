' --- START OF FILE AudioMixerForm.vb ---
Option Strict On
Option Explicit On

Imports System
Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports yoump.IServices

Public Class AudioMixerForm
    Inherits Form

    <DllImport("user32.dll", ExactSpelling:=True, SetLastError:=True)>
    Private Shared Function DestroyIcon(hIcon As IntPtr) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    Private ReadOnly _model As ProjectModel
    Private ReadOnly mixerControl As AudioMixerControl
    Private ReadOnly _audioPlayer As NAudioSyncPlayer
    Private ReadOnly _midiController As DdjSb3MidiController
    Private ReadOnly _broker As yoump.MediaParameterBroker
    Private ReadOnly _deviceMenu As New ContextMenuStrip()
    Private ReadOnly _trayIcon As New NotifyIcon()
    Private ReadOnly _trayMenu As New ContextMenuStrip()
    Private WithEvents _reconnectTimer As New System.Windows.Forms.Timer()
    Private WithEvents _configDebounceTimer As New System.Windows.Forms.Timer() With {.Interval = 350}
    Private _configWatcher As FileSystemWatcher
    Private _isMidiConnecting As Integer = 0
    Private _lastTrackCount As Integer = -1
    Private _suppressLocationSave As Boolean = True

    Public Sub New(model As ProjectModel,
                   playback As IPlaybackController,
                   Optional audioPlayer As NAudioSyncPlayer = Nothing,
                   Optional masterBus As MasterBusRenderer = Nothing,
                   Optional broker As yoump.MediaParameterBroker = Nothing,
                   Optional dispatcher As TimelineCommandDispatcher = Nothing,
                   Optional midiController As DdjSb3MidiController = Nothing)

        InitializeComponent()

        _model = model
        _audioPlayer = audioPlayer
        _midiController = midiController
        _broker = broker

        Dim cfg = MixerHardwareStateStore.Current
        _reconnectTimer.Interval = Math.Max(500, cfg.Midi.ReconnectIntervalMs)

        Me.Text = "Pioneer DJ Mixer"
        Dim initialH As Integer = Math.Max(390, cfg.Ui.WindowHeight)
        Me.ClientSize = New Size(171, initialH)
        Me.MinimumSize = Me.SizeFromClientSize(New Size(171, 390))
        Me.TopMost = cfg.Ui.AlwaysOnTop

        If cfg.Ui.WindowX.HasValue AndAlso cfg.Ui.WindowY.HasValue Then
            Dim targetRect As New Rectangle(cfg.Ui.WindowX.Value, cfg.Ui.WindowY.Value, 171, initialH)
            Dim isVisibleOnAnyScreen As Boolean = False
            For Each scr As Screen In Screen.AllScreens
                If scr.WorkingArea.IntersectsWith(targetRect) Then
                    isVisibleOnAnyScreen = True
                    Exit For
                End If
            Next

            If isVisibleOnAnyScreen Then
                Me.StartPosition = FormStartPosition.Manual
                Me.Location = New Point(cfg.Ui.WindowX.Value, cfg.Ui.WindowY.Value)
            Else
                Me.StartPosition = FormStartPosition.CenterScreen
            End If
        Else
            Me.StartPosition = FormStartPosition.CenterScreen
        End If

        Me.ShowIcon = False
        Me.MaximizeBox = False
        Me.MinimizeBox = True

        mixerControl = New AudioMixerControl(model, playback, audioPlayer, masterBus, broker, dispatcher) With {
            .Dock = DockStyle.Fill
        }

        Me.Controls.Add(mixerControl)

        If _model IsNot Nothing Then
            AddHandler _model.StateChanged, AddressOf OnProjectModelStateChanged
        End If

        If _midiController IsNot Nothing Then
            AddHandler _midiController.ConnectionChanged, AddressOf OnMidiConnectionChanged
        End If

        BuildDeviceRoutingMenu()
        InitializeSystemTray()
        InitializeConfigFileWatcher()
        UpdateWindowTitle()

        Try
            ApplyFormTheme()
        Catch
        End Try
        AddHandler ThemeManager.ThemeChanged, AddressOf OnThemeChanged
    End Sub

    Private Sub InitializeComponent()
        Me.SuspendLayout()
        Me.Name = "AudioMixerForm"
        Me.ResumeLayout(False)
    End Sub

    Private Sub InitializeConfigFileWatcher()
        Try
            Dim cfgPath As String = MixerHardwareStateStore.GetConfigFilePath()
            Dim dir As String = Path.GetDirectoryName(cfgPath)
            Dim fileName As String = Path.GetFileName(cfgPath)
            If String.IsNullOrEmpty(dir) OrElse Not Directory.Exists(dir) Then Return

            _configWatcher = New FileSystemWatcher(dir, fileName) With {
                .NotifyFilter = NotifyFilters.LastWrite Or NotifyFilters.FileName Or NotifyFilters.Size,
                .EnableRaisingEvents = True
            }
            AddHandler _configWatcher.Changed, AddressOf OnConfigFileExternalChanged
            AddHandler _configWatcher.Created, AddressOf OnConfigFileExternalChanged
            AddHandler _configWatcher.Renamed, AddressOf OnConfigFileExternalChanged
        Catch
        End Try
    End Sub

    Private Sub OnConfigFileExternalChanged(sender As Object, e As FileSystemEventArgs)
        If IsDisposed OrElse Disposing OrElse Not IsHandleCreated Then Return
        If Not MixerHardwareStateStore.Current.Ui.HotReloadConfigFile Then Return

        ' Игнорируем собственные атомарные сохранения в течение 850 мс после FlushNow()
        Dim elapsedSinceFlush As Long = Environment.TickCount64 - Interlocked.Read(MixerHardwareStateStore.LastFlushTick)
        If elapsedSinceFlush >= 0 AndAlso elapsedSinceFlush < 850 Then Return

        Try
            BeginInvoke(New Action(Sub()
                                       _configDebounceTimer.Stop()
                                       _configDebounceTimer.Start()
                                   End Sub))
        Catch
        End Try
    End Sub

    Private Sub ConfigDebounceTimer_Tick(sender As Object, e As EventArgs) Handles _configDebounceTimer.Tick
        _configDebounceTimer.Stop()
        ReloadConfigOnTheFly(showOsdNotification:=True)
    End Sub

    Private Function BuildPresetsSubMenu() As ToolStripMenuItem
        Dim presetsRoot As New ToolStripMenuItem("Пресеты эквалайзера")
        Dim presetsDict = MixerHardwareStateStore.Current.Presets
        If presetsDict IsNot Nothing Then
            For Each kvp In presetsDict
                Dim presetName As String = kvp.Key
                Dim presetVal As ChannelProfileConfig = kvp.Value.Clone()
                Dim pItem As New ToolStripMenuItem(presetName)
                AddHandler pItem.Click, Sub() ApplyPresetToActiveTrack(presetName, presetVal)
                presetsRoot.DropDownItems.Add(pItem)
            Next
        End If
        Return presetsRoot
    End Function

    Private Sub ApplyPresetToActiveTrack(presetName As String, preset As ChannelProfileConfig)
        If _model?.Tracks Is Nothing OrElse preset Is Nothing Then Return
        Dim targetId As Guid = If(_audioPlayer IsNot Nothing AndAlso _audioPlayer.FocusedTrackId <> Guid.Empty,
                                  _audioPlayer.FocusedTrackId,
                                  Guid.Empty)
        SyncLock _model.SyncRoot
            If targetId = Guid.Empty AndAlso _model.Tracks.Count > 0 Then
                targetId = _model.Tracks(0).Id
            End If
        End SyncLock
        If targetId = Guid.Empty Then Return

        If _broker IsNot Nothing Then
            _broker.ApplyPresetToTrack(targetId, preset, Me)
        End If
        mixerControl?.ShowExternalBanner(targetId, $"PRESET: {presetName}")
    End Sub

    Private Shared Sub ClearAndDisposeMenuItems(menu As ToolStripDropDown)
        If menu Is Nothing Then Return
        While menu.Items.Count > 0
            Dim item As ToolStripItem = menu.Items(0)
            menu.Items.RemoveAt(0)
            Try
                Dim ddItem As ToolStripDropDownItem = TryCast(item, ToolStripDropDownItem)
                If ddItem IsNot Nothing AndAlso ddItem.HasDropDownItems Then
                    ClearAndDisposeMenuItems(ddItem.DropDown)
                End If
                item.Dispose()
            Catch
            End Try
        End While
    End Sub

    Private Sub InitializeSystemTray()
        Try
            ClearAndDisposeMenuItems(_trayMenu)

            Using bmp As New Bitmap(32, 32)
                Using g As Graphics = Graphics.FromImage(bmp)
                    g.Clear(Color.FromArgb(18, 20, 24))
                    Using penBorder As New Pen(Color.FromArgb(0, 180, 255), 2.0F)
                        g.DrawRectangle(penBorder, 1, 1, 29, 29)
                    End Using
                    Using brLow As New SolidBrush(Color.FromArgb(255, 60, 30))
                        g.FillRectangle(brLow, 6, 14, 5, 13)
                    End Using
                    Using brMid As New SolidBrush(Color.FromArgb(50, 235, 95))
                        g.FillRectangle(brMid, 13, 8, 5, 19)
                    End Using
                    Using brHi As New SolidBrush(Color.FromArgb(0, 200, 255))
                        g.FillRectangle(brHi, 20, 11, 5, 16)
                    End Using
                End Using

                Dim hIcon As IntPtr = IntPtr.Zero
                Try
                    hIcon = bmp.GetHicon()
                    Using tempIcon As Icon = Icon.FromHandle(hIcon)
                        _trayIcon.Icon = CType(tempIcon.Clone(), Icon)
                    End Using
                Finally
                    If hIcon <> IntPtr.Zero Then
                        DestroyIcon(hIcon)
                    End If
                End Try
            End Using
            _trayIcon.Text = "Pioneer DJ Mixer (1-2 QUAD-CAPTURE)"
            _trayIcon.Visible = True

            AddHandler _trayMenu.Opening, Sub(s, e)
                                              e.Cancel = False
                                              ClearAndDisposeMenuItems(_trayMenu)
                                              Dim showItem As New ToolStripMenuItem("Показать / Скрыть микшер")
                                              AddHandler showItem.Click, Sub() ToggleTrayVisibility()

                                              Dim topItem As New ToolStripMenuItem("Поверх всех окон (Always on Top)") With {
                                                  .Checked = Me.TopMost
                                              }
                                              AddHandler topItem.Click, Sub()
                                                                            Me.TopMost = Not Me.TopMost
                                                                            MixerHardwareStateStore.Current.Ui.AlwaysOnTop = Me.TopMost
                                                                            MixerHardwareStateStore.RequestSaveDeferred()
                                                                            UpdateWindowTitle()
                                                                        End Sub

                                              Dim settingsTrayItem As New ToolStripMenuItem("Настройки микшера...")
                                              AddHandler settingsTrayItem.Click, Sub() OpenSettingsForm()

                                              Dim exitItem As New ToolStripMenuItem("Выход")
                                              AddHandler exitItem.Click, Sub() Me.Close()

                                              _trayMenu.Items.Add(showItem)
                                              _trayMenu.Items.Add(topItem)
                                              _trayMenu.Items.Add(New ToolStripSeparator())
                                              _trayMenu.Items.Add(BuildPresetsSubMenu())
                                              _trayMenu.Items.Add(settingsTrayItem)
                                              _trayMenu.Items.Add(New ToolStripSeparator())
                                              _trayMenu.Items.Add(exitItem)
                                          End Sub

            _trayIcon.ContextMenuStrip = _trayMenu
            AddHandler _trayIcon.MouseClick, Sub(s, e)
                                                 If e.Button = MouseButtons.Left Then
                                                     ToggleTrayVisibility()
                                                 End If
                                             End Sub
        Catch
        End Try
    End Sub

    Private Sub OpenConfigFileInEditor()
        Try
            MixerHardwareStateStore.FlushNow()
            Dim cfgPath As String = MixerHardwareStateStore.GetConfigFilePath()
            If File.Exists(cfgPath) Then
                Process.Start(New ProcessStartInfo(cfgPath) With {.UseShellExecute = True})
            End If
        Catch
        End Try
    End Sub

    Private Sub ReloadConfigOnTheFly(Optional showOsdNotification As Boolean = False, Optional reloadFromDisk As Boolean = True)
        Try
            _suppressLocationSave = True
            Dim prevUseAsio As Boolean = MixerHardwareStateStore.Current.Audio.UseAsio
            Dim prevOutId As String = MixerHardwareStateStore.Current.Audio.PreferredOutputDeviceId
            Dim prevPreferBt As Boolean = MixerHardwareStateStore.Current.Audio.PreferBluetoothHeadphonesOutput

            Dim cfg = If(reloadFromDisk, MixerHardwareStateStore.Load(), MixerHardwareStateStore.Current)
            If reloadFromDisk Then
                SettingsService.RaiseFpsChanged(cfg.Ui.ProjectFps)
            End If

            Me.TopMost = cfg.Ui.AlwaysOnTop
            _reconnectTimer.Interval = Math.Max(500, cfg.Midi.ReconnectIntervalMs)
            If _midiController IsNot Nothing Then
                _midiController.CrossfaderControlsBalance = cfg.Midi.CrossfaderControlsBalance
            End If
            If reloadFromDisk AndAlso _audioPlayer IsNot Nothing Then
                _audioPlayer.ApplyInitialSavedState(cfg)
                If prevUseAsio <> cfg.Audio.UseAsio OrElse
                   prevPreferBt <> cfg.Audio.PreferBluetoothHeadphonesOutput OrElse
                   Not String.Equals(prevOutId, cfg.Audio.PreferredOutputDeviceId, StringComparison.OrdinalIgnoreCase) Then
                    Dim targetOut As String = If(cfg.Audio.PreferBluetoothHeadphonesOutput OrElse String.IsNullOrWhiteSpace(cfg.Audio.PreferredOutputDeviceId),
                                                 Nothing,
                                                 cfg.Audio.PreferredOutputDeviceId)
                    _audioPlayer.StartEngine(Nothing, targetOut, False)
                End If
            End If

            If _settingsForm IsNot Nothing AndAlso Not _settingsForm.IsDisposed AndAlso reloadFromDisk Then
                _settingsForm.LoadValuesFromConfig()
            End If
            If Me.WindowState = FormWindowState.Normal Then
                Dim newH As Integer = Math.Max(390, cfg.Ui.WindowHeight)
                If cfg.Ui.WindowX.HasValue AndAlso cfg.Ui.WindowY.HasValue Then
                    Dim targetRect As New Rectangle(cfg.Ui.WindowX.Value, cfg.Ui.WindowY.Value, Math.Max(171, Me.Width), newH)
                    Dim isVisibleOnAnyScreen As Boolean = False
                    For Each scr As Screen In Screen.AllScreens
                        If scr.WorkingArea.IntersectsWith(targetRect) Then
                            isVisibleOnAnyScreen = True
                            Exit For
                        End If
                    Next
                    If isVisibleOnAnyScreen Then
                        Me.Location = New Point(cfg.Ui.WindowX.Value, cfg.Ui.WindowY.Value)
                    End If
                End If
                Me.ClientSize = New Size(Me.ClientSize.Width, newH)
                _lastTrackCount = -1
                AdjustWindowWidthToTracks()
            End If
            mixerControl?.ReloadSettingsFromConfig()
            If showOsdNotification Then
                mixerControl?.ShowExternalBanner(Guid.Empty, "CONFIG: RELOADED")
            End If
            ApplyFormTheme()
            UpdateWindowTitle()
        Catch
        Finally
            _suppressLocationSave = False
        End Try
    End Sub

    Public Sub ShowDeviceContextMenu(targetControl As Control, position As Point)
        If _deviceMenu Is Nothing OrElse targetControl Is Nothing Then Return
        _deviceMenu.Show(targetControl, position)
    End Sub

    Private Sub ToggleTrayVisibility()
        If Me.Visible AndAlso Me.WindowState <> FormWindowState.Minimized Then
            Me.Hide()
        Else
            _suppressLocationSave = True
            Try
                Me.WindowState = FormWindowState.Normal
                Me.Show()
                Me.Activate()
                _lastTrackCount = -1
                AdjustWindowWidthToTracks()
            Finally
                _suppressLocationSave = False
            End Try
        End If
    End Sub

    Protected Overrides Sub OnResize(e As EventArgs)
        MyBase.OnResize(e)
        If Me.WindowState = FormWindowState.Minimized Then
            Me.Hide()
        End If
    End Sub

    Protected Overrides Sub OnResizeEnd(e As EventArgs)
        MyBase.OnResizeEnd(e)
        If Me.WindowState = FormWindowState.Normal AndAlso Me.IsHandleCreated Then
            Dim newH As Integer = Math.Max(390, Me.ClientSize.Height)
            Dim heightChanged As Boolean = (MixerHardwareStateStore.Current.Ui.WindowHeight <> newH)

            MixerHardwareStateStore.Current.Ui.WindowHeight = newH
            If MixerHardwareStateStore.Current.Ui.WindowX.HasValue Then
                MixerHardwareStateStore.Current.Ui.WindowX = Me.Location.X
                MixerHardwareStateStore.Current.Ui.WindowY = Me.Location.Y
            End If
            MixerHardwareStateStore.RequestSaveDeferred()

            ' Подстраиваем ширину пропорционально новой высоте только после того, как пользователь отпустил мышь
            If heightChanged Then
                _suppressLocationSave = True
                Try
                    _lastTrackCount = -1
                    AdjustWindowWidthToTracks()
                Finally
                    _suppressLocationSave = False
                End Try
            End If
        End If
    End Sub

    Protected Overrides Sub OnLocationChanged(e As EventArgs)
        MyBase.OnLocationChanged(e)
        If _suppressLocationSave Then Return
        If Me.WindowState = FormWindowState.Normal AndAlso Me.Visible AndAlso Me.IsHandleCreated Then
            If MixerHardwareStateStore.Current.Ui.WindowX.HasValue Then
                MixerHardwareStateStore.Current.Ui.WindowX = Me.Location.X
                MixerHardwareStateStore.Current.Ui.WindowY = Me.Location.Y
                MixerHardwareStateStore.RequestSaveDeferred()
            End If
        End If
    End Sub

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)
        Dim cfg = MixerHardwareStateStore.Current
        Try
            _audioPlayer?.AttachProjectModel(_model)
            Dim prefOutId As String = If(cfg.Audio.PreferBluetoothHeadphonesOutput OrElse String.IsNullOrWhiteSpace(cfg.Audio.PreferredOutputDeviceId),
                                         Nothing,
                                         cfg.Audio.PreferredOutputDeviceId)
            _audioPlayer?.StartEngine(Nothing, prefOutId, False)
        Catch
        End Try

        Try
            AdjustWindowWidthToTracks()
        Finally
            _suppressLocationSave = False
        End Try

        TryAutoConnectMidiAsync()
        _reconnectTimer.Start()

        If cfg.Ui.StartMinimizedToTray Then
            Me.BeginInvoke(New Action(Sub() Me.Hide()))
        End If
    End Sub

    Private Sub OnProjectModelStateChanged()
        If IsDisposed OrElse Disposing OrElse Not IsHandleCreated Then Return
        If InvokeRequired Then
            Try
                BeginInvoke(New Action(AddressOf AdjustWindowWidthToTracks))
            Catch
            End Try
        Else
            AdjustWindowWidthToTracks()
        End If
    End Sub

    Private Sub AdjustWindowWidthToTracks()
        If IsDisposed OrElse Disposing OrElse _model?.Tracks Is Nothing Then Return
        ' Не пересчитываем размер, пока окно свёрнуто (иначе ClientSize.Height = 0 сбросит высоту до 390)
        If Me.WindowState = FormWindowState.Minimized Then Return

        Dim audioCount As Integer = 0
        SyncLock _model.SyncRoot
            For i As Integer = 0 To _model.Tracks.Count - 1
                If _model.Tracks(i).Type = TargetFormatType.Audio Then audioCount += 1
            Next
        End SyncLock
        audioCount = Math.Max(1, audioCount)

        If audioCount <> _lastTrackCount Then
            _lastTrackCount = audioCount
            Dim savedH As Integer = Math.Max(390, MixerHardwareStateStore.Current.Ui.WindowHeight)
            Dim h As Integer = If(Me.ClientSize.Height >= 390, Me.ClientSize.Height, savedH)
            Dim scale As Single = CSng(h) / 490.0F
            Dim virtualWidth As Single = (audioCount * AudioMixerControl.TRACK_WIDTH) + AudioMixerControl.MASTER_WIDTH
            Dim desiredWidth As Integer = Math.Max(171, CInt(Math.Ceiling(virtualWidth * scale)))

            Try
                Dim maxScreenW As Integer = Screen.FromControl(Me).WorkingArea.Width - 60
                desiredWidth = Math.Min(desiredWidth, maxScreenW)
            Catch
            End Try

            If Me.ClientSize.Width <> desiredWidth OrElse Me.ClientSize.Height <> h Then
                Dim prevSuppress As Boolean = _suppressLocationSave
                _suppressLocationSave = True
                Try
                    Me.ClientSize = New Size(desiredWidth, h)
                Finally
                    _suppressLocationSave = prevSuppress
                End Try
            End If
        End If
    End Sub

    Private Sub TryAutoConnectMidiAsync()
        If _midiController Is Nothing OrElse _midiController.IsConnected Then Return
        If Interlocked.CompareExchange(_isMidiConnecting, 1, 0) <> 0 Then Return

        Task.Run(Sub()
                     Try
                         If Not IsDisposed AndAlso Not Disposing Then
                             _midiController.TryAutoConnect()
                         End If
                     Catch
                     Finally
                         Interlocked.Exchange(_isMidiConnecting, 0)
                     End Try
                 End Sub)
    End Sub

    Private Sub ReconnectTimer_Tick(sender As Object, e As EventArgs) Handles _reconnectTimer.Tick
        If IsDisposed OrElse Disposing Then Return
        If _midiController IsNot Nothing AndAlso Not _midiController.IsConnected Then
            TryAutoConnectMidiAsync()
        End If
    End Sub

    Private Sub OnMidiConnectionChanged(deviceName As String, isConnected As Boolean)
        If IsDisposed OrElse Disposing OrElse Not IsHandleCreated Then Return
        If isConnected AndAlso Not String.IsNullOrWhiteSpace(deviceName) AndAlso
           Not MixerHardwareStateStore.Current.Midi.IsBlockedMidiDevice(deviceName) Then
            MixerHardwareStateStore.Current.Midi.LastConnectedDeviceName = deviceName
            MixerHardwareStateStore.RequestSaveDeferred()
        End If
        If InvokeRequired Then
            Try
                BeginInvoke(New Action(AddressOf UpdateWindowTitle))
            Catch
            End Try
        Else
            UpdateWindowTitle()
        End If
    End Sub

    Private Sub UpdateWindowTitle()
        If IsDisposed OrElse Disposing Then Return
        Dim pinSuffix As String = If(Me.TopMost, " [PIN]", "")
        If _midiController IsNot Nothing AndAlso _midiController.IsConnected Then
            Me.Text = $"Pioneer DJ Mixer [{_midiController.ConnectedDeviceName}]{pinSuffix}"
        Else
            Me.Text = $"Pioneer DJ Mixer{pinSuffix}"
        End If
    End Sub

    Private Sub BuildDeviceRoutingMenu()
        If _audioPlayer Is Nothing Then Return
        AddHandler _deviceMenu.Opening, Sub(s, e)
                                            e.Cancel = False
                                            ClearAndDisposeMenuItems(_deviceMenu)

                                            If _midiController IsNot Nothing Then
                                                Dim midiStatus As String = If(_midiController.IsConnected,
                                                                              $"MIDI: {_midiController.ConnectedDeviceName} [Активен]",
                                                                              "MIDI: DDJ-SB3 не подключен")
                                                Dim midiHeader As New ToolStripMenuItem(midiStatus) With {.Enabled = False}
                                                _deviceMenu.Items.Add(midiHeader)

                                                For Each mDev In _midiController.GetCachedMidiInDevices()
                                                    Dim idx As Integer = mDev.Index
                                                    Dim mItem As New ToolStripMenuItem($"MIDI вход [{idx}]: {mDev.Name}") With {
                                                        .Checked = (_midiController.IsConnected AndAlso _midiController.ConnectedDeviceIndex = idx)
                                                    }
                                                    AddHandler mItem.Click, Sub()
                                                                                Task.Run(Sub() _midiController.ConnectDevice(idx))
                                                                            End Sub
                                                    _deviceMenu.Items.Add(mItem)
                                                Next

                                                Dim xfaderItem As New ToolStripMenuItem("Кроссфейдер DDJ-SB3 -> Баланс L/R") With {
                                                    .Checked = _midiController.CrossfaderControlsBalance
                                                }
                                                AddHandler xfaderItem.Click, Sub()
                                                                                 _midiController.CrossfaderControlsBalance = Not _midiController.CrossfaderControlsBalance
                                                                                 MixerHardwareStateStore.Current.Midi.CrossfaderControlsBalance = _midiController.CrossfaderControlsBalance
                                                                                 MixerHardwareStateStore.RequestSaveDeferred()
                                                                             End Sub
                                                _deviceMenu.Items.Add(xfaderItem)
                                                _deviceMenu.Items.Add(New ToolStripSeparator())
                                            End If

                                            Dim lblIn As New ToolStripMenuItem($"Режим: {_audioPlayer.CaptureDeviceName}") With {.Enabled = False}
                                            _deviceMenu.Items.Add(lblIn)

                                            Dim micStudioItem As New ToolStripMenuItem("Студийная обработка микрофона (80Hz + Gate + Comp)") With {
                                                .Checked = MixerHardwareStateStore.Current.Audio.MicStudioProcessingEnabled
                                            }
                                            AddHandler micStudioItem.Click, Sub()
                                                                                MixerHardwareStateStore.Current.Audio.MicStudioProcessingEnabled = Not MixerHardwareStateStore.Current.Audio.MicStudioProcessingEnabled
                                                                                MixerHardwareStateStore.RequestSaveDeferred()
                                                                                _audioPlayer.DecayMetersIfSilent(forceImmediateSync:=True)
                                                                            End Sub
                                            _deviceMenu.Items.Add(micStudioItem)
                                            _deviceMenu.Items.Add(BuildMicStudioOptionsSubMenu())
                                            _deviceMenu.Items.Add(BuildMicIrSubMenu())

                                            Dim micMonItem As New ToolStripMenuItem("Мониторинг микрофона 1-2 (QUAD-CAPTURE)") With {
                                                .Checked = _audioPlayer.MicMonitoringEnabled
                                            }
                                            AddHandler micMonItem.Click, Sub()
                                                                             _audioPlayer.MicMonitoringEnabled = Not _audioPlayer.MicMonitoringEnabled
                                                                             MixerHardwareStateStore.Current.Audio.MicMonitoringEnabled = _audioPlayer.MicMonitoringEnabled
                                                                             MixerHardwareStateStore.RequestSaveDeferred()
                                                                         End Sub
                                            _deviceMenu.Items.Add(micMonItem)

                                            Dim installApoItem As New ToolStripMenuItem("Зарегистрировать C++ APO на 1-2 (QUAD-CAPTURE) [Админ]")
                                            AddHandler installApoItem.Click, Sub() _audioPlayer.InstallKernelApoForWindows11()
                                            _deviceMenu.Items.Add(installApoItem)

                                            _deviceMenu.Items.Add(New ToolStripSeparator())
                                            _deviceMenu.Items.Add(BuildPresetsSubMenu())
                                            _deviceMenu.Items.Add(New ToolStripSeparator())

                                            Dim btAutoItem As New ToolStripMenuItem("Авто-приоритет Bluetooth Hi-Res (Space Travel 2 Ultra / LDAC)") With {
                                                .Checked = MixerHardwareStateStore.Current.Audio.PreferBluetoothHeadphonesOutput
                                            }
                                            AddHandler btAutoItem.Click, Sub()
                                                                             Dim aCfg = MixerHardwareStateStore.Current.Audio
                                                                             aCfg.PreferBluetoothHeadphonesOutput = Not aCfg.PreferBluetoothHeadphonesOutput
                                                                             MixerHardwareStateStore.RequestSaveDeferred()
                                                                             _audioPlayer.StartEngine(Nothing, Nothing, False)
                                                                             mixerControl?.ShowExternalBanner(Guid.Empty, If(aCfg.PreferBluetoothHeadphonesOutput, "BT AUTO: ON", "BT AUTO: OFF"))
                                                                         End Sub
                                            _deviceMenu.Items.Add(btAutoItem)

                                            Dim ldacGuardItem As New ToolStripMenuItem("LDAC Guard: Блокировать HFP 16 kHz микрофон (сохранять LDAC)") With {
                                                .Checked = MixerHardwareStateStore.Current.Audio.BluetoothMicHighQualityOnly
                                            }
                                            AddHandler ldacGuardItem.Click, Sub()
                                                                                Dim aCfg = MixerHardwareStateStore.Current.Audio
                                                                                aCfg.BluetoothMicHighQualityOnly = Not aCfg.BluetoothMicHighQualityOnly
                                                                                MixerHardwareStateStore.RequestSaveDeferred()
                                                                                _audioPlayer.StartEngine(Nothing, _audioPlayer.OutputDeviceId, False)
                                                                                mixerControl?.ShowExternalBanner(Guid.Empty, If(aCfg.BluetoothMicHighQualityOnly, "LDAC GUARD: ON", "LDAC GUARD: OFF"))
                                                                            End Sub
                                            _deviceMenu.Items.Add(ldacGuardItem)

                                            If Not String.IsNullOrWhiteSpace(_audioPlayer.LastLdacGuardStatus) Then
                                                Dim guardStatusItem As New ToolStripMenuItem($"  [{_audioPlayer.LastLdacGuardStatus}]") With {.Enabled = False}
                                                _deviceMenu.Items.Add(guardStatusItem)
                                            End If

                                            Dim lblOut As New ToolStripMenuItem("Выход звука (QUAD-CAPTURE / Bluetooth LDAC):") With {.Enabled = False}
                                            _deviceMenu.Items.Add(lblOut)

                                            _audioPlayer.RefreshEndpointsCache()
                                            Dim curOutId As String = _audioPlayer.OutputDeviceId
                                            Dim curOutName As String = _audioPlayer.OutputDeviceName

                                            For Each dev In _audioPlayer.GetCachedOutputDeviceInfos()
                                                Dim devId As String = dev.Id
                                                Dim devName As String = dev.FriendlyName
                                                Dim displayLbl As String = dev.GetDisplayLabel()
                                                Dim isBtDev As Boolean = dev.IsBluetooth
                                                Dim item As New ToolStripMenuItem($"Выход: {displayLbl}") With {
                                                    .Checked = (curOutId = devId OrElse curOutName = devName)
                                                }
                                                AddHandler item.Click, Sub()
                                                                           Dim aCfg = MixerHardwareStateStore.Current.Audio
                                                                           aCfg.PreferredOutputDeviceId = devId
                                                                           aCfg.PreferredOutputDeviceName = devName
                                                                           If Not isBtDev Then
                                                                               aCfg.PreferBluetoothHeadphonesOutput = False
                                                                           End If
                                                                           MixerHardwareStateStore.RequestSaveDeferred()
                                                                           _audioPlayer.StartEngine(Nothing, devId, False)
                                                                       End Sub
                                                _deviceMenu.Items.Add(item)
                                            Next

                                            _deviceMenu.Items.Add(New ToolStripSeparator())

                                            Dim topMostItem As New ToolStripMenuItem("Поверх всех окон (Always on Top)") With {
                                                .Checked = Me.TopMost
                                            }
                                            AddHandler topMostItem.Click, Sub()
                                                                              Me.TopMost = Not Me.TopMost
                                                                              MixerHardwareStateStore.Current.Ui.AlwaysOnTop = Me.TopMost
                                                                              MixerHardwareStateStore.RequestSaveDeferred()
                                                                              UpdateWindowTitle()
                                                                          End Sub
                                            _deviceMenu.Items.Add(topMostItem)

                                            Dim settingsFormItem As New ToolStripMenuItem("Настройки микшера (Все параметры)...")
                                            AddHandler settingsFormItem.Click, Sub() OpenSettingsForm()
                                            _deviceMenu.Items.Add(settingsFormItem)

                                            Dim hideTrayItem As New ToolStripMenuItem("Свернуть в системный трей")
                                            AddHandler hideTrayItem.Click, Sub() Me.Hide()
                                            _deviceMenu.Items.Add(hideTrayItem)
                                        End Sub
        Me.ContextMenuStrip = _deviceMenu
        ' Не привязываем Me.ContextMenuStrip напрямую, чтобы правый клик сброса ручек (0 dB) не открывал меню
        Me.ContextMenuStrip = Nothing
    End Sub

    Private Function BuildMicStudioOptionsSubMenu() As ToolStripMenuItem
        Dim audioCfg = MixerHardwareStateStore.Current.Audio
        Dim root As New ToolStripMenuItem($"Режимы микрофона (Talkover: {AudioRoutingConfig.GetMicTalkoverModeName(audioCfg.MicTalkoverMode)})")

        Dim deEsserItem As New ToolStripMenuItem("Динамический De-Esser (подавление сибилянтов «С/Щ»)") With {
            .Checked = audioCfg.MicDeEsserEnabled
        }
        AddHandler deEsserItem.Click, Sub()
                                          audioCfg.MicDeEsserEnabled = Not audioCfg.MicDeEsserEnabled
                                          yoump.Audio.Native.NativeCppAutoBuilderAndInstaller.WriteMicConvolutionIrParams(
                                              audioCfg.MicIrPreset, audioCfg.MicIrMix, audioCfg.MicNoiseGateThreshold, audioCfg.GetPackedMicExtraFlags())
                                          MixerHardwareStateStore.RequestSaveDeferred()
                                          _audioPlayer?.DecayMetersIfSilent(forceImmediateSync:=True)
                                          mixerControl?.ShowExternalBanner(Guid.Empty, If(audioCfg.MicDeEsserEnabled, "DE-ESSER: ON", "DE-ESSER: OFF"))
                                      End Sub
        root.DropDownItems.Add(deEsserItem)

        Dim plosiveItem As New ToolStripMenuItem("Фильтр взрывных согласных 24 dB/oct LR4 (80 Hz Anti-Pop)") With {
            .Checked = audioCfg.MicPlosiveFilter24dB
        }
        AddHandler plosiveItem.Click, Sub()
                                          audioCfg.MicPlosiveFilter24dB = Not audioCfg.MicPlosiveFilter24dB
                                          yoump.Audio.Native.NativeCppAutoBuilderAndInstaller.WriteMicConvolutionIrParams(
                                              audioCfg.MicIrPreset, audioCfg.MicIrMix, audioCfg.MicNoiseGateThreshold, audioCfg.GetPackedMicExtraFlags())
                                          MixerHardwareStateStore.RequestSaveDeferred()
                                          _audioPlayer?.DecayMetersIfSilent(forceImmediateSync:=True)
                                          mixerControl?.ShowExternalBanner(Guid.Empty, If(audioCfg.MicPlosiveFilter24dB, "HPF 80Hz: 24dB/oct", "HPF 80Hz: 12dB/oct"))
                                      End Sub
        root.DropDownItems.Add(plosiveItem)

        Dim antiFbItem As New ToolStripMenuItem("Подавление заводки + Фаза 180° (Anti-Feedback)") With {
            .Checked = audioCfg.MicAntiFeedbackEnabled
        }
        AddHandler antiFbItem.Click, Sub()
                                         audioCfg.MicAntiFeedbackEnabled = Not audioCfg.MicAntiFeedbackEnabled
                                         yoump.Audio.Native.NativeCppAutoBuilderAndInstaller.WriteMicConvolutionIrParams(
                                             audioCfg.MicIrPreset, audioCfg.MicIrMix, audioCfg.MicNoiseGateThreshold, audioCfg.GetPackedMicExtraFlags())
                                         MixerHardwareStateStore.RequestSaveDeferred()
                                         _audioPlayer?.DecayMetersIfSilent(forceImmediateSync:=True)
                                         mixerControl?.ShowExternalBanner(Guid.Empty, If(audioCfg.MicAntiFeedbackEnabled, "ANTI-FB 180°: ON", "ANTI-FB 180°: OFF"))
                                     End Sub
        root.DropDownItems.Add(antiFbItem)

        root.DropDownItems.Add(New ToolStripSeparator())

        For tMode As Integer = 0 To 2
            Dim tm As Integer = tMode
            Dim tItem As New ToolStripMenuItem($"TALKOVER {tm}: {AudioRoutingConfig.GetMicTalkoverModeName(tm)}") With {
                .Checked = (audioCfg.MicTalkoverMode = tm)
            }
            AddHandler tItem.Click, Sub()
                                        audioCfg.MicTalkoverMode = tm
                                        MixerHardwareStateStore.RequestSaveDeferred()
                                        _audioPlayer?.DecayMetersIfSilent(forceImmediateSync:=True)
                                        mixerControl?.ShowExternalBanner(Guid.Empty, $"TALKOVER: {AudioRoutingConfig.GetMicTalkoverModeName(tm)}")
                                    End Sub
            root.DropDownItems.Add(tItem)
        Next

        root.DropDownItems.Add(New ToolStripSeparator())

        For xMode As Integer = 0 To 3
            Dim xm As Integer = xMode
            Dim xItem As New ToolStripMenuItem($"XLR Вход {xm}: {AudioRoutingConfig.GetMicXlrInputModeName(xm)}") With {
                .Checked = (audioCfg.MicXlrInputMode = xm)
            }
            AddHandler xItem.Click, Sub()
                                        audioCfg.MicXlrInputMode = xm
                                        yoump.Audio.Native.NativeCppAutoBuilderAndInstaller.WriteMicConvolutionIrParams(
                                            audioCfg.MicIrPreset, audioCfg.MicIrMix, audioCfg.MicNoiseGateThreshold, audioCfg.GetPackedMicExtraFlags())
                                        MixerHardwareStateStore.RequestSaveDeferred()
                                        _audioPlayer?.DecayMetersIfSilent(forceImmediateSync:=True)
                                        mixerControl?.ShowExternalBanner(Guid.Empty, $"XLR MODE: {xm}")
                                    End Sub
            root.DropDownItems.Add(xItem)
        Next

        Return root
    End Function

    Private Function BuildMicIrSubMenu() As ToolStripMenuItem
        Dim audioCfg = MixerHardwareStateStore.Current.Audio
        Dim curPreset As Integer = audioCfg.MicIrPreset
        Dim root As New ToolStripMenuItem($"ИК-свёртка микрофона (Zero-Latency IR): {AudioRoutingConfig.GetMicIrPresetName(curPreset)}")

        For presetId As Integer = 0 To 6
            Dim pid As Integer = presetId
            Dim item As New ToolStripMenuItem($"{pid}: {AudioRoutingConfig.GetMicIrPresetName(pid)}") With {
                .Checked = (curPreset = pid)
            }
            AddHandler item.Click, Sub()
                                       audioCfg.MicIrPreset = pid
                                       If pid = 5 OrElse pid = 6 Then
                                           If audioCfg.MicIrMix > 0.65F Then audioCfg.MicIrMix = 0.38F
                                       ElseIf pid >= 1 AndAlso pid <= 4 Then
                                           If audioCfg.MicIrMix < 0.5F Then audioCfg.MicIrMix = 1.0F
                                       End If
                                       yoump.Audio.Native.NativeCppAutoBuilderAndInstaller.WriteMicConvolutionIrParams(
                                           audioCfg.MicIrPreset, audioCfg.MicIrMix, audioCfg.MicNoiseGateThreshold, audioCfg.GetPackedMicExtraFlags())
                                       MixerHardwareStateStore.RequestSaveDeferred()
                                       _audioPlayer?.DecayMetersIfSilent(forceImmediateSync:=True)
                                       mixerControl?.ShowExternalBanner(Guid.Empty, $"MIC IR: {AudioRoutingConfig.GetMicIrPresetName(pid)}")
                                   End Sub
            root.DropDownItems.Add(item)
        Next

        root.DropDownItems.Add(New ToolStripSeparator())

        Dim customLabel As String = If(Not String.IsNullOrWhiteSpace(audioCfg.MicCustomIrFilePath),
                                       $"7: Свой .WAV импульс [{Path.GetFileName(audioCfg.MicCustomIrFilePath)}]...",
                                       "7: Загрузить свой импульс (.wav)...")
        Dim customItem As New ToolStripMenuItem(customLabel) With {
            .Checked = (curPreset = 7)
        }
        AddHandler customItem.Click, Sub()
                                         Using ofd As New OpenFileDialog() With {
                                             .Title = "Выберите импульсный файл микрофона или ревербератора (.wav)",
                                             .Filter = "WAV Impulse Response (*.wav)|*.wav|Все файлы (*.*)|*.*"
                                         }
                                             If File.Exists(audioCfg.MicCustomIrFilePath) Then
                                                 ofd.FileName = audioCfg.MicCustomIrFilePath
                                             End If
                                             If ofd.ShowDialog(Me) = DialogResult.OK Then
                                                 Dim targetSr As Integer = If(_audioPlayer IsNot Nothing, _audioPlayer.CurrentSampleRate, 48000)
                                                 If yoump.Audio.Native.NativeCppAutoBuilderAndInstaller.LoadAndUploadCustomWavIr(ofd.FileName, targetSr) Then
                                                     audioCfg.MicCustomIrFilePath = ofd.FileName
                                                     audioCfg.MicIrPreset = 7
                                                     yoump.Audio.Native.NativeCppAutoBuilderAndInstaller.WriteMicConvolutionIrParams(
                                                         7, audioCfg.MicIrMix, audioCfg.MicNoiseGateThreshold, audioCfg.GetPackedMicExtraFlags())
                                                     MixerHardwareStateStore.RequestSaveDeferred()
                                                     _audioPlayer?.DecayMetersIfSilent(forceImmediateSync:=True)
                                                     mixerControl?.ShowExternalBanner(Guid.Empty, $"IR WAV: {Path.GetFileNameWithoutExtension(ofd.FileName)}")
                                                 Else
                                                     MessageBox.Show(Me, "Не удалось прочитать импульсный .wav файл.", "Zero-Latency IR", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                                                 End If
                                             End If
                                         End Using
                                     End Sub
        root.DropDownItems.Add(customItem)
        Return root
    End Function

    Protected Overrides Sub OnHandleCreated(e As EventArgs)
        MyBase.OnHandleCreated(e)
        Try
            ApplyFormTheme()
        Catch
        End Try
    End Sub

    Private Sub AudioMixerForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ApplyFormTheme()
    End Sub

    Private _settingsForm As MixerSettingsForm = Nothing

    Private Sub OpenSettingsForm()
        Try
            If _settingsForm IsNot Nothing AndAlso Not _settingsForm.IsDisposed Then
                _settingsForm.LoadValuesFromConfig()
                _settingsForm.Activate()
                Return
            End If

            _settingsForm = New MixerSettingsForm(
                _model,
                _audioPlayer,
                _midiController,
                _broker,
                Sub()
                    ReloadConfigOnTheFly(showOsdNotification:=True, reloadFromDisk:=False)
                End Sub)
            _settingsForm.Show(Me)
        Catch
        End Try
    End Sub

    Private Sub OnThemeChanged(sender As Object, e As ThemeChangedEventArgs)
        If IsDisposed OrElse Disposing Then Return
        If InvokeRequired Then
            Try
                BeginInvoke(New Action(AddressOf ApplyFormTheme))
            Catch ex As InvalidOperationException
            End Try
        Else
            ApplyFormTheme()
        End If
    End Sub

    Private Sub ApplyFormTheme()
        ThemeManager.ApplyDwm(Me.Handle)
        Me.BackColor = ThemeManager.BackColor
        Me.ForeColor = ThemeManager.ForeColor
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        Try
            If _settingsForm IsNot Nothing AndAlso Not _settingsForm.IsDisposed Then
                _settingsForm.Close()
                _settingsForm.Dispose()
                _settingsForm = Nothing
            End If
        Catch
        End Try

        Try
            If _configWatcher IsNot Nothing Then
                _configWatcher.EnableRaisingEvents = False
                _configWatcher.Dispose()
                _configWatcher = Nothing
            End If
            _configDebounceTimer.Stop()
        Catch
        End Try

        Try
            MixerHardwareStateStore.Current.Ui.AlwaysOnTop = Me.TopMost
            If Me.WindowState = FormWindowState.Normal Then
                MixerHardwareStateStore.Current.Ui.WindowHeight = Math.Max(390, Me.ClientSize.Height)
                If MixerHardwareStateStore.Current.Ui.WindowX.HasValue Then
                    MixerHardwareStateStore.Current.Ui.WindowX = Me.Location.X
                    MixerHardwareStateStore.Current.Ui.WindowY = Me.Location.Y
                End If
            End If
            MixerHardwareStateStore.SyncFromModelAndPlayer(_model, _audioPlayer)
            MixerHardwareStateStore.FlushNow()
        Catch
        End Try

        Try
            _reconnectTimer.Stop()
        Catch
        End Try

        Try
            mixerControl?.Dispose()
        Catch
        End Try

        Try
            If _model IsNot Nothing Then
                RemoveHandler _model.StateChanged, AddressOf OnProjectModelStateChanged
            End If
        Catch
        End Try

        Try
            If _midiController IsNot Nothing Then
                RemoveHandler _midiController.ConnectionChanged, AddressOf OnMidiConnectionChanged
                _midiController.Dispose()
            End If
        Catch
        End Try

        Try
            _audioPlayer?.Dispose()
        Catch
        End Try

        MyBase.OnFormClosing(e)
    End Sub

    Protected Overrides Sub OnFormClosed(e As FormClosedEventArgs)
        Try
            _reconnectTimer.Dispose()
            _configDebounceTimer.Dispose()
        Catch
        End Try
        Try
            _trayIcon.Visible = False
            Dim oldIcon As Icon = _trayIcon.Icon
            _trayIcon.Icon = Nothing
            oldIcon?.Dispose()
            _trayIcon.Dispose()
            _trayMenu.Dispose()
            _deviceMenu.Dispose()
        Catch
        End Try
        RemoveHandler ThemeManager.ThemeChanged, AddressOf OnThemeChanged
        MyBase.OnFormClosed(e)
    End Sub
End Class
' --- END OF FILE AudioMixerForm.vb ---