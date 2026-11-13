Imports Newtonsoft.Json.Linq

Public Class PageWarThunderTools
    Implements IRefreshable

    Private IsBusy As Boolean

    Private Sub PageRefresh() Handles Me.Loaded, Me.PageEnter
        If Not IsLoaded Then Return
        PanBack.ScrollToHome()
        RefreshPage()
    End Sub

    Public Sub RefreshPage()
        If Not IsLoaded OrElse IsBusy Then Return
        IsBusy = True
        BtnToolsRefresh.IsEnabled = False
        LabToolsStatus.Text = "正在获取工具列表…"
        RunInNewThread(
        Sub()
            Dim Items As JArray = Nothing
            Dim InstalledTools As Dictionary(Of String, WarThunderInstalledTool) = Nothing
            Dim ErrorMessage As String = ""
            Try
                Items = WarThunderFetchResourceCatalog("tool")
                Items = WarThunderFilterToolCatalog(Items)
                InstalledTools = WarThunderResolveInstalledTools(Items)
            Catch ex As Exception
                ErrorMessage = ex.GetDisplay(False)
                Logger.Warn(ex, "获取 PTL 工具列表失败")
            End Try
            RunInUi(
            Sub()
                IsBusy = False
                BtnToolsRefresh.IsEnabled = True
                If Items Is Nothing Then
                    PanTools.Children.Clear()
                    LabToolsStatus.Text = "暂时无法获取工具列表，请稍后重试。"
                    Return
                End If
                BuildToolCards(Items, InstalledTools)
            End Sub)
        End Sub, "War Thunder Tool Catalog", ThreadPriority.AboveNormal)
    End Sub

    Private Sub BuildToolCards(
        Items As JArray,
        InstalledTools As Dictionary(Of String, WarThunderInstalledTool))
        PanTools.Children.Clear()
        If Items.Count = 0 Then
            LabToolsStatus.Text = "暂时还没有可安装的工具。"
            Return
        End If
        LabToolsStatus.Text = $"共有 {Items.Count} 个工具可以安装。"
        For Each Token As JToken In Items
            Dim Item = TryCast(Token, JObject)
            If Item Is Nothing Then Continue For
            Dim Card As New Border With {
                .Margin = New Thickness(0, 0, 0, 10),
                .Padding = New Thickness(14, 11, 14, 11),
                .CornerRadius = New CornerRadius(6),
                .BorderThickness = New Thickness(1),
                .BorderBrush = TryCast(FindResource("ColorBrushGray1"), Media.Brush),
                .Background = TryCast(FindResource("ColorBrushSemiTransparent"), Media.Brush)
            }
            Dim Layout As New Grid()
            Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

            Dim Details As New StackPanel()
            Dim Name As String = Item.Value(Of String)("name")
            Dim Version As String = Item.Value(Of String)("version")
            Dim ToolKey As String = WarThunderGetToolCatalogKey(Item)
            Dim InstalledTool As WarThunderInstalledTool = Nothing
            If InstalledTools IsNot Nothing AndAlso ToolKey <> "" Then
                InstalledTools.TryGetValue(ToolKey, InstalledTool)
            End If
            Details.Children.Add(New TextBlock With {
                .Text = Name & If(String.IsNullOrWhiteSpace(Version), "", "  " & Version),
                .FontSize = 14.5,
                .FontWeight = FontWeights.SemiBold,
                .Foreground = TryCast(FindResource("ColorBrush1"), Media.Brush)
            })
            Dim Description As String = Item.Value(Of String)("description")
            If Not String.IsNullOrWhiteSpace(Description) Then
                Details.Children.Add(New TextBlock With {
                    .Text = Description,
                    .Margin = New Thickness(0, 4, 0, 0),
                    .FontSize = 11.5,
                    .TextWrapping = TextWrapping.Wrap,
                    .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush)
                })
            End If
            If InstalledTool IsNot Nothing Then
                Dim InstalledVersion As String = If(
                    String.IsNullOrWhiteSpace(InstalledTool.Version), "未知版本", InstalledTool.Version)
                Details.Children.Add(New TextBlock With {
                    .Text = $"已安装 {InstalledVersion}",
                    .Margin = New Thickness(0, 5, 0, 0),
                    .FontSize = 11.5,
                    .FontWeight = FontWeights.SemiBold,
                    .Foreground = TryCast(FindResource("ColorBrush3"), Media.Brush)
                })
            End If
            Dim FileSize As Long = Item.Value(Of Long?)("size").GetValueOrDefault(0)
            Details.Children.Add(New TextBlock With {
                .Text = $"{FormatSize(FileSize)}  {Item.Value(Of String)("filename")}",
                .Margin = New Thickness(0, 5, 0, 0),
                .FontSize = 11,
                .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush)
            })
            Layout.Children.Add(Details)

            Dim ActionButton As MyButton
            If InstalledTool IsNot Nothing Then
                ActionButton = New MyButton With {
                    .Text = "卸载",
                    .Width = 92,
                    .Height = 35,
                    .Margin = New Thickness(14, 0, 0, 0),
                    .VerticalAlignment = VerticalAlignment.Center,
                    .ColorType = MyButton.ColorState.Red,
                    .Tag = InstalledTool,
                    .IsEnabled = InstalledTool.CanUninstall
                }
                If Not InstalledTool.CanUninstall Then
                    ActionButton.ToolTip = "没有找到该工具的 Inno Setup 卸载程序。"
                End If
                AddHandler ActionButton.Click, AddressOf UninstallButton_Click
            Else
                ActionButton = New MyButton With {
                    .Text = "下载并安装",
                    .Width = 105,
                    .Height = 35,
                    .Margin = New Thickness(14, 0, 0, 0),
                    .VerticalAlignment = VerticalAlignment.Center,
                    .ColorType = MyButton.ColorState.Highlight,
                    .Tag = Item
                }
                AddHandler ActionButton.Click, AddressOf InstallButton_Click
            End If
            Grid.SetColumn(ActionButton, 1)
            Layout.Children.Add(ActionButton)
            Card.Child = Layout
            PanTools.Children.Add(Card)
        Next
    End Sub

    Private Sub InstallButton_Click(sender As Object, e As EventArgs)
        If IsBusy Then Return
        Dim Item = TryCast(CType(sender, FrameworkElement).Tag, JObject)
        If Item Is Nothing Then Return
        Dim Name As String = Item.Value(Of String)("name")
        Dim Prompt As String = "是否下载并安装该工具？" & vbCrLf &
            "工具名称：" & Name & vbCrLf &
            "下载会自动加入任务列表。"
        If MyMsgBox(Prompt, "安装工具", "下载并安装", "取消") = 2 Then Return

        IsBusy = True
        BtnToolsRefresh.IsEnabled = False
        LabToolsStatus.Text = $"正在准备 {Name}…"
        RunInNewThread(
        Sub()
            Dim ErrorMessage As String = ""
            Dim ResolvedItem As JObject = WarThunderResolveToolDownload(Item, ErrorMessage)
            Dim DownloadFile As NetFile = Nothing
            If ResolvedItem IsNot Nothing Then
                Dim DownloadFolder As String = Path.Combine(
                    PathTemp, "PTL", "Tools", Guid.NewGuid().ToString("N"))
                DownloadFile = WarThunderCreateServiceDownloadNetFile(
                    ResolvedItem, DownloadFolder, ErrorMessage)
            End If
            RunInUi(
            Sub()
                If DownloadFile Is Nothing Then
                    Logger.Warn($"准备工具下载失败：{ErrorMessage}")
                    FinishTask(Name, LoadState.Failed,
                        New WarThunderInstallSummary With {
                            .Success = False,
                            .Message = "暂时无法获取安装文件，请稍后重试。"
                        }, Nothing)
                    Return
                End If
                LabToolsStatus.Text = $"正在下载并安装 {Name}…"
                WarThunderRunDownloadTask(Name, DownloadFile,
                    Function(InstallerPath)
                        Dim Summary As New WarThunderInstallSummary With {.Success = False}
                        Dim DownloadFolder As String = Path.GetDirectoryName(InstallerPath)
                        Try
                            If Not WarThunderVerifyServiceDownloadHash(
                                ResolvedItem, InstallerPath, ErrorMessage) Then
                                Summary.Message = ErrorMessage
                                Return Summary
                            End If
                            If Not WarThunderValidateInnoSetupInstaller(InstallerPath) Then
                                Summary.Message = "下载内容不是有效的安装程序，请稍后重试。"
                                Return Summary
                            End If
                            Using Installer As Process = Process.Start(New ProcessStartInfo With {
                                .FileName = InstallerPath,
                                .Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
                                .UseShellExecute = True,
                                .WorkingDirectory = DownloadFolder
                            })
                                If Installer Is Nothing Then Throw New InvalidOperationException("安装程序未能启动。")
                                Installer.WaitForExit()
                                If Installer.ExitCode <> 0 Then
                                    Summary.Message = $"安装未完成，退出代码：{Installer.ExitCode}"
                                    Return Summary
                                End If
                            End Using
                            Summary.Success = True
                            Summary.Message = $"{Name} 安装完成。"
                            Return Summary
                        Catch ex As Exception
                            Summary.Message = ex.GetDisplay(False)
                            Return Summary
                        Finally
                            Try
                                If Directory.Exists(DownloadFolder) Then Directory.Delete(DownloadFolder, True)
                            Catch ex As Exception
                                Logger.Warn(ex, $"清理工具下载文件失败：{DownloadFolder}")
                            End Try
                        End Try
                    End Function,
                    Sub(State, Result, Ex)
                        Try
                            Dim DownloadFolder As String = Path.GetDirectoryName(DownloadFile.LocalPath)
                            If Directory.Exists(DownloadFolder) Then Directory.Delete(DownloadFolder, True)
                        Catch cleanupEx As Exception
                            Logger.Warn(cleanupEx, $"清理工具下载临时文件失败：{DownloadFile.LocalPath}")
                        End Try
                        FinishTask(Name, State, Result, Ex)
                    End Sub)
                FrmMain.PageChange(FormMain.PageType.TaskManager)
            End Sub)
        End Sub, "War Thunder Tool Install", ThreadPriority.AboveNormal)
    End Sub

    Private Sub UninstallButton_Click(sender As Object, e As EventArgs)
        If IsBusy Then Return
        Dim Tool = TryCast(CType(sender, FrameworkElement).Tag, WarThunderInstalledTool)
        If Tool Is Nothing Then Return
        If Not Tool.CanUninstall Then
            MyMsgBox("没有找到该工具的卸载程序，请尝试在 Windows 应用列表中卸载。",
                     "无法卸载", IsWarn:=True)
            Return
        End If
        Dim Prompt As String = "确定要卸载这个工具吗？" & vbCrLf &
            "工具名称：" & Tool.DisplayName & vbCrLf &
            "卸载过程不会删除战争雷霆游戏文件。"
        If MyMsgBox(Prompt, "卸载工具", "卸载", "取消", IsWarn:=True) = 2 Then Return

        IsBusy = True
        BtnToolsRefresh.IsEnabled = False
        LabToolsStatus.Text = $"正在卸载 {Tool.DisplayName}…"
        RunInNewThread(
        Sub()
            Dim ErrorMessage As String = ""
            Dim Success As Boolean = WarThunderUninstallTool(Tool, ErrorMessage)
            RunInUi(
            Sub()
                IsBusy = False
                BtnToolsRefresh.IsEnabled = True
                If Success Then
                    LabToolsStatus.Text = $"{Tool.DisplayName} 已卸载。"
                    Hint(LabToolsStatus.Text, HintType.Green)
                    RefreshPage()
                Else
                    LabToolsStatus.Text = $"卸载 {Tool.DisplayName} 失败：{ErrorMessage}"
                    MyMsgBox("工具卸载失败：" & vbCrLf & ErrorMessage,
                             "卸载失败", IsWarn:=True)
                End If
            End Sub)
        End Sub, "War Thunder Tool Uninstall", ThreadPriority.AboveNormal)
    End Sub

    Private Sub FinishTask(Name As String, State As LoadState,
                           Result As WarThunderInstallSummary, Ex As Exception)
        IsBusy = False
        BtnToolsRefresh.IsEnabled = True
        Select Case State
            Case LoadState.Finished
                LabToolsStatus.Text = If(Result?.Message, $"{Name} 安装完成。")
                Hint(LabToolsStatus.Text, HintType.Green)
                RefreshPage()
            Case LoadState.Canceled
                LabToolsStatus.Text = $"{Name} 安装已取消。"
                Hint(LabToolsStatus.Text, HintType.Blue)
            Case Else
                Dim Message As String = If(Result?.Message,
                    "下载未完成，请稍后重试。")
                If Ex IsNot Nothing Then Logger.Warn(Ex, $"工具安装失败：{Name}")
                LabToolsStatus.Text = $"安装 {Name} 失败：{Message}"
                MyMsgBox("工具安装失败：" & vbCrLf & Message, "安装失败", IsWarn:=True)
        End Select
    End Sub

    Private Shared Function FormatSize(Bytes As Long) As String
        If Bytes >= 1024L * 1024 * 1024 Then Return $"{Bytes / 1024 / 1024 / 1024:0.0} GB"
        If Bytes >= 1024L * 1024 Then Return $"{Bytes / 1024 / 1024:0.0} MB"
        If Bytes >= 1024 Then Return $"{Bytes / 1024:0.0} KB"
        Return Bytes & " B"
    End Function

    Private Sub BtnToolsRefresh_Click(sender As Object, e As EventArgs) Handles BtnToolsRefresh.Click
        RefreshPage()
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        RefreshPage()
    End Sub

End Class
