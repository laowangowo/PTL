Public Class PageLaunchSettings

    Private Sub PageRefresh() Handles Me.Loaded, Me.PageEnter
        If Not IsLoaded Then Return
        PanBack.ScrollToHome()
        LoadPlayer()
        LoadSettings()
    End Sub

    Public Sub RefreshPlayerPanel()
        If Not IsLoaded Then Return
        LoadPlayer()
    End Sub

    Private Sub LoadPlayer()
        Dim UserId As String = WarThunderGetActiveUserId()
        If UserId = "" Then
            LabSettingsNickname.Text = "尚未检测到玩家档案"
            LabSettingsUid.Text = "玩家 ID 尚未设置"
            LabPlayerStatus.Text = "进入一次游戏后，Saves 目录会自动创建以玩家 ID 命名的档案。"
            ImgSettingsAvatar.Source = Nothing
            TextPlayerUid.Text = ""
            Return
        End If

        If WarThunderCurrentPlayer Is Nothing OrElse WarThunderCurrentPlayer.UserId <> UserId Then
            WarThunderCurrentPlayer = WarThunderCreatePlayerInfo(UserId)
        End If
        LabSettingsNickname.Text = WarThunderCurrentPlayer.DisplayName
        LabSettingsUid.Text = "UID " & UserId
        LabPlayerStatus.Text = If(WarThunderPlayerLoading,
            "正在从 WT Live 更新昵称和头像…",
            "点击右边刷新资料按钮即可刷新")
        ImgSettingsAvatar.Source = If(
            WarThunderCurrentPlayer.AvatarPath <> "" AndAlso File.Exists(WarThunderCurrentPlayer.AvatarPath),
            WarThunderCurrentPlayer.AvatarPath, Nothing)
        TextPlayerUid.Text = UserId
        If Not String.IsNullOrWhiteSpace(WarThunderCurrentPlayer.Nickname) Then
            TextPlayerNickname.Text = WarThunderCurrentPlayer.Nickname
        End If
    End Sub

    Private Sub LoadSettings()
        If WarThunderCurrentInstall Is Nothing OrElse Not WarThunderCurrentInstall.IsValid Then
            LabSettingsPath.Text = "尚未找到战争雷霆安装目录"
            BtnSaveSettings.IsEnabled = False
            Return
        End If

        LabSettingsPath.Text = WarThunderCurrentInstall.InstallPath & "\config.blk"
        Try
            Dim Current = WarThunderLoadLaunchSettings(WarThunderCurrentInstall.InstallPath)
            SelectComboByTag(ComboGraphicsQuality, Current.GraphicsQuality)
            SelectComboByTag(ComboResolution, Current.Resolution)
            SelectComboByTag(ComboWindowMode, Current.WindowMode)
            SelectComboByTag(ComboDriver, Current.Driver)
            SelectComboByTag(ComboSpeaker, Current.SpeakerMode)
            SelectComboByTag(ComboServer,
                If(Current.ServerCircuit.Equals(
                    "production", StringComparison.OrdinalIgnoreCase), "production", "dev"))
            CheckVSync.Checked = Current.VSync
            CheckSound.Checked = Current.SoundEnabled
            CheckVR.Checked = Current.VREnabled
            BtnSaveSettings.IsEnabled = True
        Catch ex As Exception
            BtnSaveSettings.IsEnabled = False
            LabSettingsPath.Text = "读取 config.blk 失败：" & ex.GetDisplay(False)
            Logger.Error(ex, "读取战争雷霆启动设置失败", LogBehavior.Toast)
        End Try
    End Sub

    Private Sub BtnSavePlayer_Click(sender As Object, e As RoutedEventArgs) Handles BtnSavePlayer.Click
        Try
            Dim UserId As String = TextPlayerUid.Text.Trim()
            Dim Nickname As String = TextPlayerNickname.Text.Trim()
            If UserId = "" Then Throw New ArgumentException("请填写战争雷霆玩家 ID。")
            If WarThunderGetUserFolder(UserId) = "" Then
                Throw New DirectoryNotFoundException("Saves 目录中没有找到该玩家 ID，请先进入一次该账号的游戏。")
            End If
            If UserId <> WarThunderGetActiveUserId() Then
                Dim ErrorMessage As String = ""
                If Not WarThunderSelectUser(UserId, ErrorMessage) Then Throw New InvalidOperationException(ErrorMessage)
            End If
            WarThunderSetPlayerIdentity(UserId, Nickname)
            WarThunderRefreshCurrentPlayerAsync(True)
            Hint("玩家资料设置已保存。", HintType.Green)
            LoadPlayer()
        Catch ex As Exception
            MyMsgBox("保存玩家资料失败：" & vbCrLf & ex.GetDisplay(False), "保存失败", IsWarn:=True)
        End Try
    End Sub

    Private Sub BtnRefreshPlayer_Click(sender As Object, e As RoutedEventArgs) Handles BtnRefreshPlayer.Click
        WarThunderRefreshCurrentPlayerAsync(True)
        Hint("正在刷新玩家昵称和头像。", HintType.Green)
    End Sub

    Private Sub BtnSwitchPlayer_Click(sender As Object, e As RoutedEventArgs) Handles BtnSwitchPlayer.Click
        FrmMain.PageChange(FormMain.PageType.WarThunderUsers)
    End Sub

    Private Sub BtnSaveSettings_Click(sender As Object, e As RoutedEventArgs) Handles BtnSaveSettings.Click
        If WarThunderCurrentInstall Is Nothing OrElse Not WarThunderCurrentInstall.IsValid Then Return
        Try
            Dim PreviousSettings = WarThunderLoadLaunchSettings(
                WarThunderCurrentInstall.InstallPath)
            Dim SelectedServer As String = GetComboTag(ComboServer, "production")
            Dim Current As New WarThunderLaunchSettings With {
                .GraphicsQuality = GetComboTag(ComboGraphicsQuality, "low"),
                .Resolution = GetComboTag(ComboResolution, "auto"),
                .WindowMode = GetComboTag(ComboWindowMode, "fullscreenwindowed"),
                .Driver = GetComboTag(ComboDriver, "auto"),
                .VSync = CheckVSync.Checked,
                .SoundEnabled = CheckSound.Checked,
                .SpeakerMode = GetComboTag(ComboSpeaker, "stereo"),
                .VREnabled = CheckVR.Checked,
                .ServerCircuit = SelectedServer
            }
            WarThunderSaveLaunchSettings(WarThunderCurrentInstall.InstallPath, Current)
            WarThunderUpdateState = WarThunderUpdateCheckState.NotChecked
            WarThunderRefreshAllUI()
            If Not PreviousSettings.ServerCircuit.Equals(
                SelectedServer, StringComparison.OrdinalIgnoreCase) Then
                Dim ServerName As String = If(
                    SelectedServer.Equals("production", StringComparison.OrdinalIgnoreCase),
                    "正式服", "测试服")
                MyMsgBox(
                    $"游戏服务器已切换为{ServerName}。" & vbCrLf &
                    "请重启 PTL，使服务器通道和版本检查设置完全生效。",
                    "需要重启 PTL",
                    "确定",
                    ForceWait:=True)
            Else
                Hint("启动设置已保存，将在下次启动游戏时生效。", HintType.Green)
            End If
        Catch ex As Exception
            Logger.Error(ex, "保存战争雷霆启动设置失败")
            MyMsgBox("保存启动设置失败：" & vbCrLf & ex.GetDisplay(False), "保存失败", IsWarn:=True)
        End Try
    End Sub

    Private Sub BtnChooseFolder_Click(sender As Object, e As RoutedEventArgs) Handles BtnChooseFolder.Click
        FrmLaunchLeft.SelectWarThunderInstallFolder()
        LoadSettings()
    End Sub

    Private Shared Sub SelectComboByTag(Combo As ComboBox, TagValue As String)
        For Each Item As ComboBoxItem In Combo.Items
            If String.Equals(Item.Tag?.ToString(), TagValue, StringComparison.OrdinalIgnoreCase) Then
                Combo.SelectedItem = Item
                Return
            End If
        Next
        If Combo.Items.Count > 0 Then Combo.SelectedIndex = 0
    End Sub

    Private Shared Function GetComboTag(Combo As ComboBox, DefaultValue As String) As String
        If TypeOf Combo.SelectedItem Is ComboBoxItem Then
            Dim Item = CType(Combo.SelectedItem, ComboBoxItem)
            Return If(Item.Tag?.ToString(), DefaultValue)
        End If
        Return DefaultValue
    End Function

End Class
