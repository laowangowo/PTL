Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Net.Http
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading

''' <summary>
''' 战争雷霆的安装类型。
''' </summary>
Public Enum WarThunderLaunchMode
    OfficialLauncher
    Steam
    BattleEyeDirect
    GameExecutable
End Enum

Public Enum WarThunderUpdateCheckState
    NotChecked
    Checking
    Updating
    UpToDate
    UpdateRequired
    Unknown
End Enum

''' <summary>
''' 一次战争雷霆安装探测的结果。
''' </summary>
Public Class WarThunderInstallation
    Public Property InstallPath As String = ""
    Public Property ExecutablePath As String = ""
    Public Property GameExecutablePath As String = ""
    Public Property LauncherPath As String = ""
    Public Property LaunchMode As WarThunderLaunchMode = WarThunderLaunchMode.OfficialLauncher
    Public Property SourceDescription As String = ""

    Public ReadOnly Property IsValid As Boolean
        Get
            If String.IsNullOrWhiteSpace(InstallPath) Then Return False
            Select Case LaunchMode
                Case WarThunderLaunchMode.Steam
                    Return True
                Case WarThunderLaunchMode.BattleEyeDirect
                    Return File.Exists(ExecutablePath)
                Case Else
                    Return File.Exists(ExecutablePath)
            End Select
        End Get
    End Property

    Public ReadOnly Property DisplayName As String
        Get
            Select Case LaunchMode
                Case WarThunderLaunchMode.Steam
                    Return "Steam 安装"
                Case WarThunderLaunchMode.BattleEyeDirect
                    If Path.GetFileName(ExecutablePath).Equals(
                        "beac_wt_mlauncher.exe", StringComparison.OrdinalIgnoreCase) Then
                        Return "官方客户端直启"
                    End If
                    Return "BattleEye 直启"
                Case WarThunderLaunchMode.GameExecutable
                    Return "游戏程序"
                Case Else
                    Return "官方启动器"
            End Select
        End Get
    End Property
End Class

