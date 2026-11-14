<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class MixerSettingsForm
    Inherits System.Windows.Forms.Form

    'Форма переопределяет dispose для очистки списка компонентов.
    <System.Diagnostics.DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Является обязательной для конструктора форм Windows Forms
    Private components As System.ComponentModel.IContainer

    'Примечание: следующая процедура является обязательной для конструктора форм Windows Forms
    'Для ее изменения используйте конструктор форм Windows Form.  
    'Не изменяйте ее в редакторе исходного кода.
    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        SuspendLayout()
        ' 
        ' MixerSettingsForm
        ' 
        AutoScaleMode = System.Windows.Forms.AutoScaleMode.None
        ClientSize = New System.Drawing.Size(984, 780)
        MinimumSize = New System.Drawing.Size(900, 620)
        Name = "MixerSettingsForm"
        Text = "Настройки Pioneer DJ Mixer (config.json)"
        ResumeLayout(False)
    End Sub
End Class