Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Collections.Concurrent
Imports System.Threading.Tasks
Imports MonoTorrent
Imports MonoTorrent.BEncoding
Imports MonoTorrent.Client
Imports Newtonsoft.Json.Linq

Friend NotInheritable Class WarThunderSelfUpdateResult
    Public Property Success As Boolean = False
    Public Property Version As String = ""
    Public Property Message As String = ""
    Public Property UpdatedFiles As Integer = 0
    Public Property DownloadedBytes As Long = 0
End Class

Friend NotInheritable Class WarThunderSelfUpdatePlan
    Public Property RequiredFiles As New Dictionary(Of String, WarThunderYupFile)(StringComparer.OrdinalIgnoreCase)

    Public ReadOnly Property TotalBytes As Long
        Get
            Return RequiredFiles.Values.Sum(Function(Entry) Entry.Length)
        End Get
    End Property
End Class

Friend NotInheritable Class WarThunderTargetManifest
    Public Property FilePath As String = ""
    Public Property Snapshot As WarThunderYupSnapshot = Nothing
End Class

Friend NotInheritable Class WarThunderHttpFileTask
    Public Property RelativePath As String = ""
    Public Property FinalPath As String = ""
    Public Property TempPath As String = ""
    Public Property Entry As WarThunderYupFile = Nothing
    Public Property Sources As New List(Of String)
    Public Property Chunks As New List(Of WarThunderHttpChunk)
    Public Property RemainingChunks As Integer
    Public Property IsWholeFile As Boolean
End Class

Friend NotInheritable Class WarThunderHttpChunk
    Public Property FileTask As WarThunderHttpFileTask = Nothing
    Public Property Index As Integer
    Public Property StartByte As Long
    Public Property EndByte As Long
    Public Property PartPath As String = ""

    Public ReadOnly Property Length As Long
        Get
            Return Math.Max(0, EndByte - StartByte + 1)
        End Get
    End Property
End Class

Friend NotInheritable Class WarThunderHttpDownloadContext
    Public BytesRead As Long
    Public CompletedFiles As Integer
    Public ReadOnly Property SuccessfulPaths As New ConcurrentBag(Of String)
    Public ReadOnly Property Cancellation As New Threading.CancellationTokenSource()
End Class

