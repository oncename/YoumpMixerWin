' --- START OF FILE MixerSettingsForm.vb ---
Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Windows.Forms
Imports Microsoft.Win32
Imports NAudio.CoreAudioApi
Imports NAudio.Midi
Imports yoump.Audio.Native

Partial Public Class MixerSettingsForm
    Inherits Form

    Private Const AutorunRegistryPath As String = "Software\Microsoft\Windows\CurrentVersion\Run"
    Private Const AutorunValueName As String = "YoumpPioneerDjMixer"

    Private Const PageDefaultWidth As Integer = 976
    Private Const PageDefaultHeight As Integer = 690

    Private ReadOnly _model As ProjectModel
    Private ReadOnly _audioPlayer As NAudioSyncPlayer
    Private ReadOnly _midiController As DdjSb3MidiController
    Private ReadOnly _broker As yoump.MediaParameterBroker
    Private ReadOnly _onSettingsApplied As Action

    Private ReadOnly _tabs As New DarkTabControl()
    Private _isUiBuilt As Boolean = False
    Private _isLoadingConfigValues As Boolean = False
    Private _channelProfilesEditedInGrid As Boolean = False

    ' --- Вкладка 1: Аудио и ASIO ---
    Private _cmbOutputDevice As DarkComboBox
    Private _txtOutputKeyword As TextBox
    Private _txtHardwareKeywords As TextBox
    Private _txtBlockedKeywords As TextBox
    Private _txtMicKeyword As TextBox
    Private _txtMainCaptureKeyword As TextBox
    Private _chkAutoRouteApps As DarkCheckBox
    Private _chkSanitizeRegistry As DarkCheckBox
    Private _chkPreferBtOutput As DarkCheckBox
    Private _chkBtLdacGuard As DarkCheckBox
    Private _txtBtKeywords As TextBox

    Private _chkUseAsio As DarkCheckBox
    Private _cmbAsioDriver As DarkComboBox
    Private _numAsioInOffset As NumericUpDown
    Private _numAsioOutOffset As NumericUpDown
    Private _lblAsioStatus As Label

    ' --- Вкладка 2: Микрофон, Zero-Latency IR и C++ APO ---
    Private _chkMicStudioProc As DarkCheckBox
    Private _chkMicDeEsser As DarkCheckBox
    Private _chkMicPlosive24dB As DarkCheckBox
    Private _chkMicAntiFeedback As DarkCheckBox
    Private _chkMicMonitoring As DarkCheckBox
    Private _chkLimiterEnabled As DarkCheckBox
    Private _cmbMicTalkoverMode As DarkComboBox
    Private _cmbMicXlrMode As DarkComboBox
    Private _numMicGate As NumericUpDown
    Private _numMicCapLatency As NumericUpDown
    Private _numMicOutLatency As NumericUpDown
    Private _numMicBufDuration As NumericUpDown
    Private _cmbMicIrPreset As DarkComboBox
    Private _numMicIrMixPercent As NumericUpDown
    Private _txtMicCustomIrPath As TextBox
    Private _lblDspApoStatus As Label

    ' --- Вкладка 3: MIDI ---
    Private _cmbMidiDevice As DarkComboBox
    Private _lblMidiStatus As Label
    Private _txtPreferredMidiNames As TextBox
    Private _numMidiReconnectMs As NumericUpDown
    Private _numMidiDeadzone As NumericUpDown
    Private _numMidiDumpWindowMs As NumericUpDown
    Private _chkCrossfaderBalance As DarkCheckBox
    Private _chkSendSeratoSysEx As DarkCheckBox

    ' --- Вкладка 4: Процессы ---
    Private _numSessionPollMs As NumericUpDown
    Private _numDiscoveryMs As NumericUpDown
    Private _numInactiveTimeoutMs As NumericUpDown
    Private _txtPinnedChannels As TextBox
    Private _txtIgnoredProcesses As TextBox
    Private _gridAliases As DataGridView

    ' --- Вкладка 5: Интерфейс и Мастер ---
    Private _chkDarkTheme As DarkCheckBox
    Private _chkAlwaysOnTop As DarkCheckBox
    Private _chkStartMinimized As DarkCheckBox
    Private _chkHotReloadConfig As DarkCheckBox
    Private _chkRunAtWindowsStartup As DarkCheckBox
    Private _numProjectFps As NumericUpDown
    Private _numWindowHeight As NumericUpDown
    Private _chkSaveWindowPos As DarkCheckBox
    Private _numWindowX As NumericUpDown
    Private _numWindowY As NumericUpDown
    Private _numWaveformAmpScale As NumericUpDown
    Private _numOsdDurationMs As NumericUpDown
    Private _numPeakHoldMs As NumericUpDown
    Private _numMeterDecay As NumericUpDown
    Private _numMasterVolumePercent As NumericUpDown
    Private _numMasterBalancePercent As NumericUpDown
    Private _cmbMasterLevelSource As DarkComboBox
    Private _txtCacheDir As TextBox

    ' --- Вкладка 6: Профили ---
    Private _gridChannelProfiles As DataGridView

    ' --- Вкладка 7: Пресеты и Доп. параметры ---
    Private _gridPresets As DataGridView
    Private _txtNativeRootDir As TextBox
    Private _gridCustomParams As DataGridView

    Private Class ComboDeviceItem
        Public Property Id As String
        Public Property Name As String
        Public Overrides Function ToString() As String
            Return Name
        End Function
    End Class

    Public Sub New()
        Me.New(Nothing, Nothing, Nothing, Nothing, Nothing)
    End Sub

    Public Sub New(model As ProjectModel,
                   audioPlayer As NAudioSyncPlayer,
                   midiController As DdjSb3MidiController,
                   broker As yoump.MediaParameterBroker,
                   Optional onSettingsApplied As Action = Nothing)
        InitializeComponent()

        _model = model
        _audioPlayer = audioPlayer
        _midiController = midiController
        _broker = broker
        _onSettingsApplied = onSettingsApplied

        Me.AutoScaleMode = AutoScaleMode.None
        Me.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular)
        Me.Text = "Настройки Pioneer DJ Mixer (config.json)"
        Me.MinimumSize = New Size(900, 620)
        Dim maxWorkH As Integer = 820
        Try
            maxWorkH = Math.Max(620, Screen.PrimaryScreen.WorkingArea.Height - 60)
        Catch
        End Try
        Me.ClientSize = New Size(984, Math.Min(820, maxWorkH))
        Me.StartPosition = FormStartPosition.CenterParent
        Me.FormBorderStyle = FormBorderStyle.Sizable
        Me.MaximizeBox = True
        Me.MinimizeBox = False
        Me.ShowIcon = False

        BuildUiLayout()
        ApplyDarkStyling()
        LoadValuesFromConfig()
    End Sub

    Protected Overrides Sub OnHandleCreated(e As EventArgs)
        MyBase.OnHandleCreated(e)
        ThemeManager.ApplyDwm(Me.Handle)
    End Sub

    Friend Sub BuildUiLayout()
        If _isUiBuilt Then Return
        _isUiBuilt = True
        Me.SuspendLayout()

        _tabs.Location = New Point(0, 0)
        _tabs.Size = New Size(984, 722)
        _tabs.Dock = DockStyle.Fill
        _tabs.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular)

        _tabs.TabPages.Add(CreateAudioAndAsioTab())
        _tabs.TabPages.Add(CreateMicrophoneTab())
        _tabs.TabPages.Add(CreateMidiTab())
        _tabs.TabPages.Add(CreateProcessesTab())
        _tabs.TabPages.Add(CreateUiTab())
        _tabs.TabPages.Add(CreateChannelProfilesTab())
        _tabs.TabPages.Add(CreatePresetsAndCustomTab())

        Dim bottomPanel As New Panel() With {
            .Dock = DockStyle.Bottom,
            .Height = 58,
            .Padding = New Padding(16, 11, 16, 11),
            .BackColor = Color.FromArgb(16, 18, 22)
        }

        Dim btnOpenJson As New Button() With {
            .Text = "Открыть config.json...",
            .Width = 170,
            .Height = 36,
            .Dock = DockStyle.Left,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 9.0F)
        }
        AddHandler btnOpenJson.Click, Sub() OpenConfigFileInEditor()

        Dim btnExportJson As New Button() With {
            .Text = "Экспорт...",
            .Width = 105,
            .Height = 36,
            .Dock = DockStyle.Left,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 9.0F)
        }
        AddHandler btnExportJson.Click, Sub() ExportConfigToFile()

        Dim btnImportJson As New Button() With {
            .Text = "Импорт...",
            .Width = 105,
            .Height = 36,
            .Dock = DockStyle.Left,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 9.0F)
        }
        AddHandler btnImportJson.Click, Sub() ImportConfigFromFile()

        Dim btnCancel As New Button() With {
            .Text = "Отмена",
            .Width = 110,
            .Height = 36,
            .Dock = DockStyle.Right,
            .DialogResult = DialogResult.Cancel,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 9.0F)
        }
        AddHandler btnCancel.Click, Sub() Me.Close()

        Dim btnApply As New Button() With {
            .Text = "Применить",
            .Width = 120,
            .Height = 36,
            .Dock = DockStyle.Right,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 9.0F)
        }
        AddHandler btnApply.Click, Sub()
                                       If SaveValuesToConfigAndApply() Then
                                           RefreshHardwareDiagnosticsLabels()
                                       End If
                                   End Sub

        Dim btnOk As New Button() With {
            .Text = "ОК (Сохранить)",
            .Width = 145,
            .Height = 36,
            .Dock = DockStyle.Right,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        }
        AddHandler btnOk.Click, Sub()
                                    If SaveValuesToConfigAndApply() Then
                                        Me.DialogResult = DialogResult.OK
                                        Me.Close()
                                    End If
                                End Sub

        bottomPanel.Controls.Add(btnImportJson)
        bottomPanel.Controls.Add(New Panel() With {.Width = 8, .Dock = DockStyle.Left})
        bottomPanel.Controls.Add(btnExportJson)
        bottomPanel.Controls.Add(New Panel() With {.Width = 8, .Dock = DockStyle.Left})
        bottomPanel.Controls.Add(btnOpenJson)

        bottomPanel.Controls.Add(btnOk)
        bottomPanel.Controls.Add(New Panel() With {.Width = 8, .Dock = DockStyle.Right})
        bottomPanel.Controls.Add(btnApply)
        bottomPanel.Controls.Add(New Panel() With {.Width = 8, .Dock = DockStyle.Right})
        bottomPanel.Controls.Add(btnCancel)

        Me.Controls.Add(_tabs)
        Me.Controls.Add(bottomPanel)

        Me.ResumeLayout(False)
    End Sub

    Private Function CreateAudioAndAsioTab() As TabPage
        Dim page As New TabPage("1. Аудио / ASIO") With {
            .Size = New Size(PageDefaultWidth, PageDefaultHeight),
            .AutoScroll = True,
            .Padding = New Padding(14),
            .BackColor = Color.FromArgb(18, 20, 24)
        }
        Dim y As Integer = 14

        ' --- ГРУППА 1: Основной выход WASAPI и Bluetooth Hi-Res (LDAC) ---
        Dim grpOut As New DarkGroupBox() With {
            .Text = "Основной выход WASAPI, Bluetooth Hi-Res (Space Travel 2 Ultra / LDAC) и маршрутизация",
            .Location = New Point(14, y),
            .Size = New Size(940, 240),
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right,
            .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        }

        AddLabel(grpOut, "Устройство выхода:", 18, 28)
        _cmbOutputDevice = New DarkComboBox() With {
            .Location = New Point(200, 24),
            .Width = 595
        }
        grpOut.Controls.Add(_cmbOutputDevice)

        Dim btnRefreshOut As New Button() With {
            .Text = "Обновить",
            .Location = New Point(805, 23),
            .Size = New Size(118, 28),
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnRefreshOut.Click, Sub() PopulateOutputDevicesCombo()
        grpOut.Controls.Add(btnRefreshOut)

        AddLabel(grpOut, "Ключ выхода (1-2):", 18, 64)
        _txtOutputKeyword = AddTextBox(grpOut, 200, 60, 150)

        AddLabel(grpOut, "Блокируемые выходы:", 375, 64)
        _txtBlockedKeywords = AddTextBox(grpOut, 550, 60, 373)

        AddLabel(grpOut, "Ключевые слова карты:", 18, 100)
        _txtHardwareKeywords = AddTextBox(grpOut, 200, 96, 325)

        AddLabel(grpOut, "Ключ микрофона:", 540, 100)
        _txtMicKeyword = AddTextBox(grpOut, 685, 96, 238)

        AddLabel(grpOut, "Ключ захвата MAIN:", 18, 136)
        _txtMainCaptureKeyword = AddTextBox(grpOut, 200, 132, 150)

        AddLabel(grpOut, "Ключи Bluetooth LDAC:", 375, 136)
        _txtBtKeywords = AddTextBox(grpOut, 550, 132, 373)

        _chkAutoRouteApps = AddCheckBox(grpOut, "Авто-перевод всех приложений на активный выход", 18, 168, 450)
        _chkSanitizeRegistry = AddCheckBox(grpOut, "Очищать привязку 3-4 в реестре MPC-HC/BE", 480, 168, 440)

        _chkPreferBtOutput = AddCheckBox(grpOut, "Авто-выбор Bluetooth наушников (Space Travel 2 Ultra / LDAC)", 18, 198, 455)
        _chkBtLdacGuard = AddCheckBox(grpOut, "LDAC Guard: Только HD-микрофон BT (>= 32 кГц, без HFP 16 кГц)", 480, 198, 445)

        page.Controls.Add(grpOut)
        y += grpOut.Height + 16

        ' --- ГРУППА 2: ASIO ---
        Dim grpAsio As New DarkGroupBox() With {
            .Text = "Движок ASIO (Ультра-низкая аппаратная задержка)",
            .Location = New Point(14, y),
            .Size = New Size(940, 155),
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right,
            .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        }

        _chkUseAsio = AddCheckBox(grpAsio, "Включить режим ASIO вместо стандартного захвата WASAPI", 18, 28, 500)

        _lblAsioStatus = New Label() With {
            .Text = "Статус ASIO: не активен",
            .Location = New Point(540, 31),
            .AutoSize = True,
            .Font = New Font("Segoe UI", 8.75F, FontStyle.Bold),
            .ForeColor = Color.FromArgb(165, 175, 190),
            .BackColor = Color.Transparent
        }
        grpAsio.Controls.Add(_lblAsioStatus)

        AddLabel(grpAsio, "Драйвер ASIO:", 18, 66)
        _cmbAsioDriver = New DarkComboBox() With {
            .Location = New Point(200, 62),
            .Width = 450
        }
        grpAsio.Controls.Add(_cmbAsioDriver)

        Dim btnRefreshAsio As New Button() With {
            .Text = "Обновить ASIO",
            .Location = New Point(660, 60),
            .Size = New Size(125, 30),
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnRefreshAsio.Click, Sub() PopulateAsioDriversCombo(_cmbAsioDriver.Text)
        grpAsio.Controls.Add(btnRefreshAsio)

        Dim btnAsioPanel As New Button() With {
            .Text = "Панель ASIO...",
            .Location = New Point(793, 60),
            .Size = New Size(130, 30),
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnAsioPanel.Click, Sub() _audioPlayer?.ShowAsioControlPanel()
        grpAsio.Controls.Add(btnAsioPanel)

        AddLabel(grpAsio, "Смещение входа ASIO (Offset):", 18, 108)
        _numAsioInOffset = AddNumeric(grpAsio, 240, 104, 90, 0D, 32D, 2D, 0)

        AddLabel(grpAsio, "Смещение выхода ASIO (Offset):", 360, 108)
        _numAsioOutOffset = AddNumeric(grpAsio, 595, 104, 90, 0D, 32D, 2D, 0)

        page.Controls.Add(grpAsio)
        Return page
    End Function

    Private Function CreateMicrophoneTab() As TabPage
        Dim page As New TabPage("2. Микрофон / IR") With {
            .Size = New Size(PageDefaultWidth, PageDefaultHeight),
            .AutoScroll = True,
            .Padding = New Padding(14),
            .BackColor = Color.FromArgb(18, 20, 24)
        }

        Dim grpMic As New DarkGroupBox() With {
            .Text = "Микрофон (MAIN MIC), Студийный тракт, Talkover, Zero-Latency IR и C++ APO",
            .Location = New Point(14, 14),
            .Size = New Size(940, 415),
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right,
            .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        }

        _chkMicStudioProc = AddCheckBox(grpMic, "Студийная обработка микрофона (HPF + Gate + Vocal Comp)", 18, 28, 540)
        _chkMicDeEsser = AddCheckBox(grpMic, "Динамический De-Esser (подавление сибилянтов «С/Щ» 5.5–8.5 кГц)", 18, 56, 540)
        _chkMicPlosive24dB = AddCheckBox(grpMic, "Фильтр взрывных согласных 24 dB/oct LR4 (80 Hz Anti-Pop)", 18, 84, 540)
        _chkMicAntiFeedback = AddCheckBox(grpMic, "Подавитель заводки / свиста + Инверсия фазы 180° (Anti-Feedback)", 18, 112, 540)
        _chkMicMonitoring = AddCheckBox(grpMic, "Мониторинг микрофона в колонки / наушники (Zero-Latency DSP)", 18, 140, 540)
        _chkLimiterEnabled = AddCheckBox(grpMic, "Мягкий лимитер Pioneer Soft-Knee Limiter (защита от клиппинга 0 dBFS)", 18, 168, 550)

        AddLabel(grpMic, "Порог Noise Gate:", 18, 204)
        _numMicGate = AddNumeric(grpMic, 190, 200, 100, 0D, 0.2D, 0.0005D, 4)

        AddLabel(grpMic, "Задержка захвата (мс):", 295, 204)
        _numMicCapLatency = AddNumeric(grpMic, 475, 200, 85, 3D, 100D, 1D, 0)

        AddLabel(grpMic, "Задержка вывода (мс):", 18, 238)
        _numMicOutLatency = AddNumeric(grpMic, 190, 234, 100, 5D, 200D, 1D, 0)

        AddLabel(grpMic, "Буфер микрофона (мс):", 295, 238)
        _numMicBufDuration = AddNumeric(grpMic, 475, 234, 85, 30D, 1000D, 10D, 0)

        AddLabel(grpMic, "TALKOVER (Ducking):", 18, 272)
        _cmbMicTalkoverMode = New DarkComboBox() With {
            .Location = New Point(200, 268),
            .Width = 265
        }
        For tMode As Integer = 0 To 2
            _cmbMicTalkoverMode.Items.Add($"{tMode}: {AudioRoutingConfig.GetMicTalkoverModeName(tMode)}")
        Next
        grpMic.Controls.Add(_cmbMicTalkoverMode)

        AddLabel(grpMic, "XLR 1/2/3/4:", 475, 272)
        _cmbMicXlrMode = New DarkComboBox() With {
            .Location = New Point(580, 268),
            .Width = 343
        }
        For xMode As Integer = 0 To 3
            _cmbMicXlrMode.Items.Add($"{xMode}: {AudioRoutingConfig.GetMicXlrInputModeName(xMode)}")
        Next
        grpMic.Controls.Add(_cmbMicXlrMode)

        AddLabel(grpMic, "Пресет ИК-свёртки (IR):", 18, 306)
        _cmbMicIrPreset = New DarkComboBox() With {
            .Location = New Point(200, 302),
            .Width = 265
        }
        For pid As Integer = 0 To 7
            _cmbMicIrPreset.Items.Add($"{pid}: {AudioRoutingConfig.GetMicIrPresetName(pid)}")
        Next
        AddHandler _cmbMicIrPreset.SelectedIndexChanged,
            Sub()
                If _isLoadingConfigValues Then Return
                Dim selPreset As Integer = _cmbMicIrPreset.SelectedIndex
                If selPreset = 5 OrElse selPreset = 6 Then
                    If _numMicIrMixPercent.Value > 65D Then _numMicIrMixPercent.Value = 38D
                ElseIf selPreset >= 1 AndAlso selPreset <= 4 Then
                    If _numMicIrMixPercent.Value < 50D Then _numMicIrMixPercent.Value = 100D
                End If
            End Sub
        grpMic.Controls.Add(_cmbMicIrPreset)

        AddLabel(grpMic, "Баланс IR Wet/Dry (%):", 475, 306)
        _numMicIrMixPercent = AddNumeric(grpMic, 650, 302, 85, 0D, 100D, 5D, 0)

        AddLabel(grpMic, "Свой импульс (.wav):", 18, 340)
        _txtMicCustomIrPath = AddTextBox(grpMic, 200, 336, 585)

        Dim btnBrowseIrWav As New Button() With {
            .Text = "Обзор .wav...",
            .Location = New Point(795, 335),
            .Size = New Size(128, 28),
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnBrowseIrWav.Click, Sub() BrowseCustomMicIrWav()
        grpMic.Controls.Add(btnBrowseIrWav)

        _lblDspApoStatus = New Label() With {
            .Text = "Статус DSP / APO: проверка...",
            .Location = New Point(18, 378),
            .AutoSize = True,
            .Font = New Font("Segoe UI", 8.75F, FontStyle.Bold),
            .ForeColor = Color.FromArgb(0, 200, 255),
            .BackColor = Color.Transparent
        }
        grpMic.Controls.Add(_lblDspApoStatus)

        Dim btnInstallApo As New Button() With {
            .Text = "Установить C++ APO" & vbCrLf & "[Требуются права Админа]",
            .Location = New Point(580, 28),
            .Size = New Size(343, 156),
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 9.5F, FontStyle.Bold)
        }
        AddHandler btnInstallApo.Click, Sub()
                                            _audioPlayer?.InstallKernelApoForWindows11()
                                            RefreshHardwareDiagnosticsLabels()
                                        End Sub
        grpMic.Controls.Add(btnInstallApo)

        Dim btnCheckApo As New Button() With {
            .Text = "Проверить статус DSP / APO",
            .Location = New Point(580, 194),
            .Size = New Size(343, 58),
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.75F)
        }
        AddHandler btnCheckApo.Click, Sub() RefreshHardwareDiagnosticsLabels()
        grpMic.Controls.Add(btnCheckApo)

        page.Controls.Add(grpMic)
        Return page
    End Function

    Private Sub BrowseCustomMicIrWav()
        Using ofd As New OpenFileDialog() With {
            .Title = "Выберите импульсный файл микрофона или ревербератора (.wav)",
            .Filter = "WAV Impulse Response (*.wav)|*.wav|Все файлы (*.*)|*.*"
        }
            Dim curPath As String = _txtMicCustomIrPath.Text.Trim()
            If File.Exists(curPath) Then
                ofd.FileName = curPath
            End If
            If ofd.ShowDialog(Me) = DialogResult.OK Then
                Dim targetSr As Integer = If(_audioPlayer IsNot Nothing, _audioPlayer.CurrentSampleRate, 48000)
                If NativeCppAutoBuilderAndInstaller.LoadAndUploadCustomWavIr(ofd.FileName, targetSr) Then
                    _txtMicCustomIrPath.Text = ofd.FileName
                    If _cmbMicIrPreset.Items.Count > 7 Then
                        _cmbMicIrPreset.SelectedIndex = 7
                    End If
                Else
                    MessageBox.Show(Me,
                                    "Не удалось прочитать или ресемплировать импульсный .wav файл.",
                                    "Zero-Latency IR",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Warning)
                End If
            End If
        End Using
    End Sub

    Private Function CreateMidiTab() As TabPage
        Dim page As New TabPage("3. MIDI") With {
            .Size = New Size(PageDefaultWidth, PageDefaultHeight),
            .AutoScroll = True,
            .Padding = New Padding(14),
            .BackColor = Color.FromArgb(18, 20, 24)
        }

        Dim grpMidi As New DarkGroupBox() With {
            .Text = "Контроллер Pioneer DDJ-SB3 / Serato MIDI",
            .Location = New Point(14, 14),
            .Size = New Size(940, 360),
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right,
            .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        }

        AddLabel(grpMidi, "Активное MIDI-устройство:", 18, 34)
        _cmbMidiDevice = New DarkComboBox() With {
            .Location = New Point(245, 30),
            .Width = 410
        }
        grpMidi.Controls.Add(_cmbMidiDevice)

        Dim btnRefreshMidi As New Button() With {
            .Text = "Обновить список",
            .Location = New Point(665, 29),
            .Size = New Size(125, 30),
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnRefreshMidi.Click, Sub()
                                             PopulateMidiDevicesCombo(_cmbMidiDevice.Text)
                                             RefreshHardwareDiagnosticsLabels()
                                         End Sub
        grpMidi.Controls.Add(btnRefreshMidi)

        Dim btnReconnectMidi As New Button() With {
            .Text = "Подключить MIDI",
            .Location = New Point(798, 29),
            .Size = New Size(125, 30),
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
        }
        AddHandler btnReconnectMidi.Click, Sub()
                                               Dim prevSelection As String = _cmbMidiDevice.Text
                                               PopulateMidiDevicesCombo(prevSelection)
                                               If _midiController IsNot Nothing Then
                                                   Dim selectedName As String = _cmbMidiDevice.Text
                                                   Dim matchedIdx As Integer = FindMidiDeviceIndexByName(selectedName)
                                                   If matchedIdx >= 0 Then
                                                       _midiController.ConnectDevice(matchedIdx)
                                                   Else
                                                       _midiController.TryAutoConnect()
                                                   End If
                                                   If _midiController.IsConnected AndAlso Not String.IsNullOrWhiteSpace(_midiController.ConnectedDeviceName) Then
                                                       PopulateMidiDevicesCombo(_midiController.ConnectedDeviceName)
                                                   End If
                                               End If
                                               RefreshHardwareDiagnosticsLabels()
                                           End Sub
        grpMidi.Controls.Add(btnReconnectMidi)

        _lblMidiStatus = New Label() With {
            .Text = "Статус MIDI: проверка...",
            .Location = New Point(245, 64),
            .AutoSize = True,
            .Font = New Font("Segoe UI", 8.75F, FontStyle.Bold),
            .ForeColor = Color.FromArgb(0, 200, 255),
            .BackColor = Color.Transparent
        }
        grpMidi.Controls.Add(_lblMidiStatus)

        AddLabel(grpMidi, "Приоритетные имена MIDI:", 18, 98)
        _txtPreferredMidiNames = AddTextBox(grpMidi, 245, 94, 678)

        AddLabel(grpMidi, "Интервал переподключения (мс):", 18, 138)
        _numMidiReconnectMs = AddNumeric(grpMidi, 265, 134, 105, 500D, 30000D, 250D, 0)

        AddLabel(grpMidi, "Мертвая зона ручек (0..0.08):", 445, 138)
        _numMidiDeadzone = AddNumeric(grpMidi, 665, 134, 105, 0.001D, 0.08D, 0.002D, 3)

        AddLabel(grpMidi, "Окно дампа ручек (мс):", 18, 178)
        _numMidiDumpWindowMs = AddNumeric(grpMidi, 265, 174, 105, 100D, 5000D, 100D, 0)

        _chkCrossfaderBalance = AddCheckBox(grpMidi, "Кроссфейдер DDJ-SB3 управляет балансом L/R (MASTER BALANCE)", 18, 220, 620)
        _chkSendSeratoSysEx = AddCheckBox(grpMidi, "Отправлять пробуждающий SysEx-запрос Serato при подключении DDJ-SB3", 18, 254, 640)

        Dim btnResetActiveEq As New Button() With {
            .Text = "Сбросить EQ, Filter и Баланс активного канала в центр (0 dB)",
            .Location = New Point(18, 298),
            .Size = New Size(440, 36),
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.75F)
        }
        AddHandler btnResetActiveEq.Click, Sub() ResetActiveTrackEqAndBalanceToCenter()
        grpMidi.Controls.Add(btnResetActiveEq)

        page.Controls.Add(grpMidi)
        Return page
    End Function

    Private Function CreateProcessesTab() As TabPage
        Dim page As New TabPage("4. Процессы") With {
            .Size = New Size(PageDefaultWidth, PageDefaultHeight),
            .AutoScroll = True,
            .Padding = New Padding(14),
            .BackColor = Color.FromArgb(18, 20, 24)
        }

        Dim grpTimers As New DarkGroupBox() With {
            .Text = "Тайминги сканирования аудиосессий Windows",
            .Location = New Point(14, 12),
            .Size = New Size(940, 82),
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right,
            .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        }

        AddLabel(grpTimers, "Опрос пиков (мс):", 18, 34)
        _numSessionPollMs = AddNumeric(grpTimers, 160, 30, 90, 5D, 200D, 5D, 0)

        AddLabel(grpTimers, "Поиск сессий (мс):", 275, 34)
        _numDiscoveryMs = AddNumeric(grpTimers, 420, 30, 95, 100D, 5000D, 50D, 0)

        AddLabel(grpTimers, "Таймаут неактивности (мс):", 550, 34)
        _numInactiveTimeoutMs = AddNumeric(grpTimers, 760, 30, 100, 2000D, 120000D, 1000D, 0)

        page.Controls.Add(grpTimers)

        Dim grpLists As New DarkGroupBox() With {
            .Text = "Закрепленный порядок каналов, Игнорируемые процессы и Таблица имен",
            .Location = New Point(14, 104),
            .Size = New Size(940, 560),
            .Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right,
            .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        }

        AddLabel(grpLists, "Закрепленные каналы (по 1 в строке):", 18, 26)
        _txtPinnedChannels = New TextBox() With {
            .Location = New Point(18, 48),
            .Size = New Size(270, 190),
            .Multiline = True,
            .ScrollBars = ScrollBars.Vertical,
            .Font = New Font("Consolas", 9.0F)
        }
        grpLists.Controls.Add(_txtPinnedChannels)

        AddLabel(grpLists, "Игнорируемые процессы (по 1 в строке):", 18, 248)
        _txtIgnoredProcesses = New TextBox() With {
            .Location = New Point(18, 270),
            .Size = New Size(270, 230),
            .Multiline = True,
            .ScrollBars = ScrollBars.Vertical,
            .Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left,
            .Font = New Font("Consolas", 9.0F)
        }
        grpLists.Controls.Add(_txtIgnoredProcesses)

        Dim btnResetProcLists As New Button() With {
            .Text = "Списки по умолчанию",
            .Location = New Point(18, 510),
            .Size = New Size(270, 34),
            .Anchor = AnchorStyles.Bottom Or AnchorStyles.Left,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnResetProcLists.Click, Sub()
                                                Dim defRules As New ProcessRulesConfig()
                                                _txtPinnedChannels.Lines = defRules.PinnedChannels.ToArray()
                                                _txtIgnoredProcesses.Lines = defRules.IgnoredProcesses.ToArray()
                                            End Sub
        grpLists.Controls.Add(btnResetProcLists)

        AddLabel(grpLists, "Таблица переименования (exe -> отображаемый канал):", 310, 26)
        _gridAliases = CreateStyledGrid()
        _gridAliases.Location = New Point(310, 48)
        _gridAliases.Size = New Size(612, 452)
        _gridAliases.Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right

        Dim colProc As New DataGridViewTextBoxColumn() With {
            .Name = "ProcKey",
            .HeaderText = "Имя процесса (без .exe)",
            .Width = 230
        }
        Dim colAlias As New DataGridViewTextBoxColumn() With {
            .Name = "AliasVal",
            .HeaderText = "Отображаемый канал в микшере",
            .AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        }
        _gridAliases.Columns.Add(colProc)
        _gridAliases.Columns.Add(colAlias)
        grpLists.Controls.Add(_gridAliases)

        Dim btnScanActiveSessions As New Button() With {
            .Text = "Добавить активные аудио-процессы Windows",
            .Location = New Point(310, 510),
            .Size = New Size(330, 34),
            .Anchor = AnchorStyles.Bottom Or AnchorStyles.Left,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnScanActiveSessions.Click, Sub() ScanAndAddActiveAudioProcessesToGrid()
        grpLists.Controls.Add(btnScanActiveSessions)

        Dim btnResetAliases As New Button() With {
            .Text = "Таблица имен по умолчанию",
            .Location = New Point(652, 510),
            .Size = New Size(270, 34),
            .Anchor = AnchorStyles.Bottom Or AnchorStyles.Right,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnResetAliases.Click, Sub()
                                              Dim defRules As New ProcessRulesConfig()
                                              _gridAliases.Rows.Clear()
                                              For Each kvp In defRules.ProcessAliases
                                                  _gridAliases.Rows.Add(kvp.Key, kvp.Value)
                                              Next
                                          End Sub
        grpLists.Controls.Add(btnResetAliases)

        page.Controls.Add(grpLists)
        Return page
    End Function

    Private Function CreateUiTab() As TabPage
        Dim page As New TabPage("5. Интерфейс") With {
            .Size = New Size(PageDefaultWidth, PageDefaultHeight),
            .AutoScroll = True,
            .Padding = New Padding(14),
            .BackColor = Color.FromArgb(18, 20, 24)
        }

        Dim grpUi As New DarkGroupBox() With {
            .Text = "Параметры окна, Автозагрузки, Direct3D 11 рендеринга и секции MASTER",
            .Location = New Point(14, 14),
            .Size = New Size(940, 465),
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right,
            .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        }

        _chkDarkTheme = AddCheckBox(grpUi, "Темная тема оформления (Pioneer Dark Theme)", 18, 30, 410)
        _chkAlwaysOnTop = AddCheckBox(grpUi, "Поверх всех окон (Always on Top [PIN])", 460, 30, 430)
        _chkStartMinimized = AddCheckBox(grpUi, "Запускать свернутым в системный трей", 18, 62, 410)
        _chkHotReloadConfig = AddCheckBox(grpUi, "Авто-подхват внешних изменений config.json на лету", 460, 62, 450)
        _chkRunAtWindowsStartup = AddCheckBox(grpUi, "Запускать микшер автоматически при входе в Windows (Автозагрузка)", 18, 94, 650)

        AddLabel(grpUi, "Частота кадров Direct3D 11 (FPS):", 18, 136)
        _numProjectFps = AddNumeric(grpUi, 280, 132, 100, 15D, 240D, 5D, 0)

        AddLabel(grpUi, "Высота окна микшера (px):", 460, 136)
        _numWindowHeight = AddNumeric(grpUi, 665, 132, 100, 390D, 2160D, 10D, 0)

        _chkSaveWindowPos = AddCheckBox(grpUi, "Фиксировать координаты окна (X / Y):", 18, 178, 300)
        _numWindowX = AddNumeric(grpUi, 335, 174, 95, -10000D, 10000D, 10D, 0)
        _numWindowY = AddNumeric(grpUi, 440, 174, 95, -10000D, 10000D, 10D, 0)
        AddHandler _chkSaveWindowPos.CheckedChanged, Sub()
                                                         _numWindowX.Enabled = _chkSaveWindowPos.Checked
                                                         _numWindowY.Enabled = _chkSaveWindowPos.Checked
                                                     End Sub

        Dim btnCaptureWinPos As New Button() With {
            .Text = "Взять текущие X / Y и высоту окна",
            .Location = New Point(555, 172),
            .Size = New Size(265, 29),
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnCaptureWinPos.Click, Sub()
                                               If Me.Owner IsNot Nothing Then
                                                   _chkSaveWindowPos.Checked = True
                                                   _numWindowX.Value = ClampDec(Me.Owner.Location.X, _numWindowX)
                                                   _numWindowY.Value = ClampDec(Me.Owner.Location.Y, _numWindowY)
                                                   _numWindowHeight.Value = ClampDec(Math.Max(390, Me.Owner.ClientSize.Height), _numWindowHeight)
                                               End If
                                           End Sub
        grpUi.Controls.Add(btnCaptureWinPos)

        AddLabel(grpUi, "Базовый масштаб волны (Zoom):", 18, 220)
        _numWaveformAmpScale = AddNumeric(grpUi, 280, 216, 100, 0.25D, 4D, 0.25D, 2)

        AddLabel(grpUi, "Время показа OSD-баннера (мс):", 460, 220)
        _numOsdDurationMs = AddNumeric(grpUi, 710, 216, 100, 500D, 10000D, 250D, 0)

        AddLabel(grpUi, "Удержание пиков Peak Hold (мс):", 18, 262)
        _numPeakHoldMs = AddNumeric(grpUi, 280, 258, 100, 100D, 5000D, 50D, 0)

        AddLabel(grpUi, "Коэффициент затухания LED:", 460, 262)
        _numMeterDecay = AddNumeric(grpUi, 710, 258, 100, 0.5D, 0.98D, 0.02D, 2)

        AddLabel(grpUi, "Громкость MASTER (%):", 18, 304)
        _numMasterVolumePercent = AddNumeric(grpUi, 280, 300, 100, 0D, 100D, 1D, 0)

        AddLabel(grpUi, "Баланс MASTER (-100 L .. +100 R):", 460, 304)
        _numMasterBalancePercent = AddNumeric(grpUi, 710, 300, 90, -100D, 100D, 5D, 0)

        Dim btnCenterBal As New Button() With {
            .Text = "Центр (0)",
            .Location = New Point(810, 299),
            .Size = New Size(105, 28),
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnCenterBal.Click, Sub() _numMasterBalancePercent.Value = 0D
        grpUi.Controls.Add(btnCenterBal)

        AddLabel(grpUi, "Источник индикатора MASTER:", 18, 348)
        _cmbMasterLevelSource = New DarkComboBox() With {
            .Location = New Point(280, 344),
            .Width = 210
        }
        _cmbMasterLevelSource.Items.AddRange(New Object() {"Auto", "Software", "Hardware"})
        grpUi.Controls.Add(_cmbMasterLevelSource)

        AddLabel(grpUi, "Папка кэша пиков волны (.wpk):", 18, 394)
        _txtCacheDir = AddTextBox(grpUi, 280, 390, 440)

        Dim btnBrowseCache As New Button() With {
            .Text = "Обзор...",
            .Location = New Point(730, 389),
            .Size = New Size(85, 29),
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnBrowseCache.Click, Sub()
                                             Using dlg As New FolderBrowserDialog()
                                                 If Directory.Exists(_txtCacheDir.Text) Then dlg.SelectedPath = _txtCacheDir.Text
                                                 If dlg.ShowDialog(Me) = DialogResult.OK Then _txtCacheDir.Text = dlg.SelectedPath
                                             End Using
                                         End Sub
        grpUi.Controls.Add(btnBrowseCache)

        Dim btnClearCache As New Button() With {
            .Text = "Очистить .wpk",
            .Location = New Point(822, 389),
            .Size = New Size(100, 29),
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnClearCache.Click, Sub() ClearWaveformPeakCacheFiles()
        grpUi.Controls.Add(btnClearCache)

        page.Controls.Add(grpUi)
        Return page
    End Function

    Private Function CreateChannelProfilesTab() As TabPage
        Dim page As New TabPage("6. Профили") With {
            .Size = New Size(PageDefaultWidth, PageDefaultHeight),
            .Padding = New Padding(14),
            .BackColor = Color.FromArgb(18, 20, 24)
        }

        Dim topBar As New Panel() With {
            .Dock = DockStyle.Top,
            .Height = 36,
            .BackColor = Color.Transparent
        }

        Dim lblInfo As New Label() With {
            .Text = "Сохраненные профили каналов (0.5 = 0 dB, Zoom: 0.25 .. 4.0):",
            .Location = New Point(0, 8),
            .AutoSize = True,
            .ForeColor = Color.FromArgb(180, 188, 200),
            .BackColor = Color.Transparent
        }

        Dim btnSyncLive As New Button() With {
            .Text = "Обновить из микшера",
            .Width = 155,
            .Height = 28,
            .Dock = DockStyle.Right,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnSyncLive.Click, Sub()
                                          MixerHardwareStateStore.SyncFromModelAndPlayer(_model, _audioPlayer)
                                          PopulateChannelProfilesGrid()
                                      End Sub

        Dim btnResetSelectedProfile As New Button() With {
            .Text = "Сбросить выбранный",
            .Width = 150,
            .Height = 28,
            .Dock = DockStyle.Right,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnResetSelectedProfile.Click, Sub()
                                                      _gridChannelProfiles.EndEdit()
                                                      If _gridChannelProfiles.CurrentRow IsNot Nothing AndAlso Not _gridChannelProfiles.CurrentRow.IsNewRow Then
                                                          ResetProfileRowToNeutral(_gridChannelProfiles.CurrentRow)
                                                          _channelProfilesEditedInGrid = True
                                                      End If
                                                  End Sub

        Dim btnResetAllProfiles As New Button() With {
            .Text = "Сбросить все в 0 dB",
            .Width = 150,
            .Height = 28,
            .Dock = DockStyle.Right,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnResetAllProfiles.Click, Sub()
                                                  _gridChannelProfiles.EndEdit()
                                                  For Each row As DataGridViewRow In _gridChannelProfiles.Rows
                                                      If row.IsNewRow Then Continue For
                                                      ResetProfileRowToNeutral(row)
                                                  Next
                                                  _channelProfilesEditedInGrid = True
                                              End Sub

        topBar.Controls.Add(lblInfo)
        topBar.Controls.Add(btnResetAllProfiles)
        topBar.Controls.Add(New Panel() With {.Width = 6, .Dock = DockStyle.Right})
        topBar.Controls.Add(btnResetSelectedProfile)
        topBar.Controls.Add(New Panel() With {.Width = 6, .Dock = DockStyle.Right})
        topBar.Controls.Add(btnSyncLive)

        _gridChannelProfiles = CreateStyledGrid()
        _gridChannelProfiles.Dock = DockStyle.Fill
        _gridChannelProfiles.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "ChName", .HeaderText = "Канал", .Width = 150})
        _gridChannelProfiles.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "Trim", .HeaderText = "Trim", .Width = 65})
        _gridChannelProfiles.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "EqHi", .HeaderText = "HI", .Width = 65})
        _gridChannelProfiles.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "EqMid", .HeaderText = "MID", .Width = 65})
        _gridChannelProfiles.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "EqLow", .HeaderText = "LOW", .Width = 65})
        _gridChannelProfiles.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "ColorFx", .HeaderText = "ColorFX", .Width = 70})

        Dim fxCol As New DataGridViewComboBoxColumn() With {
            .Name = "ColorFxMode",
            .HeaderText = "Режим FX",
            .Width = 110,
            .FlatStyle = FlatStyle.Flat,
            .DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
        }
        fxCol.DefaultCellStyle.BackColor = Color.FromArgb(22, 25, 32)
        fxCol.DefaultCellStyle.ForeColor = Color.FromArgb(240, 245, 255)
        fxCol.Items.AddRange("0: FILTER", "1: SPACE", "2: CRUSH", "3: LOUD")
        _gridChannelProfiles.Columns.Add(fxCol)

        _gridChannelProfiles.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "Fader", .HeaderText = "Fader", .Width = 65})
        _gridChannelProfiles.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "WaveZoom", .HeaderText = "Zoom", .Width = 65})
        _gridChannelProfiles.Columns.Add(New DataGridViewCheckBoxColumn() With {.Name = "IsMuted", .HeaderText = "Mute", .Width = 55})
        _gridChannelProfiles.Columns.Add(New DataGridViewCheckBoxColumn() With {.Name = "IsSolo", .HeaderText = "Solo", .Width = 55})

        AddHandler _gridChannelProfiles.DefaultValuesNeeded, Sub(s, e)
                                                                 ResetProfileRowToNeutral(e.Row)
                                                             End Sub
        AddHandler _gridChannelProfiles.CellValueChanged, Sub(s, e)
                                                              If Not _isLoadingConfigValues Then _channelProfilesEditedInGrid = True
                                                          End Sub
        AddHandler _gridChannelProfiles.UserDeletedRow, Sub(s, e)
                                                            If Not _isLoadingConfigValues Then _channelProfilesEditedInGrid = True
                                                        End Sub

        page.Controls.Add(_gridChannelProfiles)
        page.Controls.Add(topBar)
        Return page
    End Function

    Private Shared Sub ResetProfileRowToNeutral(row As DataGridViewRow)
        If row Is Nothing Then Return
        row.Cells("Trim").Value = "0.5"
        row.Cells("EqHi").Value = "0.5"
        row.Cells("EqMid").Value = "0.5"
        row.Cells("EqLow").Value = "0.5"
        row.Cells("ColorFx").Value = "0.5"
        row.Cells("ColorFxMode").Value = "0: FILTER"
        row.Cells("Fader").Value = "1"
        If row.DataGridView IsNot Nothing AndAlso row.DataGridView.Columns.Contains("WaveZoom") Then
            row.Cells("WaveZoom").Value = "1"
        End If
        If row.DataGridView IsNot Nothing AndAlso row.DataGridView.Columns.Contains("IsMuted") Then
            row.Cells("IsMuted").Value = False
        End If
        If row.DataGridView IsNot Nothing AndAlso row.DataGridView.Columns.Contains("IsSolo") Then
            row.Cells("IsSolo").Value = False
        End If
    End Sub

    Private Function CreatePresetsAndCustomTab() As TabPage
        Dim page As New TabPage("7. Пресеты") With {
            .Size = New Size(PageDefaultWidth, PageDefaultHeight),
            .Padding = New Padding(14),
            .BackColor = Color.FromArgb(18, 20, 24)
        }

        Dim split As New SplitContainer() With {
            .Size = New Size(948, 660),
            .Dock = DockStyle.Fill,
            .Orientation = Orientation.Horizontal,
            .Panel1MinSize = 160,
            .Panel2MinSize = 160
        }
        Try
            split.SplitterDistance = 320
        Catch
        End Try

        Dim presetsTopBar As New Panel() With {
            .Dock = DockStyle.Top,
            .Height = 34,
            .BackColor = Color.Transparent
        }

        Dim lblPresets As New Label() With {
            .Text = "Пресеты эквалайзера (меню ПКМ -> Пресеты эквалайзера):",
            .Location = New Point(0, 7),
            .AutoSize = True,
            .ForeColor = Color.FromArgb(180, 188, 200),
            .BackColor = Color.Transparent
        }

        Dim btnApplySelectedPreset As New Button() With {
            .Text = "Применить к каналу",
            .Width = 155,
            .Height = 28,
            .Dock = DockStyle.Right,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
        }
        AddHandler btnApplySelectedPreset.Click, Sub() ApplySelectedPresetRowToActiveTrack()

        Dim btnSaveCurrentTrackAsPreset As New Button() With {
            .Text = "+ Из активного канала",
            .Width = 165,
            .Height = 28,
            .Dock = DockStyle.Right,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnSaveCurrentTrackAsPreset.Click, Sub() AddPresetRowFromActiveTrack()

        Dim btnRestoreDefaultPresets As New Button() With {
            .Text = "Заводские пресеты",
            .Width = 145,
            .Height = 28,
            .Dock = DockStyle.Right,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnRestoreDefaultPresets.Click, Sub() RestoreFactoryPresetsInGrid()

        presetsTopBar.Controls.Add(lblPresets)
        presetsTopBar.Controls.Add(btnRestoreDefaultPresets)
        presetsTopBar.Controls.Add(New Panel() With {.Width = 6, .Dock = DockStyle.Right})
        presetsTopBar.Controls.Add(btnSaveCurrentTrackAsPreset)
        presetsTopBar.Controls.Add(New Panel() With {.Width = 6, .Dock = DockStyle.Right})
        presetsTopBar.Controls.Add(btnApplySelectedPreset)

        _gridPresets = CreateStyledGrid()
        _gridPresets.Dock = DockStyle.Fill
        _gridPresets.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "PresetName", .HeaderText = "Название пресета", .Width = 210})
        _gridPresets.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "Trim", .HeaderText = "Trim", .Width = 70})
        _gridPresets.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "EqHi", .HeaderText = "HI", .Width = 70})
        _gridPresets.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "EqMid", .HeaderText = "MID", .Width = 70})
        _gridPresets.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "EqLow", .HeaderText = "LOW", .Width = 70})
        _gridPresets.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "ColorFx", .HeaderText = "ColorFX", .Width = 75})

        Dim pFxCol As New DataGridViewComboBoxColumn() With {
            .Name = "ColorFxMode",
            .HeaderText = "Режим FX",
            .Width = 110,
            .FlatStyle = FlatStyle.Flat,
            .DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
        }
        pFxCol.DefaultCellStyle.BackColor = Color.FromArgb(22, 25, 32)
        pFxCol.DefaultCellStyle.ForeColor = Color.FromArgb(240, 245, 255)
        pFxCol.Items.AddRange("0: FILTER", "1: SPACE", "2: CRUSH", "3: LOUD")
        _gridPresets.Columns.Add(pFxCol)
        _gridPresets.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "Fader", .HeaderText = "Fader", .Width = 70})

        AddHandler _gridPresets.DefaultValuesNeeded, Sub(s, e)
                                                         ResetProfileRowToNeutral(e.Row)
                                                     End Sub

        split.Panel1.Controls.Add(_gridPresets)
        split.Panel1.Controls.Add(presetsTopBar)

        Dim nativeRootPanel As New Panel() With {
            .Dock = DockStyle.Top,
            .Height = 62,
            .BackColor = Color.Transparent
        }

        AddLabel(nativeRootPanel, "Корневая папка C++ модулей (NativeRootDirectory, пусто = авто):", 0, 4)
        _txtNativeRootDir = AddTextBox(nativeRootPanel, 470, 0, 350)

        Dim btnBrowseNativeRoot As New Button() With {
            .Text = "Обзор...",
            .Location = New Point(830, -1),
            .Size = New Size(90, 27),
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Segoe UI", 8.5F)
        }
        AddHandler btnBrowseNativeRoot.Click, Sub()
                                                  Using dlg As New FolderBrowserDialog()
                                                      If Directory.Exists(_txtNativeRootDir.Text) Then dlg.SelectedPath = _txtNativeRootDir.Text
                                                      If dlg.ShowDialog(Me) = DialogResult.OK Then _txtNativeRootDir.Text = dlg.SelectedPath
                                                  End Using
                                              End Sub
        nativeRootPanel.Controls.Add(btnBrowseNativeRoot)

        Dim lblCustom As New Label() With {
            .Text = "Дополнительные пользовательские параметры (CustomParameters в config.json):",
            .Location = New Point(0, 36),
            .AutoSize = True,
            .ForeColor = Color.FromArgb(180, 188, 200),
            .BackColor = Color.Transparent
        }
        nativeRootPanel.Controls.Add(lblCustom)

        _gridCustomParams = CreateStyledGrid()
        _gridCustomParams.Dock = DockStyle.Fill
        _gridCustomParams.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "ParamKey", .HeaderText = "Ключ (Key)", .Width = 280})
        _gridCustomParams.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "ParamVal", .HeaderText = "Значение (Value)", .AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill})

        split.Panel2.Controls.Add(_gridCustomParams)
        split.Panel2.Controls.Add(nativeRootPanel)

        page.Controls.Add(split)
        Return page
    End Function

    Private Shared Function CreateStyledGrid() As DataGridView
        Dim g As New DataGridView() With {
            .AllowUserToAddRows = True,
            .AllowUserToDeleteRows = True,
            .RowHeadersVisible = True,
            .RowHeadersWidth = 28,
            .SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            .MultiSelect = False,
            .EditMode = DataGridViewEditMode.EditOnEnter,
            .BackgroundColor = Color.FromArgb(20, 22, 28),
            .GridColor = Color.FromArgb(46, 52, 66),
            .BorderStyle = BorderStyle.FixedSingle,
            .CellBorderStyle = DataGridViewCellBorderStyle.Single,
            .ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            .RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            .EnableHeadersVisualStyles = False,
            .Font = New Font("Segoe UI", 8.75F)
        }
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(28, 32, 42)
        g.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(230, 238, 250)
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(28, 32, 42)
        g.ColumnHeadersHeight = 32

        g.RowHeadersDefaultCellStyle.BackColor = Color.FromArgb(24, 27, 36)
        g.RowHeadersDefaultCellStyle.ForeColor = Color.FromArgb(180, 190, 210)
        g.RowHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 110, 185)

        g.DefaultCellStyle.BackColor = Color.FromArgb(22, 25, 32)
        g.DefaultCellStyle.ForeColor = Color.FromArgb(240, 245, 255)
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 130, 210)
        g.DefaultCellStyle.SelectionForeColor = Color.White

        AddHandler g.DataError, Sub(s, e)
                                    e.ThrowException = False
                                    e.Cancel = False
                                End Sub

        AddHandler g.CurrentCellDirtyStateChanged, Sub(s, e)
                                                       If g.IsCurrentCellDirty AndAlso
                                                          (TypeOf g.CurrentCell Is DataGridViewCheckBoxCell OrElse
                                                           TypeOf g.CurrentCell Is DataGridViewComboBoxCell) Then
                                                           g.CommitEdit(DataGridViewDataErrorContexts.Commit)
                                                       End If
                                                   End Sub

        AddHandler g.EditingControlShowing, Sub(s, e)
                                                If e.Control IsNot Nothing Then
                                                    e.Control.BackColor = Color.FromArgb(26, 30, 38)
                                                    e.Control.ForeColor = Color.FromArgb(245, 248, 255)
                                                End If
                                            End Sub
        Return g
    End Function

    Private Shared Sub AddLabel(parent As Control, text As String, x As Integer, y As Integer)
        Dim lbl As New Label() With {
            .Text = text,
            .Location = New Point(x, y),
            .AutoSize = True,
            .Font = New Font("Segoe UI", 9.0F, FontStyle.Regular),
            .ForeColor = Color.FromArgb(215, 224, 238),
            .BackColor = Color.Transparent
        }
        parent.Controls.Add(lbl)
    End Sub

    Private Shared Function AddTextBox(parent As Control, x As Integer, y As Integer, w As Integer) As TextBox
        Dim tb As New TextBox() With {
            .Location = New Point(x, y),
            .Width = w,
            .BorderStyle = BorderStyle.FixedSingle,
            .Font = New Font("Segoe UI", 9.0F),
            .BackColor = Color.FromArgb(26, 30, 38),
            .ForeColor = Color.FromArgb(245, 248, 255)
        }
        parent.Controls.Add(tb)
        Return tb
    End Function

    Private Shared Function AddCheckBox(parent As Control, text As String, x As Integer, y As Integer, minWidth As Integer) As DarkCheckBox
        Dim cb As New DarkCheckBox() With {
            .Text = text,
            .Location = New Point(x, y),
            .Width = minWidth,
            .Height = 24
        }
        parent.Controls.Add(cb)
        Return cb
    End Function

    Private Shared Function AddNumeric(parent As Control, x As Integer, y As Integer, w As Integer, minVal As Decimal, maxVal As Decimal, inc As Decimal, decimals As Integer) As NumericUpDown
        Dim num As New NumericUpDown() With {
            .Location = New Point(x, y),
            .Width = w,
            .Minimum = minVal,
            .Maximum = maxVal,
            .Increment = inc,
            .DecimalPlaces = decimals,
            .BorderStyle = BorderStyle.FixedSingle,
            .Font = New Font("Segoe UI", 9.0F),
            .BackColor = Color.FromArgb(26, 30, 38),
            .ForeColor = Color.FromArgb(245, 248, 255),
            .TextAlign = HorizontalAlignment.Center
        }
        parent.Controls.Add(num)
        Return num
    End Function

    Private Shared Function GetLimiterEnabledSetting(cfg As MixerHardwareStateStore) As Boolean
        Try
            If cfg?.Audio IsNot Nothing Then
                Dim prop As PropertyInfo = cfg.Audio.GetType().GetProperty("LimiterEnabled", BindingFlags.Public Or BindingFlags.Instance)
                If prop IsNot Nothing AndAlso prop.PropertyType Is GetType(Boolean) Then
                    Return Convert.ToBoolean(prop.GetValue(cfg.Audio))
                End If
            End If
        Catch
        End Try

        Dim raw As String = MixerHardwareStateStore.GetCustomParam("LimiterEnabled", "true")
        Return Not (String.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) OrElse raw = "0")
    End Function

    Private Shared Sub SetLimiterEnabledSetting(cfg As MixerHardwareStateStore, enabled As Boolean)
        Try
            If cfg?.Audio IsNot Nothing Then
                Dim prop As PropertyInfo = cfg.Audio.GetType().GetProperty("LimiterEnabled", BindingFlags.Public Or BindingFlags.Instance)
                If prop IsNot Nothing AndAlso prop.CanWrite AndAlso prop.PropertyType Is GetType(Boolean) Then
                    prop.SetValue(cfg.Audio, enabled)
                End If
            End If
        Catch
        End Try

        MixerHardwareStateStore.SetCustomParam("LimiterEnabled", If(enabled, "true", "false"), saveImmediately:=False)
    End Sub

    Private Shared Sub EnsureAndSelectComboText(cmb As ComboBox, preferredText As String, Optional fallbackText As String = "")
        If cmb Is Nothing Then Return
        Dim target As String = If(Not String.IsNullOrWhiteSpace(preferredText), preferredText.Trim(), fallbackText.Trim())
        If String.IsNullOrEmpty(target) Then
            If cmb.Items.Count > 0 AndAlso cmb.SelectedIndex < 0 Then
                cmb.SelectedIndex = 0
            End If
            Return
        End If

        For i As Integer = 0 To cmb.Items.Count - 1
            Dim itemText As String = Convert.ToString(cmb.Items(i))
            If String.Equals(itemText, target, StringComparison.OrdinalIgnoreCase) Then
                cmb.SelectedIndex = i
                Return
            End If
        Next

        cmb.Items.Add(target)
        cmb.SelectedIndex = cmb.Items.Count - 1
    End Sub

    Private Sub PopulateAsioDriversCombo(Optional preferredDriver As String = "")
        If _cmbAsioDriver Is Nothing Then Return
        _cmbAsioDriver.Items.Clear()
        If _audioPlayer IsNot Nothing Then
            For Each drv In _audioPlayer.GetAvailableAsioDrivers()
                _cmbAsioDriver.Items.Add(drv)
            Next
        End If
        Dim cfg = MixerHardwareStateStore.Current
        Dim target As String = If(Not String.IsNullOrWhiteSpace(preferredDriver), preferredDriver, cfg.Audio.PreferredAsioDriverName)
        EnsureAndSelectComboText(_cmbAsioDriver, target, "QUAD-CAPTURE")
    End Sub

    Private Sub PopulateMidiDevicesCombo(Optional preferredSelection As String = "")
        If _cmbMidiDevice Is Nothing Then Return
        _cmbMidiDevice.Items.Clear()

        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        If _midiController IsNot Nothing Then
            For Each mDev In _midiController.GetCachedMidiInDevices()
                If Not String.IsNullOrWhiteSpace(mDev.Name) AndAlso seen.Add(mDev.Name.Trim()) Then
                    _cmbMidiDevice.Items.Add(mDev.Name.Trim())
                End If
            Next
        End If

        Try
            Dim count As Integer = MidiIn.NumberOfDevices
            For i As Integer = 0 To count - 1
                Try
                    Dim prodName As String = MidiIn.DeviceInfo(i).ProductName
                    If Not String.IsNullOrWhiteSpace(prodName) AndAlso seen.Add(prodName.Trim()) Then
                        _cmbMidiDevice.Items.Add(prodName.Trim())
                    End If
                Catch
                End Try
            Next
        Catch
        End Try

        Dim cfg = MixerHardwareStateStore.Current
        Dim target As String = preferredSelection
        If String.IsNullOrWhiteSpace(target) Then
            If _midiController IsNot Nothing AndAlso _midiController.IsConnected AndAlso
               Not cfg.Midi.IsBlockedMidiDevice(_midiController.ConnectedDeviceName) Then
                target = _midiController.ConnectedDeviceName
            ElseIf Not String.IsNullOrWhiteSpace(cfg.Midi.LastConnectedDeviceName) AndAlso
                   Not cfg.Midi.IsBlockedMidiDevice(cfg.Midi.LastConnectedDeviceName) Then
                target = cfg.Midi.LastConnectedDeviceName
            End If
        End If

        EnsureAndSelectComboText(_cmbMidiDevice, target, "DDJ-SB3")
    End Sub

    Private Function FindMidiDeviceIndexByName(deviceName As String) As Integer
        If String.IsNullOrWhiteSpace(deviceName) Then Return -1

        If _midiController IsNot Nothing Then
            For Each d In _midiController.GetCachedMidiInDevices()
                If String.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase) Then
                    Return d.Index
                End If
            Next
        End If

        Try
            Dim count As Integer = MidiIn.NumberOfDevices
            For i As Integer = 0 To count - 1
                Try
                    If String.Equals(MidiIn.DeviceInfo(i).ProductName, deviceName, StringComparison.OrdinalIgnoreCase) Then
                        Return i
                    End If
                Catch
                End Try
            Next
        Catch
        End Try

        Return -1
    End Function

    Private Sub PopulateOutputDevicesCombo()
        _cmbOutputDevice.Items.Clear()
        _cmbOutputDevice.Items.Add(New ComboDeviceItem() With {.Id = "", .Name = "[Автоматический выбор 1-2 QUAD-CAPTURE]"})

        Dim cfg = MixerHardwareStateStore.Current.Audio
        Dim selectedIdx As Integer = 0
        Dim activeOutId As String = If(_audioPlayer IsNot Nothing, _audioPlayer.OutputDeviceId, "")
        Dim targetMatchId As String = If(cfg.PreferBluetoothHeadphonesOutput AndAlso Not String.IsNullOrEmpty(activeOutId),
                                         activeOutId,
                                         If(Not String.IsNullOrEmpty(cfg.PreferredOutputDeviceId), cfg.PreferredOutputDeviceId, activeOutId))

        If _audioPlayer IsNot Nothing Then
            _audioPlayer.RefreshEndpointsCache()
            Dim devs = _audioPlayer.GetCachedOutputDeviceInfos()
            For i As Integer = 0 To devs.Count - 1
                Dim d = devs(i)
                _cmbOutputDevice.Items.Add(New ComboDeviceItem() With {.Id = d.Id, .Name = d.GetDisplayLabel()})
                If Not String.IsNullOrEmpty(targetMatchId) AndAlso d.Id.Equals(targetMatchId, StringComparison.OrdinalIgnoreCase) Then
                    selectedIdx = _cmbOutputDevice.Items.Count - 1
                End If
            Next
        End If

        If selectedIdx = 0 AndAlso Not String.IsNullOrWhiteSpace(cfg.PreferredOutputDeviceId) Then
            Dim savedLabel As String = If(Not String.IsNullOrWhiteSpace(cfg.PreferredOutputDeviceName),
                                          cfg.PreferredOutputDeviceName,
                                          cfg.PreferredOutputDeviceId)
            _cmbOutputDevice.Items.Add(New ComboDeviceItem() With {
                .Id = cfg.PreferredOutputDeviceId,
                .Name = savedLabel
            })
            selectedIdx = _cmbOutputDevice.Items.Count - 1
        End If

        If _cmbOutputDevice.Items.Count > 0 Then
            _cmbOutputDevice.SelectedIndex = selectedIdx
        End If
    End Sub

    Private Sub RefreshHardwareDiagnosticsLabels()
        Try
            If _lblDspApoStatus IsNot Nothing Then
                Dim dspAvailable As Boolean = (_audioPlayer?.NativeMixer IsNot Nothing AndAlso _audioPlayer.NativeMixer.IsAvailable)
                Dim apoActive As Boolean = NativeCppAutoBuilderAndInstaller.IsKernelApoProcessingAudio()
                If apoActive Then
                    _lblDspApoStatus.Text = "Статус DSP / APO: Yoump.DSP активен | Kernel APO (audiodg.exe) обрабатывает поток"
                    _lblDspApoStatus.ForeColor = Color.FromArgb(65, 235, 110)
                ElseIf dspAvailable Then
                    _lblDspApoStatus.Text = "Статус DSP / APO: Yoump.DSP загружен | Kernel APO в режиме ожидания потока"
                    _lblDspApoStatus.ForeColor = Color.FromArgb(0, 200, 255)
                Else
                    _lblDspApoStatus.Text = "Статус DSP / APO: Yoump.DSP.dll не загружен (проверьте сборку C++)"
                    _lblDspApoStatus.ForeColor = Color.FromArgb(255, 145, 50)
                End If
            End If

            If _lblAsioStatus IsNot Nothing Then
                If _audioPlayer IsNot Nothing AndAlso _audioPlayer.IsAsioActive Then
                    _lblAsioStatus.Text = $"Статус ASIO: Активен ({_audioPlayer.ActiveAsioDriverName})"
                    _lblAsioStatus.ForeColor = Color.FromArgb(65, 235, 110)
                Else
                    _lblAsioStatus.Text = "Статус ASIO: Выключен (используется WASAPI)"
                    _lblAsioStatus.ForeColor = Color.FromArgb(165, 175, 190)
                End If
            End If

            If _lblMidiStatus IsNot Nothing Then
                If _midiController IsNot Nothing AndAlso _midiController.IsConnected Then
                    _lblMidiStatus.Text = $"Статус MIDI: Подключен [{_midiController.ConnectedDeviceName}] (индекс {_midiController.ConnectedDeviceIndex})"
                    _lblMidiStatus.ForeColor = Color.FromArgb(65, 235, 110)
                Else
                    _lblMidiStatus.Text = "Статус MIDI: Контроллер не подключен (автопоиск активен)"
                    _lblMidiStatus.ForeColor = Color.FromArgb(255, 175, 60)
                End If
            End If
        Catch
        End Try
    End Sub

    Private Function GetActiveOrFirstTrack() As TrackModel
        If _model?.Tracks Is Nothing Then Return Nothing
        Dim targetId As Guid = If(_audioPlayer IsNot Nothing, _audioPlayer.FocusedTrackId, Guid.Empty)
        SyncLock _model.SyncRoot
            If targetId <> Guid.Empty Then
                For i As Integer = 0 To _model.Tracks.Count - 1
                    If _model.Tracks(i).Id = targetId Then Return _model.Tracks(i)
                Next
            End If
            If _model.Tracks.Count > 0 Then Return _model.Tracks(0)
        End SyncLock
        Return Nothing
    End Function

    Private Sub ResetActiveTrackEqAndBalanceToCenter()
        Dim t As TrackModel = GetActiveOrFirstTrack()
        If t IsNot Nothing Then
            If _broker IsNot Nothing Then
                _broker.SetTrackEQAndTrim(t.Id, 0.5F, 0.5F, 0.5F, t.Trim, Me)
                _broker.SetTrackColorFilter(t.Id, 0.5F, Me)
                _broker.SetMasterBalance01(0.5F, Me)
            ElseIf _audioPlayer IsNot Nothing Then
                _audioPlayer.SetTrackEQAndTrim(t.Id, 0.0F, 0.0F, 0.0F, t.Trim * 2.0F)
                _audioPlayer.SetTrackColorFilter(t.Id, 0.5F)
                _audioPlayer.SetBalance(0.5F)
            End If
            _numMasterBalancePercent.Value = 0D
            MixerHardwareStateStore.SyncFromModelAndPlayer(_model, _audioPlayer)
            PopulateChannelProfilesGrid()
        End If
    End Sub

    Private Sub ScanAndAddActiveAudioProcessesToGrid()
        _gridAliases.EndEdit()
        Dim existingKeys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each row As DataGridViewRow In _gridAliases.Rows
            If row.IsNewRow Then Continue For
            Dim k As String = Convert.ToString(row.Cells("ProcKey").Value)?.Trim()
            If Not String.IsNullOrEmpty(k) Then existingKeys.Add(k)
        Next

        Dim ignored As New HashSet(Of String)(SplitLinesClean(_txtIgnoredProcesses.Lines), StringComparer.OrdinalIgnoreCase)
        Dim addedCount As Integer = 0

        Try
            Using enumerator As New MMDeviceEnumerator()
                For Each dev In enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    Try
                        Dim sessions = dev.AudioSessionManager?.Sessions
                        If sessions Is Nothing Then Continue For
                        For i As Integer = 0 To sessions.Count - 1
                            Try
                                Using s = sessions(i)
                                    If s Is Nothing OrElse s.IsSystemSoundsSession Then Continue For
                                    Dim pid As Integer = CInt(s.GetProcessID)
                                    If pid <= 4 OrElse pid = Environment.ProcessId Then Continue For
                                    Using p As Process = Process.GetProcessById(pid)
                                        Dim procName As String = p.ProcessName?.Trim()
                                        If Not String.IsNullOrWhiteSpace(procName) AndAlso
                                           Not ignored.Contains(procName) AndAlso
                                           existingKeys.Add(procName.ToLowerInvariant()) Then
                                            Dim upAlias As String = procName.ToUpperInvariant()
                                            If upAlias.Length > 9 Then upAlias = upAlias.Substring(0, 9)
                                            _gridAliases.Rows.Add(procName.ToLowerInvariant(), upAlias)
                                            addedCount += 1
                                        End If
                                    End Using
                                End Using
                            Catch
                            End Try
                        Next
                    Catch
                    Finally
                        Try : dev.Dispose() : Catch : End Try
                    End Try
                Next
            End Using
        Catch
        End Try

        MessageBox.Show(Me,
                        If(addedCount > 0,
                           $"Добавлено новых аудио-процессов в таблицу: {addedCount}.",
                           "Все текущие активные аудио-процессы уже есть в таблице (либо находятся в списке игнорируемых)."),
                        "Сканирование аудиосессий",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information)
    End Sub

    Private Sub ClearWaveformPeakCacheFiles()
        Try
            Dim dir As String = _txtCacheDir.Text.Trim()
            If String.IsNullOrWhiteSpace(dir) Then
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "yoump")
            End If
            Dim peakDir As String = Path.Combine(dir, "PeakCache")
            Dim deleted As Integer = 0
            If Directory.Exists(peakDir) Then
                For Each f In Directory.GetFiles(peakDir, "*.wpk")
                    Try
                        File.Delete(f)
                        deleted += 1
                    Catch
                    End Try
                Next
            End If
            MessageBox.Show(Me, $"Очищено файлов кэша волн (.wpk): {deleted}.", "Кэш пиков", MessageBoxButtons.OK, MessageBoxIcon.Information)
        Catch ex As Exception
            MessageBox.Show(Me, $"Не удалось очистить кэш: {ex.Message}", "Кэш пиков", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub

    Private Sub ApplySelectedPresetRowToActiveTrack()
        _gridPresets.EndEdit()
        Dim row As DataGridViewRow = _gridPresets.CurrentRow
        If row Is Nothing OrElse row.IsNewRow Then Return

        Dim preset As New ChannelProfileConfig() With {
            .Trim = ParseCellFloat(row.Cells("Trim").Value, 0.5F, 0.0F, 1.0F),
            .EqHi = ParseCellFloat(row.Cells("EqHi").Value, 0.5F, 0.0F, 1.0F),
            .EqMid = ParseCellFloat(row.Cells("EqMid").Value, 0.5F, 0.0F, 1.0F),
            .EqLow = ParseCellFloat(row.Cells("EqLow").Value, 0.5F, 0.0F, 1.0F),
            .ColorFx = ParseCellFloat(row.Cells("ColorFx").Value, 0.5F, 0.0F, 1.0F),
            .ColorFxMode = ParseFxModeCell(row.Cells("ColorFxMode").Value),
            .Fader = ParseCellFloat(row.Cells("Fader").Value, 1.0F, 0.0F, 1.0F)
        }

        Dim t As TrackModel = GetActiveOrFirstTrack()
        If t IsNot Nothing AndAlso _broker IsNot Nothing Then
            _broker.ApplyPresetToTrack(t.Id, preset, Me)
            MixerHardwareStateStore.SyncFromModelAndPlayer(_model, _audioPlayer)
            PopulateChannelProfilesGrid()
        End If
    End Sub

    Private Sub AddPresetRowFromActiveTrack()
        _gridPresets.EndEdit()
        Dim t As TrackModel = GetActiveOrFirstTrack()
        If t Is Nothing Then Return
        Dim faderVal As Single = If(t.Clips IsNot Nothing AndAlso t.Clips.Count > 0, t.Clips(0).Volume, 1.0F)
        Dim newName As String = $"Пресет {t.Name} ({_gridPresets.Rows.Count})"
        _gridPresets.Rows.Add(
            newName,
            FormatCellFloat(t.Trim),
            FormatCellFloat(t.EqHi),
            FormatCellFloat(t.EqMid),
            FormatCellFloat(t.EqLow),
            FormatCellFloat(t.ColorFx),
            FormatFxModeCell(t.ColorFxMode),
            FormatCellFloat(faderVal))
    End Sub

    Private Sub RestoreFactoryPresetsInGrid()
        _gridPresets.EndEdit()
        _gridPresets.Rows.Clear()
        _gridPresets.Rows.Add("DJ / Flat (0 dB)", "0.5", "0.5", "0.5", "0.5", "0.5", "0: FILTER", "1")
        _gridPresets.Rows.Add("Кино / Голос (Voice Boost)", "0.55", "0.58", "0.72", "0.38", "0.5", "0: FILTER", "1")
        _gridPresets.Rows.Add("Club / Bass Boost", "0.5", "0.65", "0.48", "0.74", "0.68", "3: LOUD", "1")
        _gridPresets.Rows.Add("Ночной режим (Low Cut)", "0.48", "0.5", "0.58", "0.18", "0.58", "0: FILTER", "0.85")
    End Sub

    Private Shared Function IsWindowsStartupEnabled() As Boolean
        Try
            Using k As RegistryKey = Registry.CurrentUser.OpenSubKey(AutorunRegistryPath, writable:=False)
                If k IsNot Nothing Then
                    Dim val As String = Convert.ToString(k.GetValue(AutorunValueName, ""))
                    Return Not String.IsNullOrWhiteSpace(val)
                End If
            End Using
        Catch
        End Try
        Return False
    End Function

    Private Shared Sub SetWindowsStartupEnabled(enable As Boolean)
        Try
            Using k As RegistryKey = Registry.CurrentUser.OpenSubKey(AutorunRegistryPath, writable:=True)
                If k Is Nothing Then Return
                If enable Then
                    Dim exePath As String = Environment.ProcessPath
                    If Not String.IsNullOrWhiteSpace(exePath) AndAlso File.Exists(exePath) Then
                        k.SetValue(AutorunValueName, ChrW(34) & exePath & ChrW(34), RegistryValueKind.String)
                    End If
                Else
                    k.DeleteValue(AutorunValueName, throwOnMissingValue:=False)
                End If
            End Using
        Catch
        End Try
    End Sub

    Private Shared Function FormatFxModeCell(mode As Integer) As String
        Select Case mode And 3
            Case 1 : Return "1: SPACE"
            Case 2 : Return "2: CRUSH"
            Case 3 : Return "3: LOUD"
            Case Else : Return "0: FILTER"
        End Select
    End Function

    Private Shared Function ParseFxModeCell(raw As Object) As Integer
        Dim s As String = Convert.ToString(raw)
        If String.IsNullOrWhiteSpace(s) Then Return 0
        If s.StartsWith("1") OrElse s.IndexOf("SPACE", StringComparison.OrdinalIgnoreCase) >= 0 Then Return 1
        If s.StartsWith("2") OrElse s.IndexOf("CRUSH", StringComparison.OrdinalIgnoreCase) >= 0 Then Return 2
        If s.StartsWith("3") OrElse s.IndexOf("LOUD", StringComparison.OrdinalIgnoreCase) >= 0 Then Return 3
        Return 0
    End Function

    Private Shared Function FormatCellFloat(val As Single) As String
        Return Math.Round(CDbl(val), 4).ToString("0.####", CultureInfo.InvariantCulture)
    End Function

    Private Shared Function ParseCellFloat(raw As Object, defVal As Single, minVal As Single, maxVal As Single) As Single
        Dim s As String = Convert.ToString(raw)
        If String.IsNullOrWhiteSpace(s) Then Return defVal
        s = s.Replace(","c, "."c).Trim()
        Dim v As Single = defVal
        If Single.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, v) AndAlso Not Single.IsNaN(v) AndAlso Not Single.IsInfinity(v) Then
            Return Math.Max(minVal, Math.Min(maxVal, v))
        End If
        Return defVal
    End Function

    Private Sub ResetAllTextBoxesScroll()
        Try
            _txtOutputKeyword.SelectionStart = 0
            _txtHardwareKeywords.SelectionStart = 0
            _txtBlockedKeywords.SelectionStart = 0
            _txtMicKeyword.SelectionStart = 0
            _txtMainCaptureKeyword.SelectionStart = 0
            If _txtBtKeywords IsNot Nothing Then _txtBtKeywords.SelectionStart = 0
            _txtMicCustomIrPath.SelectionStart = 0
            _txtPreferredMidiNames.SelectionStart = 0
            _txtPinnedChannels.SelectionStart = 0
            _txtIgnoredProcesses.SelectionStart = 0
            _txtCacheDir.SelectionStart = 0
            _txtNativeRootDir.SelectionStart = 0
        Catch
        End Try
    End Sub

    Private Sub PopulateChannelProfilesGrid()
        Dim prevLoad As Boolean = _isLoadingConfigValues
        _isLoadingConfigValues = True
        Try
            Dim cfg = MixerHardwareStateStore.Current
            _gridChannelProfiles.Rows.Clear()
            If cfg.ChannelProfiles IsNot Nothing Then
                For Each kvp In cfg.ChannelProfiles
                    Dim p As ChannelProfileConfig = kvp.Value
                    If p Is Nothing Then Continue For
                    _gridChannelProfiles.Rows.Add(
                        kvp.Key,
                        FormatCellFloat(p.Trim),
                        FormatCellFloat(p.EqHi),
                        FormatCellFloat(p.EqMid),
                        FormatCellFloat(p.EqLow),
                        FormatCellFloat(p.ColorFx),
                        FormatFxModeCell(p.ColorFxMode),
                        FormatCellFloat(p.Fader),
                        FormatCellFloat(p.WaveformZoom),
                        p.IsMuted,
                        p.IsSolo)
                Next
            End If
            _channelProfilesEditedInGrid = False
        Finally
            _isLoadingConfigValues = prevLoad
        End Try
    End Sub

    Public Sub LoadValuesFromConfig()
        _isLoadingConfigValues = True
        Try
            MixerHardwareStateStore.SyncFromModelAndPlayer(_model, _audioPlayer)
            Dim cfg = MixerHardwareStateStore.Current

            ' 1 & 2. Аудио, ASIO, Микрофон и Zero-Latency IR
            PopulateOutputDevicesCombo()
            _txtOutputKeyword.Text = cfg.Audio.PreferredOutputKeyword
            _txtHardwareKeywords.Text = If(cfg.Audio.HardwareKeywords IsNot Nothing, String.Join(", ", cfg.Audio.HardwareKeywords), "")
            _txtBlockedKeywords.Text = If(cfg.Audio.BlockedOutputKeywords IsNot Nothing, String.Join(", ", cfg.Audio.BlockedOutputKeywords), "")
            _txtMicKeyword.Text = cfg.Audio.PreferredMicKeyword
            _txtMainCaptureKeyword.Text = cfg.Audio.PreferredMainCaptureKeyword
            _chkMicMonitoring.Checked = cfg.Audio.MicMonitoringEnabled
            _chkMicStudioProc.Checked = cfg.Audio.MicStudioProcessingEnabled
            _chkMicDeEsser.Checked = cfg.Audio.MicDeEsserEnabled
            _chkMicPlosive24dB.Checked = cfg.Audio.MicPlosiveFilter24dB
            _chkMicAntiFeedback.Checked = cfg.Audio.MicAntiFeedbackEnabled
            _chkLimiterEnabled.Checked = GetLimiterEnabledSetting(cfg)
            _numMicGate.Value = ClampDec(CDec(cfg.Audio.MicNoiseGateThreshold), _numMicGate)
            _numMicCapLatency.Value = ClampDec(cfg.Audio.MicCaptureLatencyMs, _numMicCapLatency)
            _numMicOutLatency.Value = ClampDec(cfg.Audio.MicOutputLatencyMs, _numMicOutLatency)
            _numMicBufDuration.Value = ClampDec(cfg.Audio.MicBufferDurationMs, _numMicBufDuration)

            Dim safeTalkover As Integer = Math.Max(0, Math.Min(2, cfg.Audio.MicTalkoverMode))
            If _cmbMicTalkoverMode IsNot Nothing AndAlso _cmbMicTalkoverMode.Items.Count > safeTalkover Then
                _cmbMicTalkoverMode.SelectedIndex = safeTalkover
            End If

            Dim safeXlrMode As Integer = Math.Max(0, Math.Min(3, cfg.Audio.MicXlrInputMode))
            If _cmbMicXlrMode IsNot Nothing AndAlso _cmbMicXlrMode.Items.Count > safeXlrMode Then
                _cmbMicXlrMode.SelectedIndex = safeXlrMode
            End If

            Dim safeIrPreset As Integer = Math.Max(0, Math.Min(7, cfg.Audio.MicIrPreset))
            If _cmbMicIrPreset IsNot Nothing AndAlso _cmbMicIrPreset.Items.Count > safeIrPreset Then
                _cmbMicIrPreset.SelectedIndex = safeIrPreset
            End If
            Dim irMixPct As Decimal = CDec(Math.Round(Math.Max(0.0F, Math.Min(1.0F, cfg.Audio.MicIrMix)) * 100.0F))
            _numMicIrMixPercent.Value = ClampDec(irMixPct, _numMicIrMixPercent)
            _txtMicCustomIrPath.Text = If(cfg.Audio.MicCustomIrFilePath, "")

            _chkAutoRouteApps.Checked = cfg.Audio.AutoRouteAppsToPrimaryOutput
            _chkSanitizeRegistry.Checked = cfg.Audio.SanitizeMediaPlayerRegistry
            _chkPreferBtOutput.Checked = cfg.Audio.PreferBluetoothHeadphonesOutput
            _chkBtLdacGuard.Checked = cfg.Audio.BluetoothMicHighQualityOnly
            _txtBtKeywords.Text = If(cfg.Audio.BluetoothHeadphoneKeywords IsNot Nothing, String.Join(", ", cfg.Audio.BluetoothHeadphoneKeywords), "")

            _chkUseAsio.Checked = cfg.Audio.UseAsio
            PopulateAsioDriversCombo(cfg.Audio.PreferredAsioDriverName)
            _numAsioInOffset.Value = ClampDec(cfg.Audio.AsioInputChannelOffset, _numAsioInOffset)
            _numAsioOutOffset.Value = ClampDec(cfg.Audio.AsioOutputChannelOffset, _numAsioOutOffset)

            ' 3. MIDI
            PopulateMidiDevicesCombo()
            _txtPreferredMidiNames.Text = If(cfg.Midi.PreferredControllerNames IsNot Nothing, String.Join(", ", cfg.Midi.PreferredControllerNames), "")
            _numMidiReconnectMs.Value = ClampDec(cfg.Midi.ReconnectIntervalMs, _numMidiReconnectMs)
            _numMidiDeadzone.Value = ClampDec(CDec(cfg.Midi.CenterSnapDeadzone), _numMidiDeadzone)
            _numMidiDumpWindowMs.Value = ClampDec(cfg.Midi.InitialDumpWindowMs, _numMidiDumpWindowMs)
            _chkCrossfaderBalance.Checked = cfg.Midi.CrossfaderControlsBalance
            _chkSendSeratoSysEx.Checked = cfg.Midi.SendSeratoStartupSysEx

            ' 4. Процессы
            _numSessionPollMs.Value = ClampDec(cfg.ProcessRules.SessionPollIntervalMs, _numSessionPollMs)
            _numDiscoveryMs.Value = ClampDec(cfg.ProcessRules.DiscoveryIntervalMs, _numDiscoveryMs)
            _numInactiveTimeoutMs.Value = ClampDec(cfg.ProcessRules.InactiveChannelTimeoutMs, _numInactiveTimeoutMs)
            _txtPinnedChannels.Lines = If(cfg.ProcessRules.PinnedChannels IsNot Nothing, cfg.ProcessRules.PinnedChannels.ToArray(), Array.Empty(Of String)())
            _txtIgnoredProcesses.Lines = If(cfg.ProcessRules.IgnoredProcesses IsNot Nothing, cfg.ProcessRules.IgnoredProcesses.ToArray(), Array.Empty(Of String)())

            _gridAliases.Rows.Clear()
            If cfg.ProcessRules.ProcessAliases IsNot Nothing Then
                For Each kvp In cfg.ProcessRules.ProcessAliases
                    _gridAliases.Rows.Add(kvp.Key, kvp.Value)
                Next
            End If

            ' 5. Интерфейс и Мастер
            _chkDarkTheme.Checked = cfg.Ui.IsDarkTheme
            _chkAlwaysOnTop.Checked = cfg.Ui.AlwaysOnTop
            _chkStartMinimized.Checked = cfg.Ui.StartMinimizedToTray
            _chkHotReloadConfig.Checked = cfg.Ui.HotReloadConfigFile
            _chkRunAtWindowsStartup.Checked = IsWindowsStartupEnabled()
            _numProjectFps.Value = ClampDec(CDec(cfg.Ui.ProjectFps), _numProjectFps)
            _numWindowHeight.Value = ClampDec(cfg.Ui.WindowHeight, _numWindowHeight)
            _chkSaveWindowPos.Checked = (cfg.Ui.WindowX.HasValue AndAlso cfg.Ui.WindowY.HasValue)
            _numWindowX.Value = ClampDec(If(cfg.Ui.WindowX.HasValue, cfg.Ui.WindowX.Value, 100), _numWindowX)
            _numWindowY.Value = ClampDec(If(cfg.Ui.WindowY.HasValue, cfg.Ui.WindowY.Value, 100), _numWindowY)
            _numWindowX.Enabled = _chkSaveWindowPos.Checked
            _numWindowY.Enabled = _chkSaveWindowPos.Checked
            _numWaveformAmpScale.Value = ClampDec(CDec(cfg.Ui.WaveformAmpScale), _numWaveformAmpScale)
            _numOsdDurationMs.Value = ClampDec(cfg.Ui.OsdDurationMs, _numOsdDurationMs)
            _numPeakHoldMs.Value = ClampDec(cfg.Ui.PeakHoldDurationMs, _numPeakHoldMs)
            _numMeterDecay.Value = ClampDec(CDec(cfg.Ui.MeterDecayFactor), _numMeterDecay)

            Dim masterVolPct As Decimal = CDec(Math.Round(cfg.MasterVolume01 * 100.0F))
            _numMasterVolumePercent.Value = ClampDec(masterVolPct, _numMasterVolumePercent)

            Dim masterBalPct As Decimal = CDec(Math.Round((cfg.MasterBalance01 - 0.5F) * 200.0F))
            _numMasterBalancePercent.Value = ClampDec(masterBalPct, _numMasterBalancePercent)

            Dim targetSource As String = If(cfg.MasterLevelSource, "Auto").Trim()
            Dim foundIndex As Integer = -1
            For i As Integer = 0 To _cmbMasterLevelSource.Items.Count - 1
                If String.Equals(Convert.ToString(_cmbMasterLevelSource.Items(i)), targetSource, StringComparison.OrdinalIgnoreCase) Then
                    foundIndex = i
                    Exit For
                End If
            Next
            _cmbMasterLevelSource.SelectedIndex = If(foundIndex >= 0, foundIndex, 0)

            _txtCacheDir.Text = cfg.Ui.CacheDirectory

            ' 6. Профили
            PopulateChannelProfilesGrid()

            ' 7. Пресеты и Доп. параметры
            _gridPresets.Rows.Clear()
            If cfg.Presets IsNot Nothing Then
                For Each kvp In cfg.Presets
                    Dim p As ChannelProfileConfig = kvp.Value
                    If p Is Nothing Then Continue For
                    _gridPresets.Rows.Add(
                        kvp.Key,
                        FormatCellFloat(p.Trim),
                        FormatCellFloat(p.EqHi),
                        FormatCellFloat(p.EqMid),
                        FormatCellFloat(p.EqLow),
                        FormatCellFloat(p.ColorFx),
                        FormatFxModeCell(p.ColorFxMode),
                        FormatCellFloat(p.Fader))
                Next
            End If

            _txtNativeRootDir.Text = MixerHardwareStateStore.GetCustomParam("NativeRootDirectory", "")

            _gridCustomParams.Rows.Clear()
            If cfg.CustomParameters IsNot Nothing Then
                For Each kvp In cfg.CustomParameters
                    If String.Equals(kvp.Key, "NativeRootDirectory", StringComparison.OrdinalIgnoreCase) OrElse
                       String.Equals(kvp.Key, "LimiterEnabled", StringComparison.OrdinalIgnoreCase) Then
                        Continue For
                    End If
                    _gridCustomParams.Rows.Add(kvp.Key, kvp.Value)
                Next
            End If

            RefreshHardwareDiagnosticsLabels()
            ResetAllTextBoxesScroll()
        Catch
        Finally
            _isLoadingConfigValues = False
        End Try
    End Sub

    Private Shared Function ClampDec(val As Decimal, num As NumericUpDown) As Decimal
        Return Math.Max(num.Minimum, Math.Min(num.Maximum, val))
    End Function

    Private Shared Function SplitCsv(text As String) As String()
        If String.IsNullOrWhiteSpace(text) Then Return Array.Empty(Of String)()
        Return text.Split(","c).Select(Function(s) s.Trim()).Where(Function(s) Not String.IsNullOrEmpty(s)).ToArray()
    End Function

    Private Shared Function SplitLinesClean(lines As String()) As List(Of String)
        Dim res As New List(Of String)()
        If lines Is Nothing Then Return res
        For Each l In lines
            If Not String.IsNullOrWhiteSpace(l) Then res.Add(l.Trim())
        Next
        Return res
    End Function

    Private Sub CommitAllGridsAndInputs()
        Try : _gridAliases?.EndEdit() : Catch : End Try
        Try : _gridChannelProfiles?.EndEdit() : Catch : End Try
        Try : _gridPresets?.EndEdit() : Catch : End Try
        Try : _gridCustomParams?.EndEdit() : Catch : End Try
        Try : Me.ValidateChildren() : Catch : End Try
    End Sub

    Private Function SaveValuesToConfigAndApply() As Boolean
        Try
            CommitAllGridsAndInputs()

            ' 0. Сначала синхронизируем текущие живые состояния ручек микшера ДО записи новых значений из формы,
            ' чтобы старые значения из _audioPlayer не затёрли новые настройки Громкости, Баланса и Мониторинга
            If Not _channelProfilesEditedInGrid Then
                MixerHardwareStateStore.SyncFromModelAndPlayer(_model, _audioPlayer)
            End If

            Dim cfg = MixerHardwareStateStore.Current
            Dim prevUseAsio As Boolean = cfg.Audio.UseAsio
            Dim prevAsioDrv As String = cfg.Audio.PreferredAsioDriverName
            Dim prevOutId As String = cfg.Audio.PreferredOutputDeviceId
            Dim prevInOffset As Integer = cfg.Audio.AsioInputChannelOffset
            Dim prevOutOffset As Integer = cfg.Audio.AsioOutputChannelOffset
            Dim prevOutKw As String = cfg.Audio.PreferredOutputKeyword
            Dim prevMicKw As String = cfg.Audio.PreferredMicKeyword
            Dim prevMainKw As String = cfg.Audio.PreferredMainCaptureKeyword
            Dim prevHwKws As String = If(cfg.Audio.HardwareKeywords IsNot Nothing, String.Join(",", cfg.Audio.HardwareKeywords), "")
            Dim prevBlkKws As String = If(cfg.Audio.BlockedOutputKeywords IsNot Nothing, String.Join(",", cfg.Audio.BlockedOutputKeywords), "")
            Dim prevPreferBt As Boolean = cfg.Audio.PreferBluetoothHeadphonesOutput
            Dim prevBtGuard As Boolean = cfg.Audio.BluetoothMicHighQualityOnly
            Dim prevBtKws As String = If(cfg.Audio.BluetoothHeadphoneKeywords IsNot Nothing, String.Join(",", cfg.Audio.BluetoothHeadphoneKeywords), "")
            Dim prevMicCapLat As Integer = cfg.Audio.MicCaptureLatencyMs
            Dim prevMicOutLat As Integer = cfg.Audio.MicOutputLatencyMs
            Dim prevMicBufDur As Integer = cfg.Audio.MicBufferDurationMs

            ' 1 & 2. Аудио, ASIO, Микрофон и Zero-Latency IR
            Dim selDev As ComboDeviceItem = TryCast(_cmbOutputDevice.SelectedItem, ComboDeviceItem)
            Dim actualActiveOutId As String = If(_audioPlayer IsNot Nothing, _audioPlayer.OutputDeviceId, prevOutId)
            Dim userSelectedNonBtDevice As Boolean = False
            If selDev IsNot Nothing Then
                cfg.Audio.PreferredOutputDeviceId = selDev.Id
                cfg.Audio.PreferredOutputDeviceName = If(String.IsNullOrEmpty(selDev.Id), "", selDev.Name)

                If Not String.IsNullOrEmpty(selDev.Id) AndAlso
                   Not String.Equals(actualActiveOutId, selDev.Id, StringComparison.OrdinalIgnoreCase) AndAlso
                   _audioPlayer IsNot Nothing Then
                    For Each ep In _audioPlayer.GetCachedOutputDeviceInfos()
                        If ep.Id.Equals(selDev.Id, StringComparison.OrdinalIgnoreCase) AndAlso Not ep.IsBluetooth Then
                            userSelectedNonBtDevice = True
                            Exit For
                        End If
                    Next
                End If
            End If

            If userSelectedNonBtDevice AndAlso prevPreferBt Then
                _chkPreferBtOutput.Checked = False
            End If
            cfg.Audio.PreferredOutputKeyword = _txtOutputKeyword.Text.Trim()
            cfg.Audio.HardwareKeywords = SplitCsv(_txtHardwareKeywords.Text)
            cfg.Audio.BlockedOutputKeywords = SplitCsv(_txtBlockedKeywords.Text)
            cfg.Audio.PreferredMicKeyword = _txtMicKeyword.Text.Trim()
            cfg.Audio.PreferredMainCaptureKeyword = _txtMainCaptureKeyword.Text.Trim()
            cfg.Audio.MicMonitoringEnabled = _chkMicMonitoring.Checked
            cfg.Audio.MicStudioProcessingEnabled = _chkMicStudioProc.Checked
            cfg.Audio.MicDeEsserEnabled = _chkMicDeEsser.Checked
            cfg.Audio.MicPlosiveFilter24dB = _chkMicPlosive24dB.Checked
            cfg.Audio.MicAntiFeedbackEnabled = _chkMicAntiFeedback.Checked
            cfg.Audio.MicTalkoverMode = Math.Max(0, Math.Min(2, _cmbMicTalkoverMode.SelectedIndex))
            cfg.Audio.MicXlrInputMode = Math.Max(0, Math.Min(3, _cmbMicXlrMode.SelectedIndex))
            SetLimiterEnabledSetting(cfg, _chkLimiterEnabled.Checked)
            cfg.Audio.MicNoiseGateThreshold = CSng(_numMicGate.Value)
            cfg.Audio.MicCaptureLatencyMs = CInt(_numMicCapLatency.Value)
            cfg.Audio.MicOutputLatencyMs = CInt(_numMicOutLatency.Value)
            cfg.Audio.MicBufferDurationMs = CInt(_numMicBufDuration.Value)

            cfg.Audio.MicIrPreset = Math.Max(0, Math.Min(7, _cmbMicIrPreset.SelectedIndex))
            cfg.Audio.MicIrMix = Math.Max(0.0F, Math.Min(1.0F, CSng(_numMicIrMixPercent.Value) / 100.0F))
            cfg.Audio.MicCustomIrFilePath = _txtMicCustomIrPath.Text.Trim()

            Dim targetSr As Integer = If(_audioPlayer IsNot Nothing, _audioPlayer.CurrentSampleRate, 48000)
            If cfg.Audio.MicIrPreset = 7 AndAlso
               Not String.IsNullOrWhiteSpace(cfg.Audio.MicCustomIrFilePath) AndAlso
               File.Exists(cfg.Audio.MicCustomIrFilePath) Then
                NativeCppAutoBuilderAndInstaller.LoadAndUploadCustomWavIr(cfg.Audio.MicCustomIrFilePath, targetSr)
            End If
            NativeCppAutoBuilderAndInstaller.WriteMicConvolutionIrParams(
                cfg.Audio.MicIrPreset,
                cfg.Audio.MicIrMix,
                cfg.Audio.MicNoiseGateThreshold,
                cfg.Audio.GetPackedMicExtraFlags())

            cfg.Audio.AutoRouteAppsToPrimaryOutput = _chkAutoRouteApps.Checked
            cfg.Audio.SanitizeMediaPlayerRegistry = _chkSanitizeRegistry.Checked
            cfg.Audio.PreferBluetoothHeadphonesOutput = _chkPreferBtOutput.Checked
            cfg.Audio.BluetoothMicHighQualityOnly = _chkBtLdacGuard.Checked
            cfg.Audio.BluetoothHeadphoneKeywords = SplitCsv(_txtBtKeywords.Text)

            cfg.Audio.UseAsio = _chkUseAsio.Checked
            Dim asioText As String = _cmbAsioDriver.Text.Trim()
            If Not String.IsNullOrWhiteSpace(asioText) Then
                cfg.Audio.PreferredAsioDriverName = asioText
            End If
            cfg.Audio.AsioInputChannelOffset = CInt(_numAsioInOffset.Value)
            cfg.Audio.AsioOutputChannelOffset = CInt(_numAsioOutOffset.Value)

            ' 3. MIDI
            Dim midiText As String = _cmbMidiDevice.Text.Trim()
            If Not String.IsNullOrWhiteSpace(midiText) AndAlso Not cfg.Midi.IsBlockedMidiDevice(midiText) Then
                cfg.Midi.LastConnectedDeviceName = midiText
            ElseIf cfg.Midi.IsBlockedMidiDevice(cfg.Midi.LastConnectedDeviceName) Then
                cfg.Midi.LastConnectedDeviceName = ""
            End If
            cfg.Midi.PreferredControllerNames = SplitCsv(_txtPreferredMidiNames.Text)
            cfg.Midi.ReconnectIntervalMs = CInt(_numMidiReconnectMs.Value)
            cfg.Midi.CenterSnapDeadzone = CSng(_numMidiDeadzone.Value)
            cfg.Midi.InitialDumpWindowMs = CInt(_numMidiDumpWindowMs.Value)
            cfg.Midi.CrossfaderControlsBalance = _chkCrossfaderBalance.Checked
            cfg.Midi.SendSeratoStartupSysEx = _chkSendSeratoSysEx.Checked

            ' 4. Процессы
            cfg.ProcessRules.SessionPollIntervalMs = CInt(_numSessionPollMs.Value)
            cfg.ProcessRules.DiscoveryIntervalMs = CInt(_numDiscoveryMs.Value)
            cfg.ProcessRules.InactiveChannelTimeoutMs = CInt(_numInactiveTimeoutMs.Value)
            cfg.ProcessRules.PinnedChannels = SplitLinesClean(_txtPinnedChannels.Lines)
            cfg.ProcessRules.IgnoredProcesses = SplitLinesClean(_txtIgnoredProcesses.Lines)

            Dim newAliases As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            For Each row As DataGridViewRow In _gridAliases.Rows
                If row.IsNewRow Then Continue For
                Dim k As String = Convert.ToString(row.Cells("ProcKey").Value)?.Trim()
                Dim v As String = Convert.ToString(row.Cells("AliasVal").Value)?.Trim()
                If Not String.IsNullOrEmpty(k) AndAlso Not String.IsNullOrEmpty(v) Then
                    newAliases(k) = v
                End If
            Next
            cfg.ProcessRules.ProcessAliases = newAliases

            ' 5. Интерфейс и Мастер
            ThemeManager.IsDarkTheme = _chkDarkTheme.Checked
            cfg.Ui.IsDarkTheme = _chkDarkTheme.Checked
            cfg.Ui.AlwaysOnTop = _chkAlwaysOnTop.Checked
            cfg.Ui.StartMinimizedToTray = _chkStartMinimized.Checked
            cfg.Ui.HotReloadConfigFile = _chkHotReloadConfig.Checked
            SetWindowsStartupEnabled(_chkRunAtWindowsStartup.Checked)

            cfg.Ui.ProjectFps = CDbl(_numProjectFps.Value)
            SettingsService.RaiseFpsChanged(cfg.Ui.ProjectFps)
            cfg.Ui.WindowHeight = CInt(_numWindowHeight.Value)
            If _chkSaveWindowPos.Checked Then
                cfg.Ui.WindowX = CInt(_numWindowX.Value)
                cfg.Ui.WindowY = CInt(_numWindowY.Value)
            Else
                cfg.Ui.WindowX = Nothing
                cfg.Ui.WindowY = Nothing
            End If
            cfg.Ui.WaveformAmpScale = CSng(_numWaveformAmpScale.Value)
            cfg.Ui.OsdDurationMs = CInt(_numOsdDurationMs.Value)
            cfg.Ui.PeakHoldDurationMs = CInt(_numPeakHoldMs.Value)
            cfg.Ui.MeterDecayFactor = CSng(_numMeterDecay.Value)

            cfg.MasterVolume01 = Math.Max(0.0F, Math.Min(1.0F, CSng(_numMasterVolumePercent.Value) / 100.0F))
            cfg.MasterBalance01 = Math.Max(0.0F, Math.Min(1.0F, (CSng(_numMasterBalancePercent.Value) / 200.0F) + 0.5F))

            cfg.MasterLevelSource = If(Convert.ToString(_cmbMasterLevelSource.SelectedItem), "Auto")
            MixerHardwareStateStore.CurrentMasterLevelSource = cfg.MasterLevelSource
            cfg.Ui.CacheDirectory = _txtCacheDir.Text.Trim()

            ' 6. Профили: если таблица редактировалась (включая удаление строк или сброс в 0 dB),
            ' собираем словарь строго из строк таблицы и синхронизируем с живыми дорожками в _model
            If _channelProfilesEditedInGrid Then
                Dim newProfiles As New Dictionary(Of String, ChannelProfileConfig)(StringComparer.OrdinalIgnoreCase)
                For Each row As DataGridViewRow In _gridChannelProfiles.Rows
                    If row.IsNewRow Then Continue For
                    Dim chName As String = Convert.ToString(row.Cells("ChName").Value)?.Trim()
                    If String.IsNullOrEmpty(chName) Then Continue For

                    Dim prof As New ChannelProfileConfig() With {
                        .Trim = ParseCellFloat(row.Cells("Trim").Value, 0.5F, 0.0F, 1.0F),
                        .EqHi = ParseCellFloat(row.Cells("EqHi").Value, 0.5F, 0.0F, 1.0F),
                        .EqMid = ParseCellFloat(row.Cells("EqMid").Value, 0.5F, 0.0F, 1.0F),
                        .EqLow = ParseCellFloat(row.Cells("EqLow").Value, 0.5F, 0.0F, 1.0F),
                        .ColorFx = ParseCellFloat(row.Cells("ColorFx").Value, 0.5F, 0.0F, 1.0F),
                        .ColorFxMode = ParseFxModeCell(row.Cells("ColorFxMode").Value),
                        .Fader = ParseCellFloat(row.Cells("Fader").Value, 1.0F, 0.0F, 1.0F),
                        .WaveformZoom = ParseCellFloat(row.Cells("WaveZoom").Value, 1.0F, 0.25F, 4.0F),
                        .IsMuted = Convert.ToBoolean(If(row.Cells("IsMuted").Value, False)),
                        .IsSolo = Convert.ToBoolean(If(row.Cells("IsSolo").Value, False))
                    }
                    newProfiles(chName.ToUpperInvariant()) = prof
                Next

                If Not newProfiles.ContainsKey("MAIN MIC") Then
                    newProfiles("MAIN MIC") = New ChannelProfileConfig()
                End If

                cfg.ChannelProfiles = newProfiles

                If _model?.Tracks IsNot Nothing Then
                    SyncLock _model.SyncRoot
                        For i As Integer = 0 To _model.Tracks.Count - 1
                            Dim t As TrackModel = _model.Tracks(i)
                            If t Is Nothing OrElse t.Type <> TargetFormatType.Audio Then Continue For
                            Dim key As String = If(String.IsNullOrWhiteSpace(t.Name), "MAIN MIC", t.Name.Trim().ToUpperInvariant())
                            If key = "MIC" OrElse key = "MIC 1-2" Then key = "MAIN MIC"

                            Dim matchedProf As ChannelProfileConfig = Nothing
                            If newProfiles.TryGetValue(key, matchedProf) AndAlso matchedProf IsNot Nothing Then
                                t.Trim = matchedProf.Trim
                                t.EqHi = matchedProf.EqHi
                                t.EqMid = matchedProf.EqMid
                                t.EqLow = matchedProf.EqLow
                                t.ColorFx = matchedProf.ColorFx
                                t.ColorFxMode = matchedProf.ColorFxMode
                                t.WaveformZoom = matchedProf.WaveformZoom
                                t.IsMuted = matchedProf.IsMuted
                                t.IsSolo = matchedProf.IsSolo
                                If t.Clips IsNot Nothing AndAlso t.Clips.Count > 0 Then
                                    t.Clips(0).Volume = matchedProf.Fader
                                End If
                            End If
                        Next
                    End SyncLock
                End If

                _channelProfilesEditedInGrid = False
            Else
                PopulateChannelProfilesGrid()
            End If

            ' 7. Пресеты и Доп. параметры
            Dim newPresets As New Dictionary(Of String, ChannelProfileConfig)(StringComparer.OrdinalIgnoreCase)
            For Each row As DataGridViewRow In _gridPresets.Rows
                If row.IsNewRow Then Continue For
                Dim pName As String = Convert.ToString(row.Cells("PresetName").Value)?.Trim()
                If String.IsNullOrEmpty(pName) Then Continue For

                Dim pr As New ChannelProfileConfig() With {
                    .Trim = ParseCellFloat(row.Cells("Trim").Value, 0.5F, 0.0F, 1.0F),
                    .EqHi = ParseCellFloat(row.Cells("EqHi").Value, 0.5F, 0.0F, 1.0F),
                    .EqMid = ParseCellFloat(row.Cells("EqMid").Value, 0.5F, 0.0F, 1.0F),
                    .EqLow = ParseCellFloat(row.Cells("EqLow").Value, 0.5F, 0.0F, 1.0F),
                    .ColorFx = ParseCellFloat(row.Cells("ColorFx").Value, 0.5F, 0.0F, 1.0F),
                    .ColorFxMode = ParseFxModeCell(row.Cells("ColorFxMode").Value),
                    .Fader = ParseCellFloat(row.Cells("Fader").Value, 1.0F, 0.0F, 1.0F)
                }
                newPresets(pName) = pr
            Next
            cfg.Presets = newPresets

            Dim newCustom As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            newCustom("LimiterEnabled") = If(_chkLimiterEnabled.Checked, "true", "false")
            Dim nativeRootVal As String = _txtNativeRootDir.Text.Trim()
            If Not String.IsNullOrEmpty(nativeRootVal) Then
                newCustom("NativeRootDirectory") = nativeRootVal
            End If
            For Each row As DataGridViewRow In _gridCustomParams.Rows
                If row.IsNewRow Then Continue For
                Dim ck As String = Convert.ToString(row.Cells("ParamKey").Value)?.Trim()
                Dim cv As String = Convert.ToString(row.Cells("ParamVal").Value)
                If Not String.IsNullOrEmpty(ck) Then
                    newCustom(ck) = If(cv, "")
                End If
            Next
            cfg.CustomParameters = newCustom

            If _audioPlayer IsNot Nothing Then
                _audioPlayer.MicMonitoringEnabled = cfg.Audio.MicMonitoringEnabled
                _audioPlayer.ApplyInitialSavedState(cfg)

                Dim curHwKws As String = If(cfg.Audio.HardwareKeywords IsNot Nothing, String.Join(",", cfg.Audio.HardwareKeywords), "")
                Dim curBlkKws As String = If(cfg.Audio.BlockedOutputKeywords IsNot Nothing, String.Join(",", cfg.Audio.BlockedOutputKeywords), "")
                Dim curBtKws As String = If(cfg.Audio.BluetoothHeadphoneKeywords IsNot Nothing, String.Join(",", cfg.Audio.BluetoothHeadphoneKeywords), "")

                If prevUseAsio <> cfg.Audio.UseAsio OrElse
                   prevPreferBt <> cfg.Audio.PreferBluetoothHeadphonesOutput OrElse
                   prevBtGuard <> cfg.Audio.BluetoothMicHighQualityOnly OrElse
                   prevMicCapLat <> cfg.Audio.MicCaptureLatencyMs OrElse
                   prevMicOutLat <> cfg.Audio.MicOutputLatencyMs OrElse
                   prevMicBufDur <> cfg.Audio.MicBufferDurationMs OrElse
                   Not String.Equals(prevBtKws, curBtKws, StringComparison.OrdinalIgnoreCase) OrElse
                   prevInOffset <> cfg.Audio.AsioInputChannelOffset OrElse
                   prevOutOffset <> cfg.Audio.AsioOutputChannelOffset OrElse
                   Not String.Equals(prevAsioDrv, cfg.Audio.PreferredAsioDriverName, StringComparison.OrdinalIgnoreCase) OrElse
                   Not String.Equals(prevOutId, cfg.Audio.PreferredOutputDeviceId, StringComparison.OrdinalIgnoreCase) OrElse
                   Not String.Equals(prevOutKw, cfg.Audio.PreferredOutputKeyword, StringComparison.OrdinalIgnoreCase) OrElse
                   Not String.Equals(prevMicKw, cfg.Audio.PreferredMicKeyword, StringComparison.OrdinalIgnoreCase) OrElse
                   Not String.Equals(prevMainKw, cfg.Audio.PreferredMainCaptureKeyword, StringComparison.OrdinalIgnoreCase) OrElse
                   Not String.Equals(prevHwKws, curHwKws, StringComparison.OrdinalIgnoreCase) OrElse
                   Not String.Equals(prevBlkKws, curBlkKws, StringComparison.OrdinalIgnoreCase) Then

                    Dim targetOut As String = If(cfg.Audio.PreferBluetoothHeadphonesOutput OrElse String.IsNullOrWhiteSpace(cfg.Audio.PreferredOutputDeviceId),
                                                 Nothing,
                                                 cfg.Audio.PreferredOutputDeviceId)
                    _audioPlayer.StartEngine(Nothing, targetOut, False)
                End If
            End If

            If _midiController IsNot Nothing Then
                _midiController.CrossfaderControlsBalance = cfg.Midi.CrossfaderControlsBalance
            End If

            MixerHardwareStateStore.FlushNow()
            _onSettingsApplied?.Invoke()
            Return True
        Catch ex As Exception
            MessageBox.Show(Me, $"Ошибка сохранения настроек: {ex.Message}", "Настройки", MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return False
        End Try
    End Function

    Private Sub ExportConfigToFile()
        Try
            If Not SaveValuesToConfigAndApply() Then Return
            Using sfd As New SaveFileDialog() With {
                .Title = "Экспорт конфигурации микшера",
                .Filter = "JSON файлы (*.json)|*.json|Все файлы (*.*)|*.*",
                .FileName = "yoump_mixer_config_backup.json"
            }
                If sfd.ShowDialog(Me) = DialogResult.OK Then
                    File.Copy(MixerHardwareStateStore.GetConfigFilePath(), sfd.FileName, overwrite:=True)
                End If
            End Using
        Catch ex As Exception
            MessageBox.Show(Me, $"Ошибка экспорта: {ex.Message}", "Экспорт", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub ImportConfigFromFile()
        Try
            Using ofd As New OpenFileDialog() With {
                .Title = "Импорт конфигурации микшера",
                .Filter = "JSON файлы (*.json)|*.json|Все файлы (*.*)|*.*"
            }
                If ofd.ShowDialog(Me) = DialogResult.OK AndAlso File.Exists(ofd.FileName) Then
                    System.Threading.Interlocked.Exchange(MixerHardwareStateStore.LastFlushTick, Environment.TickCount64)
                    File.Copy(ofd.FileName, MixerHardwareStateStore.GetConfigFilePath(), overwrite:=True)
                    System.Threading.Interlocked.Exchange(MixerHardwareStateStore.LastFlushTick, Environment.TickCount64)
                    Dim cfg = MixerHardwareStateStore.Load()
                    If _audioPlayer IsNot Nothing Then
                        _audioPlayer.ApplyInitialSavedState(cfg)
                    End If
                    LoadValuesFromConfig()
                    _onSettingsApplied?.Invoke()
                End If
            End Using
        Catch ex As Exception
            MessageBox.Show(Me, $"Ошибка импорта: {ex.Message}", "Импорт", MessageBoxButtons.OK, MessageBoxIcon.Error)
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

    Friend Sub ApplyDarkStyling()
        If Me.IsHandleCreated Then
            ThemeManager.ApplyDwm(Me.Handle)
        End If
        Dim bgMain As Color = Color.FromArgb(16, 18, 22)
        Dim bgInput As Color = Color.FromArgb(24, 28, 36)
        Dim fgText As Color = Color.FromArgb(242, 246, 255)

        Me.BackColor = bgMain
        Me.ForeColor = fgText
        StyleControlTree(Me, bgMain, bgInput, fgText)
    End Sub

    Private Shared Sub StyleControlTree(root As Control, bgMain As Color, bgInput As Color, fgText As Color)
        For Each c As Control In root.Controls
            If TypeOf c Is TabPage OrElse TypeOf c Is Panel OrElse TypeOf c Is SplitContainer Then
                c.BackColor = bgMain
                c.ForeColor = fgText
            ElseIf TypeOf c Is TextBox Then
                c.BackColor = bgInput
                c.ForeColor = fgText
            ElseIf TypeOf c Is NumericUpDown Then
                Dim nud As NumericUpDown = DirectCast(c, NumericUpDown)
                nud.BackColor = bgInput
                nud.ForeColor = fgText
                nud.BorderStyle = BorderStyle.FixedSingle
            ElseIf TypeOf c Is Button AndAlso Not (TypeOf c Is DarkCheckBox) Then
                Dim b As Button = DirectCast(c, Button)
                b.BackColor = Color.FromArgb(28, 33, 42)
                b.ForeColor = fgText
                b.FlatStyle = FlatStyle.Flat
                b.FlatAppearance.BorderSize = 1
                b.FlatAppearance.BorderColor = Color.FromArgb(48, 56, 70)
            End If

            If c.HasChildren Then
                StyleControlTree(c, bgMain, bgInput, fgText)
            End If
        Next
    End Sub

    Private Sub MixerSettingsForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load

    End Sub
End Class

#Region "Кастомные темные элементы управления Pioneer DJ Style"

''' <summary>
''' Полностью темный TabControl: исключает белые артефакты Windows и стрелки прокрутки
''' </summary>
Public Class DarkTabControl
    Inherits TabControl

    Private Shared ReadOnly BgColor As Color = Color.FromArgb(16, 18, 22)
    Private Shared ReadOnly TabNormalBg As Color = Color.FromArgb(24, 27, 34)
    Private Shared ReadOnly TabSelectedBg As Color = Color.FromArgb(34, 38, 48)
    Private Shared ReadOnly TextNormalColor As Color = Color.FromArgb(165, 175, 190)
    Private Shared ReadOnly TextSelectedColor As Color = Color.FromArgb(255, 255, 255)
    Private Shared ReadOnly AccentColor As Color = Color.FromArgb(0, 165, 255)
    Private Shared ReadOnly BorderColor As Color = Color.FromArgb(44, 50, 62)

    Public Sub New()
        Me.SetStyle(ControlStyles.AllPaintingInWmPaint Or
                    ControlStyles.UserPaint Or
                    ControlStyles.ResizeRedraw Or
                    ControlStyles.OptimizedDoubleBuffer, True)
        Me.DoubleBuffered = True
        Me.SizeMode = TabSizeMode.Normal
        Me.Padding = New Point(16, 7)
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        Dim g As Graphics = e.Graphics
        g.SmoothingMode = SmoothingMode.AntiAlias
        g.PixelOffsetMode = PixelOffsetMode.HighQuality

        Using brBg As New SolidBrush(BgColor)
            g.FillRectangle(brBg, Me.ClientRectangle)
        End Using

        For i As Integer = 0 To Me.TabCount - 1
            Dim tabRect As Rectangle = Me.GetTabRect(i)
            Dim isSelected As Boolean = (Me.SelectedIndex = i)

            Using brTab As New SolidBrush(If(isSelected, TabSelectedBg, TabNormalBg))
                g.FillRectangle(brTab, tabRect)
            End Using

            Using penTabBorder As New Pen(BorderColor, 1.0F)
                g.DrawRectangle(penTabBorder, tabRect.X, tabRect.Y, tabRect.Width, tabRect.Height)
            End Using

            If isSelected Then
                Using brAccent As New SolidBrush(AccentColor)
                    g.FillRectangle(brAccent, tabRect.X + 1, tabRect.Bottom - 3, tabRect.Width - 2, 3)
                End Using
            End If

            Dim tabText As String = Me.TabPages(i).Text
            Dim textCol As Color = If(isSelected, TextSelectedColor, TextNormalColor)
            Dim fStyle As FontStyle = If(isSelected, FontStyle.Bold, FontStyle.Regular)

            Using f As New Font("Segoe UI", 9.0F, fStyle)
                Dim flags As TextFormatFlags = TextFormatFlags.HorizontalCenter Or
                                               TextFormatFlags.VerticalCenter Or
                                               TextFormatFlags.SingleLine Or
                                               TextFormatFlags.NoClipping
                TextRenderer.DrawText(g, tabText, f, tabRect, textCol, flags)
            End Using
        Next

        If Me.TabCount > 0 AndAlso Me.SelectedTab IsNot Nothing Then
            Dim pageRect As Rectangle = Me.SelectedTab.Bounds
            pageRect.Inflate(1, 1)
            Using penBorder As New Pen(BorderColor, 1.0F)
                g.DrawRectangle(penBorder, pageRect)
            End Using
        End If
    End Sub
End Class

''' <summary>
''' Матовый темный GroupBox с тонкой границей
''' </summary>
Public Class DarkGroupBox
    Inherits GroupBox

    Private Shared ReadOnly BorderColor As Color = Color.FromArgb(48, 54, 68)
    Private Shared ReadOnly TitleColor As Color = Color.FromArgb(230, 238, 250)

    Public Sub New()
        Me.SetStyle(ControlStyles.UserPaint Or ControlStyles.AllPaintingInWmPaint Or ControlStyles.OptimizedDoubleBuffer, True)
        Me.DoubleBuffered = True
        Me.BackColor = Color.Transparent
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        Dim g As Graphics = e.Graphics
        g.SmoothingMode = SmoothingMode.AntiAlias

        Dim tSize As Size = TextRenderer.MeasureText(Me.Text, Me.Font)
        Dim borderRect As New Rectangle(0, 8, Me.Width - 1, Me.Height - 10)

        Using p As New Pen(BorderColor, 1.0F)
            g.DrawRectangle(p, borderRect)
        End Using

        Dim titleBgRect As New Rectangle(12, 0, tSize.Width + 8, tSize.Height)
        Dim parentBg As Color = If(Me.Parent IsNot Nothing AndAlso Me.Parent.BackColor <> Color.Transparent, Me.Parent.BackColor, Color.FromArgb(18, 20, 24))
        Using brBg As New SolidBrush(parentBg)
            g.FillRectangle(brBg, titleBgRect)
        End Using

        Using brText As New SolidBrush(TitleColor)
            g.DrawString(Me.Text, Me.Font, brText, 16, 1)
        End Using
    End Sub
End Class

''' <summary>
''' Кастомный CheckBox без белого фона и с неоновой галочкой
''' </summary>
Public Class DarkCheckBox
    Inherits CheckBox

    Public Sub New()
        Me.SetStyle(ControlStyles.UserPaint Or ControlStyles.AllPaintingInWmPaint Or ControlStyles.OptimizedDoubleBuffer, True)
        Me.DoubleBuffered = True
        Me.BackColor = Color.Transparent
        Me.ForeColor = Color.FromArgb(242, 246, 255)
        Me.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular)
        Me.AutoSize = False
        Me.Cursor = Cursors.Hand
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        Dim g As Graphics = e.Graphics
        g.SmoothingMode = SmoothingMode.AntiAlias
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit

        Dim parentBg As Color = If(Me.Parent IsNot Nothing AndAlso Me.Parent.BackColor <> Color.Transparent, Me.Parent.BackColor, Color.FromArgb(18, 20, 24))
        g.Clear(parentBg)

        Dim boxSize As Integer = 15
        Dim boxRect As New Rectangle(0, (Me.Height - boxSize) \ 2, boxSize, boxSize)

        Using brBox As New SolidBrush(Color.FromArgb(22, 26, 34))
            g.FillRectangle(brBox, boxRect)
        End Using

        Dim borderColor As Color = If(Me.Checked, Color.FromArgb(0, 165, 255), Color.FromArgb(64, 72, 88))
        Using pBorder As New Pen(borderColor, 1.0F)
            g.DrawRectangle(pBorder, boxRect)
        End Using

        If Me.Checked Then
            Using pCheck As New Pen(Color.FromArgb(0, 165, 255), 2.0F)
                pCheck.StartCap = LineCap.Round
                pCheck.EndCap = LineCap.Round
                g.DrawLines(pCheck, {
                    New Point(boxRect.X + 3, boxRect.Y + 7),
                    New Point(boxRect.X + 6, boxRect.Y + 11),
                    New Point(boxRect.X + 12, boxRect.Y + 4)
                })
            End Using
        End If

        Dim textRect As New Rectangle(22, 0, Me.Width - 22, Me.Height)
        Dim flags As TextFormatFlags = TextFormatFlags.Left Or TextFormatFlags.VerticalCenter Or TextFormatFlags.WordBreak
        TextRenderer.DrawText(g, Me.Text, Me.Font, textRect, Me.ForeColor, flags)
    End Sub
End Class

''' <summary>
''' Полностью темный ComboBox со стильным темным выпадающим списком (без утечек GDI и порчи Brushes.White)
''' </summary>
Public Class DarkComboBox
    Inherits ComboBox
    Public Sub New()
        Me.DrawMode = DrawMode.OwnerDrawFixed
        Me.DropDownStyle = ComboBoxStyle.DropDownList
        Me.FlatStyle = FlatStyle.Flat
        Me.BackColor = Color.FromArgb(24, 28, 36)
        Me.ForeColor = Color.FromArgb(242, 246, 255)
        Me.Font = New Font("Segoe UI", 9.0F)
        Me.ItemHeight = 22
    End Sub

    Protected Overrides Sub OnDrawItem(e As DrawItemEventArgs)
        If e.Index < 0 OrElse e.Index >= Me.Items.Count Then Return
        Dim g As Graphics = e.Graphics

        Dim isSelected As Boolean = (e.State And DrawItemState.Selected) = DrawItemState.Selected
        Dim bgColor As Color = If(isSelected, Color.FromArgb(0, 130, 215), Color.FromArgb(24, 28, 36))
        Dim textColor As Color = If(isSelected, Color.White, Me.ForeColor)

        Using bgBrush As New SolidBrush(bgColor)
            g.FillRectangle(bgBrush, e.Bounds)
        End Using

        Dim text As String = Convert.ToString(Me.Items(e.Index))
        Dim textRect As New Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 6), e.Bounds.Height)
        Dim flags As TextFormatFlags = TextFormatFlags.Left Or TextFormatFlags.VerticalCenter Or TextFormatFlags.EndEllipsis
        TextRenderer.DrawText(g, text, Me.Font, textRect, textColor, flags)
    End Sub
End Class

#End Region
' --- END OF FILE MixerSettingsForm.vb ---