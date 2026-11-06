Public Class PageSetupUI

    Private Sub PageSetupUI_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        ThemeCheckAll(True)
        AniControlEnabled += 1
        Refresh()
        AniControlEnabled -= 1

        Static Reloaded As Boolean = False
        If Reloaded Then Return
        Reloaded = True
        SliderLoad()
    End Sub

    Public Sub Refresh()
        Try
            SettingService.RefreshSettings(Me)
            BackgroundRefresh(False, False)
            RefreshWarThunderCard()
            MusicRefreshUI()
        Catch ex As Exception
            Logger.Error(ex, "刷新个性化设置失败")
        End Try
    End Sub

    Public Sub Reset()
        Try
            SettingService.ResetSettings(Me)
            Hint("个性化设置已恢复默认值。", HintType.Green, False)
        Catch ex As Exception
            Logger.Error(ex, "初始化个性化设置失败", LogBehavior.Alert)
        End Try
        Refresh()
    End Sub

    Public Sub RefreshWarThunderCard()
        Dim UserId As String = WarThunderGetActiveUserId()
        If UserId = "" Then
            LabSetupWtNickname.Text = "尚未检测到战争雷霆玩家"
            LabSetupWtUid.Text = "设置玩家 ID、昵称、画质与声音"
            ImgSetupWtAvatar.Source = Nothing
            Return
        End If
        If WarThunderCurrentPlayer Is Nothing OrElse WarThunderCurrentPlayer.UserId <> UserId Then
            WarThunderCurrentPlayer = WarThunderCreatePlayerInfo(UserId)
        End If
        LabSetupWtNickname.Text = WarThunderCurrentPlayer.DisplayName
        LabSetupWtUid.Text = $"UID {UserId} 点击右侧修改玩家与启动设置"
        ImgSetupWtAvatar.Source = If(
            WarThunderCurrentPlayer.AvatarPath <> "" AndAlso File.Exists(WarThunderCurrentPlayer.AvatarPath),
            WarThunderCurrentPlayer.AvatarPath, Nothing)
    End Sub

    Private Sub BtnSetupWtOpen_Click(sender As Object, e As EventArgs) Handles BtnSetupWtOpen.Click
        FrmMain.PageChange(FormMain.PageType.LaunchSettings)
    End Sub

    Private Sub BtnSupportPtl_Click(sender As Object, e As EventArgs) Handles BtnSupportPtl.Click
        Dim DevelopmentUrl As String = RegistryUtils.TryRead(
            "HKEY_CURRENT_USER\Software\PTL", "DevelopmentUrl", "").ToString().Trim()
        If DevelopmentUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) Then
            OpenWebsite(DevelopmentUrl)
        Else
            MyMsgBox("PTL 是社区维护的独立项目。" & vbCrLf &
                     "主题颜色需要赞助支持后解锁。" & vbCrLf &
                     "激活方式需要自己寻找(Free)",
                     "支持 PTL 开发")
        End If
    End Sub

    Private Sub BtnActivateForTheme_Click(sender As Object, e As EventArgs) _
        Handles BtnActivateForTheme.Click
        If FrmSetupLeft IsNot Nothing Then
            FrmSetupLeft.PageChange(FormMain.PageSubType.SetupSystem)
        End If
        Hint("请在启动器设置的其他页面中复制 ID，获取并输入激活码。", HintType.Blue)
    End Sub

    Public Sub RefreshThemeLock()
        Dim Unlocked As Boolean = PtlThemeSponsorUnlocked
        PanThemeLock.Visibility = If(Unlocked, Visibility.Collapsed, Visibility.Visible)
        For Each Control As MyRadioBox In {
            RadioLauncherTheme0, RadioLauncherTheme1, RadioLauncherTheme2,
            RadioLauncherTheme3, RadioLauncherTheme4
        }
            Control.IsEnabled = Unlocked
        Next
        CardLauncher.TriggerForceResize()
    End Sub

    Private Sub BtnMusicOpen_Click(sender As Object, e As EventArgs) Handles BtnMusicOpen.Click
        Directory.CreateDirectory(Paths.Base & "PTL\Musics\")
        OpenExplorer(Paths.Base & "PTL\Musics\")
    End Sub

    Private Sub BtnMusicRefresh_Click(sender As Object, e As EventArgs) Handles BtnMusicRefresh.Click
        MusicRefreshPlay(True)
    End Sub

    Private Sub BtnMusicImportWarThunder_Click(sender As Object, e As EventArgs) _
        Handles BtnMusicImportWarThunder.Click
        Hint("正在从本机战争雷霆中提取背景音乐，这可能需要十几秒。")
        WarThunderPrepareBackgroundMusicAsync(True)
    End Sub

    Public Sub MusicRefreshUI()
        If PanMusicVolume Is Nothing Then Return
        If MusicAllList IsNot Nothing AndAlso MusicAllList.Any() Then
            PanMusicVolume.Visibility = Visibility.Visible
            PanMusicDetail.Visibility = Visibility.Visible
            BtnMusicClear.Visibility = Visibility.Visible
            CardMusic.Title = "背景音乐（" & MusicAllList.Count & " 首）"
        Else
            PanMusicVolume.Visibility = Visibility.Collapsed
            PanMusicDetail.Visibility = Visibility.Collapsed
            BtnMusicClear.Visibility = Visibility.Collapsed
            CardMusic.Title = "背景音乐"
        End If
        CardMusic.TriggerForceResize()
    End Sub

    Private Sub BtnMusicClear_Click(sender As Object, e As EventArgs) Handles BtnMusicClear.Click
        If MyMsgBox("即将删除背景音乐目录中的所有文件。" & vbCrLf &
                    "此操作不可撤销，是否确定？", "警告",, "取消", IsWarn:=True) = 1 Then
            RunInNewThread(
            Sub()
                Hint("正在删除背景音乐…")
                MusicNAudio = Nothing
                MusicWaitingList = New List(Of String)
                MusicAllList = New List(Of String)
                Thread.Sleep(200)
                Try
                    DirectoryUtils.Delete(Paths.Base & "PTL\Musics")
                    DirectoryUtils.Create(Paths.Base & "PTL\Musics\")
                    RunInUi(Sub() MusicRefreshPlay(False))
                    Hint("背景音乐已删除。", HintType.Green)
                Catch ex As Exception
                    Logger.Error(ex, "删除背景音乐失败", LogBehavior.Alert)
                End Try
            End Sub)
        End If
    End Sub

    Private Sub CheckMusicStart_Change() Handles CheckMusicStart.Change
        If AniControlEnabled <> 0 Then Return
        If CheckMusicStart.Checked Then CheckMusicStop.Checked = False
    End Sub

    Private Sub CheckMusicStop_Change() Handles CheckMusicStop.Change
        If AniControlEnabled <> 0 Then Return
        If CheckMusicStop.Checked Then CheckMusicStart.Checked = False
    End Sub

    Private Sub SliderLoad()
        SliderMusicVolume.GetHintText = Function(v) Math.Ceiling(v * 0.1) & "%"
        SliderLauncherTransparent.GetHintText = Function(v) Math.Round(40 + v * 0.1) & "%"
        SliderBackgroundOpacity.GetHintText = Function(v) Math.Round(v * 0.1) & "%"
        SliderBackgroundBlur.GetHintText = Function(v) v & " 像素"
    End Sub

    Public Shared Property HiddenForceShow As Boolean
        Get
            Return True
        End Get
        Set(value As Boolean)
            ' 功能隐藏设置已移除；始终保持全部功能可见。
        End Set
    End Property

    Public Shared Sub HiddenRefresh()
        ' 保留兼容入口，不再执行任何隐藏逻辑。
    End Sub

    Private Sub BtnBackgroundOpen_Click(sender As Object, e As EventArgs) _
        Handles BtnBackgroundOpen.Click
        DirectoryUtils.Create(Paths.Base & "PTL\Pictures\")
        OpenExplorer(Paths.Base & "PTL\Pictures\")
    End Sub

    Private Sub BtnBackgroundRefresh_Click(sender As Object, e As EventArgs) _
        Handles BtnBackgroundRefresh.Click
        BackgroundRefresh(True, True)
    End Sub

    Private Sub BtnBackgroundClear_Click(sender As Object, e As EventArgs) _
        Handles BtnBackgroundClear.Click
        If MyMsgBox("即将删除背景图片文件夹中的所有文件。" & vbCrLf &
                    "此操作不可撤销，是否确定？", "警告",, "取消",
                    IsWarn:=True) <> 1 Then Return
        DirectoryUtils.Delete(Paths.Base & "PTL\Pictures")
        BackgroundRefresh(False, True)
        Hint("背景图片已清空。", HintType.Green)
    End Sub

    Public Sub BackgroundRefreshUI(Show As Boolean, Count As Integer)
        If PanBackgroundOpacity Is Nothing Then Return
        PanBackgroundOpacity.Visibility = Show.ToVisibility
        PanBackgroundBlur.Visibility = Show.ToVisibility
        PanBackgroundSuit.Visibility = Show.ToVisibility
        BtnBackgroundClear.Visibility = Show.ToVisibility
        CardBackground.Title = If(Show, $"背景图片（{Count} 张）", "背景图片")
        CardBackground.TriggerForceResize()
    End Sub

    Public Shared Sub BackgroundRefresh(IsHint As Boolean, Refresh As Boolean)
        Try
            DirectoryUtils.Create(Paths.Base & "PTL\Pictures\")
            Dim Pictures As New List(Of String)
            For Each FilePath In DirectoryUtils.EnumerateFiles(
                Paths.Base & "PTL\Pictures\", True)
                Dim Extension As String = PathUtils.GetExtension(FilePath)
                If Extension <> "ini" AndAlso Extension <> "db" Then
                    Pictures.Add(FilePath)
                End If
            Next

            If Not Pictures.Any() Then
                If Refresh Then
                    If FrmMain.ImgBack.Visibility = Visibility.Collapsed Then
                        If IsHint Then Hint("未检测到可用背景图片。", HintType.Red)
                    Else
                        FrmMain.ImgBack.Visibility = Visibility.Collapsed
                        If IsHint Then Hint("背景图片已清除。", HintType.Green)
                    End If
                End If
                FrmSetupUI?.BackgroundRefreshUI(False, 0)
                Return
            End If

            If Refresh Then
                Dim Address As String = RandomOne(Pictures)
                Try
                    Logger.Info($"加载背景图片：{Address}")
                    FrmMain.ImgBack.Background = New MyBitmap(Address)
                    FrmMain.ImgBack.Visibility = Visibility.Visible
                    FrmMain.UpdateBackgroundAndTitleBar()
                    If IsHint Then
                        Hint("背景图片已刷新：" & PathUtils.GetLastPart(Address),
                             HintType.Green, False)
                    End If
                Catch ex As Exception
                    If ex.Message.Contains("参数无效") Then
                        Logger.Error(
                            $"刷新背景图片失败，该图片可能不是标准格式。" & vbCrLf &
                            $"文件：{Address}", LogBehavior.Alert)
                    Else
                        Logger.Error(ex, $"刷新背景图片失败（{Address}）",
                                     LogBehavior.Alert)
                    End If
                End Try
            End If
            FrmSetupUI?.BackgroundRefreshUI(True, Pictures.Count)
        Catch ex As Exception
            Logger.Error(ex, "刷新背景图片时出现未知错误")
        End Try
    End Sub

    Public Shared Sub OnMainPageTypeChanged()
        ' 主页自定义设置已从 PTL 中移除。
    End Sub

    ' 以下控件仅用于兼容标题栏设置回调，不显示在界面上。
    Public ReadOnly Property CheckLogoLeft As New MyCheckBox()
    Public ReadOnly Property PanLogoText As New Grid()
    Public ReadOnly Property PanLogoChange As New Grid()
    Public ReadOnly Property CardLogo As New MyCard()

End Class