''' <summary>
''' PTL 内置的战争雷霆 YUP/P2P 更新实现。
''' </summary>
Friend Module ModWarThunderSelfUpdate

    Private Const YupMasterUrl As String = "https://yupmaster.gaijinent.com/yuitem/get_yup.php"
    Private Const CdnConfigUrl As String = "https://yupmaster.gaijinent.com/launcher/cdn_conf.php"
    Private Const DefaultTrackerUrl As String = "udp://yuptracker.gaijinent.com:27022/announce"
    Private Const BackupTrackerUrl As String = "udp://yuptracker-sa.gaijinent.com:27029/announce"
    Private Const CdnThreadLimit As Integer = 128
    Private Const CdnPrimarySeedLimit As Integer = 6
    Private Const HttpWorkerLimit As Integer = 48
    Private Const HttpMaxChunksPerLargeFile As Integer = 16
    Private Const HttpLargeFileThreshold As Long = 16L * 1024 * 1024
    Private Const HttpChunkReadBuffer As Integer = 256 * 1024
    Private Const HttpChunkIdleTimeoutSeconds As Integer = 45
    Private Const HttpSlowSourceWindowSeconds As Integer = 15
    Private Const HttpSlowSourceMinimumBytesPerSecond As Long = 64L * 1024
    Private WarThunderSelfUpdateDownloadRoot As String = ""
    Private WarThunderLastStateUiRefresh As Date = Date.MinValue

    Public Function WarThunderRunSelfUpdate(
        Install As WarThunderInstallation,
        Loader As LoaderTask(Of WarThunderInstallation, Boolean),
        RemoteVersion As String) As WarThunderSelfUpdateResult
        If Install Is Nothing OrElse Not Directory.Exists(Install.InstallPath) Then
            Throw New DirectoryNotFoundException("战争雷霆安装目录不存在。")
        End If
        WarThunderStopOfficialUpdateProcesses(Install.InstallPath, Loader)
        If WarThunderGetGameProcessIds().Any() Then
            Throw New InvalidOperationException("请先关闭战争雷霆，再开始更新。")
        End If

        WarThunderSetSelfUpdateState(Loader, 0.03, "正在获取更新清单", 0, 0, -1)
        Dim Tag As String = WarThunderGetSelfUpdateTag(Install.InstallPath)
        Dim Target = WarThunderDownloadTargetManifest(Install.InstallPath, Tag, RemoteVersion)
        If Target Is Nothing OrElse Target.Snapshot Is Nothing Then
            Throw New InvalidDataException("无法获取或解析战争雷霆更新清单。")
        End If

        Dim Remote As Version = Nothing
        Dim ManifestRemote As Version = Nothing
        If Not Version.TryParse(RemoteVersion, Remote) OrElse
           Not Version.TryParse(Target.Snapshot.Version, ManifestRemote) OrElse
           ManifestRemote < Remote Then
            Throw New InvalidDataException(
                $"更新清单版本异常：接口为 {RemoteVersion}，清单为 {Target.Snapshot.Version}。")
        End If
        Dim EffectiveVersion As String = Target.Snapshot.Version.Trim()

        WarThunderSetSelfUpdateState(Loader, 0.05, "正在检查游戏文件", 0, 0, -1)
        Dim ManifestPath As String = Path.Combine(Install.InstallPath, "warthunder.yup")
        Dim CurrentManifest = WarThunderYupSnapshot.TryRead(ManifestPath)
        If CurrentManifest Is Nothing Then
            Throw New InvalidDataException("无法读取当前战争雷霆的 YUP 清单。")
        End If
        Dim OldManifest As WarThunderYupSnapshot =
            WarThunderSelectSelfUpdateBaseline(
                Install.InstallPath, Target.Snapshot, CurrentManifest)
        Dim Plan As WarThunderSelfUpdatePlan =
            WarThunderCreateSelfUpdatePlan(Install.InstallPath, OldManifest, Target.Snapshot)
        WarThunderEnsureSelfUpdateSpace(Install.InstallPath, Plan)

        Dim DownloadedBytes As Long = 0
        If Plan.RequiredFiles.Count > 0 Then
            WarThunderSetSelfUpdateState(
                Loader, 0.1,
                $"正在准备直接更新（共 {Plan.RequiredFiles.Count} 个文件）",
                0, Plan.TotalBytes, Plan.RequiredFiles.Count)
            DownloadedBytes = WarThunderDownloadSelfUpdateFiles(
                Install, Loader, Target.FilePath, Target.Snapshot, Plan)
        End If

        WarThunderSetSelfUpdateState(Loader, 0.92, "正在安装游戏文件", 0, Plan.TotalBytes, 0)
        Dim InstalledFiles As Integer =
            WarThunderInstallSelfUpdateFiles(Install.InstallPath, Target.Snapshot, Plan, Loader)

        Dim VerificationPlan As WarThunderSelfUpdatePlan =
            WarThunderCreateSelfUpdatePlan(
                Install.InstallPath, OldManifest, Target.Snapshot)
        If VerificationPlan.RequiredFiles.Count > 0 Then
            Dim Samples As String = String.Join(
                "、", VerificationPlan.RequiredFiles.Keys.Take(5))
            Throw New InvalidDataException(
                $"更新完成后仍有 {VerificationPlan.RequiredFiles.Count} 个文件" &
                $"未通过完整性校验：{Samples}")
        End If
        Dim ContentStateError As String = ""
        If Not WarThunderPrepareContentState(
            Install.InstallPath, ContentStateError, True) Then
            Throw New InvalidDataException(
                "无法生成游戏完整性状态：" & ContentStateError)
        End If

        WarThunderSetSelfUpdateState(Loader, 0.98, "正在更新游戏清单", 0, Plan.TotalBytes, 0)
        WarThunderApplySelfUpdateManifest(Install.InstallPath, Target.Snapshot, EffectiveVersion)
        WarThunderCleanupSelfUpdateCache(Target.FilePath)

        Return New WarThunderSelfUpdateResult With {
            .Success = True,
            .Version = EffectiveVersion,
            .Message = $"游戏已更新至 {EffectiveVersion}。",
            .UpdatedFiles = Math.Max(Plan.RequiredFiles.Count, InstalledFiles),
            .DownloadedBytes = DownloadedBytes
        }
    End Function

    Private Sub WarThunderStopOfficialUpdateProcesses(
        InstallPath As String,
        Loader As LoaderBase)
        WarThunderSetSelfUpdateState(
            Loader, 0.01, "正在关闭官方更新组件", 0, 0, -1)
        Dim InstallRoot As String =
            Path.GetFullPath(InstallPath).TrimEnd("\"c)
        Dim CurrentPid As Integer = Process.GetCurrentProcess().Id
        Dim Failures As New List(Of String)
        For Each ProcessName As String In {
            "launcher",
            "gjagent",
            "gaijin_downloader",
            "gaijin_updater",
            "gaijin_updater_agent"
        }
            For Each RunningProcess As Process In
                Process.GetProcessesByName(ProcessName)
                If RunningProcess.Id = CurrentPid Then Continue For
                Try
                    If Not WarThunderShouldStopOfficialProcess(
                        RunningProcess, ProcessName, InstallRoot) Then
                        Continue For
                    End If
                    WarThunderLog(
                        $"正在关闭官方更新进程：{ProcessName}.exe (PID {RunningProcess.Id})")
                    RunningProcess.Kill()
                    If Not RunningProcess.WaitForExit(5000) Then
                        Failures.Add($"{ProcessName}.exe (PID {RunningProcess.Id})")
                    End If
                Catch ex As Exception
                    Logger.Warn(
                        ex,
                        $"关闭官方更新进程失败：{ProcessName}.exe (PID {RunningProcess.Id})")
                    Failures.Add($"{ProcessName}.exe (PID {RunningProcess.Id})")
                Finally
                    RunningProcess.Dispose()
                End Try
            Next
        Next
        If Failures.Count > 0 Then
            Throw New InvalidOperationException(
                "无法关闭官方更新进程，请手动结束以下进程后重试：" &
                String.Join("、", Failures))
        End If
    End Sub

    Private Function WarThunderShouldStopOfficialProcess(
        RunningProcess As Process,
        ProcessName As String,
        InstallRoot As String) As Boolean
        If ProcessName.Equals("launcher", StringComparison.OrdinalIgnoreCase) Then
            Try
                Return Path.GetFullPath(
                    RunningProcess.MainModule.FileName).Equals(
                        Path.Combine(InstallRoot, "launcher.exe"),
                        StringComparison.OrdinalIgnoreCase)
            Catch
                '无法读取路径时仍尝试结束 launcher.exe；若权限不足会明确报错。
                Return True
            End Try
        End If
        If ProcessName.Equals("gjagent", StringComparison.OrdinalIgnoreCase) Then
            Return True
        End If
        Try
            Dim FilePath As String =
                Path.GetFullPath(RunningProcess.MainModule.FileName)
            Return FilePath.StartsWith(
                InstallRoot & "\", StringComparison.OrdinalIgnoreCase) OrElse
                FilePath.IndexOf(
                    "\Gaijin\", StringComparison.OrdinalIgnoreCase) >= 0
        Catch
            Return True
        End Try
    End Function

    Private Function WarThunderGetSelfUpdateTag(InstallPath As String) As String
        Try
            Dim Circuit As String = WarThunderLoadLaunchSettings(InstallPath).ServerCircuit
            If String.IsNullOrWhiteSpace(Circuit) OrElse
               Circuit.Equals("production", StringComparison.OrdinalIgnoreCase) Then
                Return ""
            End If
            Return Circuit.Trim()
        Catch ex As Exception
            Logger.Warn(ex, "读取战争雷霆更新通道失败")
            Return ""
        End Try
    End Function

    Private Function WarThunderGetSelfUpdateWebSeeds(
        Tag As String,
        Version As String) As List(Of Uri)
        Dim Result As New List(Of Uri)
        If String.IsNullOrWhiteSpace(Version) Then Return Result
        Try
            Dim Url As String =
                $"{CdnConfigUrl}?proj=warthunder&tag={Uri.EscapeDataString(If(Tag, ""))}"
            Dim ResponseText As String
            Using Client As New HttpClient()
                Client.Timeout = TimeSpan.FromSeconds(12)
                Client.DefaultRequestHeaders.UserAgent.ParseAdd("PTL/" & PtlVersion)
                ResponseText = Client.GetStringAsync(Url).GetAwaiter().GetResult()
            End Using

            Dim Root As JObject = JObject.Parse(ResponseText)
            Dim SeedArray = TryCast(Root.Item("seeds"), JArray)
            If SeedArray Is Nothing Then Return Result

            Dim Candidates As New List(Of KeyValuePair(Of Integer, Uri))
            For Each RawSeed In SeedArray
                Try
                    Dim Seed = TryCast(RawSeed, JObject)
                    If Seed Is Nothing Then Continue For
                    Dim BaseUrl As String = If(
                        Seed.Item("url")?.ToString(), "").Trim()
                    If BaseUrl = "" Then Continue For
                    Dim Tier As Integer = 0
                    Integer.TryParse(If(Seed.Item("tier")?.ToString(), "0"), Tier)
                    Dim SupportsHttps As Boolean =
                        If(Seed.Item("https")?.ToString(), "0").Trim() = "1"

                    Dim Builder As New UriBuilder(BaseUrl)
                    If SupportsHttps Then
                        Builder.Scheme = Uri.UriSchemeHttps
                        Builder.Port = -1
                    End If
                    Builder.Path =
                        Builder.Path.TrimEnd("/"c) &
                        "/warthunder/" &
                        Uri.EscapeDataString(Version.Trim()) &
                        "/"
                    Candidates.Add(New KeyValuePair(Of Integer, Uri)(Tier, Builder.Uri))
                Catch ex As Exception
                    Logger.Warn(ex, "解析战争雷霆 CDN Web Seed 失败")
                End Try
            Next

            For Each Candidate In Candidates.
                OrderByDescending(Function(Item) Item.Key).
                ThenBy(Function(Item) WarThunderGetCdnSeedPriority(Item.Value)).
                Select(Function(Item) Item.Value).
                GroupBy(Function(Item) Item.AbsoluteUri).
                Select(Function(Group) Group.First()).
                Take(24)
                Result.Add(Candidate)
            Next
        Catch ex As Exception
            Logger.Warn(ex, "获取战争雷霆 CDN Web Seed 失败")
        End Try
        Return Result
    End Function

    Private Function WarThunderGetCdnSeedPriority(Seed As Uri) As Integer
        Dim Host As String = Seed.Host.ToLowerInvariant()
        If Host.Contains("gcore.cdn.gaijin.net") Then Return 0
        If Host.Contains("seed-gc-") Then Return 1
        If Host.Contains("akamai.cdn.gaijin.net") Then Return 1
        If Host.Contains("seed-sv-us-") Then Return 2
        Return 2
    End Function

    Private Function WarThunderSelfUpdateCacheBase(InstallPath As String) As String
        Return Path.Combine(
            Path.GetFullPath(InstallPath).TrimEnd("\"c),
            ".ptl-update")
    End Function

    Private Sub WarThunderEnsureSelfUpdateSpace(
        InstallPath As String,
        Plan As WarThunderSelfUpdatePlan)
        If Plan Is Nothing OrElse Plan.RequiredFiles.Count = 0 Then Return
        Dim DriveRoot As String = Path.GetPathRoot(Path.GetFullPath(InstallPath))
        If String.IsNullOrWhiteSpace(DriveRoot) Then Return
        Dim TargetDrive As New DriveInfo(DriveRoot)
        Dim LargestFile As Long = Plan.RequiredFiles.Values.
            Select(Function(Entry) Entry.Length).
            DefaultIfEmpty(0L).
            Max()
        Dim RequiredBytes As Long =
            Math.Min(Plan.TotalBytes, 4L * 1024 * 1024 * 1024) +
            LargestFile * 2 +
            512L * 1024 * 1024
        Dim ReusableBytes As Long = 0
        Dim CacheBase As String = WarThunderSelfUpdateCacheBase(InstallPath)
        If Directory.Exists(CacheBase) Then
            ReusableBytes = Directory.EnumerateFiles(
                CacheBase, "*", SearchOption.AllDirectories).
                Sum(Function(FilePath) New FileInfo(FilePath).Length)
        End If
        Dim AvailableBytes As Long =
            TargetDrive.AvailableFreeSpace + ReusableBytes
        If AvailableBytes < RequiredBytes Then
            Throw New IOException(
                $"战争雷霆直接更新空间不足。临时下载目录位于 {CacheBase}，" &
                $"预计需要 {StringUtils.FormatByteSize(RequiredBytes)}，" &
                $"当前可用 {StringUtils.FormatByteSize(AvailableBytes)}。")
        End If
    End Sub

    Private Sub WarThunderCleanupSelfUpdateCache(ManifestPath As String)
        Try
            Dim VersionFolder As String = Path.GetDirectoryName(ManifestPath)
            If String.IsNullOrWhiteSpace(VersionFolder) OrElse
               Not Directory.Exists(VersionFolder) Then Return
            Dim ParentFolder As DirectoryInfo = Directory.GetParent(VersionFolder)
            If ParentFolder Is Nothing OrElse
               Not ParentFolder.Name.Equals(
                   ".ptl-update", StringComparison.OrdinalIgnoreCase) Then Return
            Directory.Delete(VersionFolder, True)
        Catch ex As Exception
            Logger.Warn(ex, "清理战争雷霆更新缓存失败")
        End Try
    End Sub

    Private Function WarThunderDownloadTargetManifest(
        InstallPath As String,
        Tag As String,
        RemoteVersion As String) As WarThunderTargetManifest
        Dim SafeVersion As String = If(
            String.IsNullOrWhiteSpace(RemoteVersion), "latest", RemoteVersion.Trim())
        SafeVersion = String.Join("_", SafeVersion.Split(Path.GetInvalidFileNameChars()))
        Dim TargetFolder As String =
            Path.Combine(WarThunderSelfUpdateCacheBase(InstallPath), SafeVersion)
        Directory.CreateDirectory(TargetFolder)
        Dim TargetPath As String = Path.Combine(TargetFolder, "warthunder.yup")
        Dim TempPath As String = TargetPath & ".download"

        Dim Url As String =
            $"{YupMasterUrl}?proj=warthunder&tag={Uri.EscapeDataString(If(Tag, ""))}"
        Using Client As New HttpClient()
            Client.Timeout = TimeSpan.FromMinutes(3)
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("PTL/" & PtlVersion)
            Using Response = Client.GetAsync(
                Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()
                Response.EnsureSuccessStatusCode()
                Using Input = Response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                    Using Output As New FileStream(
                        TempPath, FileMode.Create, FileAccess.Write, FileShare.None)
                        Input.CopyTo(Output)
                    End Using
                End Using
            End Using
        End Using

        Dim Snapshot = WarThunderYupSnapshot.TryRead(TempPath)
        If Snapshot Is Nothing Then
            FileUtils.Delete(TempPath)
            Throw New InvalidDataException("下载到的战争雷霆更新清单无法解析。")
        End If
        FileUtils.Delete(TargetPath)
        File.Move(TempPath, TargetPath)
        Return New WarThunderTargetManifest With {
            .FilePath = TargetPath,
            .Snapshot = Snapshot
        }
    End Function

    Private Function WarThunderSelectSelfUpdateBaseline(
        InstallPath As String,
        TargetManifest As WarThunderYupSnapshot,
        CurrentManifest As WarThunderYupSnapshot) As WarThunderYupSnapshot
        If CurrentManifest Is Nothing Then Return Nothing
        Dim TargetVersion As Version = Nothing
        If TargetManifest Is Nothing OrElse
           Not Version.TryParse(
               If(String.IsNullOrWhiteSpace(TargetManifest.LocalVersion),
                  TargetManifest.Version, TargetManifest.LocalVersion),
               TargetVersion) Then
            Return CurrentManifest
        End If

        Dim BestManifest As WarThunderYupSnapshot = CurrentManifest
        Dim BestVersion As Version = Nothing
        Version.TryParse(
            If(String.IsNullOrWhiteSpace(CurrentManifest.LocalVersion),
               CurrentManifest.Version, CurrentManifest.LocalVersion),
            BestVersion)
        Dim BestWriteTime As Date = Date.MinValue
        Try
            Dim CurrentPath As String =
                Path.Combine(InstallPath, "warthunder.yup")
            If File.Exists(CurrentPath) Then
                BestWriteTime = File.GetLastWriteTimeUtc(CurrentPath)
            End If
        Catch
        End Try

        Try
            For Each CandidatePath As String In Directory.GetFiles(
                InstallPath, "warthunder_old.yup*")
                Dim Candidate As WarThunderYupSnapshot =
                    WarThunderYupSnapshot.TryRead(CandidatePath)
                If Candidate Is Nothing Then Continue For
                Dim CandidateVersion As Version = Nothing
                If Not Version.TryParse(
                    If(String.IsNullOrWhiteSpace(Candidate.LocalVersion),
                       Candidate.Version, Candidate.LocalVersion),
                    CandidateVersion) OrElse
                   CandidateVersion >= TargetVersion Then
                    Continue For
                End If

                Dim CandidateWriteTime As Date =
                    File.GetLastWriteTimeUtc(CandidatePath)
                Dim UseCandidate As Boolean =
                    BestVersion Is Nothing OrElse
                    BestVersion >= TargetVersion OrElse
                    CandidateVersion > BestVersion OrElse
                    (CandidateVersion = BestVersion AndAlso
                     CandidateWriteTime > BestWriteTime)
                If UseCandidate Then
                    BestManifest = Candidate
                    BestVersion = CandidateVersion
                    BestWriteTime = CandidateWriteTime
                End If
            Next
        Catch ex As Exception
            Logger.Warn(ex, "扫描战争雷霆历史 YUP 清单失败")
        End Try

        Dim FinalVersion As String =
            If(String.IsNullOrWhiteSpace(BestManifest.LocalVersion),
               BestManifest.Version, BestManifest.LocalVersion)
        If Not FinalVersion.Equals(
            If(String.IsNullOrWhiteSpace(CurrentManifest.LocalVersion),
               CurrentManifest.Version, CurrentManifest.LocalVersion),
            StringComparison.OrdinalIgnoreCase) Then
            Logger.Info(
                $"战争雷霆使用历史清单作为更新基线：{FinalVersion}")
        End If
        Return BestManifest
    End Function

    Private Function WarThunderCreateSelfUpdatePlan(
        InstallPath As String,
        OldManifest As WarThunderYupSnapshot,
        NewManifest As WarThunderYupSnapshot) As WarThunderSelfUpdatePlan
        Dim Result As New WarThunderSelfUpdatePlan
        For Each Entry In NewManifest.Files.Values
            If Entry.IsPatch Then Continue For
            If Not WarThunderShouldProcessSelfUpdateEntry(Entry.RelativePath) Then
                Continue For
            End If

            Dim OldEntry As WarThunderYupFile = Nothing
            Dim ExistedBefore As Boolean = OldManifest.Files.TryGetValue(Entry.RelativePath, OldEntry)

            Dim LocalPath As String = WarThunderSafeInstallPath(InstallPath, Entry.RelativePath)
            If LocalPath = "" Then Continue For
            Dim LocalExists As Boolean = File.Exists(LocalPath)
            If Not LocalExists AndAlso
               Not WarThunderShouldAddNewSelfUpdateFile(InstallPath, Entry.RelativePath) Then
                Continue For
            End If
            Dim LengthMatches As Boolean = False
            If LocalExists Then
                Try
                    LengthMatches = New FileInfo(LocalPath).Length = Entry.Length
                Catch
                End Try
            End If

            Dim ManifestChanged As Boolean =
                Not ExistedBefore OrElse Not WarThunderSameYupFile(OldEntry, Entry)
            Dim ZipNeedsRepair As Boolean =
                Entry.ZipEntries.Count > 0 AndAlso
                Not WarThunderZipTargetsExist(InstallPath, Entry)
            Dim AlreadyTarget As Boolean =
                WarThunderSelfUpdateLocalFileMatches(LocalPath, Entry)

            Dim NeedsDownload As Boolean
            If ManifestChanged Then
                NeedsDownload =
                    Not WarThunderSelfUpdateLocalFileMatchesSha1(LocalPath, Entry)
            Else
                NeedsDownload =
                    (Not LocalExists OrElse Not LengthMatches) AndAlso
                    Not AlreadyTarget
            End If
            If NeedsDownload OrElse ZipNeedsRepair Then
                Result.RequiredFiles(Entry.RelativePath) = Entry
            End If
        Next
        Return Result
    End Function

    Friend Function WarThunderCheckLocalContent(
        InstallPath As String,
        ByRef Detail As String) As Boolean
        Detail = ""
        Dim ManifestPath As String = Path.Combine(InstallPath, "warthunder.yup")
        Dim Manifest As WarThunderYupSnapshot =
            WarThunderYupSnapshot.TryRead(ManifestPath)
        If Manifest Is Nothing Then
            Detail = "无法读取本地战争雷霆 YUP 清单。"
            Return False
        End If

        Dim Baseline As WarThunderYupSnapshot =
            WarThunderSelectSelfUpdateBaseline(
                InstallPath, Manifest, Manifest)
        Dim Plan As WarThunderSelfUpdatePlan =
            WarThunderCreateSelfUpdatePlan(InstallPath, Baseline, Manifest)
        If Plan.RequiredFiles.Count = 0 Then Return True

        Dim Samples As List(Of String) = Plan.RequiredFiles.Keys.
            OrderBy(Function(PathText) PathText, StringComparer.OrdinalIgnoreCase).
            Take(5).ToList()
        Detail =
            $"检测到 {Plan.RequiredFiles.Count} 个游戏文件缺失、大小或内容不符：" &
            String.Join("、", Samples)
        If Plan.RequiredFiles.Count > Samples.Count Then Detail &= " 等"
        Logger.Info($"战争雷霆本地文件完整性检查未通过：{Detail}")
        Return False
    End Function

    ''' <summary>
    ''' 按官方启动器的规则生成 content/warthunder.blk 的包状态。
    ''' </summary>
    Friend Function WarThunderPrepareContentState(
        InstallPath As String,
        ByRef Detail As String,
        Optional Force As Boolean = False) As Boolean
        Detail = ""
        Try
            Dim InstallRoot As String =
                Path.GetFullPath(InstallPath).TrimEnd("\"c)
            Dim BlkPath As String =
                Path.Combine(InstallRoot, "content\warthunder.blk")
            If Not File.Exists(BlkPath) Then
                Detail = "找不到 content\warthunder.blk。"
                Return False
            End If

            Dim Lines As String() = File.ReadAllLines(BlkPath, Encoding.UTF8)
            If Not Force AndAlso
               Not Lines.Any(Function(LineText)
                                 Return LineText.Contains("status:i=0") OrElse
                                        LineText.Contains("status:i=3")
                             End Function) Then
                Return True
            End If

            Dim Output As New List(Of String)(Lines.Length)
            Dim PackSelection As New Dictionary(Of String, Boolean)(
                StringComparer.OrdinalIgnoreCase)
            Dim Status1 As Integer = 0
            Dim Status2 As Integer = 0
            Dim Index As Integer = 0
            While Index < Lines.Length
                If Not Lines(Index).Trim().Equals(
                    "file{", StringComparison.Ordinal) Then
                    Output.Add(Lines(Index))
                    Index += 1
                    Continue While
                End If

                Dim BlockStart As Integer = Index
                Dim Depth As Integer = 0
                Do While Index < Lines.Length
                    Depth += Lines(Index).Count(Function(Character) Character = "{"c)
                    Depth -= Lines(Index).Count(Function(Character) Character = "}"c)
                    Index += 1
                    If Depth = 0 Then Exit Do
                Loop
                If Depth <> 0 Then
                    Detail = "content\warthunder.blk 的文件块不完整。"
                    Return False
                End If

                Dim ItemPath As String = ""
                Dim ItemSize As Long = -1
                Dim PackTags As New List(Of String)
                For LineIndex As Integer = BlockStart To Index - 1
                    Dim LineText As String = Lines(LineIndex)
                    Dim PathMatch As Match = Regex.Match(
                        LineText, "^\s*path:t=""(.*)""\s*$")
                    If PathMatch.Success Then
                        ItemPath = PathMatch.Groups(1).Value.Replace("\", "/")
                    End If
                    Dim SizeMatch As Match = Regex.Match(
                        LineText, "^\s*size:i64=(\d+)\s*$")
                    If SizeMatch.Success Then
                        Long.TryParse(SizeMatch.Groups(1).Value, ItemSize)
                    End If
                    Dim TagMatch As Match = Regex.Match(
                        LineText, "^\s*tag:t=""(pack_[^""]+)""\s*$")
                    If TagMatch.Success Then
                        PackTags.Add(TagMatch.Groups(1).Value)
                    End If
                Next

                Dim ItemSelected As Boolean = PackTags.Count = 0
                If Not ItemSelected Then
                    For Each PackTag As String In PackTags
                        Dim IsSelected As Boolean
                        If Not PackSelection.TryGetValue(
                            PackTag, IsSelected) Then
                            IsSelected =
                                WarThunderContentPackIsSelected(
                                    InstallRoot, PackTag)
                            PackSelection(PackTag) = IsSelected
                        End If
                        If IsSelected Then
                            ItemSelected = True
                            Exit For
                        End If
                    Next
                End If

                Dim ItemStatus As Integer = 2
                If ItemSelected AndAlso ItemPath <> "" AndAlso ItemSize >= 0 Then
                    Dim FullPath As String =
                        WarThunderSafeInstallPath(InstallRoot, ItemPath)
                    If FullPath <> "" AndAlso
                       File.Exists(FullPath) AndAlso
                       New FileInfo(FullPath).Length = ItemSize Then
                        ItemStatus = 1
                    End If
                End If
                If ItemStatus = 1 Then Status1 += 1 Else Status2 += 1

                For LineIndex As Integer = BlockStart To Index - 1
                    Dim StatusMatch As Match = Regex.Match(
                        Lines(LineIndex), "^(\s*status:i=)(\d+)(\s*)$")
                    If StatusMatch.Success Then
                        Output.Add(
                            StatusMatch.Groups(1).Value &
                            ItemStatus.ToString() &
                            StatusMatch.Groups(3).Value)
                    Else
                        Output.Add(Lines(LineIndex))
                    End If
                Next
            End While

            Dim TempPath As String = BlkPath & ".ptl-new"
            FileUtils.Delete(TempPath)
            File.WriteAllLines(TempPath, Output, New UTF8Encoding(False))
            WarThunderAtomicReplace(TempPath, BlkPath)
            Detail = $"完整文件 {Status1}，未安装可选文件 {Status2}。"
            Logger.Info("战争雷霆完整性状态已生成：" & Detail)
            Return True
        Catch ex As Exception
            Detail = ex.GetDisplay(False)
            Logger.Warn(ex, "生成战争雷霆完整性状态失败")
            Return False
        End Try
    End Function

    Private Function WarThunderContentPackIsSelected(
        InstallRoot As String,
        PackTag As String) As Boolean
        If Not PackTag.StartsWith(
            "pack_", StringComparison.OrdinalIgnoreCase) Then Return False
        Dim Suffix As String = PackTag.Substring("pack_".Length)
        If File.Exists(
            Path.Combine(InstallRoot, "content", Suffix & ".rq2")) Then
            Return True
        End If
        Return File.Exists(
            Path.Combine(InstallRoot, "content", "pkg_" & Suffix & ".rq2"))
    End Function

    Private Function WarThunderDownloadSelfUpdateFiles(
        Install As WarThunderInstallation,
        Loader As LoaderTask(Of WarThunderInstallation, Boolean),
        ManifestPath As String,
        Manifest As WarThunderYupSnapshot,
        Plan As WarThunderSelfUpdatePlan) As Long
        Dim CacheRoot As String =
            Path.Combine(Path.GetDirectoryName(ManifestPath), "download")
        Directory.CreateDirectory(CacheRoot)
        WarThunderSelfUpdateDownloadRoot = CacheRoot

        Dim TorrentFile = MonoTorrent.Torrent.Load(ManifestPath)
        Dim WebSeeds = WarThunderGetSelfUpdateWebSeeds(
            WarThunderGetSelfUpdateTag(Install.InstallPath), Manifest.Version)
        Dim Completed As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each Pair In Plan.RequiredFiles
            Dim LocalPath As String =
                WarThunderSafeInstallPath(Install.InstallPath, Pair.Key)
            If LocalPath <> "" AndAlso
               WarThunderSelfUpdateLocalFileCanBeReused(LocalPath, Pair.Value) Then
                '只需重新展开已有 ZIP 时，不重复下载原压缩包。
                Completed.Add(Pair.Key)
            End If
        Next
        Dim DownloadedBytes As Long = 0
        Dim CompletedBytesBeforePass As Long = 0
        Dim Remaining As New WarThunderSelfUpdatePlan

        For PassIndex As Integer = 0 To 1
            Dim PassPlan As WarThunderSelfUpdatePlan =
                If(PassIndex = 0, Plan, Remaining)
            If PassPlan.RequiredFiles.Count = 0 Then Exit For
            DownloadedBytes += WarThunderDownloadSelfUpdateFilesByCdn(
                Install,
                Loader,
                PassPlan,
                Plan,
                CacheRoot,
                TorrentFile.Name,
                WebSeeds,
                Completed,
                CompletedBytesBeforePass)

            Remaining = New WarThunderSelfUpdatePlan
            For Each Pair In Plan.RequiredFiles
                If Not Completed.Contains(Pair.Key) Then
                    Remaining.RequiredFiles(Pair.Key) = Pair.Value
                End If
            Next
            If Remaining.RequiredFiles.Count = 0 Then Return DownloadedBytes

            CompletedBytesBeforePass =
                Math.Max(0, Plan.TotalBytes - Remaining.TotalBytes)
            If PassIndex = 0 Then
                Loader.DownloadStatus =
                    $"部分 CDN 文件失败，正在重试剩余 {Remaining.RequiredFiles.Count} 个文件"
                Loader.DownloadSpeed = 0
                Loader.FilesRemaining = Remaining.RequiredFiles.Count
            End If
        Next

        Loader.DownloadStatus = "CDN 未完成，正在切换 P2P 补全"
        Loader.DownloadSpeed = 0
        Loader.FilesRemaining = Remaining.RequiredFiles.Count
        Dim CdnCompletedBytes As Long =
            Math.Max(0, Plan.TotalBytes - Remaining.TotalBytes)
        DownloadedBytes += WarThunderDownloadSelfUpdateFilesByP2P(
            Install,
            Loader,
            ManifestPath,
            Manifest,
            Remaining,
            Plan.TotalBytes,
            CdnCompletedBytes)
        Return DownloadedBytes
    End Function

    Private Function WarThunderDownloadSelfUpdateFilesByCdn(
        Install As WarThunderInstallation,
        Loader As LoaderTask(Of WarThunderInstallation, Boolean),
        Plan As WarThunderSelfUpdatePlan,
        TotalPlan As WarThunderSelfUpdatePlan,
        CacheRoot As String,
        TorrentName As String,
        WebSeeds As IEnumerable(Of Uri),
        ByRef Completed As HashSet(Of String),
        CompletedBytesBeforePass As Long) As Long
        If WebSeeds Is Nothing OrElse Not WebSeeds.Any() Then Return 0
        Dim CompletedBytesAtStart As Long =
            WarThunderCompletedFileBytes(TotalPlan, Completed)

        Dim RankedWebSeeds As List(Of Uri) = WebSeeds.
            Select(Function(Seed) Seed).
            GroupBy(Function(Seed) Seed.AbsoluteUri, StringComparer.OrdinalIgnoreCase).
            Select(Function(Group) Group.First()).
            ToList()
        Dim PrimarySeedCount As Integer =
            Math.Min(CdnPrimarySeedLimit, RankedWebSeeds.Count)
        WarThunderLog(
            $"下载引擎将均衡使用前 {PrimarySeedCount} 个高优先级 CDN 端点，" &
            $"并发上限 {HttpWorkerLimit}：" &
            String.Join(", ", RankedWebSeeds.Take(PrimarySeedCount).
                Select(Function(Seed) Seed.Host)))

        Dim FileTasks As New List(Of WarThunderHttpFileTask)
        Dim Chunks As New List(Of WarThunderHttpChunk)
        For Each Pair In Plan.RequiredFiles
            If Completed.Contains(Pair.Key) Then Continue For
            Dim FinalPath As String = WarThunderSafeInstallPath(
                Install.InstallPath, Pair.Key)
            If FinalPath = "" Then Continue For
            If Pair.Value.Length = 0 Then
                Directory.CreateDirectory(Path.GetDirectoryName(FinalPath))
                File.WriteAllBytes(FinalPath, New Byte() {})
                WarThunderSetFileTimestamp(FinalPath, Pair.Value.ModifiedTime)
                Completed.Add(Pair.Key)
                Continue For
            End If
            Dim LocalPath As String =
                WarThunderCreateDirectDownloadTempPath(FinalPath, Pair.Key)
            Dim Sources = WarThunderCreateCdnFileSources(
                RankedWebSeeds, TorrentName, Pair.Key, PrimarySeedCount)
            If Sources.Count = 0 Then Continue For

            Dim FileTask As New WarThunderHttpFileTask With {
                .RelativePath = Pair.Key,
                .FinalPath = FinalPath,
                .TempPath = LocalPath,
                .Entry = Pair.Value,
                .Sources = Sources
            }
            FileTask.IsWholeFile = Pair.Value.Length <= HttpLargeFileThreshold
            Dim ChunkCount As Integer = 1
            If Not FileTask.IsWholeFile Then
                Dim DesiredChunks As Long =
                    CLng(Math.Ceiling(Pair.Value.Length / (4.0 * 1024 * 1024)))
                ChunkCount = CInt(Math.Max(2L, Math.Min(
                    CLng(HttpMaxChunksPerLargeFile), DesiredChunks)))
            End If
            Dim ChunkSize As Long = CLng(Math.Ceiling(
                Pair.Value.Length / CDbl(ChunkCount)))
            For ChunkIndex As Integer = 0 To ChunkCount - 1
                Dim StartByte As Long = CLng(ChunkIndex) * ChunkSize
                If StartByte >= Pair.Value.Length Then Exit For
                Dim EndByte As Long = Math.Min(
                    Pair.Value.Length - 1, StartByte + ChunkSize - 1)
                Dim PartPath As String =
                    If(FileTask.IsWholeFile, FileTask.TempPath,
                       FileTask.TempPath & ".part" & ChunkIndex)
                Dim Chunk As New WarThunderHttpChunk With {
                    .FileTask = FileTask,
                    .Index = ChunkIndex,
                    .StartByte = StartByte,
                    .EndByte = EndByte,
                    .PartPath = PartPath
                }
                FileTask.Chunks.Add(Chunk)
                Chunks.Add(Chunk)
            Next
            FileTask.RemainingChunks = FileTask.Chunks.Count
            WarThunderDeleteHttpFileTaskArtifacts(FileTask)
            FileTasks.Add(FileTask)
        Next
        If Chunks.Count = 0 Then
            Return Math.Max(
                0,
                WarThunderCompletedFileBytes(TotalPlan, Completed) -
                CompletedBytesAtStart)
        End If
        WarThunderLog(
            $"HTTP 任务已建立：{FileTasks.Count} 个文件，{Chunks.Count} 个下载分段，" &
            $"总计 {StringUtils.FormatByteSize(Plan.TotalBytes)}。")

        Dim Queue As New ConcurrentQueue(Of WarThunderHttpChunk)(Chunks)
        Dim Context As New WarThunderHttpDownloadContext
        Using Handler As New HttpClientHandler()
            Handler.UseProxy = False
            Handler.UseCookies = False
            Handler.AutomaticDecompression = Net.DecompressionMethods.None
            Handler.MaxConnectionsPerServer = HttpWorkerLimit
            Using Client As New HttpClient(Handler)
                Client.Timeout = TimeSpan.FromMinutes(30)
                Client.DefaultRequestHeaders.UserAgent.ParseAdd("PTL/" & PtlVersion)
                Client.DefaultRequestHeaders.ExpectContinue = False

                Dim WorkerTasks As New List(Of Task)
                For WorkerIndex As Integer = 0 To HttpWorkerLimit - 1
                    WorkerTasks.Add(Task.Factory.StartNew(
                        Sub()
                            WarThunderHttpDownloadWorker(
                                Context, Queue, Client)
                        End Sub,
                        Threading.CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default))
                Next
                Dim AllWorkers As Task = Task.WhenAll(WorkerTasks)
                Dim LastBytes As Long = 0
                Dim LastTime As Date = Date.UtcNow
                Dim SmoothedSpeed As Double = 0
                Do While Not AllWorkers.Wait(250)
                    If Loader.IsCanceled Then Context.Cancellation.Cancel()
                    Dim CurrentBytes As Long = Interlocked.Read(Context.BytesRead)
                    Dim Now As Date = Date.UtcNow
                    Dim Seconds As Double =
                        Math.Max(0.001, (Now - LastTime).TotalSeconds)
                    Dim InstantSpeed As Double =
                        Math.Max(0, (CurrentBytes - LastBytes) / Seconds)
                    SmoothedSpeed = If(
                        SmoothedSpeed <= 0, InstantSpeed,
                        SmoothedSpeed * 0.72 + InstantSpeed * 0.28)
                    LastBytes = CurrentBytes
                    LastTime = Now

                    Dim OverallDownloaded As Long = Math.Min(
                        TotalPlan.TotalBytes,
                        CompletedBytesAtStart + CurrentBytes)
                    Dim OverallProgress As Double = If(
                        TotalPlan.TotalBytes > 0,
                        Math.Max(0, Math.Min(
                            1, OverallDownloaded / TotalPlan.TotalBytes)),
                        1)
                    Dim RemainingFiles As Integer = Math.Max(
                        0, FileTasks.Count - Context.CompletedFiles)
                    WarThunderSetSelfUpdateState(
                        Loader,
                        0.1 + OverallProgress * 0.8,
                        "正在下载游戏更新",
                        CLng(SmoothedSpeed),
                        TotalPlan.TotalBytes,
                        RemainingFiles,
                        OverallDownloaded)
                Loop
                Context.Cancellation.Cancel()
                AllWorkers.GetAwaiter().GetResult()
                If Loader.IsCanceled Then
                    Throw New OperationCanceledException()
                End If
            End Using
        End Using

        For Each RelativePath As String In Context.SuccessfulPaths
            Completed.Add(RelativePath)
        Next
        Dim RemainingInPass As Integer = 0
        For Each RelativePath As String In Plan.RequiredFiles.Keys
            If Not Completed.Contains(RelativePath) Then RemainingInPass += 1
        Next
        If RemainingInPass > 0 Then
            Logger.Warn(
                $"下载引擎本轮仍有 {RemainingInPass} 个文件未完成，" &
                $"已接收 {StringUtils.FormatByteSize(Interlocked.Read(Context.BytesRead))}。")
        End If
        Return Math.Max(
            0,
            WarThunderCompletedFileBytes(TotalPlan, Completed) -
            CompletedBytesAtStart)
    End Function

    Private Sub WarThunderHttpDownloadWorker(
        Context As WarThunderHttpDownloadContext,
        Queue As ConcurrentQueue(Of WarThunderHttpChunk),
        Client As HttpClient)
        Do
            If Context.Cancellation.IsCancellationRequested Then Return
            Dim Chunk As WarThunderHttpChunk = Nothing
            If Not Queue.TryDequeue(Chunk) Then Return
            If WarThunderDownloadHttpChunk(Context, Client, Chunk) Then
                Dim Remaining As Integer =
                    Interlocked.Decrement(Chunk.FileTask.RemainingChunks)
                If Remaining = 0 AndAlso
                   WarThunderFinalizeHttpFileTask(Chunk.FileTask) Then
                    Context.SuccessfulPaths.Add(Chunk.FileTask.RelativePath)
                    Interlocked.Increment(Context.CompletedFiles)
                End If
            End If
        Loop
    End Sub

    Private Function WarThunderDownloadHttpChunk(
        Context As WarThunderHttpDownloadContext,
        Client As HttpClient,
        Chunk As WarThunderHttpChunk) As Boolean
        If Chunk Is Nothing OrElse Chunk.FileTask Is Nothing OrElse
           Chunk.FileTask.Sources.Count = 0 Then Return False
        Dim MaxAttempts As Integer = Math.Max(
            2, Math.Min(6, Chunk.FileTask.Sources.Count))
        Dim WritePath As String = Chunk.PartPath & ".writing"
        Directory.CreateDirectory(Path.GetDirectoryName(Chunk.PartPath))
        For Attempt As Integer = 0 To MaxAttempts - 1
            If Context.Cancellation.IsCancellationRequested Then Return False
            Dim SourceIndex As Integer =
                (Chunk.Index + Attempt) Mod Chunk.FileTask.Sources.Count
            Dim Url As String = Chunk.FileTask.Sources(SourceIndex)
            Try
                FileUtils.Delete(WritePath)
                Using RequestCancellation As Threading.CancellationTokenSource =
                    Threading.CancellationTokenSource.CreateLinkedTokenSource(
                        Context.Cancellation.Token)
                    RequestCancellation.CancelAfter(
                        TimeSpan.FromSeconds(HttpChunkIdleTimeoutSeconds))
                Using Request As New HttpRequestMessage(HttpMethod.Get, Url)
                    If Not Chunk.FileTask.IsWholeFile Then
                        Request.Headers.Range = New Headers.RangeHeaderValue(
                            Chunk.StartByte, Chunk.EndByte)
                    End If
                    Using Response As HttpResponseMessage = Client.SendAsync(
                        Request, HttpCompletionOption.ResponseHeadersRead,
                        RequestCancellation.Token).GetAwaiter().GetResult()
                        If Not Chunk.FileTask.IsWholeFile Then
                            If Response.StatusCode <> Net.HttpStatusCode.PartialContent Then
                                Throw New InvalidDataException(
                                    $"CDN 不支持分段请求或返回了 HTTP {CInt(Response.StatusCode)}。")
                            End If
                        ElseIf Response.StatusCode <> Net.HttpStatusCode.OK AndAlso
                               Response.StatusCode <> Net.HttpStatusCode.PartialContent Then
                            Response.EnsureSuccessStatusCode()
                        End If

                        Dim ExpectedLength As Long = Chunk.Length
                        Dim ContentLength As Long =
                            Response.Content.Headers.ContentLength.GetValueOrDefault(-1)
                        If ContentLength >= 0 AndAlso
                           ContentLength <> ExpectedLength Then
                            Throw New InvalidDataException(
                                $"CDN 分段大小不符：期望 {ExpectedLength}，实际 {ContentLength}。")
                        End If

                        Dim Written As Long = 0
                        Dim AttemptStarted As Date = Date.UtcNow
                        Dim BufferSize As Integer = CInt(Math.Min(
                            CLng(HttpChunkReadBuffer),
                            Math.Max(8L * 1024, ExpectedLength)))
                        Using Input = Response.Content.ReadAsStreamAsync().
                            GetAwaiter().GetResult()
                            Using Output As New FileStream(
                                WritePath, FileMode.Create, FileAccess.Write,
                                FileShare.None, HttpChunkReadBuffer, FileOptions.SequentialScan)
                                Dim Buffer As Byte() = New Byte(BufferSize - 1) {}
                                Do
                                    RequestCancellation.CancelAfter(
                                        TimeSpan.FromSeconds(HttpChunkIdleTimeoutSeconds))
                                    Dim ReadCount As Integer = Input.ReadAsync(
                                        Buffer, 0, Buffer.Length,
                                        RequestCancellation.Token).
                                        GetAwaiter().GetResult()
                                    If ReadCount <= 0 Then Exit Do
                                    Output.Write(Buffer, 0, ReadCount)
                                    Written += ReadCount
                                    Interlocked.Add(Context.BytesRead, ReadCount)
                                    If Written > ExpectedLength Then
                                        Throw New InvalidDataException(
                                            "CDN 返回的数据超过请求的分段大小。")
                                    End If
                                    Dim AttemptSeconds As Double =
                                        Math.Max(0.001,
                                            (Date.UtcNow - AttemptStarted).TotalSeconds)
                                    If AttemptSeconds >= HttpSlowSourceWindowSeconds AndAlso
                                       Written / AttemptSeconds <
                                       HttpSlowSourceMinimumBytesPerSecond Then
                                        Throw New TimeoutException(
                                            $"CDN 来源速度过慢：{Url}")
                                    End If
                                Loop
                            End Using
                        End Using
                        If Written <> ExpectedLength Then
                            Throw New EndOfStreamException(
                                $"CDN 数据不完整：期望 {ExpectedLength}，实际 {Written}。")
                        End If
                    End Using
                End Using
                End Using

                FileUtils.Delete(Chunk.PartPath)
                File.Move(WritePath, Chunk.PartPath)
                Return True
            Catch ex As OperationCanceledException
                FileUtils.Delete(WritePath)
                If Context.Cancellation.IsCancellationRequested Then Return False
            Catch ex As Exception
                FileUtils.Delete(WritePath)
                If Attempt = MaxAttempts - 1 Then
                    Logger.Warn(
                        ex,
                        $"战争雷霆 HTTP 下载失败（{Chunk.FileTask.RelativePath}，" &
                        $"分段 {Chunk.Index}，来源 {Url}）")
                Else
                    Thread.Sleep(180 * (Attempt + 1))
                End If
            End Try
        Next
        Return False
    End Function

    Private Function WarThunderFinalizeHttpFileTask(
        FileTask As WarThunderHttpFileTask) As Boolean
        Try
            Directory.CreateDirectory(Path.GetDirectoryName(FileTask.FinalPath))
            If FileTask.IsWholeFile Then
                WarThunderValidateHttpDownload(
                    FileTask.TempPath, FileTask.Entry)
            Else
                FileUtils.Delete(FileTask.TempPath)
                Using Output As New FileStream(
                    FileTask.TempPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 1024 * 1024, FileOptions.SequentialScan)
                    For Each Chunk In FileTask.Chunks.
                        OrderBy(Function(Item) Item.Index)
                        If Not File.Exists(Chunk.PartPath) Then
                            Throw New FileNotFoundException(
                                $"HTTP 分段文件不存在：{Chunk.PartPath}")
                        End If
                        Using Input As New FileStream(
                            Chunk.PartPath, FileMode.Open, FileAccess.Read,
                            FileShare.Read, 1024 * 1024, FileOptions.SequentialScan)
                            Input.CopyTo(Output, 1024 * 1024)
                        End Using
                    Next
                End Using
                WarThunderValidateHttpDownload(
                    FileTask.TempPath, FileTask.Entry)
            End If

            WarThunderAtomicReplace(FileTask.TempPath, FileTask.FinalPath)
            WarThunderSetFileTimestamp(
                FileTask.FinalPath, FileTask.Entry.ModifiedTime)
            For Each Chunk In FileTask.Chunks
                If Not Chunk.PartPath.Equals(
                    FileTask.FinalPath, StringComparison.OrdinalIgnoreCase) Then
                    FileUtils.Delete(Chunk.PartPath)
                End If
            Next
            Return True
        Catch ex As Exception
            Logger.Warn(
                ex, $"战争雷霆 HTTP 文件校验或落地失败：{FileTask.RelativePath}")
            WarThunderDeleteHttpFileTaskArtifacts(FileTask)
            Return False
        End Try
    End Function

    Private Sub WarThunderValidateHttpDownload(
        FilePath As String,
        Entry As WarThunderYupFile)
        If Not File.Exists(FilePath) Then
            Throw New FileNotFoundException("HTTP 下载文件不存在。", FilePath)
        End If
        If New FileInfo(FilePath).Length <> Entry.Length Then
            Throw New InvalidDataException(
                $"HTTP 下载文件大小不符：期望 {Entry.Length}，" &
                $"实际 {New FileInfo(FilePath).Length}。")
        End If
        If Not WarThunderFileHashEquals(FilePath, Entry.Sha1) Then
            Throw New InvalidDataException("HTTP 下载文件 SHA-1 校验失败。")
        End If
    End Sub

    Private Sub WarThunderDeleteHttpFileTaskArtifacts(
        FileTask As WarThunderHttpFileTask)
        If FileTask Is Nothing Then Return
        FileUtils.Delete(FileTask.TempPath)
        For Each Chunk In FileTask.Chunks
            FileUtils.Delete(Chunk.PartPath)
            FileUtils.Delete(Chunk.PartPath & ".writing")
        Next
    End Sub

    Private Function WarThunderCreateCdnFileSources(
        WebSeeds As IEnumerable(Of Uri),
        TorrentName As String,
        RelativePath As String,
        PrimarySeedCount As Integer) As List(Of String)
        Dim Seeds As List(Of String) = WebSeeds.
            Select(Function(Seed) Seed.AbsoluteUri).
            Distinct(StringComparer.OrdinalIgnoreCase).
            ToList()
        If Seeds.Count = 0 Then Return New List(Of String)

        Dim EffectivePrimaryCount As Integer =
            Math.Min(Seeds.Count, Math.Max(1, PrimarySeedCount))
        Dim StableHash As ULong = 2166136261UL
        For Each Character As Char In RelativePath
            StableHash =
                ((StableHash Xor CULng(CInt(AscW(Character)) And &HFFFF)) *
                 16777619UL) And &HFFFFFFFFUL
        Next
        Dim Offset As Integer =
            CInt(StableHash Mod CULng(EffectivePrimaryCount))

        Dim OrderedSeeds As New List(Of String)
        For Index As Integer = 0 To EffectivePrimaryCount - 1
            OrderedSeeds.Add(Seeds((Offset + Index) Mod EffectivePrimaryCount))
        Next
        OrderedSeeds.AddRange(Seeds.Skip(EffectivePrimaryCount))
        Return OrderedSeeds.
            Select(Function(Seed)
                       Return WarThunderCreateCdnFileSource(
                           Seed, TorrentName, RelativePath)
                   End Function).
            ToList()
    End Function

    Private Function WarThunderCreateDirectDownloadTempPath(
        FinalPath As String,
        RelativePath As String) As String
        Dim Token As String
        Using Hasher As SHA1 = SHA1.Create()
            Token = BitConverter.ToString(
                Hasher.ComputeHash(Encoding.UTF8.GetBytes(RelativePath))).
                Replace("-", "").
                Substring(0, 16)
        End Using
        Return Path.Combine(
            Path.GetDirectoryName(FinalPath),
            ".ptl-" & Token & ".download")
    End Function

    Private Function WarThunderCreateCdnFileSource(
        SeedBase As String,
        TorrentName As String,
        RelativePath As String) As String
        Dim EncodedPath As String = String.Join(
            "/", RelativePath.Split("/"c).Select(Function(Part) Uri.EscapeDataString(Part)))
        Dim EncodedName As String = Uri.EscapeDataString(TorrentName)
        Return SeedBase.TrimEnd("/"c) & "/" & EncodedName & "/" & EncodedPath
    End Function

    Private Function WarThunderSha1Hex(Value As Byte()) As String
        If Value Is Nothing OrElse Value.Length = 0 Then Return Nothing
        Return BitConverter.ToString(Value).Replace("-", "")
    End Function

    Private Function WarThunderCompletedFileBytes(
        Plan As WarThunderSelfUpdatePlan,
        Completed As HashSet(Of String)) As Long
        Dim Result As Long = 0
        For Each RelativePath In Completed
            Dim Entry As WarThunderYupFile = Nothing
            If Plan.RequiredFiles.TryGetValue(RelativePath, Entry) Then
                Result += Entry.Length
            End If
        Next
        Return Result
    End Function

    Private Function WarThunderDownloadSelfUpdateFilesByP2P(
        Install As WarThunderInstallation,
        Loader As LoaderTask(Of WarThunderInstallation, Boolean),
        ManifestPath As String,
        Manifest As WarThunderYupSnapshot,
        Plan As WarThunderSelfUpdatePlan,
        TotalBytes As Long,
        CompletedBytesBeforeP2P As Long) As Long
        Dim CacheRoot As String =
            Path.Combine(Path.GetDirectoryName(ManifestPath), "download")
        Directory.CreateDirectory(CacheRoot)
        WarThunderSelfUpdateDownloadRoot = CacheRoot

        Dim TorrentFile = MonoTorrent.Torrent.Load(ManifestPath)
        Dim UpdateTag = WarThunderGetSelfUpdateTag(Install.InstallPath)
        Dim WebSeeds = WarThunderGetSelfUpdateWebSeeds(UpdateTag, Manifest.Version)
        For Each WebSeed In WebSeeds
            TorrentFile.HttpSeeds.Add(WebSeed)
        Next
        WarThunderLog($"战争雷霆更新已配置 {WebSeeds.Count} 个官方 CDN Web Seed。")

        Dim Settings = New EngineSettingsBuilder With {
            .CacheDirectory = Path.Combine(Path.GetDirectoryName(ManifestPath), "client-cache"),
            .ListenPort = FindFreePort(),
            .DhtPort = 0,
            .MaximumConnections = 220,
            .MaximumHalfOpenConnections = 50,
            .AllowLocalPeerDiscovery = False,
            .AllowPortForwarding = False,
            .AutoSaveLoadDhtCache = True,
            .AutoSaveLoadFastResume = True
        }.ToSettings()
        Dim TorrentSettings = New TorrentSettingsBuilder With {
            .WebSeedDelay = TimeSpan.Zero,
            .WebSeedSpeedTrigger = Integer.MaxValue,
            .MaximumConnections = 128
        }.ToSettings()

        Dim Engine As New ClientEngine(Settings)
        Dim Manager As TorrentManager = Nothing
        Try
            Manager = Engine.AddAsync(
                TorrentFile, CacheRoot, TorrentSettings).GetAwaiter().GetResult()
            For Index As Integer = 0 To Manager.Files.Count - 1
                Dim TorrentEntry = Manager.Files(Index)
                Manager.SetFilePriorityAsync(
                    TorrentEntry, MonoTorrent.Priority.DoNotDownload).GetAwaiter().GetResult()
            Next

            Dim SelectedFiles As New Dictionary(Of String, ITorrentFileInfo)(StringComparer.OrdinalIgnoreCase)
            For Each Pair In Plan.RequiredFiles
                If Pair.Value.TorrentIndex < 0 OrElse
                   Pair.Value.TorrentIndex >= Manager.Files.Count Then
                    Throw New InvalidDataException($"YUP 中的文件无法映射到 P2P 任务：{Pair.Key}")
                End If
                Dim TorrentEntry = Manager.Files(Pair.Value.TorrentIndex)
                Manager.SetFilePriorityAsync(
                    TorrentEntry, MonoTorrent.Priority.Normal).GetAwaiter().GetResult()
                SelectedFiles(Pair.Key) = TorrentEntry
            Next

            Try
                Manager.TrackerManager.AddTrackerAsync(
                    New Uri(DefaultTrackerUrl)).GetAwaiter().GetResult()
            Catch ex As Exception
                Logger.Warn(ex, "向 MonoTorrent 添加战争雷霆 tracker 失败")
            End Try
            Try
                Manager.TrackerManager.AddTrackerAsync(
                    New Uri(BackupTrackerUrl)).GetAwaiter().GetResult()
            Catch ex As Exception
                Logger.Warn(ex, "向 MonoTorrent 添加战争雷霆备用 tracker 失败")
            End Try

            Engine.StartAllAsync().GetAwaiter().GetResult()

            Dim TrackerPeers = WarThunderQueryTrackerPeers(
                Manager.InfoHash.ToArray(), Settings.ListenPort)
            WarThunderLog($"战争雷霆 tracker 返回 {TrackerPeers.Count} 个 P2P 节点。")
            If TrackerPeers.Count > 0 Then
                Dim Peers As New List(Of Peer)
                For Each EndPoint In TrackerPeers
                    Peers.Add(New Peer(
                        New BEncodedString(WarThunderCreateRandomPeerId()),
                        New Uri($"ipv4://{EndPoint.Address}:{EndPoint.Port}")))
                Next
                Manager.AddPeersAsync(Peers).GetAwaiter().GetResult()
            End If

            Dim LastProgress As Double = -1
            Dim LastProgressAt As Date = Date.UtcNow
            Do
                WarThunderThrowIfSelfUpdateCanceled(Loader)
                Dim CurrentProgress As Double =
                    WarThunderCalculateP2PProgress(Plan, SelectedFiles)
                Dim DownloadedInRemaining As Long =
                    CLng(Math.Min(Plan.TotalBytes, Plan.TotalBytes * CurrentProgress))
                Dim Downloaded As Long = Math.Min(
                    TotalBytes,
                    CompletedBytesBeforeP2P + DownloadedInRemaining)
                Dim OverallProgress As Double = If(
                    TotalBytes > 0,
                    Math.Max(0, Math.Min(1, Downloaded / TotalBytes)),
                    1)
                Dim Remaining As Integer =
                    Plan.RequiredFiles.Keys.Where(
                        Function(RelativePath)
                            Dim FileInfo As ITorrentFileInfo = SelectedFiles(RelativePath)
                            Return Not FileInfo.BitField.AllTrue
                        End Function).Count()
                WarThunderSetSelfUpdateState(
                    Loader,
                    0.1 + OverallProgress * 0.8,
                    "正在通过 P2P 与 CDN 下载游戏更新",
                    Engine.TotalDownloadSpeed,
                    TotalBytes,
                    Remaining,
                    Downloaded)

                If Math.Abs(CurrentProgress - LastProgress) >= 0.0001 Then
                    LastProgress = CurrentProgress
                    LastProgressAt = Date.UtcNow
                ElseIf Date.UtcNow - LastProgressAt > TimeSpan.FromMinutes(5) Then
                    Throw New TimeoutException("P2P 下载长时间没有进展，请检查网络后重试。")
                End If

                If Manager.Complete OrElse CurrentProgress >= 0.999999 Then Exit Do
                Thread.Sleep(500)
            Loop

            Dim DownloadedBytes As Long = 0
            For Each Pair In Plan.RequiredFiles
                Dim TorrentEntry = SelectedFiles(Pair.Key)
                If Not File.Exists(TorrentEntry.FullPath) Then
                    Throw New FileNotFoundException(
                        $"P2P 下载完成后文件不存在：{Pair.Key}", TorrentEntry.FullPath)
                End If
                If Pair.Value.Sha1 IsNot Nothing AndAlso
                   Not WarThunderFileHashEquals(TorrentEntry.FullPath, Pair.Value.Sha1) Then
                    Throw New InvalidDataException($"P2P 文件校验失败：{Pair.Key}")
                End If
                DownloadedBytes += Pair.Value.Length
            Next
            Return DownloadedBytes
        Finally
            Try
                If Manager IsNot Nothing Then
                    Manager.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()
                End If
                Engine.StopAllAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()
            Catch ex As Exception
                Logger.Warn(ex, "停止战争雷霆 P2P 更新引擎失败")
            Finally
                Engine.Dispose()
            End Try
        End Try
    End Function

    Private Function WarThunderCalculateP2PProgress(
        Plan As WarThunderSelfUpdatePlan,
        Files As Dictionary(Of String, ITorrentFileInfo)) As Double
        If Plan.TotalBytes <= 0 Then Return 1
        Dim Completed As Double = 0
        For Each Pair In Plan.RequiredFiles
            Dim Entry = Files(Pair.Key)
            Completed += Pair.Value.Length * (Entry.BitField.PercentComplete / 100.0)
        Next
        Return Math.Max(0, Math.Min(1, Completed / Plan.TotalBytes))
    End Function

    Private Function WarThunderInstallSelfUpdateFiles(
        InstallPath As String,
        Manifest As WarThunderYupSnapshot,
        Plan As WarThunderSelfUpdatePlan,
        Loader As LoaderTask(Of WarThunderInstallation, Boolean)) As Integer
        Dim InstallPathRoot As String = InstallPath.TrimEnd("\"c) & "\"
        If Plan.RequiredFiles.Count > 0 AndAlso
           (WarThunderSelfUpdateDownloadRoot = "" OrElse
            Not Directory.Exists(WarThunderSelfUpdateDownloadRoot)) Then
            Throw New FileNotFoundException("P2P 下载目录不存在。")
        End If

        Dim Index As Integer = 0
        For Each Pair In Plan.RequiredFiles
            WarThunderThrowIfSelfUpdateCanceled(Loader)
            Dim DestinationPath As String =
                WarThunderSafeInstallPath(InstallPathRoot, Pair.Key)
            If DestinationPath = "" Then
                Throw New InvalidDataException($"更新清单包含不安全的路径：{Pair.Key}")
            End If
            Dim DestinationValid As Boolean =
                File.Exists(DestinationPath) AndAlso
                New FileInfo(DestinationPath).Length = Pair.Value.Length
            If Not DestinationValid Then
                Dim SourcePath As String =
                    WarThunderFindDownloadedSelfUpdateFile(Pair.Value)
                If SourcePath = "" OrElse
                   Not File.Exists(SourcePath) OrElse
                   New FileInfo(SourcePath).Length <> Pair.Value.Length Then
                    Throw New FileNotFoundException(
                        $"找不到已下载或已覆盖的更新文件：{Pair.Key}")
                End If
                WarThunderAtomicCopy(SourcePath, DestinationPath)
            End If
            Directory.CreateDirectory(Path.GetDirectoryName(DestinationPath))
            WarThunderSetFileTimestamp(DestinationPath, Pair.Value.ModifiedTime)

            If Pair.Value.ZipEntries.Count > 0 Then
                WarThunderExtractYupZip(
                    DestinationPath, Pair.Value.ZipEntries, Pair.Value.ModifiedTime)
            End If

            Index += 1
            WarThunderSetSelfUpdateState(
                Loader,
                0.9 + 0.08 * Index / Math.Max(1, Plan.RequiredFiles.Count),
                $"正在安装游戏文件（{Index}/{Plan.RequiredFiles.Count}）",
                0, Plan.TotalBytes, Math.Max(0, Plan.RequiredFiles.Count - Index))
        Next
        Return Index
    End Function

    Private Function WarThunderFindDownloadedSelfUpdateFile(Entry As WarThunderYupFile) As String
        If Entry Is Nothing Then Return ""
        Dim UpdateRoot As String = WarThunderSelfUpdateDownloadRoot
        If Not Directory.Exists(UpdateRoot) Then Return ""
        Dim DirectPath As String =
            WarThunderSafeInstallPath(UpdateRoot, Entry.RelativePath)
        If DirectPath <> "" AndAlso File.Exists(DirectPath) Then Return DirectPath

        Dim RelativeNeedle As String = "\" & Entry.RelativePath.Replace("/", "\")
        Try
            Return Directory.EnumerateFiles(
                UpdateRoot, Path.GetFileName(Entry.RelativePath),
                SearchOption.AllDirectories).
                Where(Function(PathText)
                          Return PathText.EndsWith(RelativeNeedle, StringComparison.OrdinalIgnoreCase)
                      End Function).
                OrderByDescending(Function(PathText) File.GetLastWriteTimeUtc(PathText)).
                FirstOrDefault()
        Catch ex As Exception
            Logger.Warn(ex, $"查找战争雷霆更新文件失败：{Entry.RelativePath}")
            Return ""
        End Try
    End Function

    Private Sub WarThunderExtractYupZip(
        ZipPath As String,
        ZipEntries As IEnumerable(Of String),
        ModifiedTime As Long)
        Dim DestinationRoot As String = Path.GetDirectoryName(ZipPath)
        Dim Wanted As New HashSet(Of String)(
            ZipEntries.Select(Function(Name) Name.Replace("\", "/")),
            StringComparer.OrdinalIgnoreCase)
        Dim Extracted As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Using Archive As ZipArchive = ZipFile.OpenRead(ZipPath)
            For Each Entry In Archive.Entries
                If Entry.FullName.EndsWith("/", StringComparison.Ordinal) Then Continue For
                Dim RelativeEntryName As String = Entry.FullName.Replace("\", "/")
                If Not Wanted.Contains(RelativeEntryName) Then Continue For
                Dim DestinationPath As String =
                    WarThunderSafeInstallPath(DestinationRoot, Entry.FullName)
                If DestinationPath = "" Then
                    Throw New InvalidDataException($"ZIP 包含不安全的路径：{Entry.FullName}")
                End If
                Directory.CreateDirectory(Path.GetDirectoryName(DestinationPath))
                Dim TempPath As String = DestinationPath & ".ptl-new"
                FileUtils.Delete(TempPath)
                Entry.ExtractToFile(TempPath, True)
                WarThunderAtomicReplace(TempPath, DestinationPath)
                WarThunderSetFileTimestamp(DestinationPath, ModifiedTime)
                Extracted.Add(RelativeEntryName)
            Next
        End Using
        Dim MissingEntries As List(Of String) = Wanted.
            Where(Function(Name) Not Extracted.Contains(Name)).
            Take(5).ToList()
        If MissingEntries.Count > 0 Then
            Throw New InvalidDataException(
                $"压缩包缺少更新清单要求的文件：{String.Join("、", MissingEntries)}")
        End If
    End Sub

    Private Sub WarThunderApplySelfUpdateManifest(
        InstallPath As String,
        Manifest As WarThunderYupSnapshot,
        NewVersion As String)
        If Manifest?.RawData Is Nothing Then
            Throw New InvalidDataException("更新清单缺少原始 Bencode 数据。")
        End If
        Dim NewData As Byte() =
            WarThunderPatchYupVersionBytes(Manifest.RawData, NewVersion)
        Dim OriginalHash As String =
            MonoTorrent.Torrent.Load(Manifest.RawData).InfoHash.ToHex()
        Dim PatchedHash As String =
            MonoTorrent.Torrent.Load(NewData).InfoHash.ToHex()
        If Not OriginalHash.Equals(PatchedHash, StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidDataException("更新清单重写后 P2P 信息哈希发生变化。")
        End If

        Dim ManifestPath As String = Path.Combine(InstallPath, "warthunder.yup")
        Dim OldManifestPath As String = Path.Combine(InstallPath, "warthunder_old.yup")
        If File.Exists(ManifestPath) Then
            File.Copy(ManifestPath, OldManifestPath, True)
        End If
        Dim TempPath As String = ManifestPath & ".ptl-new"
        FileUtils.Delete(TempPath)
        Using Stream As New FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None)
            Stream.Write(NewData, 0, NewData.Length)
            Stream.Flush(True)
        End Using
        WarThunderAtomicReplace(TempPath, ManifestPath)
    End Sub

    Private Function WarThunderPatchYupVersionBytes(
        Data As Byte(),
        NewVersion As String) As Byte()
        Dim YupStart As Integer
        Dim YupEnd As Integer
        If Not WarThunderTryFindDictionaryValue(
            Data, 0, "yup", YupStart, YupEnd) Then
            Throw New InvalidDataException("更新清单缺少 yup 配置。")
        End If
        Dim VersionStart As Integer
        Dim VersionEnd As Integer
        If Not WarThunderTryFindDictionaryValue(
            Data, YupStart, "version", VersionStart, VersionEnd) Then
            Throw New InvalidDataException("更新清单缺少 yup.version。")
        End If
        Dim VersionBytes As Byte() = Encoding.UTF8.GetBytes(If(NewVersion, ""))
        Dim Prefix As Byte() = Encoding.ASCII.GetBytes(VersionBytes.Length.ToString() & ":")
        Using Output As New MemoryStream()
            Output.Write(Data, 0, VersionStart)
            Output.Write(Prefix, 0, Prefix.Length)
            Output.Write(VersionBytes, 0, VersionBytes.Length)
            Output.Write(Data, VersionEnd, Data.Length - VersionEnd)
            Return Output.ToArray()
        End Using
    End Function

    Private Function WarThunderTryFindDictionaryValue(
        Data As Byte(),
        DictionaryStart As Integer,
        Key As String,
        ByRef ValueStart As Integer,
        ByRef ValueEnd As Integer) As Boolean
        If DictionaryStart < 0 OrElse DictionaryStart >= Data.Length OrElse
           Data(DictionaryStart) <> 100 Then Return False
        Dim Position As Integer = DictionaryStart + 1
        Do While Position < Data.Length AndAlso Data(Position) <> 101
            Dim RawKeyStart As Integer = Position
            Dim RawKeyEnd As Integer
            If Not WarThunderSkipBencodeValue(Data, Position, RawKeyEnd) Then Return False
            Dim RawKey As String = Encoding.UTF8.GetString(
                Data, RawKeyStart, RawKeyEnd - RawKeyStart)
            Dim Colon As Integer = RawKey.IndexOf(":"c)
            Dim EntryValueStart As Integer = RawKeyEnd
            Dim EntryValueEnd As Integer
            If Not WarThunderSkipBencodeValue(
                Data, EntryValueStart, EntryValueEnd) Then Return False
            If Colon >= 0 AndAlso
               RawKey.Substring(Colon + 1).Equals(Key, StringComparison.Ordinal) Then
                ValueStart = EntryValueStart
                ValueEnd = EntryValueEnd
                Return True
            End If
            Position = EntryValueEnd
        Loop
        Return False
    End Function

    Private Function WarThunderSkipBencodeValue(
        Data As Byte(),
        Start As Integer,
        ByRef [End] As Integer) As Boolean
        If Start < 0 OrElse Start >= Data.Length Then Return False
        Select Case Data(Start)
            Case 105 'i
                Dim Marker As Integer = Array.IndexOf(Data, CByte(101), Start + 1)
                If Marker < 0 Then Return False
                [End] = Marker + 1
                Return True
            Case 108 'l
                Dim Position As Integer = Start + 1
                Do While Position < Data.Length AndAlso Data(Position) <> 101
                    Dim ItemEnd As Integer
                    If Not WarThunderSkipBencodeValue(Data, Position, ItemEnd) Then Return False
                    Position = ItemEnd
                Loop
                If Position >= Data.Length Then Return False
                [End] = Position + 1
                Return True
            Case 100 'd
                Dim Position As Integer = Start + 1
                Do While Position < Data.Length AndAlso Data(Position) <> 101
                    Dim KeyEnd As Integer
                    If Not WarThunderSkipBencodeValue(Data, Position, KeyEnd) Then Return False
                    Dim ItemEnd As Integer
                    If Not WarThunderSkipBencodeValue(Data, KeyEnd, ItemEnd) Then Return False
                    Position = ItemEnd
                Loop
                If Position >= Data.Length Then Return False
                [End] = Position + 1
                Return True
            Case Else
                If Data(Start) < 48 OrElse Data(Start) > 57 Then Return False
                Dim Colon As Integer = Array.IndexOf(Data, CByte(58), Start)
                If Colon < 0 Then Return False
                Dim Length As Integer
                If Not Integer.TryParse(
                    Encoding.ASCII.GetString(Data, Start, Colon - Start), Length) OrElse
                   Length < 0 OrElse Colon + 1 + Length > Data.Length Then Return False
                [End] = Colon + 1 + Length
                Return True
        End Select
    End Function

    Private Function WarThunderEncodeBencodeValue(Value As Object) As Byte()
        Using Stream As New MemoryStream()
            WarThunderWriteBencodeValue(Stream, Value)
            Return Stream.ToArray()
        End Using
    End Function

    Private Sub WarThunderWriteBencodeValue(Stream As Stream, Value As Object)
        If Value Is Nothing Then Throw New InvalidDataException("Bencode 值不能为空。")
        Dim Bytes = TryCast(Value, Byte())
        If Bytes IsNot Nothing Then
            Dim Prefix = Encoding.ASCII.GetBytes(Bytes.Length.ToString() & ":")
            Stream.Write(Prefix, 0, Prefix.Length)
            Stream.Write(Bytes, 0, Bytes.Length)
            Return
        End If
        If TypeOf Value Is String Then
            WarThunderWriteBencodeValue(Stream, Encoding.UTF8.GetBytes(CStr(Value)))
            Return
        End If
        If TypeOf Value Is Integer OrElse TypeOf Value Is Long Then
            Dim Prefix = Encoding.ASCII.GetBytes("i" & Convert.ToInt64(Value).ToString() & "e")
            Stream.Write(Prefix, 0, Prefix.Length)
            Return
        End If
        Dim Values = TryCast(Value, List(Of Object))
        If Values IsNot Nothing Then
            Stream.WriteByte(108)
            For Each Item In Values
                WarThunderWriteBencodeValue(Stream, Item)
            Next
            Stream.WriteByte(101)
            Return
        End If
        Dim Dictionary = TryCast(Value, Dictionary(Of String, Object))
        If Dictionary IsNot Nothing Then
            Stream.WriteByte(100)
            For Each Pair In Dictionary.OrderBy(Function(Item) Item.Key, StringComparer.Ordinal)
                WarThunderWriteBencodeValue(Stream, Encoding.UTF8.GetBytes(Pair.Key))
                WarThunderWriteBencodeValue(Stream, Pair.Value)
            Next
            Stream.WriteByte(101)
            Return
        End If
        Throw New InvalidDataException($"不支持的 Bencode 类型：{Value.GetType().FullName}")
    End Sub

    Private Function WarThunderQueryTrackerPeers(
        InfoHash As Byte(),
        ListenPort As Integer) As List(Of IPEndPoint)
        Dim Endpoints As New List(Of IPEndPoint)
        For Each TrackerDefinition In {
            New KeyValuePair(Of String, Integer)("yuptracker.gaijinent.com", 27022),
            New KeyValuePair(Of String, Integer)("yuptracker-sa.gaijinent.com", 27029),
            New KeyValuePair(Of String, Integer)("yuptracker-sa.gaijinent.com", 27022)
        }
            Try
                For Each Address In Dns.GetHostAddresses(TrackerDefinition.Key)
                    If Address.AddressFamily = AddressFamily.InterNetwork Then
                        Endpoints.Add(New IPEndPoint(Address, TrackerDefinition.Value))
                    End If
                Next
            Catch ex As Exception
                Logger.Warn(ex, $"解析战争雷霆 tracker 失败：{TrackerDefinition.Key}")
            End Try
        Next
        Dim Queries = Endpoints.
            Distinct().
            Take(16).
            Select(
                Function(TrackerEndpoint)
                    Return Task.Run(
                        Function()
                            Try
                                Return WarThunderQueryUdpTracker(
                                    TrackerEndpoint, InfoHash, ListenPort)
                            Catch ex As Exception
                                Logger.Warn(ex, $"查询战争雷霆 P2P tracker 失败：{TrackerEndpoint}")
                                Return New List(Of IPEndPoint)
                            End Try
                        End Function)
                End Function).
            ToArray()
        Task.WhenAll(Queries).GetAwaiter().GetResult()
        For Each Query In Queries
            If Query.Result.Count > 0 Then Return Query.Result
        Next
        Return New List(Of IPEndPoint)
    End Function

    Private Function WarThunderQueryUdpTracker(
        TrackerEndpoint As IPEndPoint,
        InfoHash As Byte(),
        ListenPort As Integer) As List(Of IPEndPoint)
        Using Client As New UdpClient()
            Client.Client.ReceiveTimeout = 3000
            Dim Transaction As Integer = WarThunderRandomInteger()
            Dim ConnectRequest As Byte() = New Byte(15) {}
            WarThunderWriteUInt64BigEndian(ConnectRequest, 0, &H41727101980UL)
            WarThunderWriteInt32BigEndian(ConnectRequest, 8, 0)
            WarThunderWriteInt32BigEndian(ConnectRequest, 12, Transaction)
            Client.Send(ConnectRequest, ConnectRequest.Length, TrackerEndpoint)
            Dim Remote As New IPEndPoint(IPAddress.Any, 0)
            Dim ConnectResponse As Byte() = Client.Receive(Remote)
            If ConnectResponse.Length < 16 OrElse
               WarThunderReadInt32BigEndian(ConnectResponse, 0) <> 0 OrElse
               WarThunderReadInt32BigEndian(ConnectResponse, 4) <> Transaction Then
                Throw New InvalidDataException("Tracker 返回了无效的 UDP connect 响应。")
            End If
            Dim ConnectionId As ULong = WarThunderReadUInt64BigEndian(ConnectResponse, 8)

            Transaction = WarThunderRandomInteger()
            Dim Announce As Byte() = New Byte(97) {}
            WarThunderWriteUInt64BigEndian(Announce, 0, ConnectionId)
            WarThunderWriteInt32BigEndian(Announce, 8, 1)
            WarThunderWriteInt32BigEndian(Announce, 12, Transaction)
            Buffer.BlockCopy(InfoHash, 0, Announce, 16, Math.Min(20, InfoHash.Length))
            Buffer.BlockCopy(WarThunderCreateRandomPeerId(), 0, Announce, 36, 20)
            WarThunderWriteInt64BigEndian(Announce, 56, 0)
            WarThunderWriteInt64BigEndian(Announce, 64, 1)
            WarThunderWriteInt64BigEndian(Announce, 72, 0)
            WarThunderWriteInt32BigEndian(Announce, 80, 2)
            WarThunderWriteInt32BigEndian(Announce, 84, 0)
            WarThunderWriteInt32BigEndian(Announce, 88, WarThunderRandomInteger())
            WarThunderWriteInt32BigEndian(Announce, 92, 200)
            WarThunderWriteUInt16BigEndian(
                Announce, 96, CUShort(Math.Max(1, Math.Min(65535, ListenPort))))
            Client.Send(Announce, Announce.Length, TrackerEndpoint)
            Dim AnnounceResponse As Byte() = Client.Receive(Remote)
            If AnnounceResponse.Length < 20 OrElse
               WarThunderReadInt32BigEndian(AnnounceResponse, 0) <> 1 OrElse
               WarThunderReadInt32BigEndian(AnnounceResponse, 4) <> Transaction Then
                Throw New InvalidDataException("Tracker 返回了无效的 UDP announce 响应。")
            End If

            Dim Result As New List(Of IPEndPoint)
            For Offset As Integer = 20 To AnnounceResponse.Length - 6 Step 6
                Dim Address As New IPAddress(
                    AnnounceResponse.Skip(Offset).Take(4).ToArray())
                Dim Port As Integer =
                    (CInt(AnnounceResponse(Offset + 4)) << 8) Or
                    CInt(AnnounceResponse(Offset + 5))
                If Port > 0 AndAlso Not Address.Equals(IPAddress.Any) Then
                    Result.Add(New IPEndPoint(Address, Port))
                End If
            Next
            Return Result
        End Using
    End Function

    Private Function WarThunderCreateRandomPeerId() As Byte()
        Const Alphabet As String = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
        Dim Result As Byte() = New Byte(19) {}
        For Index As Integer = 0 To Result.Length - 1
            Result(Index) = CByte(AscW(Alphabet(WarThunderRandomInteger(0, Alphabet.Length))))
        Next
        Return Result
    End Function

    Private Function WarThunderRandomInteger() As Integer
        Dim Buffer As Byte() = New Byte(3) {}
        Using Random As RandomNumberGenerator = RandomNumberGenerator.Create()
            Random.GetBytes(Buffer)
        End Using
        Return BitConverter.ToInt32(Buffer, 0)
    End Function

    Private Function WarThunderRandomInteger(
        Minimum As Integer,
        MaximumExclusive As Integer) As Integer
        If MaximumExclusive <= Minimum Then Return Minimum
        Dim Value As UInteger = CUInt(WarThunderRandomInteger() And Integer.MaxValue)
        Dim Range As ULong = CULng(CLng(MaximumExclusive) - CLng(Minimum))
        Return CInt(CLng(Minimum) + CLng(Value Mod Range))
    End Function

    Private Sub WarThunderWriteUInt64BigEndian(Buffer As Byte(), Offset As Integer, Value As ULong)
        For Index As Integer = 7 To 0 Step -1
            Buffer(Offset + Index) = CByte(Value And &HFFUL)
            Value >>= 8
        Next
    End Sub

    Private Sub WarThunderWriteUInt16BigEndian(Buffer As Byte(), Offset As Integer, Value As UShort)
        Buffer(Offset) = CByte((Value >> 8) And &HFFUS)
        Buffer(Offset + 1) = CByte(Value And &HFFUS)
    End Sub

    Private Sub WarThunderWriteInt32BigEndian(Buffer As Byte(), Offset As Integer, Value As Integer)
        Dim Raw As ULong = CULng(CLng(Value) And &HFFFFFFFFL)
        Buffer(Offset) = CByte((Raw >> 24) And &HFFUI)
        Buffer(Offset + 1) = CByte((Raw >> 16) And &HFFUI)
        Buffer(Offset + 2) = CByte((Raw >> 8) And &HFFUI)
        Buffer(Offset + 3) = CByte(Raw And &HFFUI)
    End Sub

    Private Sub WarThunderWriteInt64BigEndian(Buffer As Byte(), Offset As Integer, Value As Long)
        WarThunderWriteUInt64BigEndian(Buffer, Offset, CULng(Value))
    End Sub

    Private Function WarThunderReadUInt64BigEndian(Buffer As Byte(), Offset As Integer) As ULong
        Dim Result As ULong = 0
        For Index As Integer = 0 To 7
            Result = (Result << 8) Or Buffer(Offset + Index)
        Next
        Return Result
    End Function

    Private Function WarThunderReadInt32BigEndian(Buffer As Byte(), Offset As Integer) As Integer
        Dim Padded As Byte() = {
            Buffer(Offset + 3), Buffer(Offset + 2),
            Buffer(Offset + 1), Buffer(Offset)
        }
        Return BitConverter.ToInt32(Padded, 0)
    End Function

    Private Sub WarThunderSetSelfUpdateState(
        Loader As LoaderBase,
        Progress As Double,
        Status As String,
        Speed As Long,
        TotalBytes As Long,
        RemainingFiles As Integer,
        Optional DownloadedBytes As Long = -1)
        Dim EffectiveDownloadedBytes As Long =
            If(DownloadedBytes >= 0, DownloadedBytes, WarThunderUpdateDownloadedBytes)
        Dim DisplayStatus As String = Status
        If Speed > 0 AndAlso TotalBytes > 0 AndAlso
           EffectiveDownloadedBytes >= 0 AndAlso
           EffectiveDownloadedBytes < TotalBytes Then
            Dim RemainingBytes As Long =
                Math.Max(0, TotalBytes - EffectiveDownloadedBytes)
            Dim EtaSeconds As Double =
                Math.Ceiling(RemainingBytes / CDbl(Speed))
            If EtaSeconds > 0 AndAlso EtaSeconds <= Long.MaxValue Then
                DisplayStatus &= "  预计剩余 " & WarThunderFormatEta(CLng(EtaSeconds))
            End If
        End If

        Loader.Progress = Math.Max(0, Math.Min(1, Progress))
        Loader.DownloadStatus = DisplayStatus
        Loader.DownloadSpeed = Math.Max(0, Speed)
        Loader.FilesRemaining = RemainingFiles
        WarThunderUpdateProgress = Loader.Progress
        WarThunderUpdateStatus = DisplayStatus
        WarThunderUpdateSpeed = Math.Max(0, Speed)
        If TotalBytes > 0 Then WarThunderUpdateTotalBytes = TotalBytes
        If DownloadedBytes >= 0 Then WarThunderUpdateDownloadedBytes = DownloadedBytes

        Dim Now As Date = Date.UtcNow
        If (Now - WarThunderLastStateUiRefresh).TotalMilliseconds >= 500 Then
            WarThunderLastStateUiRefresh = Now
            WarThunderRefreshAllUI()
        End If
    End Sub

    Private Function WarThunderFormatEta(Seconds As Long) As String
        If Seconds < 60 Then Return $"{Math.Max(1, Seconds)} 秒"
        If Seconds < 60 * 60 Then
            Dim Minutes As Long = Seconds \ 60
            Dim RemainingSeconds As Long = Seconds Mod 60
            Return If(
                RemainingSeconds = 0,
                $"{Minutes} 分钟",
                $"{Minutes} 分 {RemainingSeconds} 秒")
        End If
        If Seconds < 24 * 60 * 60 Then
            Dim Hours As Long = Seconds \ (60 * 60)
            Dim Minutes As Long = (Seconds Mod (60 * 60)) \ 60
            Return If(
                Minutes = 0,
                $"{Hours} 小时",
                $"{Hours} 小时 {Minutes} 分")
        End If
        Dim Days As Long = Seconds \ (24 * 60 * 60)
        Dim RemainingHours As Long = (Seconds Mod (24 * 60 * 60)) \ (60 * 60)
        Return If(
            RemainingHours = 0,
            $"{Days} 天",
            $"{Days} 天 {RemainingHours} 小时")
    End Function

    Private Sub WarThunderThrowIfSelfUpdateCanceled(
        Loader As LoaderTask(Of WarThunderInstallation, Boolean))
        If Loader.IsCanceled Then Throw New OperationCanceledException()
    End Sub

    Private Function WarThunderSameYupFile(
        OldEntry As WarThunderYupFile,
        NewEntry As WarThunderYupFile) As Boolean
        If OldEntry Is Nothing Then Return False
        If OldEntry.Length <> NewEntry.Length Then Return False
        If OldEntry.Sha1 Is Nothing OrElse NewEntry.Sha1 Is Nothing Then
            Return OldEntry.ModifiedTime = NewEntry.ModifiedTime
        End If
        Return OldEntry.Sha1.SequenceEqual(NewEntry.Sha1)
    End Function

    Private Function WarThunderLocalTimeMatchesYup(
        LocalPath As String,
        Entry As WarThunderYupFile) As Boolean
        If Entry.ModifiedTime <= 0 Then Return False
        Try
            Dim TargetTime As Date =
                DateTimeOffset.FromUnixTimeSeconds(Entry.ModifiedTime).UtcDateTime
            Return Math.Abs(
                (File.GetLastWriteTimeUtc(LocalPath) - TargetTime).TotalSeconds) <= 2
        Catch
            Return False
        End Try
    End Function

    Private Function WarThunderSelfUpdateLocalFileMatches(
        LocalPath As String,
        Entry As WarThunderYupFile) As Boolean
        If String.IsNullOrWhiteSpace(LocalPath) OrElse
           Entry Is Nothing OrElse Not File.Exists(LocalPath) Then
            Return False
        End If
        Try
            If New FileInfo(LocalPath).Length <> Entry.Length Then Return False
        Catch
            Return False
        End Try
        Return WarThunderLocalTimeMatchesYup(LocalPath, Entry)
    End Function

    Private Function WarThunderSelfUpdateLocalFileMatchesSha1(
        LocalPath As String,
        Entry As WarThunderYupFile) As Boolean
        If String.IsNullOrWhiteSpace(LocalPath) OrElse
           Entry Is Nothing OrElse Not File.Exists(LocalPath) Then
            Return False
        End If
        Try
            If New FileInfo(LocalPath).Length <> Entry.Length Then Return False
            If Entry.Sha1 Is Nothing OrElse Entry.Sha1.Length = 0 Then Return True
            Return WarThunderFileHashEquals(LocalPath, Entry.Sha1)
        Catch ex As Exception
            Logger.Warn(ex, $"校验本地游戏文件失败：{Entry.RelativePath}")
            Return False
        End Try
    End Function

    Private Function WarThunderSelfUpdateLocalFileCanBeReused(
        LocalPath As String,
        Entry As WarThunderYupFile) As Boolean
        If WarThunderSelfUpdateLocalFileMatches(LocalPath, Entry) Then Return True
        If String.IsNullOrWhiteSpace(LocalPath) OrElse
           Entry Is Nothing OrElse Not File.Exists(LocalPath) Then
            Return False
        End If
        Try
            If New FileInfo(LocalPath).Length <> Entry.Length Then Return False
        Catch
            Return False
        End Try
        If Entry.Sha1 Is Nothing OrElse Entry.Sha1.Length = 0 Then Return False
        Try
            Return WarThunderFileHashEquals(LocalPath, Entry.Sha1)
        Catch ex As Exception
            Logger.Warn(ex, $"校验本地更新压缩包失败：{Entry.RelativePath}")
            Return False
        End Try
    End Function

    Private Function WarThunderShouldProcessSelfUpdateEntry(
        RelativePath As String) As Boolean
        Dim PathText As String = RelativePath.Replace("\", "/").ToLowerInvariant()
        For Each Prefix In {
            "android/", "linux/", "linux64/", "mac/", "mac_app/", "mac_cef/",
            "mac_sign/", "ps4/", "ps5/", "scarlett/", "xbone/", "win-arm64ec/"
        }
            If PathText.StartsWith(Prefix, StringComparison.Ordinal) Then Return False
        Next
        For Each Name In {
            "frameworks.zip",
            "linux64/linux64.zip",
            "mac.zip",
            "mac_app.zip",
            "mac_cef_bin.zip",
            "mac_cef_framework.zip",
            "mac_sign.zip",
            "stcode_linux64.zip",
            "stcode_macosx.zip"
        }
            If PathText.Equals(Name, StringComparison.Ordinal) Then Return False
        Next
        If PathText.StartsWith("content/hc_pacific/", StringComparison.Ordinal) Then
            Return False
        End If
        If Not Environment.Is64BitOperatingSystem Then
            Return Not PathText.StartsWith("win64/", StringComparison.Ordinal) AndAlso
                   Not PathText.Equals("stcode_win64.zip", StringComparison.Ordinal)
        End If
        Dim ProcessorArchitecture As String =
            If(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "")
        If Not ProcessorArchitecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase) Then
            If PathText.StartsWith("win-arm64ec/", StringComparison.Ordinal) OrElse
               PathText.Equals("stcode_win-arm64ec.zip", StringComparison.Ordinal) Then
                Return False
            End If
        End If
        Return True
    End Function

    Private Function WarThunderZipTargetsExist(
        InstallPath As String,
        Entry As WarThunderYupFile) As Boolean
        If Entry.ZipEntries.Count = 0 Then Return True
        Dim ZipPath As String =
            WarThunderSafeInstallPath(InstallPath, Entry.RelativePath)
        If ZipPath = "" OrElse Not File.Exists(ZipPath) Then Return False
        Dim Folder As String = Path.GetDirectoryName(ZipPath)
        Try
            Using Archive As ZipArchive = ZipFile.OpenRead(ZipPath)
                Dim ArchiveEntries As New Dictionary(Of String, ZipArchiveEntry)(
                    StringComparer.OrdinalIgnoreCase)
                For Each ArchiveEntry In Archive.Entries
                    If Not ArchiveEntry.FullName.EndsWith("/", StringComparison.Ordinal) Then
                        ArchiveEntries(ArchiveEntry.FullName.Replace("\", "/")) = ArchiveEntry
                    End If
                Next
                For Each ZipEntry In Entry.ZipEntries
                    Dim RelativeEntryName As String = ZipEntry.Replace("\", "/")
                    Dim ArchiveEntry As ZipArchiveEntry = Nothing
                    If Not ArchiveEntries.TryGetValue(
                        RelativeEntryName, ArchiveEntry) Then
                        Return False
                    End If
                    Dim TargetPath As String =
                        WarThunderSafeInstallPath(Folder, RelativeEntryName)
                    If TargetPath = "" OrElse Not File.Exists(TargetPath) OrElse
                       New FileInfo(TargetPath).Length <> ArchiveEntry.Length Then
                        Return False
                    End If
                Next
            End Using
            Return True
        Catch ex As Exception
            Logger.Warn(ex, $"检查战争雷霆压缩包展开结果失败：{Entry.RelativePath}")
            Return False
        End Try
    End Function

    Private Function WarThunderShouldAddNewSelfUpdateFile(
        InstallPath As String,
        RelativePath As String) As Boolean
        Dim PathText As String = RelativePath.Replace("\", "/").ToLowerInvariant()
        ' compiledShaders 下的 .shdump.bin 是 Windows 客户端运行所需文件，
        ' 不能按扩展名或 Shader 名称过滤，否则官方启动器会再次补下载这些文件。
        For Each Prefix In {
            "android/", "linux/", "linux64/", "mac/", "mac_app/", "mac_cef/",
            "mac_sign/", "ps4/", "ps5/", "scarlett/", "xbone/", "win-arm64ec/"
        }
            If PathText.StartsWith(Prefix, StringComparison.Ordinal) Then Return False
        Next
        For Each FileName In {
            "frameworks.zip",
            "mac.zip",
            "mac_app.zip",
            "mac_cef_bin.zip",
            "mac_cef_framework.zip",
            "mac_sign.zip",
            "stcode_linux64.zip",
            "stcode_macosx.zip",
            "stcode_win-arm64ec.zip"
        }
            If PathText.Equals(FileName, StringComparison.Ordinal) Then Return False
        Next
        If PathText.StartsWith("content/hc_pacific/", StringComparison.Ordinal) Then
            Return False
        End If

        If PathText.StartsWith("content.hq/uhq_aircraft/", StringComparison.Ordinal) OrElse
           PathText.StartsWith("patch/content.hq/uhq_aircraft/", StringComparison.Ordinal) Then
            Return WarThunderPackEnabled(InstallPath, "pkg_uhq_aircraft")
        End If
        If PathText.StartsWith("content.hq/uhq_environment/", StringComparison.Ordinal) OrElse
           PathText.StartsWith("patch/content.hq/uhq_environment/", StringComparison.Ordinal) Then
            Return WarThunderPackEnabled(InstallPath, "pkg_uhq_environment")
        End If
        If PathText.StartsWith("content.hq/uhq_vehicles/", StringComparison.Ordinal) OrElse
           PathText.StartsWith("patch/content.hq/uhq_vehicles/", StringComparison.Ordinal) Then
            Return WarThunderPackEnabled(InstallPath, "pkg_uhq_vehicles")
        End If
        If PathText.StartsWith("content.hq/pkg_infantry", StringComparison.Ordinal) OrElse
           PathText.StartsWith("content/pkg_infantry", StringComparison.Ordinal) OrElse
           PathText.StartsWith("patch/content.hq/pkg_infantry", StringComparison.Ordinal) OrElse
           PathText.StartsWith("patch/content/pkg_infantry", StringComparison.Ordinal) OrElse
           PathText.StartsWith("levels/hvg_", StringComparison.Ordinal) OrElse
           PathText.StartsWith("sound/_infantry_", StringComparison.Ordinal) OrElse
           PathText.StartsWith("sound/infantry_", StringComparison.Ordinal) Then
            Return WarThunderPackEnabled(InstallPath, "pkg_infantry")
        End If

        If PathText.StartsWith("content.hq/hq_tex/", StringComparison.Ordinal) OrElse
           PathText.StartsWith("content.hq/pkg_cockpits/", StringComparison.Ordinal) OrElse
           PathText.StartsWith("patch/content.hq/hq_tex/", StringComparison.Ordinal) OrElse
           PathText.StartsWith("patch/content.hq/pkg_cockpits/", StringComparison.Ordinal) Then
            Return WarThunderPackEnabled(InstallPath, "pkg_main")
        End If
        If PathText.StartsWith("sound/", StringComparison.Ordinal) Then
            Dim SoundName As String = Path.GetFileName(PathText)
            If SoundName.StartsWith("_crew_dialogs_", StringComparison.Ordinal) OrElse
               SoundName.StartsWith("_dialogs_", StringComparison.Ordinal) OrElse
               SoundName.StartsWith("dialogs_chat_", StringComparison.Ordinal) Then
                Return WarThunderPackEnabled(InstallPath, "pkg_main")
            End If
        End If

        Dim LanguagePacks = {
            New KeyValuePair(Of String, String)("_zh.", "pkg_Chinese"),
            New KeyValuePair(Of String, String)("_cz.", "pkg_Czech"),
            New KeyValuePair(Of String, String)("_en.", "pkg_English"),
            New KeyValuePair(Of String, String)("_fr.", "pkg_French"),
            New KeyValuePair(Of String, String)("_de.", "pkg_German"),
            New KeyValuePair(Of String, String)("_hu.", "pkg_Hungarian"),
            New KeyValuePair(Of String, String)("_it.", "pkg_Italian"),
            New KeyValuePair(Of String, String)("_jp.", "pkg_Japanese"),
            New KeyValuePair(Of String, String)("_ko.", "pkg_Korean"),
            New KeyValuePair(Of String, String)("_pl.", "pkg_Polish"),
            New KeyValuePair(Of String, String)("_pt.", "pkg_Portuguese"),
            New KeyValuePair(Of String, String)("_ru.", "pkg_Russian"),
            New KeyValuePair(Of String, String)("_sp.", "pkg_Spanish"),
            New KeyValuePair(Of String, String)("_tr.", "pkg_Turkish"),
            New KeyValuePair(Of String, String)("_vi.", "pkg_Vietnamese")
        }
        If PathText.StartsWith("sound/", StringComparison.Ordinal) Then
            For Each LanguagePack In LanguagePacks
                If PathText.IndexOf(LanguagePack.Key, StringComparison.Ordinal) >= 0 Then
                    Return WarThunderPackEnabled(InstallPath, LanguagePack.Value)
                End If
            Next
        End If
        Return True
    End Function

    Private Function WarThunderPackEnabled(InstallPath As String, PackName As String) As Boolean
        Return File.Exists(Path.Combine(InstallPath, "content", PackName & ".rq2"))
    End Function

    Private Function WarThunderSafeInstallPath(RootPath As String, RelativePath As String) As String
        Try
            Dim Root As String = Path.GetFullPath(RootPath).TrimEnd("\"c) & "\"
            Dim Result As String =
                Path.GetFullPath(Path.Combine(Root, RelativePath.Replace("/", "\")))
            If Not Result.StartsWith(Root, StringComparison.OrdinalIgnoreCase) Then Return ""
            Return Result
        Catch
            Return ""
        End Try
    End Function

    Private Function WarThunderFileHashEquals(FilePath As String, Expected As Byte()) As Boolean
        If Expected Is Nothing OrElse Expected.Length = 0 Then Return True
        Using Stream As New FileStream(
            FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)
            Using Sha1 As SHA1 = SHA1.Create()
                Return Sha1.ComputeHash(Stream).SequenceEqual(Expected)
            End Using
        End Using
    End Function

    Private Sub WarThunderAtomicCopy(SourcePath As String, DestinationPath As String)
        Directory.CreateDirectory(Path.GetDirectoryName(DestinationPath))
        Try
            If File.Exists(DestinationPath) Then
                File.Replace(SourcePath, DestinationPath, Nothing, True)
            Else
                File.Move(SourcePath, DestinationPath)
            End If
        Catch ex As IOException
            Logger.Warn(ex, $"无法直接移动更新文件，将改为复制：{DestinationPath}")
            Dim TempPath As String = DestinationPath & ".ptl-new"
            FileUtils.Delete(TempPath)
            File.Copy(SourcePath, TempPath, True)
            WarThunderAtomicReplace(TempPath, DestinationPath)
            FileUtils.Delete(SourcePath)
        End Try
    End Sub

    Private Sub WarThunderAtomicReplace(TempPath As String, DestinationPath As String)
        If File.Exists(DestinationPath) Then
            File.Replace(TempPath, DestinationPath, Nothing, True)
        Else
            File.Move(TempPath, DestinationPath)
        End If
    End Sub

    Private Sub WarThunderSetFileTimestamp(FilePath As String, UnixTime As Long)
        If UnixTime <= 0 Then Return
        Try
            File.SetLastWriteTimeUtc(
                FilePath, DateTimeOffset.FromUnixTimeSeconds(UnixTime).UtcDateTime)
        Catch ex As Exception
            Logger.Warn(ex, $"设置战争雷霆文件时间失败：{FilePath}")
        End Try
    End Sub

End Module