''' <summary>
''' 战争雷霆安装探测与直接启动。
''' </summary>
Public Module ModWarThunder

    Private Const SteamAppId As String = "236390"
    Private Const SteamInstallRelativePath As String = "steamapps\common\War Thunder"
    Private Const WarThunderRegistryKey As String = "HKEY_CURRENT_USER\Software\PTL\WarThunderLauncher"
    Private Const LaunchWithProgramsSetting As String = "WarThunderLaunchWithPrograms"
    Private WarThunderScanLock As New Object
    Private WarThunderUpdateTaskRunning As Boolean = False
    Private WarThunderGameMonitorRunning As Boolean
    Private ReadOnly WarThunderGameMonitorLock As New Object()

    Public WarThunderCurrentInstall As WarThunderInstallation = Nothing
    Public WarThunderScanning As Boolean = False
    Public WarThunderGameRunning As Boolean = False
    Public WarThunderLastError As String = ""
    Public WarThunderUpdateState As WarThunderUpdateCheckState = WarThunderUpdateCheckState.NotChecked
    Public WarThunderLocalVersion As String = ""
    Public WarThunderRemoteVersion As String = ""
    Public WarThunderUpdateProgress As Double = 0
    Public WarThunderUpdateStatus As String = ""
    Public WarThunderUpdateSpeed As Long = 0
    Public WarThunderUpdateDownloadedBytes As Long = 0
    Public WarThunderUpdateTotalBytes As Long = 0

    ''' <summary>
    ''' 在后台探测战争雷霆，并在完成后刷新主页。
    ''' </summary>
    Public Sub WarThunderScanAsync(Optional Force As Boolean = False)
        SyncLock WarThunderScanLock
            If WarThunderScanning OrElse (Not Force AndAlso WarThunderCurrentInstall IsNot Nothing) Then Return
            WarThunderScanning = True
        End SyncLock
        WarThunderLastError = ""
        RunInNewThread(
        Sub()
            Try
                WarThunderCurrentInstall = WarThunderDetectInstallation()
                If WarThunderCurrentInstall IsNot Nothing Then
                    RegistryUtils.Write(WarThunderRegistryKey, "InstallPath", WarThunderCurrentInstall.InstallPath)
                    WarThunderUpdateState = WarThunderUpdateCheckState.NotChecked
                    WarThunderLog($"已找到战争雷霆：{WarThunderCurrentInstall.InstallPath}")
                    WarThunderRefreshCurrentPlayerAsync()
                    WarThunderPrepareBackgroundMusicAsync()
                    If WarThunderGetGameProcessIds().Any() Then WarThunderStartGameProcessWatcher()
                    If WarThunderCurrentInstall.LaunchMode <> WarThunderLaunchMode.Steam Then
                        WarThunderCheckUpdate(WarThunderCurrentInstall)
                    End If
                Else
                    WarThunderLog("未自动找到战争雷霆安装目录，请手动选择。")
                End If
            Catch ex As Exception
                WarThunderLastError = ex.GetDisplay(False)
                Logger.Warn(ex, "自动探测战争雷霆安装目录失败")
                WarThunderLog($"自动探测失败：{ex.GetDisplay(False)}")
            Finally
                SyncLock WarThunderScanLock
                    WarThunderScanning = False
                End SyncLock
                RunInUi(AddressOf WarThunderRefreshAllUI)
            End Try
        End Sub, "War Thunder Detect", ThreadPriority.AboveNormal)
    End Sub

    ''' <summary>
    ''' 自动查找官方客户端或 Steam 客户端中的战争雷霆。
    ''' </summary>
    Public Function WarThunderDetectInstallation() As WarThunderInstallation
        Dim SavedFolder As String = RegistryUtils.TryRead(WarThunderRegistryKey, "InstallPath", "").ToString()
        Dim Result As WarThunderInstallation = WarThunderCreateInstallation(SavedFolder)
        If Result IsNot Nothing Then
            Result.SourceDescription = "已保存的安装目录"
            Return Result
        End If

        'Gaijin 官方客户端的主要注册表位置
        For Each RegistryFolder In {
            "HKEY_CURRENT_USER\Software\Gaijin\WarThunder",
            "HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Gaijin\WarThunder",
            "HKEY_LOCAL_MACHINE\SOFTWARE\Gaijin\WarThunder"
        }
            For Each Entry In {"InstallPath", "InstallDir", "Dir", "Path"}
                Try
                    Dim Value = RegistryUtils.TryRead(RegistryFolder, Entry, "").ToString()
                    Result = WarThunderCreateInstallation(Value)
                    If Result IsNot Nothing Then
                        Result.SourceDescription = "系统注册表"
                        Return Result
                    End If
                Catch
                    '部分注册表位置不存在属正常情况
                End Try
            Next
        Next

        '常见安装位置
        Dim Candidates As New List(Of String) From {
            Path.GetDirectoryName(PathExe),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }
        For Each CommonPath In Candidates
            If String.IsNullOrWhiteSpace(CommonPath) Then Continue For
            If Path.GetFileName(CommonPath).Equals("War Thunder", StringComparison.OrdinalIgnoreCase) OrElse
               Path.GetFileName(CommonPath).Equals("WarThunder", StringComparison.OrdinalIgnoreCase) Then
                Result = WarThunderCreateInstallation(CommonPath)
                If Result IsNot Nothing Then
                    Result.SourceDescription = "常见安装目录"
                    Return Result
                End If
            End If
            Result = WarThunderCreateInstallation(Path.Combine(CommonPath, "Gaijin Entertainment", "War Thunder"))
            If Result IsNot Nothing Then
                Result.SourceDescription = "常见安装目录"
                Return Result
            End If
            Result = WarThunderCreateInstallation(Path.Combine(CommonPath, "War Thunder"))
            If Result IsNot Nothing Then
                Result.SourceDescription = "常见安装目录"
                Return Result
            End If
        Next

        '各磁盘下的常见位置
        Try
            For Each Drive In DriveInfo.GetDrives()
                If Not Drive.IsReady OrElse Drive.DriveType <> DriveType.Fixed Then Continue For
                Dim RootPath As String = Drive.RootDirectory.FullName
                For Each RelativePath In {
                    "WarThunder",
                    "War Thunder",
                    "Games\WarThunder",
                    "Games\War Thunder",
                    "Program Files\Gaijin Entertainment\War Thunder",
                    "Program Files (x86)\Gaijin Entertainment\War Thunder"
                }
                    Result = WarThunderCreateInstallation(Path.Combine(RootPath, RelativePath))
                    If Result IsNot Nothing Then
                        Result.SourceDescription = "磁盘扫描"
                        Return Result
                    End If
                Next
            Next
        Catch ex As Exception
            Logger.Warn(ex, "扫描磁盘查找战争雷霆安装目录失败")
        End Try

        'Steam 库
        Result = WarThunderDetectSteamInstallation()
        If Result IsNot Nothing Then Return Result

        Return Nothing
    End Function

    ''' <summary>
    ''' 检查一个目录，或将目录下方一层的战争雷霆文件夹转换为安装信息。
    ''' </summary>
    Public Function WarThunderCreateInstallation(Folder As String) As WarThunderInstallation
        Folder = WarThunderNormalizeFolder(Folder)
        If Folder = "" Then Return Nothing

        Dim Result = WarThunderCreateInstallationExact(Folder)
        If Result IsNot Nothing Then Return Result

        '允许用户选择 Gaijin Entertainment、Games 或盘符根目录
        Try
            If Directory.Exists(Folder) Then
                For Each ChildName In {"War Thunder", "WarThunder"}
                    Result = WarThunderCreateInstallationExact(Path.Combine(Folder, ChildName))
                    If Result IsNot Nothing Then Return Result
                Next
            End If
        Catch ex As Exception
            Logger.Warn(ex, $"检查战争雷霆子目录失败（{Folder}）")
        End Try
        Return Nothing
    End Function

    Private Function WarThunderCreateInstallationExact(Folder As String) As WarThunderInstallation
        If String.IsNullOrWhiteSpace(Folder) OrElse Not Directory.Exists(Folder) Then Return Nothing
        Dim LauncherPath As String = Path.Combine(Folder, "launcher.exe")
        Dim BattleEyePath As String = Path.Combine(Folder, "aces_BE.exe")
        Dim NativeLauncherPath As String = Path.Combine(Folder, "beac_wt_mlauncher.exe")
        Dim Win64GamePath As String = Path.Combine(Folder, "win64\aces.exe")
        Dim Win32GamePath As String = Path.Combine(Folder, "win32\aces.exe")
        Dim RootGamePath As String = Path.Combine(Folder, "aces.exe")
        Dim ParentFolder As String = Path.GetDirectoryName(Folder.TrimEnd("\"c))
        Dim SteamAppsFolder As String = If(String.IsNullOrWhiteSpace(ParentFolder), "",
                                            Path.GetDirectoryName(ParentFolder))
        Dim SteamManifestPath As String = If(String.IsNullOrWhiteSpace(SteamAppsFolder), "",
                                             Path.Combine(SteamAppsFolder, $"appmanifest_{SteamAppId}.acf"))

        Dim IsInsideSteamLibrary As Boolean =
            Folder.IndexOf("\steamapps\common\", StringComparison.OrdinalIgnoreCase) >= 0
        If File.Exists(SteamManifestPath) OrElse
           (IsInsideSteamLibrary AndAlso
            (File.Exists(LauncherPath) OrElse
             File.Exists(NativeLauncherPath) OrElse
             File.Exists(Win64GamePath) OrElse
             File.Exists(Win32GamePath))) Then
            Return New WarThunderInstallation With {
                .InstallPath = Folder,
                .ExecutablePath = If(
                    File.Exists(SteamManifestPath), SteamManifestPath, LauncherPath),
                .LauncherPath = LauncherPath,
                .LaunchMode = WarThunderLaunchMode.Steam,
                .SourceDescription = "Steam 游戏库"
            }
        End If

        Dim InstalledGamePath As String = If(File.Exists(Win64GamePath), Win64GamePath,
                                             If(File.Exists(Win32GamePath), Win32GamePath,
                                                If(File.Exists(RootGamePath), RootGamePath, "")))
        '新版本会把 win64\aces.exe 放在 win64\pc64.zip 中，因此不能要求该文件已解压。
        Dim GamePath As String = If(InstalledGamePath <> "", InstalledGamePath, Win64GamePath)
        '当前官方 launcher.exe 会优先启动 beac_wt_mlauncher.exe，再由它加载 win64\aces.exe。
        If File.Exists(NativeLauncherPath) Then
            Return New WarThunderInstallation With {
                .InstallPath = Folder,
                .ExecutablePath = NativeLauncherPath,
                .GameExecutablePath = GamePath,
                .LauncherPath = LauncherPath,
                .LaunchMode = WarThunderLaunchMode.BattleEyeDirect,
                .SourceDescription = "官方客户端直启"
            }
        End If

        If File.Exists(BattleEyePath) Then
            Return New WarThunderInstallation With {
                .InstallPath = Folder,
                .ExecutablePath = BattleEyePath,
                .GameExecutablePath = GamePath,
                .LauncherPath = LauncherPath,
                .LaunchMode = WarThunderLaunchMode.BattleEyeDirect,
                .SourceDescription = "BattleEye 直启"
            }
        End If

        If File.Exists(LauncherPath) Then
            Return New WarThunderInstallation With {
                .InstallPath = Folder,
                .ExecutablePath = LauncherPath,
                .LauncherPath = LauncherPath,
                .LaunchMode = WarThunderLaunchMode.OfficialLauncher,
                .SourceDescription = "Gaijin 官方客户端"
            }
        End If

        If GamePath <> "" Then
            Return New WarThunderInstallation With {
                .InstallPath = Folder,
                .ExecutablePath = GamePath,
                .GameExecutablePath = GamePath,
                .LauncherPath = LauncherPath,
                .LaunchMode = WarThunderLaunchMode.GameExecutable,
                .SourceDescription = "游戏可执行文件"
            }
        End If
        Return Nothing
    End Function

    Private Function WarThunderDetectSteamInstallation() As WarThunderInstallation
        Dim SteamRoot As String = WarThunderNormalizeFolder(RegistryUtils.TryRead(
            "HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", "").ToString())
        If SteamRoot = "" Then Return Nothing

        Dim Libraries As New List(Of String) From {SteamRoot}
        Dim LibraryFile As String = Path.Combine(SteamRoot, "steamapps\libraryfolders.vdf")
        If File.Exists(LibraryFile) Then
            Try
                Dim Text As String = File.ReadAllText(LibraryFile)
                For Each Match As Match In Regex.Matches(Text, """path""\s+""([^""]+)""", RegexOptions.IgnoreCase)
                    Dim LibraryPath As String = WarThunderNormalizeFolder(Match.Groups(1).Value.Replace("\\", "\"))
                    If LibraryPath <> "" AndAlso Not Libraries.Contains(LibraryPath) Then Libraries.Add(LibraryPath)
                Next
            Catch ex As Exception
                Logger.Warn(ex, "读取 Steam 游戏库列表失败")
            End Try
        End If

        For Each LibraryPath In Libraries
            Try
                Dim InstallFolder As String = Path.Combine(LibraryPath, SteamInstallRelativePath)
                Dim ManifestPath As String = Path.Combine(LibraryPath, $"steamapps\appmanifest_{SteamAppId}.acf")
                If File.Exists(ManifestPath) AndAlso Directory.Exists(InstallFolder) Then
                    Dim Result = WarThunderCreateInstallationExact(InstallFolder)
                    If Result IsNot Nothing Then
                        Result.LaunchMode = WarThunderLaunchMode.Steam
                        Result.ExecutablePath = ManifestPath
                        Result.LauncherPath = Path.Combine(InstallFolder, "launcher.exe")
                        Result.SourceDescription = "Steam 游戏库"
                        Return Result
                    End If
                End If
            Catch ex As Exception
                Logger.Warn(ex, $"检查 Steam 游戏库中的战争雷霆失败（{LibraryPath}）")
            End Try
        Next
        Return Nothing
    End Function

    Private Function WarThunderNormalizeFolder(Folder As String) As String
        If String.IsNullOrWhiteSpace(Folder) Then Return ""
        Try
            Folder = Folder.Trim().Trim(""""c).Replace("/", "\")
            If Folder.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then
                Folder = Path.GetDirectoryName(Folder)
            End If
            If String.IsNullOrWhiteSpace(Folder) Then Return ""
            Return Path.GetFullPath(Folder).TrimEnd("\"c)
        Catch
            Return ""
        End Try
    End Function

    ''' <summary>
    ''' 保存用户手动选择的战争雷霆目录。
    ''' </summary>
    Public Function WarThunderSetInstallFolder(Folder As String) As Boolean
        Dim Result = WarThunderCreateInstallation(Folder)
        If Result Is Nothing Then Return False
        Result.SourceDescription = "用户选择的安装目录"
        WarThunderCurrentInstall = Result
        WarThunderLastError = ""
        WarThunderUpdateState = WarThunderUpdateCheckState.NotChecked
        WarThunderLocalVersion = ""
        WarThunderRemoteVersion = ""
        WarThunderUpdateProgress = 0
        WarThunderUpdateStatus = ""
        WarThunderUpdateSpeed = 0
        WarThunderUpdateDownloadedBytes = 0
        WarThunderUpdateTotalBytes = 0
        RegistryUtils.Write(WarThunderRegistryKey, "InstallPath", Result.InstallPath)
        WarThunderLog($"已设置战争雷霆安装目录：{Result.InstallPath}")
        WarThunderPrepareBackgroundMusicAsync()
        WarThunderRefreshAllUI()
        Return True
    End Function

    ''' <summary>
    ''' 获取随战争雷霆一起启动的程序列表。
    ''' </summary>
    Public Function WarThunderGetLaunchWithPrograms() As List(Of String)
        Dim Result As New List(Of String)
        Try
            Dim RawValue As String =
                Settings.Get(Of String)(LaunchWithProgramsSetting)
            If String.IsNullOrWhiteSpace(RawValue) Then Return Result
            Dim Items As JArray = JArray.Parse(RawValue)
            For Each Item As JToken In Items
                Dim ProgramPath As String = Item.Value(Of String)()
                If String.IsNullOrWhiteSpace(ProgramPath) Then Continue For
                AddLaunchWithProgramPath(Result, ProgramPath)
            Next
        Catch ex As Exception
            Logger.Warn(ex, "读取随雷启动程序列表失败")
        End Try
        Return Result
    End Function

    Public Sub WarThunderSetLaunchWithPrograms(Paths As IEnumerable(Of String))
        Dim Normalized As New List(Of String)
        If Paths IsNot Nothing Then
            For Each ProgramPath As String In Paths
                AddLaunchWithProgramPath(Normalized, ProgramPath)
                If Normalized.Count >= 32 Then Exit For
            Next
        End If
        Settings.Set(
            LaunchWithProgramsSetting,
            New JArray(Normalized.Cast(Of Object)().ToArray()).ToString(
                Newtonsoft.Json.Formatting.None))
    End Sub

    Private Sub AddLaunchWithProgramPath(
        Items As List(Of String),
        ProgramPath As String)
        Try
            If String.IsNullOrWhiteSpace(ProgramPath) Then Return
            Dim FullPath As String =
                Path.GetFullPath(ProgramPath.Trim().Trim(""""c))
            If Not Path.GetExtension(FullPath).Equals(
                ".exe", StringComparison.OrdinalIgnoreCase) Then Return
            If Items.Any(Function(ExistingPath)
                             Return ExistingPath.Equals(
                                 FullPath, StringComparison.OrdinalIgnoreCase)
                         End Function) Then
                Return
            End If
            Items.Add(FullPath)
        Catch ex As Exception
            Logger.Warn(ex, $"忽略无效的随雷启动程序路径：{ProgramPath}")
        End Try
    End Sub

    Private Sub WarThunderStartLaunchWithPrograms()
        For Each ProgramPath As String In WarThunderGetLaunchWithPrograms()
            Try
                If Not File.Exists(ProgramPath) Then
                    WarThunderLog($"随雷启动程序不存在，已跳过：{ProgramPath}")
                    Continue For
                End If
                Process.Start(New ProcessStartInfo With {
                    .FileName = ProgramPath,
                    .WorkingDirectory = Path.GetDirectoryName(ProgramPath),
                    .UseShellExecute = True
                })
                WarThunderLog($"已启动随雷程序：{ProgramPath}")
            Catch ex As Exception
                Logger.Warn(ex, $"启动随雷程序失败：{ProgramPath}")
                WarThunderLog($"启动随雷程序失败：{ProgramPath} · {ex.Message}")
            End Try
        Next
    End Sub

    ''' <summary>
    ''' 检查更新后启动战争雷霆。确认需要更新时启动官方启动器，无法读取本地版本时仍尝试直接启动。
    ''' </summary>
    Public Function WarThunderStart(ByRef ErrorMessage As String) As Boolean
        ErrorMessage = ""
        If WarThunderCurrentInstall Is Nothing OrElse Not WarThunderCurrentInstall.IsValid Then
            ErrorMessage = "没有找到有效的战争雷霆安装目录。"
            Return False
        End If

        Try
            Dim Install = WarThunderCurrentInstall
            If Install.LaunchMode = WarThunderLaunchMode.Steam Then
                WarThunderUpdateState = WarThunderUpdateCheckState.Unknown
                Return WarThunderLaunchSteam(Install, ErrorMessage)
            End If

            If Install.LaunchMode = WarThunderLaunchMode.BattleEyeDirect Then
                Dim UpdateResult As WarThunderUpdateCheckState = WarThunderCheckUpdate(Install)
                If UpdateResult = WarThunderUpdateCheckState.UpToDate OrElse
                   UpdateResult = WarThunderUpdateCheckState.Unknown Then
                    If WarThunderNeedsCoreRepair(Install) Then
                        If WarThunderStartDirectUpdate(Install, ErrorMessage, True) Then Return True
                        ErrorMessage = "检测到战争雷霆核心文件缺失，但 PTL 自动修复启动失败。"
                        WarThunderLog(ErrorMessage)
                        Return False
                    End If
                    If UpdateResult = WarThunderUpdateCheckState.UpToDate Then
                        Return WarThunderLaunchBattleEye(Install, ErrorMessage)
                    End If
                End If
                If UpdateResult = WarThunderUpdateCheckState.UpdateRequired Then
                    If WarThunderStartDirectUpdate(Install, ErrorMessage) Then Return True
                    ErrorMessage = $"检测到新版本 {WarThunderRemoteVersion}，但 PTL 更新器启动失败。"
                    WarThunderLog(ErrorMessage)
                    Return False
                End If
                ErrorMessage = "无法读取本地版本，仍直接启动游戏。"
                WarThunderLog(ErrorMessage)
                Return WarThunderLaunchBattleEye(Install, ErrorMessage)
            End If

            If Install.LaunchMode = WarThunderLaunchMode.OfficialLauncher Then
                ErrorMessage = "正在启动官方启动器（会自动检查并安装更新）。"
                ErrorMessage = "未找到 aces_BE.exe，PTL 不会启动官方 launcher.exe。请重新选择战争雷霆安装目录。"
                Return False
            End If

            ErrorMessage = "未找到 beac_wt_mlauncher.exe、aces_BE.exe 或官方启动器，无法安全启动游戏。"
            WarThunderLog(ErrorMessage)
            Return False
        Catch ex As Exception
            ErrorMessage = ex.Message
            Logger.Error(ex, "启动战争雷霆失败")
            WarThunderLog($"启动失败：{ex.GetDisplay(False)}")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 调用 Gaijin 版本接口，并与最近一次官方启动日志中的本地版本比较。
    ''' </summary>
    Private Function WarThunderCheckUpdate(Install As WarThunderInstallation) As WarThunderUpdateCheckState
        WarThunderUpdateState = WarThunderUpdateCheckState.Checking
        WarThunderLocalVersion = ""
        WarThunderRemoteVersion = ""
        WarThunderLocalVersion = WarThunderGetLocalVersion(Install)
        WarThunderRefreshAllUI()
        Try
            Dim ServerCircuit As String = WarThunderGetServerCircuit(Install.InstallPath)
            Dim ServerName As String = If(
                ServerCircuit.Equals("production", StringComparison.OrdinalIgnoreCase),
                "正式服", "测试服")
            WarThunderLog($"正在通过 Gaijin 版本接口检查{ServerName}游戏更新…")
            Dim RemoteText As String = WarThunderFetchRemoteVersion(ServerCircuit)
            Dim RemoteMatch = Regex.Match(RemoteText, "^\s*([0-9]+(?:\.[0-9]+){3})\s*$")
            If Not RemoteMatch.Success Then
                WarThunderUpdateState = WarThunderUpdateCheckState.Unknown
                WarThunderLog($"版本接口返回了无法识别的数据：{RemoteText}")
                Return WarThunderUpdateCheckState.Unknown
            End If
            WarThunderRemoteVersion = RemoteMatch.Groups(1).Value

            WarThunderLocalVersion = WarThunderGetLocalVersion(Install)
            If WarThunderLocalVersion = "" Then
                WarThunderUpdateState = WarThunderUpdateCheckState.Unknown
                WarThunderLog("未能在官方启动日志中找到本地游戏版本。")
                Return WarThunderUpdateCheckState.Unknown
            End If
            RegistryUtils.Write(WarThunderRegistryKey, "LastKnownVersion", WarThunderLocalVersion)

            Dim Remote As Version = Nothing, Local As Version = Nothing
            If Not Version.TryParse(WarThunderRemoteVersion, Remote) OrElse
               Not Version.TryParse(WarThunderLocalVersion, Local) Then
                WarThunderUpdateState = WarThunderUpdateCheckState.Unknown
                WarThunderLog($"版本号格式无法解析：本地 {WarThunderLocalVersion}，线上 {WarThunderRemoteVersion}")
                Return WarThunderUpdateCheckState.Unknown
            End If

            If Remote > Local Then
                WarThunderUpdateState = WarThunderUpdateCheckState.UpdateRequired
                WarThunderLog($"检测到游戏更新：{WarThunderLocalVersion} → {WarThunderRemoteVersion}")
                Return WarThunderUpdateCheckState.UpdateRequired
            End If

            Dim IntegrityDetail As String = ""
            If Not WarThunderCheckLocalContent(Install.InstallPath, IntegrityDetail) Then
                WarThunderUpdateState = WarThunderUpdateCheckState.UpdateRequired
                WarThunderLog(
                    $"版本号相同，但本地游戏文件尚未完成更新：{IntegrityDetail}")
                Return WarThunderUpdateCheckState.UpdateRequired
            End If

            WarThunderUpdateState = WarThunderUpdateCheckState.UpToDate
            WarThunderLog($"游戏已是最新版本：{WarThunderLocalVersion}")
            Return WarThunderUpdateCheckState.UpToDate
        Catch ex As Exception
            WarThunderUpdateState = WarThunderUpdateCheckState.Unknown
            WarThunderLog($"检查游戏更新失败：{ex.GetDisplay(False)}")
            Logger.Warn(ex, "检查战争雷霆更新失败")
            Return WarThunderUpdateCheckState.Unknown
        Finally
            WarThunderRefreshAllUI()
        End Try
    End Function

    Private Function WarThunderNeedsCoreRepair(Install As WarThunderInstallation) As Boolean
        If Install Is Nothing OrElse Install.LaunchMode <> WarThunderLaunchMode.BattleEyeDirect Then
            Return False
        End If
        If Not File.Exists(Path.Combine(Install.InstallPath, "aces.vromfs.bin")) Then Return True
        If Not File.Exists(Path.Combine(Install.InstallPath, "stcode_win64.zip")) Then Return True

        Dim GamePackage As String
        If Environment.Is64BitOperatingSystem Then
            GamePackage = Path.Combine(Install.InstallPath, "win64\pc64.zip")
            If File.Exists(Path.Combine(Install.InstallPath, "win64\aces.exe")) Then Return False
        Else
            GamePackage = Path.Combine(Install.InstallPath, "win32\pc.zip")
            If File.Exists(Path.Combine(Install.InstallPath, "win32\aces.exe")) Then Return False
        End If
        Return Not File.Exists(GamePackage)
    End Function

    Private Function WarThunderGetServerCircuit(InstallPath As String) As String
        Try
            Dim Settings = WarThunderLoadLaunchSettings(InstallPath)
            If Not String.IsNullOrWhiteSpace(Settings.ServerCircuit) Then
                Return Settings.ServerCircuit.Trim()
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读取战争雷霆服务器通道失败")
        End Try
        Return "production"
    End Function

    Private Function WarThunderFetchRemoteVersion(ServerCircuit As String) As String
        Using Client As New HttpClient()
            Client.Timeout = TimeSpan.FromSeconds(12)
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("PTL/" & PtlVersion)
            Dim UpdateTag As String = If(
                ServerCircuit.Equals("production", StringComparison.OrdinalIgnoreCase),
                "", ServerCircuit)
            Dim Response As String = Client.GetStringAsync(
                "https://yupmaster.gaijinent.com/yuitem/get_version.php?proj=warthunder&tag=" &
                Uri.EscapeDataString(UpdateTag)).GetAwaiter().GetResult()
            Return Response.Trim()
        End Using
    End Function

    Private Function WarThunderGetLocalVersion(Install As WarThunderInstallation) As String
        Try
            Dim ManifestPath As String = Path.Combine(Install.InstallPath, "warthunder.yup")
            Dim ManifestVersion As String = WarThunderYupSnapshot.TryRead(ManifestPath)?.LocalVersion
            If Not String.IsNullOrWhiteSpace(ManifestVersion) Then Return ManifestVersion.Trim()
        Catch ex As Exception
            Logger.Warn(ex, "读取战争雷霆 YUP 本地版本失败")
        End Try

        Dim LogFolder As String = Path.Combine(Install.InstallPath, ".launcher_log")
        If Directory.Exists(LogFolder) Then
            Try
                Dim AllLogFiles = Directory.GetFiles(LogFolder, "*.txt")
                Dim RecentLogFiles = AllLogFiles.OrderByDescending(
                    Function(path) File.GetLastWriteTimeUtc(path)).Take(30)
                For Each LogFile In RecentLogFiles
                    Try
                        Dim LogText As String = WarThunderReadSharedText(LogFile)
                        Dim VersionMatch = Regex.Match(LogText, "game version:\s*([0-9]+(?:\.[0-9]+){3})")
                        If Not VersionMatch.Success Then
                            VersionMatch = Regex.Match(LogText,
                                "yup launcher_settings section mount:\s*SUCCESS\s+appVersion\s+([0-9]+(?:\.[0-9]+){3})")
                        End If
                        If VersionMatch.Success Then Return VersionMatch.Groups(1).Value
                    Catch ex As Exception
                        Logger.Warn(ex, $"读取战争雷霆本地版本日志失败：{LogFile}")
                    End Try
                Next
            Catch ex As Exception
                Logger.Warn(ex, "扫描战争雷霆本地版本日志失败")
            End Try
        End If
        Return RegistryUtils.TryRead(WarThunderRegistryKey, "LastKnownVersion", "").ToString()
    End Function

    Private Function WarThunderReadSharedText(FilePath As String) As String
        Using Stream As New FileStream(FilePath, FileMode.Open, FileAccess.Read,
                                        FileShare.ReadWrite Or FileShare.Delete)
            Using Reader As New StreamReader(Stream, Encoding.UTF8, True)
                Return Reader.ReadToEnd()
            End Using
        End Using
    End Function

    Private Function WarThunderLaunchBattleEye(Install As WarThunderInstallation,
                                               ByRef Message As String) As Boolean
        If Not File.Exists(Install.ExecutablePath) Then
            Message = "反作弊启动组件不存在。"
            WarThunderUpdateState = WarThunderUpdateCheckState.Unknown
            Return False
        End If
        Dim ContentStateError As String = ""
        If Not WarThunderPrepareContentState(
            Install.InstallPath, ContentStateError) Then
            Message = "准备游戏完整性状态失败：" & ContentStateError
            WarThunderUpdateState = WarThunderUpdateCheckState.Unknown
            WarThunderLog(Message)
            Return False
        End If
        If Not WarThunderRestoreSelectedAuthForLaunch(Message) Then Return False

        '官方启动器当前同样只传入以下参数；游戏可执行文件可能位于 win64\pc64.zip。
        Dim Arguments As String = "-forcestart -nosteam"
        Process.Start(New ProcessStartInfo With {
            .FileName = Install.ExecutablePath,
            .Arguments = Arguments,
            .WorkingDirectory = Install.InstallPath,
            .UseShellExecute = True
        })
        Message = $"游戏已是最新版本 {WarThunderLocalVersion}，正在直接启动。"
        WarThunderLog($"已通过 {Path.GetFileName(Install.ExecutablePath)} 启动：{Arguments}")
        WarThunderStartLaunchWithPrograms()
        WarThunderStartGameProcessWatcher()
        Return True
    End Function

    ''' <summary>
    ''' 通过 Steam 客户端或 steam:// 协议启动战争雷霆。
    ''' </summary>
    Private Function WarThunderLaunchSteam(
        Install As WarThunderInstallation,
        ByRef Message As String) As Boolean
        Dim SteamExecutable As String = WarThunderFindSteamExecutable()
        Dim DirectError As Exception = Nothing
        If SteamExecutable <> "" Then
            Try
                Dim SteamProcess As Process = Process.Start(New ProcessStartInfo With {
                    .FileName = SteamExecutable,
                    .Arguments = $"-applaunch {SteamAppId}",
                    .WorkingDirectory = Path.GetDirectoryName(SteamExecutable),
                    .UseShellExecute = False
                })
                If SteamProcess Is Nothing Then
                    Throw New InvalidOperationException("Steam 客户端没有返回进程。")
                End If
                Message = "已请求 Steam 启动战争雷霆，游戏更新由 Steam 自动处理。"
                WarThunderLog($"已通过 Steam 启动战争雷霆：{SteamExecutable} -applaunch {SteamAppId}")
                WarThunderStartLaunchWithPrograms()
                WarThunderStartGameProcessWatcher()
                WarThunderRefreshAllUI()
                Return True
            Catch ex As Exception
                DirectError = ex
                Logger.Warn(ex, "通过 Steam 可执行文件启动战争雷霆失败，尝试协议链接")
            End Try
        End If

        Try
            Process.Start(New ProcessStartInfo With {
                .FileName = $"steam://rungameid/{SteamAppId}",
                .UseShellExecute = True
            })
            Message = "已请求 Steam 启动战争雷霆，游戏更新由 Steam 自动处理。"
            WarThunderLog($"已通过 Steam 协议启动战争雷霆：{Install.InstallPath}")
            WarThunderStartLaunchWithPrograms()
            WarThunderStartGameProcessWatcher()
            WarThunderRefreshAllUI()
            Return True
        Catch ex As Exception
            Message = "无法调用 Steam 启动战争雷霆。"
            WarThunderLog(
                $"{Message} 可执行文件错误：{DirectError?.GetDisplay(False)}；协议错误：{ex.GetDisplay(False)}")
            Logger.Error(ex, "通过 Steam 协议启动战争雷霆失败")
            WarThunderRefreshAllUI()
            Return False
        End Try
    End Function

    Private Function WarThunderFindSteamExecutable() As String
        Dim SteamRoot As String = WarThunderNormalizeFolder(RegistryUtils.TryRead(
            "HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", "").ToString())
        If SteamRoot <> "" Then
            Dim SteamExecutable As String = Path.Combine(SteamRoot, "steam.exe")
            If File.Exists(SteamExecutable) Then Return SteamExecutable
        End If
        For Each BaseFolder In {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        }
            If String.IsNullOrWhiteSpace(BaseFolder) Then Continue For
            Dim SteamExecutable As String = Path.Combine(BaseFolder, "Steam", "steam.exe")
            If File.Exists(SteamExecutable) Then Return SteamExecutable
        Next
        Return ""
    End Function

    ''' <summary>
    ''' 当前仍在运行的战争雷霆进程 PID。
    ''' </summary>
    Public Function WarThunderGetGameProcessIds() As List(Of Integer)
        Dim Result As New List(Of Integer)
        For Each ProcessName In {"aces", "aces_BE", "beac_wt_mlauncher"}
            For Each GameProcess As Process In Process.GetProcessesByName(ProcessName)
                Try
                    Result.Add(GameProcess.Id)
                Catch
                Finally
                    GameProcess.Dispose()
                End Try
            Next
        Next
        Return Result
    End Function

    ''' <summary>
    ''' 反作弊启动组件可能很快退出并把实际游戏交给 aces.exe，因此持续监听游戏进程。
    ''' </summary>
    Public Sub WarThunderStartGameProcessWatcher()
        SyncLock WarThunderGameMonitorLock
            If WarThunderGameMonitorRunning Then Return
            WarThunderGameMonitorRunning = True
        End SyncLock
        RunInNewThread(
        Sub()
            Dim ProcessSeen As Boolean = WarThunderGetGameProcessIds().Any()
            Dim WaitUntil As Date = Date.UtcNow.AddMinutes(2)
            Dim MissingSince As Date? = Nothing
            Try
                Do
                    Dim IsRunning As Boolean = WarThunderGetGameProcessIds().Any()
                    If IsRunning Then
                        ProcessSeen = True
                        MissingSince = Nothing
                        If Not WarThunderGameRunning Then
                            WarThunderGameRunning = True
                            WarThunderLog("检测到战争雷霆游戏进程，启动按钮已禁用。")
                            WarThunderApplyMusicOnGameStarted()
                            WarThunderRefreshAllUI()
                        End If
                    ElseIf WarThunderGameRunning Then
                        If Not MissingSince.HasValue Then
                            MissingSince = Date.UtcNow
                        ElseIf Date.UtcNow - MissingSince.Value >= TimeSpan.FromSeconds(10) Then
                            WarThunderGameRunning = False
                            Dim ExitedUserId As String = WarThunderGetActiveUserId()
                            If ExitedUserId <> "" Then
                                Dim AuthError As String = ""
                                If WarThunderCaptureCurrentAuthSnapshot(
                                    ExitedUserId, AuthError) Then
                                    WarThunderLog(
                                        $"已保存玩家 UID {ExitedUserId} 的游戏登录快照。")
                                Else
                                    WarThunderLog(
                                        $"保存玩家 UID {ExitedUserId} 登录快照失败：{AuthError}")
                                End If
                            End If
                            WarThunderLog("战争雷霆游戏进程已退出，启动按钮已恢复。")
                            WarThunderApplyMusicOnGameStopped()
                            WarThunderRefreshAllUI()
                            Exit Do
                        End If
                    ElseIf ProcessSeen OrElse Date.UtcNow >= WaitUntil Then
                        Exit Do
                    End If
                    Thread.Sleep(800)
                Loop
            Catch ex As Exception
                Logger.Warn(ex, "监听战争雷霆游戏进程失败")
            Finally
                WarThunderGameRunning = WarThunderGetGameProcessIds().Any()
                SyncLock WarThunderGameMonitorLock
                    WarThunderGameMonitorRunning = False
                End SyncLock
                WarThunderRefreshAllUI()
            End Try
        End Sub, "War Thunder Game Monitor", ThreadPriority.BelowNormal)
    End Sub

    Public Function WarThunderKillGameProcesses() As Integer
        Dim Count As Integer
        For Each ProcessId As Integer In WarThunderGetGameProcessIds()
            Try
                Using GameProcess As Process = Process.GetProcessById(ProcessId)
                    If Not GameProcess.HasExited Then
                        GameProcess.Kill()
                        Count += 1
                    End If
                End Using
            Catch ex As Exception
                Logger.Warn(ex, $"结束战争雷霆进程失败（PID {ProcessId}）")
            End Try
        Next
        WarThunderGameRunning = WarThunderGetGameProcessIds().Any()
        WarThunderRefreshAllUI()
        Return Count
    End Function

    Private Sub WarThunderApplyMusicOnGameStarted()
        RunInUi(
        Sub()
            If Settings.Get(Of Boolean)("UiMusicStop") Then
                If MusicPause() Then Logger.Info("已根据设置，在战争雷霆启动后暂停背景音乐")
            ElseIf Settings.Get(Of Boolean)("UiMusicStart") Then
                If MusicResume() Then Logger.Info("已根据设置，在战争雷霆启动后播放背景音乐")
            End If
        End Sub)
    End Sub

    Private Sub WarThunderApplyMusicOnGameStopped()
        RunInUi(
        Sub()
            If Settings.Get(Of Boolean)("UiMusicStop") Then
                If MusicResume() Then Logger.Info("已根据设置，在战争雷霆退出后恢复背景音乐")
            ElseIf Settings.Get(Of Boolean)("UiMusicStart") Then
                If MusicPause() Then Logger.Info("已根据设置，在战争雷霆退出后暂停背景音乐")
            End If
        End Sub)
    End Sub

    ''' <summary>
    ''' 使用 PTL 内置的 YUP/P2P 更新引擎完成游戏更新。
    ''' </summary>
    Private Function WarThunderStartDirectUpdate(Install As WarThunderInstallation,
                                                 ByRef Message As String,
                                                 Optional RepairOnly As Boolean = False) As Boolean
        If WarThunderUpdateTaskRunning Then
            Message = "战争雷霆更新组件正在运行。"
            Return True
        End If
        Dim ManifestPath As String = Path.Combine(Install.InstallPath, "warthunder.yup")
        If Not File.Exists(ManifestPath) Then
            Message = "未找到战争雷霆 YUP 清单。"
            WarThunderLog(Message)
            Return False
        End If
        Dim TargetVersion As String = If(
            String.IsNullOrWhiteSpace(WarThunderRemoteVersion),
            WarThunderGetLocalVersion(Install),
            WarThunderRemoteVersion)
        If String.IsNullOrWhiteSpace(TargetVersion) Then
            Message = "无法确定战争雷霆当前版本，不能修复游戏文件。"
            WarThunderLog(Message)
            Return False
        End If
        WarThunderUpdateTaskRunning = True
        Try
            WarThunderUpdateState = WarThunderUpdateCheckState.Updating
            WarThunderUpdateProgress = 0
            WarThunderUpdateStatus = If(RepairOnly, "正在准备修复", "正在准备更新")
            WarThunderUpdateSpeed = 0
            WarThunderUpdateDownloadedBytes = 0
            WarThunderUpdateTotalBytes = 0
            Message = If(
                RepairOnly,
                $"检测到游戏核心文件缺失，正在由 PTL 修复 {TargetVersion}…",
                $"游戏正在后台更新至 {TargetVersion}…")
            WarThunderLog(If(RepairOnly,
                "已启动 PTL 内置文件修复。",
                "已启动 PTL 内置 P2P 更新引擎。"))
            WarThunderRefreshAllUI()
            Dim UpdateLoader As New LoaderTask(Of WarThunderInstallation, Boolean)(
                "正在更新战争雷霆",
                AddressOf WarThunderRunBuiltInUpdateTask,
                Nothing,
                ThreadPriority.BelowNormal)
            Dim UpdateTaskbar As New LoaderCombo(Of WarThunderInstallation)(
                "战争雷霆游戏更新",
                New LoaderBase() {UpdateLoader})
            AddHandler UpdateTaskbar.OnStateChangedUi,
                Sub(CurrentLoader As LoaderBase, NewState As LoadState, OldState As LoadState)
                    If NewState = LoadState.Failed OrElse
                       NewState = LoadState.Finished OrElse
                       NewState = LoadState.Canceled Then
                        WarThunderUpdateTaskRunning = False
                        If NewState = LoadState.Failed Then
                            WarThunderUpdateState = WarThunderUpdateCheckState.UpdateRequired
                        End If
                        WarThunderRefreshAllUI()
                    End If
                End Sub
            LoaderTaskbarAdd(UpdateTaskbar)
            UpdateTaskbar.Start(Install)
            Return True
        Catch ex As Exception
            WarThunderUpdateTaskRunning = False
            WarThunderUpdateState = WarThunderUpdateCheckState.UpdateRequired
            Message = ex.GetDisplay(False)
            WarThunderLog($"启动直接更新失败：{Message}")
            Logger.Error(ex, "启动战争雷霆直接更新失败")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 在 PTL 原生任务中运行内置 YUP/P2P 更新器。
    ''' </summary>
    Private Sub WarThunderRunBuiltInUpdateTask(
        Loader As LoaderTask(Of WarThunderInstallation, Boolean))
        Dim Install As WarThunderInstallation = Loader.Input
        If Install Is Nothing Then
            Loader.Failed(New InvalidOperationException("缺少战争雷霆安装信息。"))
            Return
        End If

        Try
            WarThunderUpdateState = WarThunderUpdateCheckState.Updating
            WarThunderUpdateProgress = 0
            WarThunderUpdateStatus = "正在准备更新"
            WarThunderUpdateSpeed = 0
            WarThunderUpdateDownloadedBytes = 0
            WarThunderUpdateTotalBytes = 0
            WarThunderRefreshAllUI()

            Dim TargetVersion As String = If(
                String.IsNullOrWhiteSpace(WarThunderRemoteVersion),
                WarThunderGetLocalVersion(Install),
                WarThunderRemoteVersion)
            Dim Result = WarThunderRunSelfUpdate(
                Install, Loader, TargetVersion)
            If Result Is Nothing OrElse Not Result.Success Then
                Throw New InvalidOperationException(
                    If(Result?.Message, "PTL 更新器没有返回有效结果。"))
            End If

            WarThunderLocalVersion = Result.Version
            RegistryUtils.Write(WarThunderRegistryKey, "LastKnownVersion", Result.Version)
            WarThunderUpdateState = WarThunderUpdateCheckState.UpToDate
            WarThunderUpdateProgress = 1
            WarThunderUpdateStatus = "更新完成"
            WarThunderUpdateSpeed = 0
            If WarThunderUpdateTotalBytes > 0 Then
                WarThunderUpdateDownloadedBytes = WarThunderUpdateTotalBytes
            End If
            Loader.Output = True
            Loader.DownloadStatus = "更新完成"
            Loader.DownloadSpeed = 0
            Loader.FilesRemaining = 0
            WarThunderLog($"PTL 已将游戏更新至 {Result.Version}。")
            WarThunderRefreshAllUI()

            Dim LaunchMessage As String = ""
            If WarThunderLaunchBattleEye(Install, LaunchMessage) Then
                WarThunderLog(LaunchMessage)
            Else
                WarThunderLog($"更新已完成，但自动启动游戏失败：{LaunchMessage}")
            End If
        Catch ex As OperationCanceledException
            WarThunderUpdateState = WarThunderUpdateCheckState.UpdateRequired
            WarThunderUpdateStatus = "更新已取消"
            WarThunderUpdateSpeed = 0
            Loader.DownloadStatus = "更新已取消"
            Loader.DownloadSpeed = 0
            WarThunderLog("PTL 游戏更新已取消。")
            Throw
        Catch ex As Exception
            WarThunderUpdateState = WarThunderUpdateCheckState.UpdateRequired
            WarThunderUpdateStatus = "更新失败"
            WarThunderUpdateSpeed = 0
            Loader.DownloadStatus = "更新失败"
            Loader.DownloadSpeed = 0
            WarThunderLog($"PTL 游戏更新失败：{ex.GetDisplay(False)}")
            Logger.Error(ex, "PTL 战争雷霆游戏更新失败")
            Throw
        Finally
            Loader.DownloadSpeed = 0
            WarThunderUpdateTaskRunning = False
            WarThunderRefreshAllUI()
        End Try
    End Sub

    
    ''' <summary>
    ''' 启动前恢复当前所选 UID 的 Gaijin 登录快照。
    ''' 首次使用某账号时尚无快照，由游戏现有登录状态完成一次登录。
    ''' </summary>
    Private Function WarThunderRestoreSelectedAuthForLaunch(
        ByRef ErrorMessage As String) As Boolean
        Dim UserId As String = WarThunderGetActiveUserId()
        If UserId = "" OrElse Not WarThunderHasAuthSnapshot(UserId) Then
            WarThunderLog(
                $"玩家 UID {If(UserId, "未知")} 尚未保存登录快照，将使用游戏当前登录状态。")
            Return True
        End If
        Dim AuthError As String = ""
        If WarThunderRestoreAuthSnapshot(UserId, AuthError) Then
            WarThunderLog($"已准备玩家 UID {UserId} 的登录凭据。")
            Return True
        End If
        ErrorMessage = "恢复玩家登录凭据失败：" & AuthError
        WarThunderLog(ErrorMessage)
        Return False
    End Function

    ''' <summary>
    ''' 打开当前战争雷霆安装目录。
    ''' </summary>
    Public Sub WarThunderOpenFolder()
        If WarThunderCurrentInstall Is Nothing OrElse Not WarThunderCurrentInstall.IsValid Then
            Hint("请先选择战争雷霆安装目录。", HintType.Red)
            Return
        End If
        OpenExplorer(WarThunderCurrentInstall.InstallPath & "\")
    End Sub

    ''' <summary>
    ''' 将启动记录写入启动器运行日志。
    ''' </summary>
    Public Sub WarThunderLog(Text As String)
        Try
            Logger.Info($"[War Thunder] {Text}")
        Catch
            '启动器尚未完成日志初始化时不应影响启动本身
        End Try
    End Sub

    ''' <summary>
    ''' 刷新战争雷霆相关的左右两栏 UI。
    ''' </summary>
    Public Sub WarThunderRefreshAllUI()
        If FrmMain Is Nothing Then Return
        RunInUi(AddressOf WarThunderRefreshAllUIReal)
    End Sub

    Private Sub WarThunderRefreshAllUIReal()
        If FrmMain Is Nothing Then Return
        Try
            FrmLaunchLeft?.RefreshButtonsUI()
            If FrmMain?.BtnExtraShutdown IsNot Nothing Then
                FrmMain.BtnExtraShutdown.ToolTip = "关闭战争雷霆"
                FrmMain.BtnExtraShutdown.ShowRefresh()
            End If
        Catch ex As Exception
            Logger.Warn(ex, "刷新战争雷霆主页失败")
        End Try
    End Sub

End Module
