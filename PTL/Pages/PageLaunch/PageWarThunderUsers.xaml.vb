Public Class PageWarThunderUsers
    Implements IRefreshable

    Private Sub PageRefresh() Handles Me.Loaded, Me.PageEnter
        If Not IsLoaded Then Return
        PanBack.ScrollToHome()
        RefreshUsers()
    End Sub

    Public Sub RefreshUsers()
        If Not IsLoaded OrElse PanUsers Is Nothing Then Return
        PanUsers.Children.Clear()
        Dim Users As List(Of String) = WarThunderGetUserFolders()
        If Users.Count = 0 Then
            PanUsers.Children.Add(New TextBlock With {
                .Text = "没有找到玩家目录。请先在战争中登录至少一个账号。",
                .Margin = New Thickness(0, 4, 0, 12),
                .FontSize = 12.5,
                .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush)
            })
            Return
        End If

        Dim ActiveUid As String = WarThunderGetActiveUserId()
        For Each Folder In Users
            Dim Uid As String = Path.GetFileName(Folder)
            Dim Player As WarThunderPlayerInfo = WarThunderCreatePlayerInfo(Uid)
            If Uid = ActiveUid AndAlso WarThunderCurrentPlayer IsNot Nothing AndAlso
               WarThunderCurrentPlayer.UserId = Uid Then
                Player = WarThunderCurrentPlayer
            End If

            Dim Card As New Border With {
                .Margin = New Thickness(0, 0, 0, 10),
                .Padding = New Thickness(14, 11, 14, 11),
                .CornerRadius = New CornerRadius(6),
                .BorderThickness = New Thickness(1),
                .BorderBrush = TryCast(FindResource("ColorBrushGray1"), Media.Brush),
                .Background = TryCast(FindResource("ColorBrushSemiTransparent"), Media.Brush)
            }
            Dim Layout As New Grid()
            Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(58)})
            Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

            Dim Avatar As New MyImage With {
                .Width = 48,
                .Height = 48,
                .Stretch = Media.Stretch.UniformToFill,
                .VerticalAlignment = VerticalAlignment.Center,
                .HorizontalAlignment = HorizontalAlignment.Left
            }
            Avatar.Clip = New Media.RectangleGeometry(New Rect(0, 0, 48, 48), 24, 24)
            If Player.AvatarPath <> "" AndAlso File.Exists(Player.AvatarPath) Then Avatar.Source = Player.AvatarPath
            Grid.SetColumn(Avatar, 0)
            Layout.Children.Add(Avatar)

            Dim Details As New StackPanel With {.Margin = New Thickness(0, 0, 12, 0), .VerticalAlignment = VerticalAlignment.Center}
            Details.Children.Add(New TextBlock With {
                .Text = Player.DisplayName,
                .FontSize = 14.5,
                .FontWeight = FontWeights.SemiBold,
                .TextTrimming = TextTrimming.CharacterEllipsis,
                .Foreground = TryCast(FindResource("ColorBrush1"), Media.Brush)
            })
            Dim LastWrite As DateTime = New DirectoryInfo(Folder).LastWriteTime
            Details.Children.Add(New TextBlock With {
                .Text = $"UID {Uid} 最近使用: {LastWrite:yyyy-MM-dd HH:mm}",
                .Margin = New Thickness(0, 4, 0, 0),
                .FontSize = 11.5,
                .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush)
            })
            Details.Children.Add(New TextBlock With {
                .Text = WarThunderGetAuthSnapshotStatus(Uid),
                .Margin = New Thickness(0, 3, 0, 0),
                .FontSize = 11,
                .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush)
            })
            Grid.SetColumn(Details, 1)
            Layout.Children.Add(Details)

            Dim HasAuthSnapshot As Boolean = WarThunderHasAuthSnapshot(Uid)
            Dim SwitchButton As New MyButton With {
                .Text = If(Uid = ActiveUid, "当前玩家", "切换"),
                .Width = 100,
                .Height = 34,
                .IsEnabled = Uid <> ActiveUid AndAlso HasAuthSnapshot,
                .Tag = Uid,
                .VerticalAlignment = VerticalAlignment.Center
            }
            If Uid <> ActiveUid AndAlso Not HasAuthSnapshot Then
                SwitchButton.ToolTip = "请先登录此账号并退出游戏，以保存登录凭据快照。"
            End If
            AddHandler SwitchButton.Click, AddressOf SwitchButton_Click
            Grid.SetColumn(SwitchButton, 2)
            Layout.Children.Add(SwitchButton)

            Card.Child = Layout
            PanUsers.Children.Add(Card)
        Next
    End Sub

    Private Sub SwitchButton_Click(sender As Object, e As EventArgs)
        Dim UserId As String = CType(CType(sender, FrameworkElement).Tag, String)
        If UserId = WarThunderGetActiveUserId() Then Return
        If MyMsgBox($"是否把本地玩家档案切换为 UID {UserId}？" & vbCrLf &
                    "该操作不会删除任何账号数据。", "切换玩家", "切换", "取消") = 2 Then Return
        Dim ErrorMessage As String = ""
        If WarThunderSelectUser(UserId, ErrorMessage) Then
            Hint($"已切换为 UID {UserId}。", HintType.Green)
            WarThunderRefreshCurrentPlayerAsync(True)
            RefreshUsers()
        Else
            MyMsgBox("切换玩家失败：" & vbCrLf & ErrorMessage, "切换失败", IsWarn:=True)
        End If
    End Sub

    Private Sub BtnRefreshUsers_Click(sender As Object, e As EventArgs) Handles BtnRefreshUsers.Click
        WarThunderRefreshCurrentPlayerAsync(True)
        RefreshUsers()
    End Sub

    Private Sub BtnSaveCurrentAuth_Click(
        sender As Object, e As EventArgs) Handles BtnSaveCurrentAuth.Click
        Dim UserId As String = WarThunderGetActiveUserId()
        If UserId = "" Then
            Hint("尚未识别当前玩家 ID。", HintType.Red)
            Return
        End If
        If WarThunderGetGameProcessIds().Any() Then
            Hint("请先完全退出战争雷霆，再保存当前登录。", HintType.Red)
            Return
        End If
        If MyMsgBox(
            $"请确认游戏当前登录的是 UID {UserId}。" & vbCrLf &
            "PTL 只会保存 Windows 已加密的登录数据，不会读取明文密码。",
            "保存当前登录", "保存", "取消") = 2 Then
            Return
        End If
        Dim ErrorMessage As String = ""
        If WarThunderCaptureCurrentAuthSnapshot(UserId, ErrorMessage) Then
            Hint($"已保存 UID {UserId} 的登录凭据。", HintType.Green)
            RefreshUsers()
        Else
            MyMsgBox("保存登录凭据失败：" & vbCrLf & ErrorMessage,
                     "保存失败", IsWarn:=True)
        End If
    End Sub

    Private Sub BtnOpenSaves_Click(sender As Object, e As EventArgs) Handles BtnOpenSaves.Click
        Dim Folder As String = WarThunderGetSavesFolder()
        Directory.CreateDirectory(Folder)
        OpenExplorer(Folder & "\")
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        RefreshUsers()
    End Sub

End Class
