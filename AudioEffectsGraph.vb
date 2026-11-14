' --- START OF FILE AudioEffectsGraph.vb ---
Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports System.Numerics
Imports NAudio.Wave
Imports yoump.Audio.Native

Namespace yoump.Audio

    Public Interface IAudioEffect
        ReadOnly Property Name As String
        Property IsEnabled As Boolean
        Function Process(buffer() As Single, offset As Integer, count As Integer) As Integer
        Sub Init(sampleRate As Integer, channels As Integer)
    End Interface

    Public Class FastBiQuadFilter
        Private a1, a2, b0, b1, b2 As Single
        Private x1, x2, y1, y2 As Single

        Public Sub Reset()
            x1 = 0.0F
            x2 = 0.0F
            y1 = 0.0F
            y2 = 0.0F
        End Sub

        Public Sub SetLowShelf(sampleRate As Single, cutoffFrequency As Single, shelfSlope As Single, dbGain As Single)
            If sampleRate <= 0.0F Then Return
            Dim safeCutoff As Single = Math.Max(10.0F, Math.Min(sampleRate * 0.49F, cutoffFrequency))
            Dim safeSlope As Double = Math.Max(0.01, CDbl(shelfSlope))
            Dim w0 As Double = 2.0 * Math.PI * safeCutoff / sampleRate
            Dim cosW0 As Double = Math.Cos(w0)
            Dim sinW0 As Double = Math.Sin(w0)
            Dim A As Double = Math.Pow(10, dbGain / 40.0)
            Dim alpha As Double = (sinW0 / 2.0) * Math.Sqrt(Math.Max(0.0, (A + 1.0 / A) * (1.0 / safeSlope - 1.0) + 2.0))
            Dim temp As Double = 2.0 * Math.Sqrt(A) * alpha

            Dim b0_d As Double = A * ((A + 1.0) - (A - 1.0) * cosW0 + temp)
            Dim b1_d As Double = 2.0 * A * ((A - 1.0) - (A + 1.0) * cosW0)
            Dim b2_d As Double = A * ((A + 1.0) - (A - 1.0) * cosW0 - temp)
            Dim a0_d As Double = (A + 1.0) + (A - 1.0) * cosW0 + temp
            Dim a1_d As Double = -2.0 * ((A - 1.0) + (A + 1.0) * cosW0)
            Dim a2_d As Double = (A + 1.0) + (A - 1.0) * cosW0 - temp

            SetCoefficients(a0_d, a1_d, a2_d, b0_d, b1_d, b2_d)
        End Sub

        Public Sub SetHighShelf(sampleRate As Single, cutoffFrequency As Single, shelfSlope As Single, dbGain As Single)
            If sampleRate <= 0.0F Then Return
            Dim safeCutoff As Single = Math.Max(10.0F, Math.Min(sampleRate * 0.49F, cutoffFrequency))
            Dim safeSlope As Double = Math.Max(0.01, CDbl(shelfSlope))
            Dim w0 As Double = 2.0 * Math.PI * safeCutoff / sampleRate
            Dim cosW0 As Double = Math.Cos(w0)
            Dim sinW0 As Double = Math.Sin(w0)
            Dim A As Double = Math.Pow(10, dbGain / 40.0)
            Dim alpha As Double = (sinW0 / 2.0) * Math.Sqrt(Math.Max(0.0, (A + 1.0 / A) * (1.0 / safeSlope - 1.0) + 2.0))
            Dim temp As Double = 2.0 * Math.Sqrt(A) * alpha

            Dim b0_d As Double = A * ((A + 1.0) + (A - 1.0) * cosW0 + temp)
            Dim b1_d As Double = -2.0 * A * ((A - 1.0) + (A + 1.0) * cosW0)
            Dim b2_d As Double = A * ((A + 1.0) + (A - 1.0) * cosW0 - temp)
            Dim a0_d As Double = (A + 1.0) - (A - 1.0) * cosW0 + temp
            Dim a1_d As Double = 2.0 * ((A - 1.0) - (A + 1.0) * cosW0)
            Dim a2_d As Double = (A + 1.0) - (A - 1.0) * cosW0 - temp

            SetCoefficients(a0_d, a1_d, a2_d, b0_d, b1_d, b2_d)
        End Sub

        Public Sub SetPeakingEq(sampleRate As Single, centreFrequency As Single, q As Single, dbGain As Single)
            If sampleRate <= 0.0F Then Return
            Dim safeCentre As Single = Math.Max(10.0F, Math.Min(sampleRate * 0.49F, centreFrequency))
            Dim safeQ As Double = Math.Max(0.1, CDbl(q))
            Dim w0 As Double = 2.0 * Math.PI * safeCentre / sampleRate
            Dim cosW0 As Double = Math.Cos(w0)
            Dim alpha As Double = Math.Sin(w0) / (2.0 * safeQ)
            Dim A As Double = Math.Pow(10, dbGain / 40.0)

            Dim b0_d As Double = 1.0 + alpha * A
            Dim b1_d As Double = -2.0 * cosW0
            Dim b2_d As Double = 1.0 - alpha * A
            Dim a0_d As Double = 1.0 + alpha / A
            Dim a1_d As Double = -2.0 * cosW0
            Dim a2_d As Double = 1.0 - alpha / A

            SetCoefficients(a0_d, a1_d, a2_d, b0_d, b1_d, b2_d)
        End Sub

        Public Sub SetLowPassFilter(sampleRate As Single, cutoffFrequency As Single, q As Single)
            If sampleRate <= 0.0F Then Return
            Dim safeCutoff As Single = Math.Max(10.0F, Math.Min(sampleRate * 0.49F, cutoffFrequency))
            Dim w0 As Double = 2.0 * Math.PI * safeCutoff / sampleRate
            Dim cosW0 As Double = Math.Cos(w0)
            Dim alpha As Double = Math.Sin(w0) / (2.0 * Math.Max(0.1F, q))

            Dim b0_d As Double = (1.0 - cosW0) / 2.0
            Dim b1_d As Double = 1.0 - cosW0
            Dim b2_d As Double = (1.0 - cosW0) / 2.0
            Dim a0_d As Double = 1.0 + alpha
            Dim a1_d As Double = -2.0 * cosW0
            Dim a2_d As Double = 1.0 - alpha

            SetCoefficients(a0_d, a1_d, a2_d, b0_d, b1_d, b2_d)
        End Sub

        Public Sub SetHighPassFilter(sampleRate As Single, cutoffFrequency As Single, q As Single)
            If sampleRate <= 0.0F Then Return
            Dim safeCutoff As Single = Math.Max(10.0F, Math.Min(sampleRate * 0.49F, cutoffFrequency))
            Dim w0 As Double = 2.0 * Math.PI * safeCutoff / sampleRate
            Dim cosW0 As Double = Math.Cos(w0)
            Dim alpha As Double = Math.Sin(w0) / (2.0 * Math.Max(0.1F, q))

            Dim b0_d As Double = (1.0 + cosW0) / 2.0
            Dim b1_d As Double = -(1.0 + cosW0)
            Dim b2_d As Double = (1.0 + cosW0) / 2.0
            Dim a0_d As Double = 1.0 + alpha
            Dim a1_d As Double = -2.0 * cosW0
            Dim a2_d As Double = 1.0 - alpha

            SetCoefficients(a0_d, a1_d, a2_d, b0_d, b1_d, b2_d)
        End Sub

        Public Sub SetAllPassFilter(sampleRate As Single, cutoffFrequency As Single, q As Single)
            If sampleRate <= 0.0F Then Return
            Dim safeCutoff As Single = Math.Max(10.0F, Math.Min(sampleRate * 0.49F, cutoffFrequency))
            Dim w0 As Double = 2.0 * Math.PI * safeCutoff / sampleRate
            Dim cosW0 As Double = Math.Cos(w0)
            Dim alpha As Double = Math.Sin(w0) / (2.0 * Math.Max(0.1F, q))

            Dim b0_d As Double = 1.0 - alpha
            Dim b1_d As Double = -2.0 * cosW0
            Dim b2_d As Double = 1.0 + alpha
            Dim a0_d As Double = 1.0 + alpha
            Dim a1_d As Double = -2.0 * cosW0
            Dim a2_d As Double = 1.0 - alpha

            SetCoefficients(a0_d, a1_d, a2_d, b0_d, b1_d, b2_d)
        End Sub

        Private Sub SetCoefficients(a0 As Double, a1 As Double, a2 As Double, b0 As Double, b1 As Double, b2 As Double)
            Me.b0 = CSng(b0 / a0)
            Me.b1 = CSng(b1 / a0)
            Me.b2 = CSng(b2 / a0)
            Me.a1 = CSng(a1 / a0)
            Me.a2 = CSng(a2 / a0)
        End Sub

        Public Function Transform(inSample As Single) As Single
            Dim result As Single = b0 * inSample + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
            If Single.IsNaN(result) OrElse Single.IsInfinity(result) Then
                result = 0.0F
                x1 = 0.0F
                x2 = 0.0F
                y1 = 0.0F
                y2 = 0.0F
            Else
                If Math.Abs(result) < 1.0E-20F Then result = 0.0F
                x2 = x1
                x1 = inSample
                y2 = y1
                y1 = result
            End If
            Return result
        End Function
    End Class

    Public Class CytomicSvfFilter
        Private ic1eq As Single = 0.0F
        Private ic2eq As Single = 0.0F
        Private a1 As Single = 0.0F
        Private a2 As Single = 0.0F
        Private a3 As Single = 0.0F
        Private k As Single = 1.0F

        Public Sub Reset()
            ic1eq = 0.0F
            ic2eq = 0.0F
        End Sub

        Public Sub SetCoeffs(sampleRate As Single, cutoff As Single, q As Single)
            If sampleRate <= 0.0F Then Return
            Dim safeCutoff As Single = Math.Max(15.0F, Math.Min(sampleRate * 0.47F, cutoff))
            Dim g As Single = CSng(Math.Tan(Math.PI * safeCutoff / sampleRate))
            k = 1.0F / Math.Max(0.1F, q)
            a1 = 1.0F / (1.0F + g * (g + k))
            a2 = g * a1
            a3 = g * a2
        End Sub

        Public Function ProcessLowPass(v0 As Single) As Single
            Dim v3 As Single = v0 - ic2eq
            Dim v1 As Single = a1 * ic1eq + a2 * v3
            Dim v2 As Single = ic2eq + a2 * ic1eq + a3 * v3
            ic1eq = 2.0F * v1 - ic1eq
            ic2eq = 2.0F * v2 - ic2eq
            If Single.IsNaN(v2) OrElse Single.IsInfinity(v2) Then
                ic1eq = 0.0F
                ic2eq = 0.0F
                Return 0.0F
            End If
            If Math.Abs(ic1eq) < 1.0E-20F Then ic1eq = 0.0F
            If Math.Abs(ic2eq) < 1.0E-20F Then ic2eq = 0.0F
            Return v2
        End Function

        Public Function ProcessHighPass(v0 As Single) As Single
            Dim v3 As Single = v0 - ic2eq
            Dim v1 As Single = a1 * ic1eq + a2 * v3
            Dim v2 As Single = ic2eq + a2 * ic1eq + a3 * v3
            ic1eq = 2.0F * v1 - ic1eq
            ic2eq = 2.0F * v2 - ic2eq
            Dim res As Single = v0 - k * v1 - v2
            If Single.IsNaN(res) OrElse Single.IsInfinity(res) Then
                ic1eq = 0.0F
                ic2eq = 0.0F
                Return 0.0F
            End If
            If Math.Abs(ic1eq) < 1.0E-20F Then ic1eq = 0.0F
            If Math.Abs(ic2eq) < 1.0E-20F Then ic2eq = 0.0F
            Return res
        End Function
    End Class

    Public Class EffectChainProvider
        Implements ISampleProvider

        Private ReadOnly _source As ISampleProvider
        Private ReadOnly _effectsList As New List(Of IAudioEffect)()
        Private ReadOnly _effectsLock As New Object()

        Private _activeEffects As IAudioEffect() = Array.Empty(Of IAudioEffect)()

        Public CurrentChannelPeak As Single = 0.0F
        Public CurrentPeakL As Single = 0.0F
        Public CurrentPeakR As Single = 0.0F

        Public ExternalDirectPeakPfl As Single = 0.0F
        Public ExternalDirectPeakL As Single = 0.0F
        Public ExternalDirectPeakR As Single = 0.0F

        Public Sub New(source As ISampleProvider)
            ArgumentNullException.ThrowIfNull(source)
            _source = source
        End Sub

        Public ReadOnly Property WaveFormat As WaveFormat Implements ISampleProvider.WaveFormat
            Get
                Return _source.WaveFormat
            End Get
        End Property

        Public ReadOnly Property Effects As IReadOnlyList(Of IAudioEffect)
            Get
                Return Volatile.Read(_activeEffects)
            End Get
        End Property

        Public Sub AddEffect(effect As IAudioEffect)
            ArgumentNullException.ThrowIfNull(effect)
            effect.Init(WaveFormat.SampleRate, WaveFormat.Channels)

            SyncLock _effectsLock
                _effectsList.Add(effect)
                Volatile.Write(_activeEffects, _effectsList.ToArray())
            End SyncLock
        End Sub

        Public Function Read(buffer() As Single, offset As Integer, count As Integer) As Integer Implements ISampleProvider.Read
            Dim samplesRead As Integer = _source.Read(buffer, offset, count)

            If samplesRead = 0 Then
                Volatile.Write(CurrentChannelPeak, Volatile.Read(ExternalDirectPeakPfl))
                Volatile.Write(CurrentPeakL, Volatile.Read(ExternalDirectPeakL))
                Volatile.Write(CurrentPeakR, Volatile.Read(ExternalDirectPeakR))
                Return 0
            End If

            For idx As Integer = 0 To samplesRead - 1
                Dim val As Single = buffer(offset + idx)
                If Single.IsNaN(val) OrElse Single.IsInfinity(val) Then
                    buffer(offset + idx) = 0.0F
                ElseIf val > 4.0F Then
                    buffer(offset + idx) = 4.0F
                ElseIf val < -4.0F Then
                    buffer(offset + idx) = -4.0F
                End If
            Next

            Dim currentEffects = Volatile.Read(_activeEffects)
            Dim hasNativeMixer As Boolean = False

            For i As Integer = 0 To currentEffects.Length - 1
                Dim fx As IAudioEffect = currentEffects(i)
                If Not fx.IsEnabled Then Continue For

                fx.Process(buffer, offset, samplesRead)

                If TypeOf fx Is NativeMixerEffect Then
                    hasNativeMixer = True
                    Dim m = DirectCast(fx, NativeMixerEffect).GetMeters()
                    Volatile.Write(CurrentChannelPeak, Math.Max(m.channelPeakPfl, Volatile.Read(ExternalDirectPeakPfl)))
                    Volatile.Write(CurrentPeakL, Math.Max(m.masterPeakL, Volatile.Read(ExternalDirectPeakL)))
                    Volatile.Write(CurrentPeakR, Math.Max(m.masterPeakR, Volatile.Read(ExternalDirectPeakR)))
                ElseIf TypeOf fx Is PioneerColorFilterEffect Then
                    Dim chMax As Single = 0.0F
                    For s As Integer = 0 To samplesRead - 1
                        Dim a As Single = Math.Abs(buffer(offset + s))
                        If a > chMax Then chMax = a
                    Next
                    If Single.IsNaN(chMax) OrElse Single.IsInfinity(chMax) Then chMax = 0.0F
                    Volatile.Write(CurrentChannelPeak, Math.Max(Math.Min(1.0F, chMax * 1.35F), Volatile.Read(ExternalDirectPeakPfl)))
                End If
            Next

            If hasNativeMixer Then
                Return samplesRead
            End If

            Dim maxL As Single = 0.0F
            Dim maxR As Single = 0.0F
            Dim channels As Integer = _source.WaveFormat.Channels

            If channels = 2 Then
                For i As Integer = 0 To samplesRead - 1 Step 2
                    Dim absL As Single = Math.Abs(buffer(offset + i))
                    If absL > maxL Then maxL = absL

                    If i + 1 < samplesRead Then
                        Dim absR As Single = Math.Abs(buffer(offset + i + 1))
                        If absR > maxR Then maxR = absR
                    End If
                Next
            Else
                For i As Integer = 0 To samplesRead - 1
                    Dim absVal As Single = Math.Abs(buffer(offset + i))
                    If absVal > maxL Then maxL = absVal
                Next
                maxR = maxL
            End If

            If Single.IsNaN(maxL) OrElse Single.IsInfinity(maxL) Then maxL = 0.0F
            If Single.IsNaN(maxR) OrElse Single.IsInfinity(maxR) Then maxR = 0.0F

            Volatile.Write(CurrentPeakL, Math.Max(Math.Min(1.0F, maxL * 1.35F), Volatile.Read(ExternalDirectPeakL)))
            Volatile.Write(CurrentPeakR, Math.Max(Math.Min(1.0F, maxR * 1.35F), Volatile.Read(ExternalDirectPeakR)))

            Return samplesRead
        End Function
    End Class

    Public Class BasicEqualizerEffect
        Implements IAudioEffect

        Private Const SQRT2_INV As Single = 0.707106769F

        Public ReadOnly Property Name As String Implements IAudioEffect.Name
            Get
                Return "Pioneer DJM 3-Band Isolator EQ"
            End Get
        End Property

        Public Property IsEnabled As Boolean = True Implements IAudioEffect.IsEnabled

        Private _lpLow250_1 As FastBiQuadFilter()
        Private _lpLow250_2 As FastBiQuadFilter()
        Private _hpLow250_1 As FastBiQuadFilter()
        Private _hpLow250_2 As FastBiQuadFilter()

        Private _lpMid3500_1 As FastBiQuadFilter()
        Private _lpMid3500_2 As FastBiQuadFilter()
        Private _hpHigh3500_1 As FastBiQuadFilter()
        Private _hpHigh3500_2 As FastBiQuadFilter()

        Private _apLow3500 As FastBiQuadFilter()

        Private _channels As Integer
        Private _sampleRate As Integer

        Private _targetLowGain As Single = 0.0F
        Private _targetMidGain As Single = 0.0F
        Private _targetHighGain As Single = 0.0F

        Private _targetTrim As Single = 1.0F
        Private _curTrim As Single = 1.0F

        Private _curLowLin As Single = 1.0F
        Private _curMidLin As Single = 1.0F
        Private _curHighLin As Single = 1.0F

        Public Property TrimGain As Single
            Get
                Return Volatile.Read(_targetTrim)
            End Get
            Set(value As Single)
                Volatile.Write(_targetTrim, value)
            End Set
        End Property

        Public Property LowGainDb As Single
            Get
                Return Volatile.Read(_targetLowGain)
            End Get
            Set(value As Single)
                Volatile.Write(_targetLowGain, value)
            End Set
        End Property

        Public Property MidGainDb As Single
            Get
                Return Volatile.Read(_targetMidGain)
            End Get
            Set(value As Single)
                Volatile.Write(_targetMidGain, value)
            End Set
        End Property

        Public Property HighGainDb As Single
            Get
                Return Volatile.Read(_targetHighGain)
            End Get
            Set(value As Single)
                Volatile.Write(_targetHighGain, value)
            End Set
        End Property

        Public Shared Function CalculatePioneerTrimLinear(trimGain0To2 As Single) As Single
            Dim knob As Single = trimGain0To2 * 0.5F
            If knob <= 0.015F Then Return 0.0F
            If knob >= 0.492F AndAlso knob <= 0.508F Then Return 1.0F
            If knob > 0.5F Then
                Dim boostDb As Single = ((Math.Min(1.0F, knob) - 0.5F) / 0.5F) * 9.0F
                Return CSng(Math.Pow(10.0, boostDb / 20.0))
            Else
                Dim norm As Single = (knob - 0.015F) / 0.485F
                Return norm * norm
            End If
        End Function

        Public Shared Function DbToIsolatorLinear(db As Single) As Single
            If db <= -11.7F Then Return 0.0F
            If Math.Abs(db) < 0.05F Then Return 1.0F
            If db < 0.0F Then
                Dim norm As Single = Math.Max(0.0F, (db + 12.0F) / 12.0F)
                Return norm * norm * norm
            Else
                Dim boostDb As Single = Math.Min(6.0F, db)
                Return CSng(Math.Pow(10.0, boostDb / 20.0))
            End If
        End Function

        Public Sub Init(sampleRate As Integer, channels As Integer) Implements IAudioEffect.Init
            _sampleRate = Math.Max(8000, sampleRate)
            _channels = Math.Max(1, channels)

            _lpLow250_1 = New FastBiQuadFilter(_channels - 1) {}
            _lpLow250_2 = New FastBiQuadFilter(_channels - 1) {}
            _hpLow250_1 = New FastBiQuadFilter(_channels - 1) {}
            _hpLow250_2 = New FastBiQuadFilter(_channels - 1) {}

            _lpMid3500_1 = New FastBiQuadFilter(_channels - 1) {}
            _lpMid3500_2 = New FastBiQuadFilter(_channels - 1) {}
            _hpHigh3500_1 = New FastBiQuadFilter(_channels - 1) {}
            _hpHigh3500_2 = New FastBiQuadFilter(_channels - 1) {}

            _apLow3500 = New FastBiQuadFilter(_channels - 1) {}

            For i As Integer = 0 To _channels - 1
                _lpLow250_1(i) = New FastBiQuadFilter()
                _lpLow250_2(i) = New FastBiQuadFilter()
                _hpLow250_1(i) = New FastBiQuadFilter()
                _hpLow250_2(i) = New FastBiQuadFilter()

                _lpMid3500_1(i) = New FastBiQuadFilter()
                _lpMid3500_2(i) = New FastBiQuadFilter()
                _hpHigh3500_1(i) = New FastBiQuadFilter()
                _hpHigh3500_2(i) = New FastBiQuadFilter()

                _apLow3500(i) = New FastBiQuadFilter()

                _lpLow250_1(i).SetLowPassFilter(_sampleRate, 250.0F, SQRT2_INV)
                _lpLow250_2(i).SetLowPassFilter(_sampleRate, 250.0F, SQRT2_INV)
                _hpLow250_1(i).SetHighPassFilter(_sampleRate, 250.0F, SQRT2_INV)
                _hpLow250_2(i).SetHighPassFilter(_sampleRate, 250.0F, SQRT2_INV)

                _lpMid3500_1(i).SetLowPassFilter(_sampleRate, 3500.0F, SQRT2_INV)
                _lpMid3500_2(i).SetLowPassFilter(_sampleRate, 3500.0F, SQRT2_INV)
                _hpHigh3500_1(i).SetHighPassFilter(_sampleRate, 3500.0F, SQRT2_INV)
                _hpHigh3500_2(i).SetHighPassFilter(_sampleRate, 3500.0F, SQRT2_INV)

                _apLow3500(i).SetAllPassFilter(_sampleRate, 3500.0F, SQRT2_INV)
            Next

            _curLowLin = DbToIsolatorLinear(Volatile.Read(_targetLowGain))
            _curMidLin = DbToIsolatorLinear(Volatile.Read(_targetMidGain))
            _curHighLin = DbToIsolatorLinear(Volatile.Read(_targetHighGain))
            _curTrim = CalculatePioneerTrimLinear(Volatile.Read(_targetTrim))
        End Sub

        Public Function Process(buffer() As Single, offset As Integer, count As Integer) As Integer Implements IAudioEffect.Process
            If _channels <= 0 OrElse _lpLow250_1 Is Nothing Then Return count

            Dim targetLowLin As Single = DbToIsolatorLinear(Volatile.Read(_targetLowGain))
            Dim targetMidLin As Single = DbToIsolatorLinear(Volatile.Read(_targetMidGain))
            Dim targetHighLin As Single = DbToIsolatorLinear(Volatile.Read(_targetHighGain))
            Dim tTrim As Single = CalculatePioneerTrimLinear(Volatile.Read(_targetTrim))

            Dim frames As Integer = count \ _channels
            If frames <= 0 Then Return count

            Dim stepLow As Single = (targetLowLin - _curLowLin) / frames
            Dim stepMid As Single = (targetMidLin - _curMidLin) / frames
            Dim stepHigh As Single = (targetHighLin - _curHighLin) / frames
            Dim stepTrim As Single = (tTrim - _curTrim) / frames

            For i As Integer = 0 To frames - 1
                _curLowLin += stepLow
                _curMidLin += stepMid
                _curHighLin += stepHigh
                _curTrim += stepTrim

                For ch As Integer = 0 To _channels - 1
                    Dim sampleIdx As Integer = offset + (i * _channels) + ch
                    Dim x As Single = buffer(sampleIdx)
                    If Single.IsNaN(x) OrElse Single.IsInfinity(x) Then x = 0.0F
                    x *= _curTrim

                    Dim lowBranch As Single = _lpLow250_2(ch).Transform(_lpLow250_1(ch).Transform(x))
                    Dim highBranch As Single = _hpLow250_2(ch).Transform(_hpLow250_1(ch).Transform(x))

                    Dim lowBand As Single = _apLow3500(ch).Transform(lowBranch)
                    Dim midBand As Single = _lpMid3500_2(ch).Transform(_lpMid3500_1(ch).Transform(highBranch))
                    Dim highBand As Single = _hpHigh3500_2(ch).Transform(_hpHigh3500_1(ch).Transform(highBranch))

                    ' Всегда суммируем фазово-согласованные полосы LR4 (без скачка фазы при выходе из 0 dB)
                    Dim outSample As Single = (lowBand * _curLowLin) + (midBand * _curMidLin) + (highBand * _curHighLin)

                    If Single.IsNaN(outSample) OrElse Single.IsInfinity(outSample) Then outSample = 0.0F
                    buffer(sampleIdx) = outSample
                Next
            Next

            _curLowLin = targetLowLin
            _curMidLin = targetMidLin
            _curHighLin = targetHighLin
            _curTrim = tTrim

            Return count
        End Function
    End Class

    Public Class PioneerColorFilterEffect
        Implements IAudioEffect

        Public ReadOnly Property Name As String Implements IAudioEffect.Name
            Get
                Return "Pioneer Color Filter"
            End Get
        End Property

        Public Property IsEnabled As Boolean = True Implements IAudioEffect.IsEnabled

        Private _svf1 As CytomicSvfFilter()
        Private _svf2 As CytomicSvfFilter()
        Private _svfWasBypassed As Boolean = True
        Private _wasLowPassMode As Boolean = True
        Private _channels As Integer
        Private _sampleRate As Integer

        Private _targetKnob As Single = 0.5F
        Private _currentKnob As Single = 0.5F

        Public Property KnobValue As Single
            Get
                Return Volatile.Read(_targetKnob)
            End Get
            Set(value As Single)
                Dim clamped As Single = Math.Max(0.0F, Math.Min(1.0F, value))
                Volatile.Write(_targetKnob, clamped)
            End Set
        End Property

        Public Sub Init(sampleRate As Integer, channels As Integer) Implements IAudioEffect.Init
            _sampleRate = Math.Max(8000, sampleRate)
            _channels = Math.Max(1, channels)

            _svf1 = New CytomicSvfFilter(_channels - 1) {}
            _svf2 = New CytomicSvfFilter(_channels - 1) {}
            For i As Integer = 0 To _channels - 1
                _svf1(i) = New CytomicSvfFilter()
                _svf2(i) = New CytomicSvfFilter()
                _svf1(i).SetCoeffs(_sampleRate, 20000.0F, 1.45F)
                _svf2(i).SetCoeffs(_sampleRate, 20000.0F, 0.85F)
            Next

            _svfWasBypassed = True
            _currentKnob = Volatile.Read(_targetKnob)
            _wasLowPassMode = (_currentKnob < 0.5F)
        End Sub

        Public Function Process(buffer() As Single, offset As Integer, count As Integer) As Integer Implements IAudioEffect.Process
            If _channels <= 0 OrElse _svf1 Is Nothing Then Return count

            Dim target As Single = Volatile.Read(_targetKnob)
            Dim frames As Integer = count \ _channels
            If frames <= 0 Then Return count

            If Math.Abs(target - 0.5F) < 0.008F AndAlso Math.Abs(_currentKnob - 0.5F) < 0.008F Then
                _currentKnob = target
                If Not _svfWasBypassed Then
                    For ch As Integer = 0 To _channels - 1
                        _svf1(ch).Reset()
                        _svf2(ch).Reset()
                    Next
                    _svfWasBypassed = True
                End If
                Return count
            End If

            Dim knobStep As Single = (target - _currentKnob) / frames
            If _svfWasBypassed Then
                _wasLowPassMode = ((_currentKnob + knobStep) < 0.5F)
                _svfWasBypassed = False
            End If

            For i As Integer = 0 To frames - 1
                _currentKnob += knobStep
                Dim k As Single = _currentKnob

                Dim isLowPass As Boolean = (k < 0.5F)
                Dim modeSwitched As Boolean = False
                If isLowPass <> _wasLowPassMode Then
                    For ch As Integer = 0 To _channels - 1
                        _svf1(ch).Reset()
                        _svf2(ch).Reset()
                    Next
                    _wasLowPassMode = isLowPass
                    modeSwitched = True
                End If

                If modeSwitched OrElse (i And 7) = 0 OrElse i = frames - 1 Then
                    If isLowPass Then
                        Dim t As Single = Math.Max(0.0F, Math.Min(1.0F, k / 0.48F))
                        Dim freq As Single = CSng(30.0 * Math.Pow(18000.0 / 30.0, t))
                        For ch As Integer = 0 To _channels - 1
                            _svf1(ch).SetCoeffs(_sampleRate, freq, 1.45F)
                            _svf2(ch).SetCoeffs(_sampleRate, freq, 0.85F)
                        Next
                    Else
                        Dim t As Single = Math.Max(0.0F, Math.Min(1.0F, (k - 0.52F) / 0.48F))
                        Dim freq As Single = CSng(25.0 * Math.Pow(15000.0 / 25.0, t))
                        For ch As Integer = 0 To _channels - 1
                            _svf1(ch).SetCoeffs(_sampleRate, freq, 1.4F)
                            _svf2(ch).SetCoeffs(_sampleRate, freq, 0.85F)
                        Next
                    End If
                End If

                Dim distFromCenter As Single = Math.Abs(k - 0.5F)
                Dim wet As Single = 1.0F
                If distFromCenter <= 0.01F Then
                    wet = 0.0F
                ElseIf distFromCenter < 0.03F Then
                    wet = (distFromCenter - 0.01F) / 0.02F
                End If

                Dim edgeAtten As Single = 1.0F
                If k < 0.025F Then
                    edgeAtten = Math.Max(0.0F, k / 0.025F)
                ElseIf k > 0.975F Then
                    edgeAtten = Math.Max(0.0F, (1.0F - k) / 0.025F)
                End If

                For ch As Integer = 0 To _channels - 1
                    Dim sampleIdx As Integer = offset + (i * _channels) + ch
                    Dim dry As Single = buffer(sampleIdx)
                    If Single.IsNaN(dry) OrElse Single.IsInfinity(dry) Then dry = 0.0F

                    Dim filtered As Single
                    If isLowPass Then
                        filtered = _svf2(ch).ProcessLowPass(_svf1(ch).ProcessLowPass(dry)) * edgeAtten
                    Else
                        filtered = _svf2(ch).ProcessHighPass(_svf1(ch).ProcessHighPass(dry)) * edgeAtten
                    End If

                    Dim sample As Single = (dry * (1.0F - wet)) + (filtered * wet)
                    If Single.IsNaN(sample) OrElse Single.IsInfinity(sample) Then sample = 0.0F
                    buffer(sampleIdx) = sample
                Next
            Next

            _currentKnob = target
            Return count
        End Function
    End Class

    Public Class StereoBalanceEffect
        Implements IAudioEffect

        Public ReadOnly Property Name As String Implements IAudioEffect.Name
            Get
                Return "Stereo Balance"
            End Get
        End Property

        Public Property IsEnabled As Boolean = True Implements IAudioEffect.IsEnabled

        Private _channels As Integer = 2
        Private _balance As Single = 0.5F
        Private _gainL As Single = 1.0F
        Private _gainR As Single = 1.0F

        Private _currentGainL As Single = 1.0F
        Private _currentGainR As Single = 1.0F

        Public Property Balance As Single
            Get
                Return Volatile.Read(_balance)
            End Get
            Set(value As Single)
                Dim b As Single = Math.Max(0.0F, Math.Min(1.0F, value))
                Volatile.Write(_balance, b)
                If b < 0.5F Then
                    Volatile.Write(_gainL, 1.0F)
                    Volatile.Write(_gainR, b * 2.0F)
                Else
                    Volatile.Write(_gainL, (1.0F - b) * 2.0F)
                    Volatile.Write(_gainR, 1.0F)
                End If
            End Set
        End Property

        Public Sub Init(sampleRate As Integer, channels As Integer) Implements IAudioEffect.Init
            _channels = Math.Max(1, channels)
        End Sub

        Public Function Process(buffer() As Single, offset As Integer, count As Integer) As Integer Implements IAudioEffect.Process
            If _channels < 2 OrElse count <= 0 Then Return count

            Dim curBal As Single = Volatile.Read(_balance)
            Dim targetGL As Single = Volatile.Read(_gainL)
            Dim targetGR As Single = Volatile.Read(_gainR)

            ' Пропускаем обработку только тогда, когда и целевой, и текущий баланс уже плавно дошли до центра (1.0F)
            If Math.Abs(curBal - 0.5F) < 0.001F AndAlso
               Math.Abs(_currentGainL - 1.0F) < 0.001F AndAlso
               Math.Abs(_currentGainR - 1.0F) < 0.001F Then
                _currentGainL = 1.0F
                _currentGainR = 1.0F
                Return count
            End If

            Dim frames As Integer = count \ _channels
            If frames <= 0 Then Return count

            Dim stepL As Single = (targetGL - _currentGainL) / frames
            Dim stepR As Single = (targetGR - _currentGainR) / frames

            For i As Integer = 0 To frames - 1
                _currentGainL += stepL
                _currentGainR += stepR

                Dim baseIdx As Integer = offset + (i * _channels)
                Dim valL As Single = buffer(baseIdx) * _currentGainL
                If Single.IsNaN(valL) OrElse Single.IsInfinity(valL) Then valL = 0.0F
                buffer(baseIdx) = valL

                If baseIdx + 1 < offset + count Then
                    Dim valR As Single = buffer(baseIdx + 1) * _currentGainR
                    If Single.IsNaN(valR) OrElse Single.IsInfinity(valR) Then valR = 0.0F
                    buffer(baseIdx + 1) = valR
                End If
            Next

            _currentGainL = targetGL
            _currentGainR = targetGR
            Return count
        End Function
    End Class

End Namespace
' --- END OF FILE AudioEffectsGraph.vb ---