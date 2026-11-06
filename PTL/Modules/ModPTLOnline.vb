Imports Newtonsoft.Json.Linq
Imports System.Security.Cryptography
Imports System.Text

Public Class PtlUpdateInfo
    Public Property CurrentVersion As String = ""
    Public Property LatestVersion As String = ""
    Public Property UpdateAvailable As Boolean
    Public Property Changelog As String = ""
    Public Property DownloadUrl As String = ""
    Public Property Filename As String = ""
    Public Property Size As Long
    Public Property Sha256 As String = ""
    Public Property PublishedAt As String = ""
End Class

''' <summary>
''' PTL 官方服务端的启动器更新与公告。
''' </summary>
Public Module ModPtlOnline

    Private Const OnlineRegistryKey As String = "HKEY_CURRENT_USER\Software\PTL\Online"
    Private Const UpdatePromptValue As String = "LastPromptedUpdateVersion"
    Private Const AnnouncementValue As String = "LastAnnouncementKey"
    Private Const DeviceIdValue As String = "DeviceId"
    Private ReadOnly UpdateLock As New Object()
    Private ReadOnly UpdateDownloadLock As New Object()
    Private ReadOnly AnnouncementLock As New Object()
    Private ReadOnly UsageReportLock As New Object()
    Private IsUpdateChecking As Boolean
    Private IsUpdateDownloading As Boolean
    Private IsAnnouncementChecking As Boolean
    Private IsUsageReporting As Boolean
    Private LastUpdateStatusText As String = $"当前版本 {PtlVersion}"
    Private PendingUpdatePath As String = ""
    Private PendingUpdateInfo As PtlUpdateInfo = Nothing

    Private ReadOnly Property PendingUpdateNoticePath As String
        Get
            Return IO.Path.Combine(
                Paths.AppDataThenName, "PendingUpdateNotice.json")
        End Get
    End Property

    Public Function PtlGetVersionStatusText() As String
        Return LastUpdateStatusText
    End Function

    Public Sub PtlStartupOnlineChecks()
        PtlShowPendingUpdateNotice()
        PtlReportUsageAsync()
        PtlCheckAnnouncementAsync()
        If PtlGetUpdateMode() <= 1 Then
            PtlCheckLauncherUpdate(False, True)
        End If
    End Sub

    ''' <summary>
    ''' 0：有新版本时自动下载；1：有新版本时显示提示；2：关闭更新提示。
    ''' </summary>
    Public Function PtlGetUpdateMode() As Integer
        Dim Value As Integer = Settings.Get(Of Integer)("SystemSystemUpdate")
        If Value < 0 OrElse Value > 2 Then Return 2
        Return Value
    End Function

    ''' <summary>
    ''' 以随机设备 ID 上报一次 PTL 启动使用量。
    ''' </summary>
    Public Sub PtlReportUsageAsync()
        If BuildType = BuildTypes.Debug Then Return
        If Not Settings.Get(Of Boolean)("SystemSystemTelemetry") Then Return
        SyncLock UsageReportLock
            If IsUsageReporting Then Return
            IsUsageReporting = True
        End SyncLock

        RunInNewThread(
        Sub()
            Try
                Dim DeviceId As String = PtlGetOrCreateDeviceId()
                PtlPostUsageReport(DeviceId)
                Logger.Info($"PTL 使用统计上报成功：{DeviceId}")
            Catch ex As Exception
                Logger.Info($"PTL 使用统计上报失败：{ex.Message}")
            Finally
                SyncLock UsageReportLock
                    IsUsageReporting = False
                End SyncLock
            End Try
        End Sub, "PTL Usage Report", ThreadPriority.Lowest)
    End Sub

    Private Function PtlGetOrCreateDeviceId() As String
        Dim DeviceId As String = RegistryUtils.TryRead(
            OnlineRegistryKey, DeviceIdValue, "").ToString().Trim()
        Dim IsValid As Boolean =
            DeviceId.Length >= 16 AndAlso DeviceId.Length <= 64 AndAlso
            DeviceId.All(Function(CharacterValue)
                             Return Char.IsLetterOrDigit(CharacterValue) OrElse
                                 CharacterValue = "-"c
                         End Function)
        If IsValid Then Return DeviceId

        DeviceId = Guid.NewGuid().ToString("N")
        RegistryUtils.Write(OnlineRegistryKey, DeviceIdValue, DeviceId)
        Logger.Info($"已生成 PTL 随机设备 ID：{DeviceId}")
        Return DeviceId
    End Function

    Private Sub PtlPostUsageReport(DeviceId As String)
        Dim Url As String = ServiceBaseUrl & "/api/apps/pcl-wt/report"
        Dim Payload As String = New JObject From {
            New JProperty("deviceId", DeviceId)
        }.ToString(Newtonsoft.Json.Formatting.None)
        Try
            PtlPostUsageReportCore(Url, Payload, False, 8)
        Catch DirectError As Exception
            Logger.Info($"PTL 统计上报直连失败，尝试系统代理：{DirectError.Message}")
            PtlPostUsageReportCore(Url, Payload, True, 12)
        End Try
    End Sub

    Private Sub PtlPostUsageReportCore(
        Url As String, Payload As String, UseProxy As Boolean, TimeoutSeconds As Integer)
        Using Handler As New HttpClientHandler()
            Handler.UseProxy = UseProxy
            Using Client As New HttpClient(Handler)
                Client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
                Client.DefaultRequestHeaders.UserAgent.ParseAdd("PTL/" & PtlVersion)
                Client.DefaultRequestHeaders.Accept.ParseAdd("application/json")
                Using Content As New StringContent(
                    Payload, Encoding.UTF8, "application/json")
                    Using Response As HttpResponseMessage = Client.PostAsync(
                        Url, Content).GetAwaiter().GetResult()
                        If Response.IsSuccessStatusCode Then Return
                        Dim ResponseText As String =
                            Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        Throw New InvalidOperationException(
                            $"统计接口返回 HTTP {CInt(Response.StatusCode)}：{ResponseText}")
                    End Using
                End Using
            End Using
        End Using
    End Sub

    Public Sub PtlCheckLauncherUpdate(Optional ShowNoUpdate As Boolean = False,
                                       Optional Automatic As Boolean = False)
        SyncLock UpdateLock
            If IsUpdateChecking Then
                If ShowNoUpdate Then Hint("正在检查 PTL 更新，请稍候。")
                Return
            End If
            IsUpdateChecking = True
        End SyncLock

        RunInUi(
        Sub()
            If FrmSetupSystem IsNot Nothing Then FrmSetupSystem.BtnSystemUpdate.IsEnabled = False
        End Sub)

        RunInNewThread(
        Sub()
            Try
                Dim UpdateInfo As PtlUpdateInfo = PtlFetchUpdateInfo()
                LastUpdateStatusText =
                    $"当前版本 {UpdateInfo.CurrentVersion} " &
                    If(UpdateInfo.UpdateAvailable,
                       $"发现新版本 {UpdateInfo.LatestVersion}",
                       "已是最新版本")
                RunInUi(
                Sub()
                    If FrmSetupSystem IsNot Nothing Then
                        FrmSetupSystem.LabLauncherVersion.Text = LastUpdateStatusText
                    End If
                End Sub)
                Logger.Info(
                    $"PTL 更新检查：本地 {UpdateInfo.CurrentVersion}，最新 {UpdateInfo.LatestVersion}，" &
                    $"需要更新={UpdateInfo.UpdateAvailable}")

                If UpdateInfo.UpdateAvailable Then
                    Dim UpdateMode As Integer = PtlGetUpdateMode()
                    If Automatic AndAlso UpdateMode = 0 Then
                        RunInUi(Sub() PtlStartUpdateDownload(UpdateInfo, False))
                    Else
                        Dim LastPrompted As String = RegistryUtils.TryRead(
                            OnlineRegistryKey, UpdatePromptValue, "").ToString()
                        Dim ShouldPrompt As Boolean =
                            Not Automatic OrElse UpdateMode = 1
                        If ShouldPrompt AndAlso
                           (Not Automatic OrElse
                            Not LastPrompted.Equals(
                                UpdateInfo.LatestVersion, StringComparison.OrdinalIgnoreCase)) Then
                            RegistryUtils.Write(
                                OnlineRegistryKey, UpdatePromptValue, UpdateInfo.LatestVersion)
                            RunInUi(Sub() PtlShowUpdate(UpdateInfo))
                        End If
                    End If
                ElseIf ShowNoUpdate Then
                    Hint($"PTL {UpdateInfo.CurrentVersion} 已是最新版本。", HintType.Green)
                End If
            Catch ex As Exception
                Logger.Warn(ex, "检查 PTL 更新失败")
                If ShowNoUpdate Then
                    MyMsgBox("无法检查 PTL 更新。" & vbCrLf & ex.GetDisplay(False),
                             "更新检查失败", IsWarn:=True)
                End If
            Finally
                SyncLock UpdateLock
                    IsUpdateChecking = False
                End SyncLock
                RunInUi(
                Sub()
                    If FrmSetupSystem IsNot Nothing Then FrmSetupSystem.BtnSystemUpdate.IsEnabled = True
                End Sub)
            End Try
        End Sub, "PTL Update Check", ThreadPriority.BelowNormal)
    End Sub

    Private Function PtlFetchUpdateInfo() As PtlUpdateInfo
        Dim Data As JObject = PtlFetchJson(
            ServiceBaseUrl & "/api/apps/pcl-wt/update?current_version=" &
            Uri.EscapeDataString(PtlVersion))
        Return New PtlUpdateInfo With {
            .CurrentVersion = If(Data.Value(Of String)("current_version"), PtlVersion),
            .LatestVersion = Data.Value(Of String)("latest_version"),
            .UpdateAvailable = Data.Value(Of Boolean?)("update_available").GetValueOrDefault(False),
            .Changelog = If(Data.Value(Of String)("changelog"), ""),
            .DownloadUrl = If(Data.Value(Of String)("download_url"), ""),
            .Filename = If(Data.Value(Of String)("filename"), ""),
            .Size = Data.Value(Of Long?)("size").GetValueOrDefault(0),
            .Sha256 = If(Data.Value(Of String)("sha256"), ""),
            .PublishedAt = If(Data.Value(Of String)("published_at"), "")
        }
    End Function

    Private Sub PtlShowUpdate(UpdateInfo As PtlUpdateInfo)
        Dim Body As New StringBuilder()
        Body.AppendLine($"当前版本：{UpdateInfo.CurrentVersion}")
        Body.AppendLine($"最新版本：{UpdateInfo.LatestVersion}")
        If Not String.IsNullOrWhiteSpace(UpdateInfo.PublishedAt) Then
            Body.AppendLine($"发布时间：{UpdateInfo.PublishedAt}")
        End If
        If Not String.IsNullOrWhiteSpace(UpdateInfo.Changelog) Then
            Body.AppendLine()
            Body.AppendLine(If(UpdateInfo.Changelog.Length > 3000,
                               UpdateInfo.Changelog.Substring(0, 3000) & vbCrLf & "……",
                               UpdateInfo.Changelog))
        End If
        Body.AppendLine()
        Body.Append("是否现在下载新版本？")

        If MyMsgBox(
            Body.ToString(),
            "发现 PTL 新版本",
            "确定",
            ForceWait:=True) = 1 Then
            PtlStartUpdateDownload(UpdateInfo, True)
        End If
    End Sub

    Private Sub PtlStartUpdateDownload(
        UpdateInfo As PtlUpdateInfo,
        ShowTaskManager As Boolean)
        If UpdateInfo Is Nothing OrElse String.IsNullOrWhiteSpace(UpdateInfo.DownloadUrl) Then
            MyMsgBox("更新信息中没有可用的下载地址。", "更新失败", IsWarn:=True)
            Return
        End If
        If Not UpdateInfo.DownloadUrl.StartsWith(
            "https://", StringComparison.OrdinalIgnoreCase) Then
            MyMsgBox("更新下载地址必须使用 HTTPS。", "更新失败", IsWarn:=True)
            Return
        End If

        Dim SafeVersion As String = String.Join("",
            UpdateInfo.LatestVersion.Where(Function(CharacterValue)
                                               Return Char.IsLetterOrDigit(CharacterValue) OrElse
                                                   CharacterValue = "."c OrElse
                                                   CharacterValue = "-"c
                                           End Function))
        If SafeVersion = "" Then SafeVersion = "latest"
        Dim UpdateFolder As String = IO.Path.Combine(PathTemp, "PTL", "Updates")
        Dim LocalPath As String = IO.Path.Combine(
            UpdateFolder, $"PTL {SafeVersion}.exe")
        Dim ExistingError As String = ""
        If IO.File.Exists(LocalPath) AndAlso
           PtlValidateDownloadedUpdate(UpdateInfo, LocalPath, ExistingError) Then
            PendingUpdatePath = LocalPath
            PendingUpdateInfo = UpdateInfo
            IsUpdateWaitingRestart = True
            PtlPromptApplyUpdate()
            Return
        End If

        SyncLock UpdateDownloadLock
            If IsUpdateDownloading Then
                Hint("PTL 更新正在下载，请稍候。", HintType.Blue)
                Return
            End If
            IsUpdateDownloading = True
        End SyncLock

        IO.Directory.CreateDirectory(UpdateFolder)
        Dim DownloadTask As New LoaderTask(Of Integer, String)(
            "下载 PTL " & UpdateInfo.LatestVersion,
            Sub(Loader)
                PtlDownloadUpdateFile(Loader, UpdateInfo, LocalPath)
                Loader.Output = LocalPath
            End Sub) With {
            .ProgressWeight = 10,
            .Block = True
        }
        Dim DownloadCombo As New LoaderCombo(Of Integer)(
            "下载 PTL " & UpdateInfo.LatestVersion,
            {DownloadTask}) With {
            .ProgressWeight = 10,
            .Block = True
        }
        DownloadCombo.OnStateChanged =
        Sub(Loader)
            Select Case Loader.State
                Case LoadState.Finished, LoadState.Failed, LoadState.Canceled
                    SyncLock UpdateDownloadLock
                        IsUpdateDownloading = False
                    End SyncLock
                    RunInUi(
                    Sub()
                        If Loader.State = LoadState.Finished AndAlso
                           IO.File.Exists(LocalPath) Then
                            PendingUpdatePath = LocalPath
                            PendingUpdateInfo = UpdateInfo
                            IsUpdateWaitingRestart = True
                            LastUpdateStatusText =
                                $"版本 {UpdateInfo.LatestVersion} 已下载，等待重启"
                            If FrmSetupSystem IsNot Nothing Then
                                FrmSetupSystem.LabLauncherVersion.Text =
                                    LastUpdateStatusText
                            End If
                            PtlPromptApplyUpdate()
                        ElseIf Loader.State = LoadState.Failed Then
                            Dim MessageText As String =
                                If(Loader.Error, New Exception("下载失败。")).GetDisplay(False)
                            Logger.Warn(Loader.Error,
                                $"下载 PTL {UpdateInfo.LatestVersion} 失败")
                            Hint("PTL 更新下载失败：" & MessageText,
                                 HintType.Red)
                        Else
                            Hint("PTL 更新下载已取消。", HintType.Blue)
                        End If
                    End Sub)
            End Select
        End Sub
        DownloadCombo.Start(0)
        LoaderTaskbarAdd(DownloadCombo)
        FrmMain.BtnExtraDownload.ShowRefresh()
        If ShowTaskManager Then FrmMain.PageChange(FormMain.PageType.TaskManager)
    End Sub

    Private Sub PtlDownloadUpdateFile(
        Loader As LoaderTask(Of Integer, String),
        UpdateInfo As PtlUpdateInfo,
        LocalPath As String)
        Dim PartPath As String = LocalPath & ".part"
        Dim ErrorMessage As String = ""
        For AttemptIndex As Integer = 0 To 1
            Try
                Loader.Progress = 0
                PtlDownloadUpdateFileCore(
                    Loader, UpdateInfo, PartPath, AttemptIndex = 1)
                If Not PtlValidateDownloadedUpdate(
                    UpdateInfo, PartPath, ErrorMessage) Then
                    Throw New InvalidDataException(ErrorMessage)
                End If
                If IO.File.Exists(LocalPath) Then IO.File.Delete(LocalPath)
                IO.File.Move(PartPath, LocalPath)
                Loader.Progress = 1
                Logger.Info(
                    $"PTL {UpdateInfo.LatestVersion} 更新下载并校验完成：{LocalPath}")
                Return
            Catch ex As Exception
                Loader.CreateCancellationToken.ThrowIfCancellationRequested()
                Try
                    If IO.File.Exists(PartPath) Then IO.File.Delete(PartPath)
                Catch
                End Try
                If AttemptIndex = 0 Then
                    Logger.Info(
                        $"PTL 更新直连下载失败，尝试系统代理：{ex.Message}")
                    Continue For
                End If
                Throw
            End Try
        Next
    End Sub

    Private Sub PtlDownloadUpdateFileCore(
        Loader As LoaderTask(Of Integer, String),
        UpdateInfo As PtlUpdateInfo,
        PartPath As String,
        UseProxy As Boolean)
        Using Handler As New HttpClientHandler()
            Handler.UseProxy = UseProxy
            Handler.AutomaticDecompression =
                Net.DecompressionMethods.GZip Or Net.DecompressionMethods.Deflate
            Using Client As New HttpClient(Handler)
                Client.Timeout = TimeSpan.FromMinutes(5)
                Client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "PTL/" & PtlVersion)
                Using Response As HttpResponseMessage = Client.GetAsync(
                    UpdateInfo.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()
                    Response.EnsureSuccessStatusCode()
                    Dim TotalBytes As Long =
                        Response.Content.Headers.ContentLength.GetValueOrDefault(
                            UpdateInfo.Size)
                    Using Input = Response.Content.ReadAsStreamAsync().
                        GetAwaiter().GetResult()
                        Using Output As New IO.FileStream(
                            PartPath, IO.FileMode.Create, IO.FileAccess.Write,
                            IO.FileShare.None)
                            Dim Buffer(81919) As Byte
                            Dim TotalRead As Long = 0
                            Dim CancellationTokenValue =
                                Loader.CreateCancellationToken
                            Do
                                CancellationTokenValue.ThrowIfCancellationRequested()
                                Dim ReadCount As Integer =
                                    Input.Read(Buffer, 0, Buffer.Length)
                                If ReadCount <= 0 Then Exit Do
                                Output.Write(Buffer, 0, ReadCount)
                                TotalRead += ReadCount
                                If TotalBytes > 0 Then
                                    Loader.Progress =
                                        Math.Min(0.99, TotalRead / TotalBytes)
                                End If
                            Loop
                        End Using
                    End Using
                End Using
            End Using
        End Using
    End Sub

    Private Function PtlValidateDownloadedUpdate(
        UpdateInfo As PtlUpdateInfo,
        FilePath As String,
        ByRef ErrorMessage As String) As Boolean
        ErrorMessage = ""
        Try
            Dim Info As New IO.FileInfo(FilePath)
            If Not Info.Exists OrElse Info.Length < 1024 Then
                ErrorMessage = "下载的更新文件无效。"
                Return False
            End If
            If UpdateInfo.Size > 0 AndAlso Info.Length <> UpdateInfo.Size Then
                ErrorMessage = "下载文件大小与更新信息不一致。"
                Return False
            End If
            If Not String.IsNullOrWhiteSpace(UpdateInfo.Sha256) Then
                Dim ActualHash As String = CryptographyUtils.ComputeFileHash(
                    FilePath, CryptographyUtils.HashMethod.Sha256)
                If Not ActualHash.Equals(
                    UpdateInfo.Sha256, StringComparison.OrdinalIgnoreCase) Then
                    ErrorMessage = "下载文件 SHA-256 校验失败。"
                    Return False
                End If
            End If
            Using Stream As New IO.FileStream(
                FilePath, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read)
                If Stream.ReadByte() <> &H4D OrElse Stream.ReadByte() <> &H5A Then
                    ErrorMessage = "下载内容不是有效的 Windows 可执行文件。"
                    Return False
                End If
            End Using
            Return True
        Catch ex As Exception
            ErrorMessage = ex.GetDisplay(False)
            Return False
        End Try
    End Function

    Public Sub PtlPromptApplyDownloadedUpdate()
        If PendingUpdateInfo Is Nothing OrElse
           String.IsNullOrWhiteSpace(PendingUpdatePath) OrElse
           Not IO.File.Exists(PendingUpdatePath) Then
            Return
        End If
        RunInUi(AddressOf PtlPromptApplyUpdate)
    End Sub

    Private Sub PtlPromptApplyUpdate()
        If PendingUpdateInfo Is Nothing OrElse
           String.IsNullOrWhiteSpace(PendingUpdatePath) OrElse
           Not IO.File.Exists(PendingUpdatePath) Then
            Return
        End If
        Dim Body As String =
            $"PTL更新已准备好"
        If MyMsgBox(
            Body,
            "更新已准备好",
            "确定",
            ForceWait:=True) <> 1 Then Return
        PtlLaunchUpdateHelper()
    End Sub

    Private Sub PtlLaunchUpdateHelper()
        Try
            Dim HelperFolder As String = IO.Path.Combine(
                PathTemp, "PTL", "Updates", "Helper")
            IO.Directory.CreateDirectory(HelperFolder)
            Dim HelperPath As String = IO.Path.Combine(
                HelperFolder, $"PTL Update {Guid.NewGuid():N}.cmd")
            Dim LogPath As String = IO.Path.ChangeExtension(HelperPath, ".log")
            Dim CurrentPid As Integer = Process.GetCurrentProcess().Id
            PtlWritePendingUpdateNotice(PendingUpdateInfo)
            Dim Script As String =
                "@echo off" & vbCrLf &
                "setlocal EnableExtensions EnableDelayedExpansion" & vbCrLf &
                $"set ""UPDATER_PID={CurrentPid}""" & vbCrLf &
                $"set ""UPDATER_SOURCE={PtlEscapeBatchValue(PendingUpdatePath)}""" & vbCrLf &
                $"set ""UPDATER_TARGET={PtlEscapeBatchValue(PathExe)}""" & vbCrLf &
                $"set ""UPDATER_LOG={PtlEscapeBatchValue(LogPath)}""" & vbCrLf &
                "set /a WAIT_COUNT=0" & vbCrLf &
                ":wait_old" & vbCrLf &
                "%SystemRoot%\System32\tasklist.exe /FI ""PID eq %UPDATER_PID%"" /NH 2>nul | " &
                "%SystemRoot%\System32\findstr.exe /C:""%UPDATER_PID%"" >nul" & vbCrLf &
                "if errorlevel 1 goto copy_update" & vbCrLf &
                "set /a WAIT_COUNT+=1" & vbCrLf &
                "if !WAIT_COUNT! GEQ 120 goto copy_update" & vbCrLf &
                "%SystemRoot%\System32\ping.exe 127.0.0.1 -n 2 >nul" & vbCrLf &
                "goto wait_old" & vbCrLf &
                ":copy_update" & vbCrLf &
                "set /a COPY_COUNT=0" & vbCrLf &
                ":copy_retry" & vbCrLf &
                "copy /Y ""%UPDATER_SOURCE%"" ""%UPDATER_TARGET%"" >nul 2>&1" & vbCrLf &
                "if not errorlevel 1 goto launch_new" & vbCrLf &
                "set /a COPY_COUNT+=1" & vbCrLf &
                "if !COPY_COUNT! GEQ 120 exit /b 1" & vbCrLf &
                "%SystemRoot%\System32\ping.exe 127.0.0.1 -n 2 >nul" & vbCrLf &
                "goto copy_retry" & vbCrLf &
                ":launch_new" & vbCrLf &
                "%SystemRoot%\System32\ping.exe 127.0.0.1 -n 3 >nul" & vbCrLf &
                "start """" ""%UPDATER_TARGET%""" & vbCrLf &
                "exit /b 0" & vbCrLf
            IO.File.WriteAllText(HelperPath, Script, Text.Encoding.Default)
            Dim ComSpecPath As String =
                Environment.GetEnvironmentVariable("ComSpec")
            If String.IsNullOrWhiteSpace(ComSpecPath) Then ComSpecPath = "cmd.exe"
            Using HelperProcess As Process = Process.Start(New ProcessStartInfo With {
                .FileName = ComSpecPath,
                .Arguments = "/d /c " & PtlQuoteArgument(HelperPath),
                .WorkingDirectory = Paths.Base,
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .WindowStyle = ProcessWindowStyle.Hidden
            })
                If HelperProcess Is Nothing Then
                    Throw New InvalidOperationException("更新助手未能启动。")
                End If
            End Using
            IsUpdateWaitingRestart = False
            PendingUpdatePath = ""
            PendingUpdateInfo = Nothing
            Logger.Info($"已启动 PTL 更新助手：{HelperPath}")
            FrmMain.EndProgram(False)
        Catch ex As Exception
            Logger.Error(ex, "启动 PTL 更新助手失败")
            MyMsgBox("无法启动更新助手：" & vbCrLf & ex.GetDisplay(False),
                     "更新失败", IsWarn:=True)
        End Try
    End Sub

    Private Function PtlQuoteArgument(Value As String) As String
        Return """" & If(Value, "").Replace("""", "\""") & """"
    End Function

    Private Function PtlEscapeBatchValue(Value As String) As String
        Return If(Value, "").Replace("%", "%%")
    End Function

    Private Sub PtlWritePendingUpdateNotice(UpdateInfo As PtlUpdateInfo)
        If UpdateInfo Is Nothing Then Return
        Dim Notice As New JObject From {
            New JProperty("target_version", UpdateInfo.LatestVersion),
            New JProperty("changelog", If(UpdateInfo.Changelog, "")),
            New JProperty("published_at", If(UpdateInfo.PublishedAt, "")),
            New JProperty("saved_at_utc", Date.UtcNow.ToString("O"))
        }
        Dim Folder As String = IO.Path.GetDirectoryName(PendingUpdateNoticePath)
        IO.Directory.CreateDirectory(Folder)
        Dim TempPath As String = PendingUpdateNoticePath & ".tmp"
        IO.File.WriteAllText(
            TempPath,
            Notice.ToString(Newtonsoft.Json.Formatting.None),
            New UTF8Encoding(False))
        If IO.File.Exists(PendingUpdateNoticePath) Then
            IO.File.Replace(
                TempPath, PendingUpdateNoticePath, Nothing, True)
        Else
            IO.File.Move(TempPath, PendingUpdateNoticePath)
        End If
    End Sub

    Private Sub PtlShowPendingUpdateNotice()
        If Not IO.File.Exists(PendingUpdateNoticePath) Then Return
        Try
            Dim Notice As JObject = JObject.Parse(
                IO.File.ReadAllText(PendingUpdateNoticePath))
            Dim TargetVersion As String =
                If(Notice.Value(Of String)("target_version"), "").Trim()
            If Not TargetVersion.Equals(
                PtlVersion, StringComparison.OrdinalIgnoreCase) Then
                Return
            End If
            Dim PublishedAt As String =
                If(Notice.Value(Of String)("published_at"), "").Trim()
            Dim Changelog As String =
                If(Notice.Value(Of String)("changelog"), "").Trim()
            If Changelog = "" Then
                Changelog = "此版本未提供更新日志。"
            End If
            If PublishedAt <> "" Then
                Changelog = $"*发布时间：{PublishedAt}*" & vbCrLf & vbCrLf & Changelog
            End If
            RunInUiWait(
            Sub()
                MyMsgBoxMarkdown(
                    Changelog,
                    $"PTL 已更新至 {TargetVersion}",
                    "确定")
                Try
                    IO.File.Delete(PendingUpdateNoticePath)
                Catch ex As Exception
                    Logger.Warn(ex, "清理已展示的更新日志失败")
                End Try
            End Sub)
        Catch ex As Exception
            Logger.Warn(ex, "读取待展示的 PTL 更新日志失败")
            Try
                IO.File.Delete(PendingUpdateNoticePath)
            Catch
            End Try
        End Try
    End Sub

    Public Sub PtlCheckAnnouncementAsync()
        If Settings.Get(Of Integer)("SystemSystemActivity") = 2 Then Return
        SyncLock AnnouncementLock
            If IsAnnouncementChecking Then Return
            IsAnnouncementChecking = True
        End SyncLock

        RunInNewThread(
        Sub()
            Try
                Dim Data As JObject = PtlFetchJson(ServiceBaseUrl & "/api/apps/pcl-wt/announcement")
                Dim IsActive As Boolean = Data.Value(Of Boolean?)("active").GetValueOrDefault(False)
                Dim Text As String = If(Data.Value(Of String)("text"), "").Trim()
                If Not IsActive OrElse Text = "" Then Return

                Dim ExpiresAt As Date
                Dim ExpiresText As String = Data.Value(Of String)("expires_at")
                If Not String.IsNullOrWhiteSpace(ExpiresText) AndAlso
                   Date.TryParse(ExpiresText, Nothing,
                                 Globalization.DateTimeStyles.AdjustToUniversal,
                                 ExpiresAt) AndAlso
                   ExpiresAt.ToUniversalTime() <= Date.UtcNow Then
                    Return
                End If

                Dim Level As String = If(Data.Value(Of String)("level"), "info").Trim().ToLowerInvariant()
                If Settings.Get(Of Integer)("SystemSystemActivity") = 1 AndAlso
                   Level <> "warning" AndAlso Level <> "error" AndAlso Level <> "critical" Then
                    Return
                End If

                Dim UpdatedAt As String = If(Data.Value(Of String)("updated_at"), "")
                Dim SeenKey As String = PtlComputeHash($"{Level}|{UpdatedAt}|{Text}")
                If RegistryUtils.TryRead(OnlineRegistryKey, AnnouncementValue, "").ToString() = SeenKey Then
                    Return
                End If
                RegistryUtils.Write(OnlineRegistryKey, AnnouncementValue, SeenKey)

                Dim Title As String =
                    If({"warning", "error", "critical"}.Contains(Level),
                       "PTL 重要公告", "PTL 公告")
                Logger.Info($"收到 PTL 公告（{Level}）：{Text}")
                MyMsgBox(Text, Title, "我知道了", IsWarn:={"warning", "error", "critical"}.Contains(Level))
            Catch ex As Exception
                Logger.Warn(ex, "获取 PTL 公告失败")
            Finally
                SyncLock AnnouncementLock
                    IsAnnouncementChecking = False
                End SyncLock
            End Try
        End Sub, "PTL Announcement", ThreadPriority.BelowNormal)
    End Sub

    Private Function PtlFetchJson(Url As String) As JObject
        Try
            Return PtlFetchJsonCore(Url, False, 10)
        Catch DirectError As Exception
            Logger.Info($"PTL 在线服务直连失败，尝试系统代理：{DirectError.Message}")
            Return PtlFetchJsonCore(Url, True, 15)
        End Try
    End Function

    Private Function PtlFetchJsonCore(
        Url As String, UseProxy As Boolean, TimeoutSeconds As Integer) As JObject
        Using Handler As New HttpClientHandler()
            Handler.UseProxy = UseProxy
            Using Client As New HttpClient(Handler)
                Client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
                Client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "PTL/" & PtlVersion)
                Client.DefaultRequestHeaders.Accept.ParseAdd("application/json")
                Using Response As HttpResponseMessage = Client.GetAsync(
                    Url).GetAwaiter().GetResult()
                    Dim Content As String =
                        Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    If Not Response.IsSuccessStatusCode Then
                        Throw New InvalidOperationException(
                            $"在线服务返回 HTTP {CInt(Response.StatusCode)}：{Content}")
                    End If
                    Return JObject.Parse(Content)
                End Using
            End Using
        End Using
    End Function

    Private Function PtlComputeHash(Value As String) As String
        Using Hasher = SHA256.Create()
            Return Convert.ToBase64String(Hasher.ComputeHash(Encoding.UTF8.GetBytes(Value)))
        End Using
    End Function

End Module
