Imports Newtonsoft.Json.Linq
Imports System.Windows.Threading

Public Class PageWarThunderResources
    Implements IRefreshable

    Private Mode As WarThunderResourceKind = WarThunderResourceKind.Sights
    Private LoadedMode As WarThunderResourceKind = WarThunderResourceKind.Auto
    Private PageIndex As Integer
    Private HasMore As Boolean
    Private IsCatalogLoading As Boolean
    Private IsInstalling As Boolean
    Private CatalogRequestVersion As Integer
    Private SearchHelpBase As String = ""

    Private Sub PageRefresh() Handles Me.Loaded, Me.PageEnter
        If Not IsLoaded Then Return
        PanBack.ScrollToHome()
        RefreshMode()
    End Sub

    Public Sub SetMode(Value As WarThunderResourceKind)
        If Value = WarThunderResourceKind.Auto Then Value = WarThunderResourceKind.Sights
        If Mode = Value Then Return
        Mode = Value
        PageIndex = 0
        LoadedMode = WarThunderResourceKind.Auto
    End Sub

    Public Sub RefreshPage()
        If IsLoaded Then StartCatalogSearch()
    End Sub

    Private Sub RefreshMode()
        If CardSearch Is Nothing Then Return
        Dim IsSights = Mode = WarThunderResourceKind.Sights
        Dim IsCamouflage = Mode = WarThunderResourceKind.Camouflage
        Dim IsSound = Mode = WarThunderResourceKind.Sound
        CardSearch.Title = $"搜索{WarThunderResourceKindName(Mode)}"
        SearchHelpBase = "资源来源: WT Live"
        LabSearchHelp.Text = SearchHelpBase
        CheckUseLoops.Visibility = If(IsSound, Visibility.Visible, Visibility.Collapsed)
        If IsSights Then
            Dim UserId As String = WarThunderGetActiveUserId()
            Dim Folder As String = WarThunderGetUserSightsFolder(UserId)
            LabDestination.Text = If(
                Folder = "",
                "尚未检测到当前玩家的 UserSights 目录",
                Folder)
        ElseIf IsCamouflage Then
            Dim UserId As String = WarThunderGetActiveUserId()
            Dim Folder As String = WarThunderGetUserSkinsFolder(UserId)
            LabDestination.Text = If(
                Folder = "",
                "尚未检测到当前玩家的 UserSkins 目录",
                Folder)
        Else
            Dim Folder As String = WarThunderGetSoundModFolder()
            LabDestination.Text = If(
                Folder = "",
                "尚未找到战争雷霆 sound\mod 目录",
                Folder)
        End If
        If LoadedMode <> Mode Then
            LoadedMode = Mode
            PageIndex = 0
            StartCatalogSearch()
        End If
    End Sub

    Private Sub StartCatalogSearch()
        If IsInstalling OrElse Not IsLoaded Then Return
        CatalogRequestVersion += 1
        Dim RequestVersion As Integer = CatalogRequestVersion
        IsCatalogLoading = True
        SetLoading(True)
        HintError.Visibility = Visibility.Collapsed
        SetSearchControlsEnabled(False)

        Dim RequestMode As WarThunderResourceKind = Mode
        Dim RequestPage As Integer = PageIndex
        Dim RequestSort As String = GetComboTag(ComboSort, "created")
        Dim RequestPeriod As String = GetComboTag(ComboPeriod, "0")
        Dim RequestTags As String = TextSearchTags.Text.Trim()

        RunInNewThread(
        Sub()
            Dim Result As JObject = Nothing
            Dim ErrorMessage As String = ""
            Try
                Result = WarThunderFetchWtLiveResourceCatalog(
                    WarThunderGetWtLiveContent(RequestMode),
                    RequestPage,
                    RequestSort,
                    RequestPeriod,
                    RequestTags)
            Catch ex As Exception
                ErrorMessage = ex.GetDisplay(False)
                Logger.Warn(ex, "获取 WT Live 资源列表失败")
            End Try
            RunInUi(
            Sub()
                If RequestVersion <> CatalogRequestVersion Then Return
                IsCatalogLoading = False
                SetSearchControlsEnabled(True)
                SetLoading(False)
                If Mode <> RequestMode OrElse PageIndex <> RequestPage Then Return
                If Result Is Nothing Then
                    PanProjects.Children.Clear()
                    CardPages.Visibility = Visibility.Collapsed
                    ShowLoadError("无法获取 WT Live 资源。" & vbCrLf & ErrorMessage)
                    Return
                End If
                Try
                    BuildCatalog(Result)
                Catch ex As Exception
                    Logger.Error(ex, "渲染 WT Live 资源列表失败")
                    ShowLoadError("WT Live 资源列表渲染失败，请刷新后重试。")
                End Try
            End Sub)
        End Sub, "WT Live Resource Catalog", ThreadPriority.AboveNormal)
    End Sub

    Private Sub BuildCatalog(Result As JObject)
        PanProjects.Children.Clear()
        Dim Items = TryCast(Result("items"), JArray)
        If Items Is Nothing Then
            ShowLoadError("WT Live 返回了无法识别的资源列表。")
            Return
        End If
        HasMore = Result.Value(Of Boolean?)("has_more").GetValueOrDefault(
            Items.Count >= 25)
        Dim WarningText As String = Result.Value(Of String)("warning")
        If Not String.IsNullOrWhiteSpace(WarningText) Then
            HintError.Text = WarningText
            HintError.Visibility = Visibility.Visible
        End If

        If Items.Count = 0 Then
            PanProjects.Children.Add(New TextBlock With {
                .Text = $"没有找到符合条件的{WarThunderResourceKindName(Mode)}。",
                .Margin = New Thickness(18, 24, 18, 24),
                .HorizontalAlignment = HorizontalAlignment.Center,
                .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush)
            })
        Else
            Dim RenderedCount As Integer = 0
            Dim SkippedCount As Integer = 0
            For Each Token In Items
                Dim Item = TryCast(Token, JObject)
                If Item Is Nothing Then Continue For
                Try
                    Dim Row As New MyWarThunderResourceItem
                    Row.SetData(Item)
                    AddHandler Row.InstallRequested,
                        Sub(SelectedItem As JObject) StartServiceInstall(SelectedItem)
                    AddHandler Row.OpenPostRequested,
                        Sub(Url As String) OpenWebsite(Url)
                    PanProjects.Children.Add(Row)
                    RenderedCount += 1
                Catch ex As Exception
                    SkippedCount += 1
                    Logger.Warn(ex,
                        $"跳过无法渲染的 WT Live 资源：" &
                        Item.Value(Of String)("id"))
                End Try
            Next
            If PanProjects.Children.Count = 0 Then
                PanProjects.Children.Add(New TextBlock With {
                    .Text = "这一页的资源数据无法正常显示，请刷新或切换到下一页。",
                    .Margin = New Thickness(18, 24, 18, 24),
                    .HorizontalAlignment = HorizontalAlignment.Center,
                    .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush)
                })
            End If
            Logger.Info(
                $"WT Live 资源列表渲染完成：{Mode}，第 {PageIndex + 1} 页，" &
                $"成功 {RenderedCount} 条，跳过 {SkippedCount} 条")
        End If

        CardPages.Visibility = If(PageIndex > 0 OrElse HasMore,
                                  Visibility.Visible, Visibility.Collapsed)
        LabPage.Text = (PageIndex + 1).ToString()
        BtnPageFirst.IsEnabled = PageIndex > 0
        BtnPageFirst.Opacity = If(PageIndex > 0, 1, 0.2)
        BtnPageLeft.IsEnabled = PageIndex > 0
        BtnPageLeft.Opacity = If(PageIndex > 0, 1, 0.2)
        BtnPageRight.IsEnabled = HasMore
        BtnPageRight.Opacity = If(HasMore, 1, 0.2)
        Dim CountText As String = $""
        If Not String.IsNullOrWhiteSpace(WarningText) Then
            LabSearchHelp.Text = CountText
        Else
            LabSearchHelp.Text = CountText & SearchHelpBase
        End If
        RefreshCatalogLayout()
    End Sub

    Private Sub RefreshCatalogLayout()
        Dispatcher.BeginInvoke(
            Sub()
                CardProjects.InvalidateMeasure()
                CardProjects.InvalidateArrange()
                PanContent.InvalidateMeasure()
                PanContent.InvalidateArrange()
                PanContent.UpdateLayout()
                Logger.Info(
                    $"WT Live 布局：PanContent={PanContent.ActualHeight:0.#}/" &
                    $"{PanContent.Visibility}，CardProjects={CardProjects.ActualHeight:0.#}，" &
                    $"PanProjects={PanProjects.ActualHeight:0.#}，" &
                    $"CardPages={CardPages.ActualHeight:0.#}/{CardPages.Visibility}，" &
                    $"行数={PanProjects.Children.Count}")
            End Sub, DispatcherPriority.Loaded)
    End Sub

    Private Sub ShowLoadError(Message As String)
        HintError.Text = Message
        HintError.Visibility = Visibility.Visible
        PanProjects.Children.Clear()
        PanProjects.Children.Add(New TextBlock With {
            .Text = "请稍后重试，或检查服务端与网络设置。",
            .Margin = New Thickness(18, 24, 18, 24),
            .HorizontalAlignment = HorizontalAlignment.Center,
            .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush)
        })
    End Sub

    Private Sub StartServiceInstall(Item As JObject)
        If IsInstalling Then Return
        Dim Name As String = If(Item.Value(Of String)("title"),
                                Item.Value(Of String)("name"))
        Dim ErrorMessage As String = ""
        Dim DownloadFolder As String = Path.Combine(
            PathTemp, "PTL", "Resources", Guid.NewGuid().ToString("N"))
        Dim DownloadFile = WarThunderCreateServiceDownloadNetFile(
            Item, DownloadFolder, ErrorMessage)
        If DownloadFile Is Nothing Then
            Logger.Warn($"准备 WT Live 资源下载失败：{ErrorMessage}")
            MyMsgBox("暂时无法开始下载，请稍后重试。", "下载失败", IsWarn:=True)
            Return
        End If

        Dim InstallKind As WarThunderResourceKind = Mode
        Dim InstallUseLoops As Boolean = CheckUseLoops.Checked
        IsInstalling = True
        SetSearchControlsEnabled(False)
        PanProjects.IsEnabled = False
        Hint($"已加入下载任务：{Name}", HintType.Blue)
        WarThunderRunDownloadTask(Name, DownloadFile,
            Function(DownloadPath)
                Dim Summary As WarThunderInstallSummary
                Dim DownloadDir As String = Path.GetDirectoryName(DownloadPath)
                Try
                    Dim VerifyMessage As String = ""
                    If Not WarThunderVerifyServiceDownloadHash(
                        Item, DownloadPath, VerifyMessage) Then
                        Return New WarThunderInstallSummary With {
                            .Success = False,
                            .Kind = InstallKind,
                            .Message = VerifyMessage
                        }
                    End If
                    Summary = WarThunderInstallResourceFile(
                        DownloadPath, InstallKind, InstallUseLoops)
                Catch ex As Exception
                    Summary = New WarThunderInstallSummary With {
                        .Success = False,
                        .Kind = InstallKind,
                        .Message = ex.GetDisplay(False)
                    }
                Finally
                    Try
                        If Directory.Exists(DownloadDir) Then Directory.Delete(DownloadDir, True)
                    Catch ex As Exception
                        Logger.Warn(ex, $"清理资源临时目录失败：{DownloadDir}")
                    End Try
                End Try
                Return Summary
            End Function,
            Sub(State, Result, Ex)
                Dim DownloadDir As String = Path.GetDirectoryName(DownloadFile.LocalPath)
                Try
                    If Directory.Exists(DownloadDir) Then Directory.Delete(DownloadDir, True)
                Catch cleanupException As Exception
                    Logger.Warn(cleanupException, $"清理资源临时目录失败：{DownloadDir}")
                End Try
                IsInstalling = False
                SetSearchControlsEnabled(True)
                PanProjects.IsEnabled = True
                Select Case State
                    Case LoadState.Finished
                        Dim MessageText As String = If(Result?.Message, "资源安装完成。")
                        LabSearchHelp.Text = MessageText
                        Hint(MessageText, HintType.Green)
                    Case LoadState.Canceled
                        Hint("资源安装已取消。", HintType.Blue)
                    Case Else
                        Dim MessageText As String = If(Result?.Message, "下载未完成，请稍后重试。")
                        If Ex IsNot Nothing Then Logger.Warn(Ex, $"WT Live 资源安装失败：{Name}")
                        MyMsgBox("安装战争雷霆资源失败：" & vbCrLf & MessageText,
                                 "安装失败", IsWarn:=True)
                End Select
            End Sub)
        FrmMain.PageChange(FormMain.PageType.TaskManager)
    End Sub

    Private Sub BtnChooseResource_Click(sender As Object, e As RoutedEventArgs) _
        Handles BtnChooseResource.Click
        Dim Dialog As New Microsoft.Win32.OpenFileDialog With {
            .Title = $"选择战争雷霆{WarThunderResourceKindName(Mode)}",
            .Filter = If(Mode = WarThunderResourceKind.Sights,
                         "战争雷霆瞄具 (*.zip;*.blk)|*.zip;*.blk|所有文件 (*.*)|*.*",
                         If(Mode = WarThunderResourceKind.Camouflage,
                            "战争雷霆涂装 (*.zip)|*.zip|所有文件 (*.*)|*.*",
                            "声音模组 (*.zip)|*.zip|所有文件 (*.*)|*.*")),
            .Multiselect = False,
            .CheckFileExists = True
        }
        If Dialog.ShowDialog() = True Then InstallLocalFiles({Dialog.FileName})
    End Sub

    Private Sub InstallLocalFiles(Files As String())
        If Files Is Nothing OrElse Files.Length = 0 OrElse IsInstalling Then Return
        Dim InstallKind As WarThunderResourceKind = Mode
        Dim InstallUseLoops As Boolean = CheckUseLoops.Checked
        IsInstalling = True
        SetSearchControlsEnabled(False)
        PanProjects.IsEnabled = False
        RunInNewThread(
        Sub()
            Dim Summary As WarThunderInstallSummary
            Try
                Dim Results = WarThunderInstallResourceFiles(
                    Files, InstallKind, InstallUseLoops)
                Summary = If(Results.Count = 0,
                    New WarThunderInstallSummary With {
                        .Success = False,
                        .Kind = InstallKind,
                        .Message = "没有找到可安装的文件。"
                    },
                    Results.First())
            Catch ex As Exception
                Summary = New WarThunderInstallSummary With {
                    .Success = False,
                    .Kind = Mode,
                    .Message = ex.GetDisplay(False)
                }
            End Try
            RunInUi(
            Sub()
                IsInstalling = False
                SetSearchControlsEnabled(True)
                PanProjects.IsEnabled = True
                LabSearchHelp.Text = Summary.Message
                Hint(Summary.Message, If(Summary.Success, HintType.Green, HintType.Red))
                If Not Summary.Success Then
                    MyMsgBox("安装战争雷霆资源失败：" & vbCrLf & Summary.Message,
                             "安装失败", IsWarn:=True)
                End If
            End Sub)
        End Sub, "War Thunder Local Resource Install", ThreadPriority.AboveNormal)
    End Sub

    Private Sub BtnSearch_Click(sender As Object, e As RoutedEventArgs) Handles BtnSearch.Click
        PageIndex = 0
        StartCatalogSearch()
    End Sub

    Private Sub BtnReset_Click(sender As Object, e As RoutedEventArgs) Handles BtnReset.Click
        TextSearchTags.Text = ""
        ComboSort.SelectedIndex = 0
        ComboPeriod.SelectedIndex = 0
        PageIndex = 0
        StartCatalogSearch()
    End Sub

    Private Sub TextSearchTags_KeyDown(sender As Object, e As KeyEventArgs) _
        Handles TextSearchTags.KeyDown
        If e.Key <> Key.Enter Then Return
        e.Handled = True
        PageIndex = 0
        StartCatalogSearch()
    End Sub

    Private Sub BtnPageFirst_Click(sender As Object, e As RoutedEventArgs) Handles BtnPageFirst.Click
        ChangePage(0)
    End Sub

    Private Sub BtnPageLeft_Click(sender As Object, e As RoutedEventArgs) Handles BtnPageLeft.Click
        ChangePage(PageIndex - 1)
    End Sub

    Private Sub BtnPageRight_Click(sender As Object, e As RoutedEventArgs) Handles BtnPageRight.Click
        If HasMore Then ChangePage(PageIndex + 1)
    End Sub

    Private Sub ChangePage(NewPage As Integer)
        If NewPage < 0 OrElse NewPage = PageIndex Then Return
        PageIndex = NewPage
        FrmMain.BackToTop()
        StartCatalogSearch()
    End Sub

    Private Sub BtnOpenResourceFolder_Click(sender As Object, e As RoutedEventArgs) _
        Handles BtnOpenResourceFolder.Click
        Dim Folder As String = WarThunderGetResourceRoot(Mode)
        If Folder = "" Then
            Hint("尚未找到对应的安装目录。", HintType.Red)
            Return
        End If
        Directory.CreateDirectory(Folder)
        OpenExplorer(Folder & "\")
    End Sub

    Private Sub SetLoading(Value As Boolean)
        PanLoad.Visibility = If(Value, Visibility.Visible, Visibility.Collapsed)
        PanContent.Visibility = If(Value, Visibility.Collapsed, Visibility.Visible)
        Load.State.LoadingState =
            If(Value, MyLoading.MyLoadingState.Run, MyLoading.MyLoadingState.Stop)
    End Sub

    Private Sub SetSearchControlsEnabled(Value As Boolean)
        BtnSearch.IsEnabled = Value
        BtnReset.IsEnabled = Value
        BtnChooseResource.IsEnabled = Value
        BtnOpenResourceFolder.IsEnabled = Value
        TextSearchTags.IsEnabled = Value
        ComboSort.IsEnabled = Value
        ComboPeriod.IsEnabled = Value
    End Sub

    Private Shared Function GetComboTag(Combo As ComboBox, DefaultValue As String) As String
        Dim Item = TryCast(Combo.SelectedItem, MyComboBoxItem)
        Return If(Item?.Tag?.ToString(), DefaultValue)
    End Function

    Private Shared Function WarThunderGetResourceRoot(
        Kind As WarThunderResourceKind) As String
        Select Case Kind
            Case WarThunderResourceKind.Sights
                Return WarThunderGetUserSightsFolder()
            Case WarThunderResourceKind.Camouflage
                Return WarThunderGetUserSkinsFolder()
            Case Else
                Return WarThunderGetSoundModFolder()
        End Select
    End Function

    Private Shared Function WarThunderGetWtLiveContent(
        Kind As WarThunderResourceKind) As String
        Select Case Kind
            Case WarThunderResourceKind.Sights
                Return "sight"
            Case WarThunderResourceKind.Camouflage
                Return "camouflage"
            Case Else
                Return "sound"
        End Select
    End Function

    Public Sub Refresh() Implements IRefreshable.Refresh
        StartCatalogSearch()
    End Sub

End Class
