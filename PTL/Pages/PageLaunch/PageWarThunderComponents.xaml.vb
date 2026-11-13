Public Class PageWarThunderComponents
    Implements IRefreshable

    Private Mode As WarThunderComponentKind = WarThunderComponentKind.Sights
    Private IsRefreshing As Boolean
    Private IsRemoving As Boolean
    Private PreviewRequestId As Integer
    Private PreviewRenderId As Integer
    Private PreviewComponentPath As String = ""
    Private IsPopulatingSightFiles As Boolean
    Private IsPreviewBackgroundInitialized As Boolean
    Private PreviewBackgroundColor As System.Drawing.Color

    Private Sub PageRefresh() Handles Me.Loaded, Me.PageEnter
        If Not IsLoaded Then Return
        InitializePreviewBackground()
        PanBack.ScrollToHome()
        UpdateModeUi()
        RefreshPage()
    End Sub

    Public Sub SetMode(Kind As WarThunderComponentKind)
        If Mode = Kind Then Return
        Mode = Kind
        ClearSightPreview()
        UpdateModeUi()
        If IsLoaded Then RefreshPage()
    End Sub

    Private Sub UpdateModeUi()
        Dim IsTextMode As Boolean = Mode = WarThunderComponentKind.Text
        BtnTextToggle.Visibility =
            If(IsTextMode, Visibility.Visible, Visibility.Collapsed)
        BtnTextGuide.Visibility =
            If(IsTextMode, Visibility.Visible, Visibility.Collapsed)
        BtnOpen.Text = If(IsTextMode, "打开文本目录", "打开目录")
        If IsTextMode AndAlso WarThunderCurrentInstall IsNot Nothing AndAlso
           WarThunderCurrentInstall.IsValid Then
            Try
                BtnTextToggle.Text = If(
                    WarThunderIsTestLocalizationEnabled(
                        WarThunderCurrentInstall.InstallPath),
                    "停用自定义文本",
                    "启用自定义文本")
            Catch ex As Exception
                Logger.Warn(ex, "读取自定义本地化开关失败")
                BtnTextToggle.Text = "启用自定义文本"
            End Try
        End If
    End Sub

    Public Sub RefreshPage()
        If IsRefreshing OrElse IsRemoving Then Return
        IsRefreshing = True
        BtnRefresh.IsEnabled = False
        BtnTextToggle.IsEnabled = False
        LabStatus.Text =
            $"正在读取已安装{WarThunderComponentKindName(Mode)}…"
        RunInNewThread(
        Sub()
            Dim Components = WarThunderEnumerateInstalledComponents(Mode)
            RunInUi(
            Sub()
                IsRefreshing = False
                BtnRefresh.IsEnabled = True
                BtnTextToggle.IsEnabled = True
                BuildComponentList(Components)
                RefreshTarget()
                Dim KindName As String = WarThunderComponentKindName(Mode)
                If Mode = WarThunderComponentKind.Text Then
                    LabStatus.Text = If(
                        Components.Count = 0,
                        "当前没有可编辑的 CSV 或 BLK 文本文件。",
                        $"找到 {Components.Count} 个可编辑文本文件。")
                Else
                    LabStatus.Text = If(Components.Count = 0,
                        $"当前没有安装{KindName}组件。",
                        $"已安装 {Components.Count} 个{KindName}组件。")
                End If
            End Sub)
        End Sub, "War Thunder Component Scan", ThreadPriority.BelowNormal)
    End Sub

    Private Sub RefreshTarget()
        Dim KindName As String = WarThunderComponentKindName(Mode)
        CardComponents.Title = If(
            Mode = WarThunderComponentKind.Text,
            "本地化文本",
            $"已安装{KindName}")
        Dim Root As String = WarThunderGetComponentRoot(Mode)
        If Mode = WarThunderComponentKind.Sights Then
            Dim UserId As String = WarThunderGetActiveUserId()
            LabTarget.Text = If(Root = "",
                $"未找到当前玩家的{KindName}目录。",
                $"当前玩家 UID：{UserId} {Root}")
        ElseIf Mode = WarThunderComponentKind.Camouflage Then
            LabTarget.Text = If(Root = "",
                "未找到战争雷霆 UserSkins 目录。",
                $"涂装会按文件夹安装到游戏目录：{Root}")
        ElseIf Mode = WarThunderComponentKind.Text Then
            Dim Enabled As Boolean = False
            If WarThunderCurrentInstall IsNot Nothing AndAlso
               WarThunderCurrentInstall.IsValid Then
                Try
                    Enabled = WarThunderIsTestLocalizationEnabled(
                        WarThunderCurrentInstall.InstallPath)
                Catch ex As Exception
                    Logger.Warn(ex, "读取自定义本地化状态失败")
                End Try
            End If
            LabTarget.Text = If(Root = "",
                "未找到战争雷霆安装目录。",
                $"{If(Enabled, "自定义文本已启用", "自定义文本尚未启用")} · {Root}" & vbCrLf &
                "请使用支持 UTF-8 的编辑器修改 CSV，并保持原有列结构。")
        Else
            LabTarget.Text = If(Root = "",
                "未找到战争雷霆 sound 目录。",
                $"音效包会安装到 sound\mod，不会修改游戏原版文件：{Root}")
        End If
        BtnOpen.IsEnabled = Root <> ""
    End Sub

    Private Sub BuildComponentList(Components As List(Of WarThunderInstalledComponent))
        PanComponents.Children.Clear()
        If Components.Count = 0 Then Return
        For Each ComponentItem As WarThunderInstalledComponent In Components
            Dim Component = ComponentItem
            Dim Card As New Border With {
                .Margin = New Thickness(0, 0, 0, 9),
                .Padding = New Thickness(14, 11, 14, 11),
                .CornerRadius = New CornerRadius(6),
                .BorderThickness = New Thickness(1),
                .BorderBrush = TryCast(FindResource("ColorBrushGray1"), Media.Brush),
                .Background = TryCast(FindResource("ColorBrushSemiTransparent"), Media.Brush)
            }
            Dim Layout As New Grid()
            Layout.ColumnDefinitions.Add(New ColumnDefinition With {
                .Width = New GridLength(1, GridUnitType.Star)
            })
            Layout.ColumnDefinitions.Add(New ColumnDefinition With {
                .Width = GridLength.Auto
            })
            Dim Details As New StackPanel With {
                .Tag = Component
            }
            If Mode = WarThunderComponentKind.Sights Then
                Details.Background = Media.Brushes.Transparent
                Details.Cursor = System.Windows.Input.Cursors.Hand
                Details.ToolTip = "双击载入瞄具预览"
                AddHandler Details.PreviewMouseLeftButtonDown,
                    AddressOf ComponentDetails_PreviewMouseLeftButtonDown
            End If
            Details.Children.Add(New TextBlock With {
                .Text = Component.Name,
                .FontSize = 14,
                .FontWeight = FontWeights.SemiBold,
                .TextTrimming = TextTrimming.CharacterEllipsis,
                .Foreground = TryCast(FindResource("ColorBrush1"), Media.Brush)
            })
            Details.Children.Add(New TextBlock With {
                .Text = $"{Component.FileCount} 个文件: {FormatSize(Component.TotalSize)}",
                .Margin = New Thickness(0, 4, 0, 0),
                .FontSize = 11.5,
                .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush)
            })
            Details.Children.Add(New TextBlock With {
                .Text = Component.Path,
                .Margin = New Thickness(0, 3, 12, 0),
                .FontSize = 10.5,
                .TextTrimming = TextTrimming.CharacterEllipsis,
                .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush),
                .ToolTip = Component.Path
            })
            Layout.Children.Add(Details)
            Dim Actions As New StackPanel With {
                .Orientation = Orientation.Horizontal,
                .VerticalAlignment = VerticalAlignment.Center,
                .Margin = New Thickness(14, 0, 0, 0)
            }
            If Mode = WarThunderComponentKind.Sights Then
                Dim PreviewButton As New MyButton With {
                    .Text = "预览",
                    .Width = 76,
                    .Height = 34,
                    .Margin = New Thickness(0, 0, 8, 0),
                    .VerticalAlignment = VerticalAlignment.Center,
                    .ColorType = MyButton.ColorState.Highlight,
                    .Tag = Component
                }
                AddHandler PreviewButton.Click, AddressOf PreviewButton_Click
                Actions.Children.Add(PreviewButton)
            End If
            If Mode = WarThunderComponentKind.Text Then
                Dim EditButton As New MyButton With {
                    .Text = "编辑",
                    .Width = 86,
                    .Height = 34,
                    .VerticalAlignment = VerticalAlignment.Center,
                    .ColorType = MyButton.ColorState.Highlight,
                    .Tag = Component
                }
                AddHandler EditButton.Click, AddressOf EditTextButton_Click
                Actions.Children.Add(EditButton)
            Else
                Dim RemoveButton As New MyButton With {
                    .Text = "卸载",
                    .Width = 86,
                    .Height = 34,
                    .VerticalAlignment = VerticalAlignment.Center,
                    .ColorType = MyButton.ColorState.Red,
                    .Tag = Component
                }
                AddHandler RemoveButton.Click, AddressOf RemoveButton_Click
                Actions.Children.Add(RemoveButton)
            End If
            Grid.SetColumn(Actions, 1)
            Layout.Children.Add(Actions)
            Card.Child = Layout
            PanComponents.Children.Add(Card)
        Next
    End Sub

    Private Sub PreviewButton_Click(sender As Object, e As EventArgs)
        If Mode <> WarThunderComponentKind.Sights Then Return
        Dim Component = TryCast(CType(sender, FrameworkElement).Tag,
                                WarThunderInstalledComponent)
        If Component Is Nothing Then Return
        ShowSightPreview(Component)
    End Sub

    Private Sub ComponentDetails_PreviewMouseLeftButtonDown(
        sender As Object, e As MouseButtonEventArgs)
        If Mode <> WarThunderComponentKind.Sights OrElse e.ClickCount <> 2 Then Return
        Dim Component = TryCast(CType(sender, FrameworkElement).Tag,
                                WarThunderInstalledComponent)
        If Component Is Nothing Then Return
        ShowSightPreview(Component)
        e.Handled = True
    End Sub

    Private Sub ShowSightPreview(Component As WarThunderInstalledComponent)
        PreviewRequestId += 1
        PreviewRenderId += 1
        Dim RequestId As Integer = PreviewRequestId
        PreviewComponentPath = Component.Path
        CardPreview.Visibility = Visibility.Visible
        PanBack.ScrollToHome()
        ImgSightPreview.Source = Nothing
        LabPreviewPlaceholder.Visibility = Visibility.Visible
        LabPreviewPlaceholder.Text = "正在读取瞄具…"
        LabPreviewFile.Text = Component.Name
        LabPreviewStatus.Text = "正在查找瞄具定义文件…"
        LabPreviewStatus.Foreground = TryCast(
            FindResource("ColorBrushGray2"), Media.Brush)
        ComboSightFiles.Items.Clear()
        ComboSightFiles.IsEnabled = False
        BtnPreviewReload.IsEnabled = False

        RunInNewThread(
        Sub()
            Dim Files As List(Of String) =
                WarThunderFindSightDefinitionFiles(Component.Path)
            RunInUi(
            Sub()
                If RequestId <> PreviewRequestId OrElse
                   Mode <> WarThunderComponentKind.Sights Then
                    Return
                End If
                If Files.Count = 0 Then
                    LabPreviewPlaceholder.Text = "没有找到 BLK 瞄具文件"
                    LabPreviewStatus.Text =
                        "这个组件中没有可读取的 BLK 瞄具定义文件。"
                    Return
                End If

                IsPopulatingSightFiles = True
                Try
                    For Each FilePath As String In Files
                        Dim DisplayPath As String = FilePath
                        If IO.Directory.Exists(Component.Path) Then
                            DisplayPath = FilePath.Substring(
                                Component.Path.TrimEnd(IO.Path.DirectorySeparatorChar).Length).
                                TrimStart(IO.Path.DirectorySeparatorChar)
                        Else
                            DisplayPath = IO.Path.GetFileName(FilePath)
                        End If
                        ComboSightFiles.Items.Add(New MyComboBoxItem With {
                            .Content = DisplayPath,
                            .Tag = FilePath
                        })
                    Next
                    ComboSightFiles.IsEnabled = Files.Count > 1
                    ComboSightFiles.SelectedIndex = 0
                Finally
                    IsPopulatingSightFiles = False
                End Try
                RenderSelectedSightPreview()
            End Sub)
        End Sub, "War Thunder Sight Preview Scan", ThreadPriority.BelowNormal)
    End Sub

    Private Sub ComboSightFiles_SelectionChanged(
        sender As Object, e As SelectionChangedEventArgs) Handles ComboSightFiles.SelectionChanged
        If IsPopulatingSightFiles Then Return
        RenderSelectedSightPreview()
    End Sub

    Private Sub InitializePreviewBackground()
        If IsPreviewBackgroundInitialized Then Return
        IsPreviewBackgroundInitialized = True
        Dim SavedValue As String = Settings.Get(Of String)(
            "WarThunderSightPreviewBackground")
        ApplyPreviewBackground(
            WarThunderParseSightPreviewBackground(SavedValue),
            SaveSetting:=False,
            Redraw:=False)
    End Sub

    Private Sub ApplyPreviewBackground(
        Value As System.Drawing.Color,
        Optional SaveSetting As Boolean = True,
        Optional Redraw As Boolean = True)
        PreviewBackgroundColor = System.Drawing.Color.FromArgb(
            255, Value.R, Value.G, Value.B)
        Dim ColorValue As Media.Color = Media.Color.FromRgb(
            PreviewBackgroundColor.R,
            PreviewBackgroundColor.G,
            PreviewBackgroundColor.B)
        PreviewBackgroundSwatch.Background =
            New Media.SolidColorBrush(ColorValue)
        Dim HexValue As String =
            WarThunderFormatSightPreviewBackground(PreviewBackgroundColor)
        LabPreviewBackground.Text = $"预览背景: {HexValue}"
        BtnPreviewBackground.ToolTip = "选择瞄具预览的背景颜色"
        If SaveSetting Then
            Settings.Set("WarThunderSightPreviewBackground", HexValue)
        End If
        If Redraw AndAlso ComboSightFiles.SelectedItem IsNot Nothing Then
            RenderSelectedSightPreview()
        End If
    End Sub

    Private Sub BtnPreviewBackground_Click(
        sender As Object, e As EventArgs) Handles BtnPreviewBackground.Click
        Using Dialog As New Windows.Forms.ColorDialog()
            Dialog.AllowFullOpen = True
            Dialog.AnyColor = True
            Dialog.FullOpen = True
            Dialog.SolidColorOnly = True
            Dialog.Color = PreviewBackgroundColor
            If Dialog.ShowDialog() <> Windows.Forms.DialogResult.OK Then Return
            ApplyPreviewBackground(Dialog.Color)
        End Using
    End Sub

    Private Sub RenderSelectedSightPreview()
        Dim Choice = TryCast(ComboSightFiles.SelectedItem, MyComboBoxItem)
        If Choice Is Nothing Then Return
        Dim BlkPath As String = Convert.ToString(Choice.Tag)
        If String.IsNullOrWhiteSpace(BlkPath) OrElse Not IO.File.Exists(BlkPath) Then
            LabPreviewPlaceholder.Text = "瞄具文件已经不存在"
            LabPreviewPlaceholder.Visibility = Visibility.Visible
            ImgSightPreview.Source = Nothing
            LabPreviewStatus.Text = "请刷新组件列表后重试。"
            BtnPreviewReload.IsEnabled = False
            Return
        End If

        PreviewRenderId += 1
        Dim RenderId As Integer = PreviewRenderId
        Dim RequestId As Integer = PreviewRequestId
        Dim BackgroundAtRender As System.Drawing.Color = PreviewBackgroundColor
        ImgSightPreview.Source = Nothing
        LabPreviewPlaceholder.Visibility = Visibility.Visible
        LabPreviewPlaceholder.Text = "正在生成预览…"
        LabPreviewFile.Text = IO.Path.GetFileName(BlkPath)
        LabPreviewStatus.Text = "正在解析瞄具..."
        LabPreviewStatus.Foreground = TryCast(
            FindResource("ColorBrushGray2"), Media.Brush)
        BtnPreviewReload.IsEnabled = False

        RunInNewThread(
        Sub()
            Dim Preview As WarThunderSightPreviewResult = Nothing
            Dim ErrorMessage As String = ""
            Try
                Preview = WarThunderRenderSightPreview(
                    BlkPath, BackgroundAtRender)
            Catch ex As Exception
                ErrorMessage = ex.GetDisplay(False)
                Logger.Warn(ex, $"生成瞄具预览失败：{BlkPath}")
            End Try
            RunInUi(
            Sub()
                If RenderId <> PreviewRenderId OrElse RequestId <> PreviewRequestId Then Return
                BtnPreviewReload.IsEnabled = True
                If Preview Is Nothing Then
                    LabPreviewPlaceholder.Text = "预览生成失败"
                    LabPreviewStatus.Text = ErrorMessage
                    LabPreviewStatus.Foreground = TryCast(
                        FindResource("ColorBrushRedLight"), Media.Brush)
                    Return
                End If
                ImgSightPreview.Source = Preview.Image
                LabPreviewPlaceholder.Visibility = Visibility.Collapsed
                LabPreviewStatus.Text = ""
                LabPreviewStatus.Foreground = TryCast(
                    FindResource("ColorBrushGray2"), Media.Brush)
            End Sub)
        End Sub, "War Thunder Sight Preview Render", ThreadPriority.BelowNormal)
    End Sub

    Private Sub ClearSightPreview()
        PreviewRequestId += 1
        PreviewRenderId += 1
        PreviewComponentPath = ""
        IsPopulatingSightFiles = True
        ComboSightFiles.Items.Clear()
        IsPopulatingSightFiles = False
        ComboSightFiles.IsEnabled = False
        ImgSightPreview.Source = Nothing
        LabPreviewFile.Text = "尚未选择瞄具"
        LabPreviewStatus.Text = "选择瞄具后会自动生成预览。"
        BtnPreviewReload.IsEnabled = False
        CardPreview.Visibility = Visibility.Collapsed
    End Sub

    Private Sub BtnPreviewReload_Click(
        sender As Object, e As EventArgs) Handles BtnPreviewReload.Click
        RenderSelectedSightPreview()
    End Sub

    Private Sub EditTextButton_Click(sender As Object, e As EventArgs)
        Dim Component = TryCast(CType(sender, FrameworkElement).Tag,
                                WarThunderInstalledComponent)
        If Component Is Nothing OrElse Not IO.File.Exists(Component.Path) Then
            Hint("文本文件已经不存在，请刷新列表。", HintType.Red)
            Return
        End If
        Try
            Process.Start(New ProcessStartInfo With {
                .FileName = Component.Path,
                .WorkingDirectory = IO.Path.GetDirectoryName(Component.Path),
                .UseShellExecute = True
            })
        Catch ex As Exception
            Logger.Warn(ex, $"打开本地化文本失败：{Component.Path}")
            MyMsgBox(
                "无法打开文本编辑器。" & vbCrLf &
                "请先为 CSV 文件关联 Notepad++、Visual Studio Code 等编辑器。" &
                vbCrLf & vbCrLf & ex.GetDisplay(False),
                "无法编辑文本", IsWarn:=True)
        End Try
    End Sub

    Private Sub BtnTextToggle_Click(sender As Object, e As EventArgs) _
        Handles BtnTextToggle.Click
        If WarThunderCurrentInstall Is Nothing OrElse
           Not WarThunderCurrentInstall.IsValid Then
            Hint("请先选择战争雷霆安装目录。", HintType.Red)
            Return
        End If
        Try
            Dim Enabled As Boolean = WarThunderIsTestLocalizationEnabled(
                WarThunderCurrentInstall.InstallPath)
            If Not Enabled Then
                If MyMsgBox(
                    "启用后，游戏会从游戏目录的 lang 文件夹读取文本。" & vbCrLf &
                    "CSV 必须是 UTF-8 编码，并保持完整结构；文件缺失或格式错误可能导致游戏无法启动。" &
                    vbCrLf & vbCrLf & "确定要启用自定义文本吗？",
                    "启用自定义文本", "启用", "取消", IsWarn:=True) = 2 Then
                    Return
                End If
            End If
            WarThunderSetTestLocalization(
                WarThunderCurrentInstall.InstallPath, Not Enabled)
            Dim Root As String = WarThunderGetComponentRoot(
                WarThunderComponentKind.Text)
            If Not Enabled AndAlso Root <> "" Then
                IO.Directory.CreateDirectory(Root)
            End If
            Hint(
                If(Enabled,
                   "已停用自定义文本，重启游戏后生效。",
                   "已启用自定义文本，重启游戏后生效。"),
                HintType.Green)
            UpdateModeUi()
            RefreshPage()
        Catch ex As Exception
            Logger.Error(ex, "切换战争雷霆自定义本地化失败")
            MyMsgBox(
                "切换自定义文本失败：" & vbCrLf & ex.GetDisplay(False),
                "操作失败", IsWarn:=True)
        End Try
    End Sub

    Private Sub BtnTextGuide_Click(sender As Object, e As EventArgs) _
        Handles BtnTextGuide.Click
        OpenWebsite(
            "https://wiki.biligame.com/warthunder/%E6%9C%AC%E5%9C%B0%E5%8C%96")
    End Sub

    Private Sub RemoveButton_Click(sender As Object, e As EventArgs)
        If IsRemoving Then Return
        Dim Component = TryCast(CType(sender, FrameworkElement).Tag,
                                WarThunderInstalledComponent)
        If Component Is Nothing Then Return
        Dim KindName As String = WarThunderComponentKindName(Component.Kind)
        Dim ConfirmText As String
        If Component.IsManagedSoundOverlay Then
            ConfirmText =
                $"确定要卸载这个{KindName}吗？" & vbCrLf &
                Component.Name & vbCrLf & vbCrLf &
                "安装前的原版 sound 文件将从备份恢复。"
        Else
            ConfirmText =
                $"确定要卸载这个{KindName}组件吗？" & vbCrLf &
                Component.Name & vbCrLf & vbCrLf &
                "组件会被直接永久删除，无法恢复。"
        End If
        If MyMsgBox(
            ConfirmText,
            "卸载组件", "卸载", "取消", IsWarn:=True) = 2 Then
            Return
        End If
        IsRemoving = True
        BtnRefresh.IsEnabled = False
        RunInNewThread(
        Sub()
            Dim ErrorMessage As String = ""
            Dim Success As Boolean = WarThunderRemoveInstalledComponent(
                Component, ErrorMessage)
            RunInUi(
            Sub()
                IsRemoving = False
                BtnRefresh.IsEnabled = True
                If Success Then
                    If PreviewComponentPath = Component.Path Then ClearSightPreview()
                    Hint(
                        If(Component.IsManagedSoundOverlay,
                           $"已卸载 {Component.Name} 并恢复原版 sound 文件。",
                           $"已删除 {Component.Name}。"),
                        HintType.Green)
                    RefreshPage()
                Else
                    MyMsgBox("卸载组件失败：" & vbCrLf & ErrorMessage,
                             "卸载失败", IsWarn:=True)
                End If
            End Sub)
        End Sub, "War Thunder Component Remove", ThreadPriority.AboveNormal)
    End Sub

    Private Sub BtnRefresh_Click(sender As Object, e As EventArgs) Handles BtnRefresh.Click
        RefreshPage()
    End Sub

    Private Sub BtnOpen_Click(sender As Object, e As EventArgs) Handles BtnOpen.Click
        Dim Root As String = WarThunderGetComponentRoot(Mode)
        If Root = "" Then Return
        Directory.CreateDirectory(Root)
        OpenExplorer(Root & "\")
    End Sub

    Private Shared Function FormatSize(Bytes As Long) As String
        If Bytes >= 1024L * 1024 * 1024 Then Return $"{Bytes / 1024 / 1024 / 1024:0.0} GB"
        If Bytes >= 1024L * 1024 Then Return $"{Bytes / 1024 / 1024:0.0} MB"
        If Bytes >= 1024 Then Return $"{Bytes / 1024:0.0} KB"
        Return Bytes & " B"
    End Function

    Public Sub Refresh() Implements IRefreshable.Refresh
        RefreshPage()
    End Sub

End Class
