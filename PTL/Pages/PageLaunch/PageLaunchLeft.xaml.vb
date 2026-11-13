Public Class PageLaunchLeft

    Private IsLoad As Boolean = False

    Public Sub PageLaunchLeft_Loaded() Handles Me.Loaded
        If IsLoad Then
            RefreshWarThunderButtons()
            WarThunderRefreshCurrentPlayerAsync()
            Return
        End If

        IsLoad = True
        AprilPosTrans.X = 0
        AprilPosTrans.Y = 0
        RefreshWarThunderButtons()
        WarThunderScanAsync()
        WarThunderRefreshCurrentPlayerAsync()
    End Sub

    Private Sub BtnVersion_Click() Handles BtnVersion.Click
        If WarThunderCurrentInstall Is Nothing OrElse Not WarThunderCurrentInstall.IsValid Then
            SelectWarThunderInstallFolder()
        Else
            FrmMain.PageChange(FormMain.PageType.LaunchSettings)
        End If
    End Sub

    Public Sub LaunchButtonClick() Handles BtnLaunch.Click
        HandleWarThunderLaunchClick()
    End Sub

    Public Sub RefreshButtonsUI() Handles BtnLaunch.Loaded
        RefreshWarThunderButtons()
    End Sub

    Private Sub BtnMore_Click() Handles BtnMore.Click
        FrmMain.PageChange(FormMain.PageType.WarThunderComponents)
    End Sub

    ''' <summary>
    ''' 打开文件夹选择器，并保存有效的战争雷霆安装目录。
    ''' </summary>
    Public Sub SelectWarThunderInstallFolder()
        If WarThunderScanning Then
            Hint("正在检测战争雷霆安装目录，请稍候。")
            Return
        End If

        Dim InitialFolder As String = ""
        If WarThunderCurrentInstall IsNot Nothing Then InitialFolder = WarThunderCurrentInstall.InstallPath
        Try
            Using Dialog As New Windows.Forms.FolderBrowserDialog()
                Dialog.Description = "请选择包含战争雷霆游戏的安装文件夹"
                Dialog.ShowNewFolderButton = False
                If Directory.Exists(InitialFolder) Then Dialog.SelectedPath = InitialFolder
                If Dialog.ShowDialog() <> Windows.Forms.DialogResult.OK Then Return
                If WarThunderSetInstallFolder(Dialog.SelectedPath) Then
                    Hint("战争雷霆安装目录已保存。", HintType.Green)
                    Return
                End If
            End Using
        Catch ex As Exception
            Logger.Error(ex, "打开战争雷霆目录选择器失败")
        End Try

        MyMsgBox("所选目录中未检测到战争雷霆游戏文件。" & vbCrLf &
                 "请选择战争雷霆的安装根目录，或它上层的 Games、Gaijin Entertainment 文件夹。",
                 "目录无效", IsWarn:=True)
    End Sub

    ''' <summary>
    ''' 刷新主页启动按钮。
    ''' </summary>
    Private Sub RefreshWarThunderButtons()
        If BtnLaunch Is Nothing Then Return
        RefreshWarThunderPlayer()
        BtnMore.Visibility = Visibility.Collapsed
        BtnVersion.Text = "启动设置"
        BtnVersion.IsEnabled = Not WarThunderScanning

        If WarThunderGameRunning Then
            BtnLaunch.Text = "游戏运行中"
            BtnLaunch.IsEnabled = False
            BtnVersion.IsEnabled = False
            LabVersion.Text = "战争雷霆正在运行"
            BtnMore.Text = "管理组件"
            BtnMore.Visibility = Visibility.Visible
            Return
        End If

        If WarThunderUpdateState = WarThunderUpdateCheckState.Checking Then
            BtnLaunch.Text = "正在检查更新"
            BtnLaunch.IsEnabled = False
            LabVersion.Text = "正在读取本地版本并连接 Gaijin 版本接口"
            Return
        End If
        If WarThunderUpdateState = WarThunderUpdateCheckState.Updating Then
            BtnLaunch.Text = "正在更新游戏"
            BtnLaunch.IsEnabled = False
            BtnVersion.IsEnabled = False
            LabVersion.Text = If(
                String.IsNullOrWhiteSpace(WarThunderUpdateStatus),
                "游戏正在后台安装更新",
                WarThunderUpdateStatus)
            Return
        End If
        If WarThunderScanning Then
            BtnLaunch.Text = "正在查找战争雷霆"
            BtnLaunch.IsEnabled = False
            LabVersion.Text = "正在检查注册表、常见目录与 Steam 游戏库"
            Return
        End If

        Dim Install = WarThunderCurrentInstall
        If Install IsNot Nothing AndAlso Install.IsValid Then
            If WarThunderUpdateState = WarThunderUpdateCheckState.UpdateRequired Then
                BtnLaunch.Text = "更新战争雷霆"
                LabVersion.Text = $"新版本 {WarThunderRemoteVersion}"
            ElseIf WarThunderUpdateState = WarThunderUpdateCheckState.UpToDate Then
                BtnLaunch.Text = "启动战争雷霆"
                LabVersion.Text = $"已是最新版本 {WarThunderLocalVersion} {Install.DisplayName}"
            Else
                BtnLaunch.Text = "启动战争雷霆"
                LabVersion.Text = Install.DisplayName & " " & Install.InstallPath
            End If
            BtnLaunch.IsEnabled = True
            BtnMore.Text = "管理组件"
            BtnMore.Visibility = Visibility.Visible
        Else
            BtnLaunch.Text = "选择战争雷霆目录"
            BtnLaunch.IsEnabled = True
            LabVersion.Text = "未检测到安装，点击后选择游戏目录"
        End If
    End Sub

    ''' <summary>
    ''' 刷新左侧玩家头像、昵称与 UID。
    ''' </summary>
    Public Sub RefreshWarThunderPlayer()
        If LabWtPlayerNickname Is Nothing Then Return
        Dim UserId As String = WarThunderGetActiveUserId()
        If UserId = "" Then
            LabWtPlayerNickname.Text = "未检测到玩家档案"
            LabWtPlayerUid.Text = "点击选择用户"
            ImgWtPlayerAvatar.Source = Nothing
            Return
        End If
        If WarThunderCurrentPlayer Is Nothing OrElse WarThunderCurrentPlayer.UserId <> UserId Then
            WarThunderCurrentPlayer = WarThunderCreatePlayerInfo(UserId)
        End If
        LabWtPlayerNickname.Text = WarThunderCurrentPlayer.DisplayName
        LabWtPlayerUid.Text = "UID " & UserId & If(WarThunderPlayerLoading, " 正在更新", " 点击切换用户")
        ImgWtPlayerAvatar.Source = If(
            WarThunderCurrentPlayer.AvatarPath <> "" AndAlso File.Exists(WarThunderCurrentPlayer.AvatarPath),
            WarThunderCurrentPlayer.AvatarPath, Nothing)
    End Sub

    Private Sub PanWtPlayer_MouseLeftButtonUp(sender As Object, e As MouseButtonEventArgs)
        e.Handled = True
        FrmMain.PageChange(FormMain.PageType.WarThunderUsers)
    End Sub

    Private Sub HandleWarThunderLaunchClick()
        If WarThunderScanning OrElse WarThunderGameRunning Then Return
        If WarThunderCurrentInstall Is Nothing OrElse Not WarThunderCurrentInstall.IsValid Then
            SelectWarThunderInstallFolder()
            Return
        End If

        Dim PreviousText As String = BtnLaunch.Text
        BtnLaunch.Text = "正在检查更新…"
        BtnLaunch.IsEnabled = False
        BtnVersion.IsEnabled = False
        WarThunderLog($"准备启动 {WarThunderCurrentInstall.DisplayName}：{WarThunderCurrentInstall.InstallPath}")
        RunInNewThread(
        Sub()
            Dim ErrorMessage As String = ""
            Dim Success As Boolean = WarThunderStart(ErrorMessage)
            Thread.Sleep(220)
            RunInUi(
            Sub()
                BtnLaunch.Text = PreviousText
                BtnVersion.IsEnabled = True
                RefreshWarThunderButtons()
                If Success Then
                    Hint(If(ErrorMessage = "", "战争雷霆启动请求已发送。", ErrorMessage), HintType.Green)
                Else
                    Hint("战争雷霆启动失败。", HintType.Red)
                    MyMsgBox("无法启动战争雷霆。" & vbCrLf & ErrorMessage, "启动失败", IsWarn:=True)
                End If
            End Sub)
        End Sub, "War Thunder Launch", ThreadPriority.AboveNormal)
    End Sub

End Class
