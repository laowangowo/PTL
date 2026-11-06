Imports System.Security.Cryptography
Imports System.Text

Public Class PageSetupSystem

    Private Sub PageSetupSystem_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        Dim UpdateMode As Integer = Settings.Get(Of Integer)("SystemSystemUpdate")
        If UpdateMode < 0 OrElse UpdateMode > 2 Then
            Settings.Set("SystemSystemUpdate", 2)
        End If
        LabLauncherVersion.Text = PtlGetVersionStatusText()

        Static IsLoaded As Boolean = False
        If IsLoaded Then Return
        IsLoaded = True
        AniControlEnabled += 1
        Reload()
        AniControlEnabled -= 1
    End Sub

    Public Sub Reload()
        SettingService.RefreshSettings(Me)
        RefreshActivationUI()
    End Sub

    Private Sub RefreshActivationUI()
        Dim MachineCode As String = PtlGetMachineCode()
        LabActivationStatus.Text = If(PtlIsActivated(),
            $"PTL状态：已激活",
            $"PTL状态：未激活")
    End Sub

    Private Sub BtnCopyMachineId_Click(sender As Object, e As EventArgs) _
        Handles BtnCopyMachineId.Click
        Dim MachineCode As String = PtlGetMachineCode()
        If MachineCode = "" Then
            Hint("无法生成当前设备的识别码，请稍后重试。", HintType.Red)
            Return
        End If
        ClipboardSet(MachineCode, False)
        RefreshActivationUI()
        Hint($"识别码已复制。", HintType.Green)
    End Sub

    Private Sub BtnActivatePtl_Click(sender As Object, e As EventArgs) _
        Handles BtnActivatePtl.Click
        If PtlIsActivated() Then
            MyMsgBox("当前设备已经激活。", "PTL 激活")
            Return
        End If
        Dim ActivationCode As String = MyMsgBoxInput(
            "激活 PTL",
            "请复制取激活码，然后粘贴到这里",
            "",
            HintText:="输入激活码")
        If String.IsNullOrWhiteSpace(ActivationCode) Then Return
        Dim ErrorMessage As String = ""
        If PtlActivate(ActivationCode, ErrorMessage) Then
            RefreshActivationUI()
            ThemeCheckAll(True)
            FrmSetupUI?.RefreshThemeLock()
            MyMsgBox("PTL 激活成功。", "激活成功")
        Else
            MyMsgBox("激活失败：" & vbCrLf & ErrorMessage, "激活失败", IsWarn:=True)
        End If
    End Sub

    Private IsMemoryOptimizing As Boolean

    Private Sub BtnDailyLuck_Click(sender As Object, e As EventArgs) _
        Handles BtnDailyLuck.Click
        Dim Luck As Integer = PtlGetDailyLuck()
        Dim Comment As String
        Select Case Luck
            Case Is >= 95
                Comment = "欧皇附体，今天适合挑战高收益目标！"
            Case Is >= 80
                Comment = "运气很好，可能会遇到意想不到的惊喜。"
            Case Is >= 60
                Comment = "整体平稳，按计划行动会有不错的结果。"
            Case Is >= 40
                Comment = "普普通通的一天，保持平常心就好。"
            Case Is >= 20
                Comment = "建议稳扎稳打，避免不必要的冒险。"
            Case Else
                Comment = "今天适合低调行事，多存一点银狮总没错。"
        End Select
        MyMsgBox(
            $"今日人品：{Luck} / 100" & vbCrLf &
            Comment & vbCrLf & vbCrLf &
            "人品值每天更新，同一天内保持不变。",
            "今日人品")
    End Sub

    Private Shared Function PtlGetDailyLuck() As Integer
        Dim Seed As String = Date.Now.ToString("yyyyMMdd") & "|" & PtlGetMachineCode()
        Using Hasher = SHA256.Create()
            Dim Hash = Hasher.ComputeHash(Encoding.UTF8.GetBytes(Seed))
            Dim Value As UInteger =
                CUInt(Hash(0)) << 24 Or
                CUInt(Hash(1)) << 16 Or
                CUInt(Hash(2)) << 8 Or
                CUInt(Hash(3))
            Return CInt(Value And &H7FFFFFFFUI) Mod 101
        End Using
    End Function

    Private Sub BtnMemoryOptimize_Click(sender As Object, e As EventArgs) _
        Handles BtnMemoryOptimize.Click
        If IsMemoryOptimizing Then Return
        IsMemoryOptimizing = True
        BtnMemoryOptimize.IsEnabled = False
        Hint("正在优化内存，请稍候…", HintType.Blue)
        RunInNewThread(
        Sub()
            Dim Result As PtlMemoryResult = Nothing
            Dim ErrorMessage As String = ""
            Try
                Result = PtlOptimizeMemory()
            Catch ex As Exception
                ErrorMessage = ex.GetDisplay(False)
            End Try
            RunInUi(
            Sub()
                IsMemoryOptimizing = False
                BtnMemoryOptimize.IsEnabled = True
                If Result Is Nothing Then
                    Logger.Warn($"PTL 内存优化失败：{ErrorMessage}")
                    MyMsgBox("内存优化失败，请稍后重试。", "优化失败", IsWarn:=True)
                    Return
                End If
                Dim IncreaseMB As Long = Result.AvailableIncrease \ 1024 \ 1024
                Dim AvailableMB As Long = Result.AvailableAfter \ 1024 \ 1024
                Hint(
                    $"内存优化完成：" &
                    If(IncreaseMB > 0, $"增加了 {IncreaseMB} MB 可用内存", $"当前可用 {AvailableMB} MB。"),
                    HintType.Green)
            End Sub)
        End Sub, "PTL Memory Optimize", ThreadPriority.AboveNormal)
    End Sub

    Public Sub Reset()
        Try
            SettingService.ResetSettings(Me)
            Hint("启动器设置已恢复默认值。", HintType.Green, False)
        Catch ex As Exception
            Logger.Error(ex, "初始化启动器设置失败", LogBehavior.Alert)
        End Try
        Reload()
    End Sub

    Private Sub ComboSystemActivity_SelectionChanged(sender As Object,
                                                     e As SelectionChangedEventArgs) _
        Handles ComboSystemActivity.SelectionChanged
        If AniControlEnabled <> 0 OrElse ComboSystemActivity.SelectedIndex <> 2 Then Return
        If MyMsgBox("关闭后，你将无法收到 PTL 的重要更新和兼容性通知。" & vbCrLf &
                    "建议至少保留[仅显示重要通知]。", "关闭通知",
                    "仍然关闭", "取消", IsWarn:=True) = 2 Then
            If e.RemovedItems.Count > 0 Then ComboSystemActivity.SelectedItem = e.RemovedItems(0)
        End If
    End Sub

    Private Sub ComboSystemUpdate_SelectionChanged(sender As Object,
                                                   e As SelectionChangedEventArgs) _
        Handles ComboSystemUpdate.SelectionChanged
        If AniControlEnabled <> 0 OrElse ComboSystemUpdate.SelectedIndex <> 2 Then Return
        If MyMsgBox("关闭更新提示后，PTL 的兼容性修复可能无法及时送达。" & vbCrLf &
                    "你仍可以在本页手动检查更新。", "关闭更新提示",
                    "仍然关闭", "取消", IsWarn:=True) = 2 Then
            If e.RemovedItems.Count > 0 Then ComboSystemUpdate.SelectedItem = e.RemovedItems(0)
        End If
    End Sub

    Private Sub BtnSystemUpdate_Click(sender As Object, e As EventArgs) Handles BtnSystemUpdate.Click
        UpdateCheckByButton()
    End Sub

    Public Shared Function IsLauncherNewest() As Boolean?
        Try
            Dim ServerContent As String = If(FileUtils.TryReadAsString(PathTemp & "Cache\Notice.cfg"), "")
            If ServerContent.Split("|").Count < 3 Then Return Nothing
            Return ServerContent.Split("|")(If(BuildType = BuildTypes.Release, 2, 1)) <= VersionCode
        Catch ex As Exception
            Logger.Error(ex, "确认启动器更新失败")
            Return Nothing
        End Try
    End Function

End Class
