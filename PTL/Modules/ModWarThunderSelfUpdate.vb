Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
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

''' <summary>
''' PTL 内置的战争雷霆 YUP/P2P 更新实现。
''' </summary>
Friend Module ModWarThunderSelfUpdate

    Private Const YupMasterUrl As String = "https://yupmaster.gaijinent.com/yuitem/get_yup.php"
    Private Const CdnConfigUrl As String = "https://yupmaster.gaijinent.com/launcher/cdn_conf.php"
    Private Const DefaultTrackerUrl As String = "udp://yuptracker.gaijinent.com:27022/announce"
    Private Const BackupTrackerUrl As String = "udp://yuptracker-sa.gaijinent.com:27029/announce"
    Private Const CdnThreadLimit As Integer = 128
    Private Const CdnPrimarySeedLimit As Integer = 8
    Private WarThunderSelfUpdateDownloadRoot As String = ""

    Public Function WarThunderRunSelfUpdate(
        Install As WarThunderInstallation,
        Loader As LoaderTask(Of WarThunderInstallation, Boolean),
        RemoteVersion As String) As WarThunderSelfUpdateResult
        If Install Is Nothing OrElse Not Directory.Exists(Install.InstallPath) Then
            Throw New DirectoryNotFoundException("战争雷霆安装目录不存在。")
        End If
        If WarThunderGetGameProcessIds().Any() Then
            Throw New InvalidOperationException("请先关闭战争雷霆，再开始更新。")
        End If

        WarThunderSetSelfUpdateState(Loader, 0.01, "正在获取更新清单", 0, 0, -1)
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
        Dim OldManifest = WarThunderYupSnapshot.TryRead(ManifestPath)
        If OldManifest Is Nothing Then
            Throw New InvalidDataException("无法读取当前战争雷霆的 YUP 清单。")
        End If
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

    Private Function WarThunderCreateSelfUpdatePlan(
        InstallPath As String,
        OldManifest As WarThunderYupSnapshot,
        NewManifest As WarThunderYupSnapshot) As WarThunderSelfUpdatePlan
        Dim Result As New WarThunderSelfUpdatePlan
        For Each Entry In NewManifest.Files.Values
            If Entry.IsPatch Then Continue For

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
                LocalExists AndAlso LengthMatches AndAlso
                WarThunderLocalTimeMatchesYup(LocalPath, Entry)

            If (ManifestChanged OrElse Not LocalExists OrElse Not LengthMatches OrElse ZipNeedsRepair) AndAlso
               Not AlreadyTarget Then
                Result.RequiredFiles(Entry.RelativePath) = Entry
            End If
        Next
        Return Result
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
            $"战争雷霆更新将均衡使用前 {PrimarySeedCount} 个高优先级 CDN 端点。")

        Dim Files As New List(Of NetFile)
        Dim FileKeys As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Dim FileSizes As New Dictionary(Of String, Long)(StringComparer.OrdinalIgnoreCase)
        For Each Pair In Plan.RequiredFiles
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
            Dim Checker As New FileChecker With {
                .ActualSize = Pair.Value.Length,
                .Hash = WarThunderSha1Hex(Pair.Value.Sha1)
            }
            Files.Add(New NetFile(Sources, LocalPath, Checker) With {
                .DownloadTempDirectory = Path.Combine(CacheRoot, ".parts"),
                .PostProcess = Sub(TempPath)
                                   WarThunderAtomicReplace(TempPath, FinalPath)
                                   WarThunderSetFileTimestamp(
                                       FinalPath, Pair.Value.ModifiedTime)
                               End Sub
            })
            FileKeys(LocalPath) = Pair.Key
            FileSizes(LocalPath) = Pair.Value.Length
        Next
        If Files.Count = 0 Then
            Return Math.Max(
                0,
                WarThunderCompletedFileBytes(TotalPlan, Completed) -
                CompletedBytesAtStart)
        End If

        Dim CdnLoader As New LoaderDownload(
            $"通过 CDN 下载战争雷霆更新（{Files.Count} 个文件）", Files) With {
            .Show = False
        }
        Dim PreviousThreadLimitBoost As Integer = ModNet.NetTaskThreadLimitBoost
        ModNet.NetTaskThreadLimitBoost = Math.Max(
            PreviousThreadLimitBoost, CdnThreadLimit)
        Try
            CdnLoader.Start()
            Do While CdnLoader.State = LoadState.Loading
                If Loader.IsCanceled Then
                    CdnLoader.Cancel()
                    Throw New OperationCanceledException()
                End If
                Dim PassCompletedBytes As Long = 0
                For Each File In Files
                    Dim FileLength As Long = 0
                    If File.State = NetState.Finished AndAlso
                       FileSizes.TryGetValue(File.LocalPath, FileLength) Then
                        PassCompletedBytes += FileLength
                    End If
                Next
                Dim OverallDownloaded As Long = Math.Min(
                    TotalPlan.TotalBytes,
                    CompletedBytesBeforePass + PassCompletedBytes)
                Dim OverallProgress As Double = If(
                    TotalPlan.TotalBytes > 0,
                    Math.Max(0, Math.Min(
                        1, OverallDownloaded / TotalPlan.TotalBytes)),
                    1)
                WarThunderSetSelfUpdateState(
                    Loader,
                    0.1 + OverallProgress * 0.8,
                    "正在通过多路 CDN 下载游戏更新",
                    CdnLoader.DownloadSpeed,
                    TotalPlan.TotalBytes,
                    CdnLoader.FilesRemaining,
                    OverallDownloaded)
                Thread.Sleep(100)
            Loop
        Finally
            ModNet.NetTaskThreadLimitBoost = PreviousThreadLimitBoost
        End Try

        For Each File In Files
            Dim RelativePath As String = Nothing
            If File.State = NetState.Finished AndAlso
               FileKeys.TryGetValue(File.LocalPath, RelativePath) Then
                Completed.Add(RelativePath)
            End If
        Next
        If CdnLoader.State = LoadState.Failed Then
            Dim CdnErrorText As String =
                If(CdnLoader.Error Is Nothing, "", CdnLoader.Error.ToString())
            If CdnErrorText.Contains("磁盘空间不足") OrElse
               CdnErrorText.IndexOf(
                   "not enough space on the disk",
                   StringComparison.OrdinalIgnoreCase) >= 0 Then
                Throw New IOException(
                    $"游戏目录所在磁盘空间不足。临时下载目录：{CacheRoot}。请清理该磁盘后重试。")
            End If
            Dim RemainingInPass As Integer = 0
            For Each RelativePath In Plan.RequiredFiles.Keys
                If Not Completed.Contains(RelativePath) Then
                    RemainingInPass += 1
                End If
            Next
            Logger.Warn(CdnLoader.Error,
                $"战争雷霆 CDN 本轮下载未完成，剩余 {RemainingInPass} 个文件")
        End If
        Return Math.Max(
            0,
            WarThunderCompletedFileBytes(TotalPlan, Completed) -
            CompletedBytesAtStart)
    End Function

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
        Using Archive As ZipArchive = ZipFile.OpenRead(ZipPath)
            For Each Entry In Archive.Entries
                If Entry.FullName.EndsWith("/", StringComparison.Ordinal) Then Continue For
                If Not Wanted.Contains(Entry.FullName.Replace("\", "/")) Then Continue For
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
            Next
        End Using
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
        Loader.Progress = Math.Max(0, Math.Min(1, Progress))
        Loader.DownloadStatus = Status
        Loader.DownloadSpeed = Math.Max(0, Speed)
        Loader.FilesRemaining = RemainingFiles
        WarThunderUpdateProgress = Loader.Progress
        WarThunderUpdateStatus = Status
        WarThunderUpdateSpeed = Math.Max(0, Speed)
        If TotalBytes > 0 Then WarThunderUpdateTotalBytes = TotalBytes
        If DownloadedBytes >= 0 Then WarThunderUpdateDownloadedBytes = DownloadedBytes
    End Sub

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

    Private Function WarThunderZipTargetsExist(
        InstallPath As String,
        Entry As WarThunderYupFile) As Boolean
        If Entry.ZipEntries.Count = 0 Then Return True
        Dim Folder As String = Path.GetDirectoryName(
            WarThunderSafeInstallPath(InstallPath, Entry.RelativePath))
        For Each ZipEntry In Entry.ZipEntries
            Dim TargetPath As String = WarThunderSafeInstallPath(Folder, ZipEntry)
            If TargetPath = "" OrElse Not File.Exists(TargetPath) Then Return False
        Next
        Return True
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
