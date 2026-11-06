Imports System.IO.Compression

Public Enum WarThunderResourceKind
    Auto
    Sights
    Camouflage
    Sound
End Enum

Public Class WarThunderInstallSummary
    Public Property Success As Boolean
    Public Property Kind As WarThunderResourceKind
    Public Property FileCount As Integer
    Public Property Destination As String = ""
    Public Property Message As String = ""
End Class

''' <summary>
''' 战争雷霆玩家档案、瞄具和声音模组安装。
''' </summary>
Public Module ModWarThunderMods

    Private Const MaxArchiveFiles As Integer = 5000
    Private Const MaxArchiveEntryBytes As Long = 512L * 1024 * 1024
    Private Const MaxArchiveTotalBytes As Long = 2L * 1024 * 1024 * 1024
    Private ReadOnly InstallLock As New Object()

    Private ReadOnly SightExtensions As String() = {".blk", ".png", ".dds", ".tga", ".svg", ".jpg", ".jpeg"}
    Private ReadOnly CamouflageExtensions As String() = {
        ".blk", ".dds", ".tga", ".tif", ".tiff", ".bmp", ".png", ".jpg", ".jpeg"
    }
    Private ReadOnly SoundExtensions As String() = {
        ".bank", ".fev", ".fsb", ".wav", ".ogg", ".mp3", ".flac", ".wem",
        ".txt", ".json", ".ini", ".cfg", ".blk", ".md"
    }
    Private ReadOnly SoundKnownRoots As String() = {
        "engines", "crew", "weapons", "aircraft", "tanks", "ships", "helicopters",
        "music", "ui", "effects", "ambience", "explosions", "guns", "voices", "mod"
    }

    Public Function WarThunderResourceKindName(Kind As WarThunderResourceKind) As String
        Select Case Kind
            Case WarThunderResourceKind.Sights
                Return "瞄具"
            Case WarThunderResourceKind.Camouflage
                Return "涂装"
            Case WarThunderResourceKind.Sound
                Return "音效包"
            Case Else
                Return "资源"
        End Select
    End Function

    Public Function WarThunderGetUserFolder(UserId As String) As String
        If String.IsNullOrWhiteSpace(UserId) Then Return ""
        Dim Folder As String = Path.Combine(WarThunderGetSavesFolder(), UserId)
        Return If(Directory.Exists(Folder), Folder, "")
    End Function

    Public Function WarThunderGetUserSightsFolder(Optional UserId As String = "") As String
        If String.IsNullOrWhiteSpace(UserId) Then UserId = WarThunderGetActiveUserId()
        Dim UserFolder As String = WarThunderGetUserFolder(UserId)
        If UserFolder = "" Then Return ""
        Return Path.Combine(UserFolder, "production", "UserSights")
    End Function

    Public Function WarThunderGetUserSkinsFolder(Optional UserId As String = "") As String
        If String.IsNullOrWhiteSpace(UserId) Then UserId = WarThunderGetActiveUserId()
        Dim UserFolder As String = WarThunderGetUserFolder(UserId)
        If UserFolder = "" Then Return ""
        Return Path.Combine(UserFolder, "production", "UserSkins")
    End Function

    Public Function WarThunderGetSoundModFolder() As String
        If WarThunderCurrentInstall Is Nothing OrElse WarThunderCurrentInstall.InstallPath = "" Then Return ""
        Return Path.Combine(WarThunderCurrentInstall.InstallPath, "sound", "mod")
    End Function

    ''' <summary>
    ''' 切换本地档案。不会删除账号数据，也不会修改密码或登录凭据。
    ''' </summary>
    Public Function WarThunderSelectUser(UserId As String, ByRef ErrorMessage As String) As Boolean
        ErrorMessage = ""
        UserId = If(UserId, "").Trim()
        If WarThunderGetUserFolder(UserId) = "" Then
            ErrorMessage = "该玩家 ID 没有对应的本地存档目录。"
            Return False
        End If
        If Process.GetProcessesByName("aces").Any() OrElse
           Process.GetProcessesByName("aces_BE").Any() OrElse
           Process.GetProcessesByName("beac_wt_mlauncher").Any() Then
            ErrorMessage = "战争雷霆正在运行，请先退出游戏再切换玩家档案。"
            Return False
        End If
        If Not WarThunderHasAuthSnapshot(UserId) Then
            ErrorMessage = "该玩家还没有登录凭据快照。" & vbCrLf &
                "请先使用此账号登录一次战争雷霆，退出游戏后再切换。"
            Return False
        End If

        Dim PreviousUserId As String = ""
        Try
            PreviousUserId = WarThunderGetActiveUserId()
            Dim AuthError As String = ""
            If Not WarThunderRestoreAuthSnapshot(UserId, AuthError) Then
                ErrorMessage = AuthError
                Return False
            End If

            Dim SavesFolder As String = WarThunderGetSavesFolder()
            Dim LastLoginPath As String = Path.Combine(SavesFolder, "lastlogin.blk")
            Dim LastFolder As String = Path.Combine(SavesFolder, "last")
            Dim LastUidPath As String = Path.Combine(LastFolder, "lastUID")
            Dim LastProduction As String = Path.Combine(LastFolder, "production")
            Dim BackupFolder As String = Path.Combine(
                Paths.AppDataThenName, "WarThunder", "Backups", "Users",
                Date.Now.ToString("yyyyMMdd_HHmmss"))
            If File.Exists(LastLoginPath) Then
                Directory.CreateDirectory(BackupFolder)
                File.Copy(LastLoginPath, Path.Combine(BackupFolder, "lastlogin.blk"), True)
            End If
            If File.Exists(LastUidPath) Then
                Directory.CreateDirectory(BackupFolder)
                File.Copy(LastUidPath, Path.Combine(BackupFolder, "lastUID"), True)
            End If

            Dim TempPath As String = LastLoginPath & ".ptl.tmp"
            File.WriteAllText(TempPath, "uid:i64=" & UserId & vbCrLf, New Text.UTF8Encoding(False))
            If File.Exists(LastLoginPath) Then
                File.Replace(TempPath, LastLoginPath, Nothing, True)
            Else
                File.Move(TempPath, LastLoginPath)
            End If
            Directory.CreateDirectory(LastFolder)
            File.WriteAllBytes(LastUidPath,
                Text.Encoding.UTF8.GetBytes(UserId & ChrW(0)))
            WarThunderMirrorUserProduction(UserId, SavesFolder, LastProduction, BackupFolder)

            RegistryUtils.Write("HKEY_CURRENT_USER\Software\PTL\WarThunderProfile",
                                "LastUserId", UserId)
            WarThunderCurrentPlayer = WarThunderCreatePlayerInfo(UserId)
            WarThunderRefreshCurrentPlayerAsync()
            FrmWarThunderStats?.SwitchToActiveUser()
            WarThunderLog($"已将本地玩家档案切换为 UID {UserId}")
            Return True
        Catch ex As Exception
            If PreviousUserId <> "" AndAlso WarThunderHasAuthSnapshot(PreviousUserId) Then
                Dim RestoreError As String = ""
                WarThunderRestoreAuthSnapshot(PreviousUserId, RestoreError)
            End If
            ErrorMessage = ex.GetDisplay(False)
            Logger.Error(ex, "切换战争雷霆玩家档案失败")
            Return False
        End Try
    End Function

    Private Sub WarThunderMirrorUserProduction(
        UserId As String,
        SavesFolder As String,
        LastProduction As String,
        BackupFolder As String)
        Dim SourceProduction As String =
            Path.Combine(SavesFolder, UserId, "production")
        If Not Directory.Exists(SourceProduction) Then
            Throw New DirectoryNotFoundException("目标玩家的 production 目录不存在。")
        End If
        Directory.CreateDirectory(LastProduction)
        For Each FileName In {"global.blk", "machine.blk", "storage.blk"}
            Dim SourcePath As String = Path.Combine(SourceProduction, FileName)
            Dim TargetPath As String = Path.Combine(LastProduction, FileName)
            If Not File.Exists(SourcePath) Then Continue For
            If File.Exists(TargetPath) Then
                Directory.CreateDirectory(BackupFolder)
                File.Copy(TargetPath, Path.Combine(BackupFolder, "last_" & FileName), True)
            End If
            File.Copy(SourcePath, TargetPath, True)
        Next
    End Sub

    Public Function WarThunderInstallResourceFiles(Files As IEnumerable(Of String),
                                                   Kind As WarThunderResourceKind,
                                                   Optional UseLoopsForGunSound As Boolean = False) As List(Of WarThunderInstallSummary)
        Dim Results As New List(Of WarThunderInstallSummary)
        For Each FilePath In Files
            Results.Add(WarThunderInstallResourceFile(FilePath, Kind, UseLoopsForGunSound))
        Next
        Return Results
    End Function

    Public Function WarThunderInstallResourceFile(FilePath As String,
                                                  Kind As WarThunderResourceKind,
                                                  Optional UseLoopsForGunSound As Boolean = False) As WarThunderInstallSummary
        Dim Summary As New WarThunderInstallSummary With {
            .Success = False,
            .Kind = Kind,
            .Message = "安装失败"
        }
        SyncLock InstallLock
            Try
                If Not File.Exists(FilePath) Then Throw New FileNotFoundException("待安装文件不存在。", FilePath)
                Dim Extension As String = PathUtils.GetExtension(FilePath)
                If Extension = "blk" AndAlso Kind = WarThunderResourceKind.Auto Then
                    Kind = WarThunderResourceKind.Sights
                End If
                If Kind = WarThunderResourceKind.Auto Then
                    If Extension <> "zip" Then Throw New InvalidDataException("仅支持 BLK 瞄具或 ZIP 资源包。")
                    Dim SightCount As Integer, CamouflageCount As Integer, SoundCount As Integer
                    WarThunderInspectArchive(
                        FilePath, SightCount, CamouflageCount, SoundCount)
                    If SoundCount > 0 AndAlso
                       (SightCount > 0 OrElse CamouflageCount > 0) Then
                        Throw New InvalidDataException(
                            "这个压缩包同时包含多种资源，请从下载页选择安装类型。")
                    ElseIf CamouflageCount > 0 Then
                        Kind = WarThunderResourceKind.Camouflage
                    ElseIf SightCount > 0 Then
                        Kind = WarThunderResourceKind.Sights
                    ElseIf SoundCount > 0 Then
                        Kind = WarThunderResourceKind.Sound
                    Else
                        Throw New InvalidDataException(
                            "压缩包中没有识别到瞄具、涂装或声音文件。")
                    End If
                End If

                Select Case Kind
                    Case WarThunderResourceKind.Sights
                        Summary = WarThunderInstallSightsFile(FilePath)
                    Case WarThunderResourceKind.Camouflage
                        Summary = WarThunderInstallCamouflageFile(FilePath)
                    Case WarThunderResourceKind.Sound
                        Summary = WarThunderInstallSoundFile(FilePath, UseLoopsForGunSound)
                    Case Else
                        Throw New InvalidDataException("无法判断资源类型。")
                End Select
                Logger.Info($"战争雷霆资源安装完成：{Summary.Message}")
            Catch ex As Exception
                Summary.Message = ex.GetDisplay(False)
                Logger.Error(ex, $"安装战争雷霆资源失败：{FilePath}")
            End Try
        End SyncLock
        Return Summary
    End Function

    Private Function WarThunderInstallSightsFile(FilePath As String) As WarThunderInstallSummary
        Dim UserId As String = WarThunderGetActiveUserId()
        Dim Destination As String = WarThunderGetUserSightsFolder(UserId)
        If Destination = "" Then Throw New DirectoryNotFoundException("请先选择有效的战争雷霆玩家档案。")
        Directory.CreateDirectory(Destination)
        Dim BackupRoot As String = WarThunderCreateBackupRoot("Sights")
        Dim Count As Integer

        If PathUtils.GetExtension(FilePath) = "blk" Then
            Dim TargetPath As String = Path.Combine(Destination, Path.GetFileName(FilePath))
            WarThunderSafeCopy(FilePath, TargetPath, BackupRoot, Destination)
            Count = 1
        Else
            Using Archive As ZipArchive = ZipFile.OpenRead(FilePath)
                WarThunderValidateArchive(Archive)
                For Each Entry In Archive.Entries
                    If Entry.FullName.EndsWithF("/") Then Continue For
                    Dim RelativePath As String = WarThunderNormalizeSightEntry(Entry.FullName)
                    If RelativePath = "" OrElse
                       Not SightExtensions.Contains(Path.GetExtension(RelativePath).ToLowerInvariant()) Then Continue For
                    Dim TargetPath As String = WarThunderSafeTargetPath(Destination, RelativePath)
                    Directory.CreateDirectory(Path.GetDirectoryName(TargetPath))
                    If File.Exists(TargetPath) AndAlso BackupRoot <> "" Then
                        WarThunderBackupFile(TargetPath, BackupRoot, Destination)
                    End If
                    Using Input = Entry.Open()
                        Using Output As New FileStream(TargetPath, FileMode.Create, FileAccess.Write, FileShare.None)
                            Input.CopyTo(Output)
                        End Using
                    End Using
                    Count += 1
                Next
            End Using
        End If

        If Count = 0 Then Throw New InvalidDataException("没有找到可安装的 BLK 瞄具文件。")
        WarThunderLog($"已安装 {Count} 个瞄具相关文件到 {Destination}")
        Return New WarThunderInstallSummary With {
            .Success = True,
            .Kind = WarThunderResourceKind.Sights,
            .FileCount = Count,
            .Destination = Destination,
            .Message = $"已安装 {Count} 个瞄具相关文件。"
        }
    End Function

    Private Function WarThunderInstallCamouflageFile(
        FilePath As String) As WarThunderInstallSummary
        If PathUtils.GetExtension(FilePath) <> "zip" Then
            Throw New InvalidDataException("涂装资源必须是 ZIP 压缩包。")
        End If
        Dim UserId As String = WarThunderGetActiveUserId()
        Dim Destination As String = WarThunderGetUserSkinsFolder(UserId)
        If Destination = "" Then
            Throw New DirectoryNotFoundException(
                "请先选择有效的战争雷霆玩家档案，再安装涂装。")
        End If
        Directory.CreateDirectory(Destination)
        Dim BackupRoot As String = WarThunderCreateBackupRoot("Camouflage")
        Dim Count As Integer

        Using Archive As ZipArchive = ZipFile.OpenRead(FilePath)
            WarThunderValidateArchive(Archive)
            For Each Entry In Archive.Entries
                If Entry.FullName.EndsWithF("/") Then Continue For
                Dim RelativePath As String =
                    WarThunderNormalizeCamouflageEntry(Entry.FullName)
                Dim Extension As String =
                    Path.GetExtension(RelativePath).ToLowerInvariant()
                If RelativePath = "" OrElse
                   Not CamouflageExtensions.Contains(Extension) Then Continue For
                Dim TargetPath As String =
                    WarThunderSafeTargetPath(Destination, RelativePath)
                Directory.CreateDirectory(Path.GetDirectoryName(TargetPath))
                If File.Exists(TargetPath) AndAlso BackupRoot <> "" Then
                    WarThunderBackupFile(TargetPath, BackupRoot, Destination)
                End If
                Using Input = Entry.Open()
                    Using Output As New FileStream(
                        TargetPath, FileMode.Create, FileAccess.Write, FileShare.None)
                        Input.CopyTo(Output)
                    End Using
                End Using
                Count += 1
            Next
        End Using

        If Count = 0 Then
            Throw New InvalidDataException(
                "压缩包中没有找到可安装的涂装定义或纹理文件。")
        End If
        WarThunderLog($"已安装 {Count} 个涂装文件到 {Destination}")
        Return New WarThunderInstallSummary With {
            .Success = True,
            .Kind = WarThunderResourceKind.Camouflage,
            .FileCount = Count,
            .Destination = Destination,
            .Message =
                $"已安装 {Count} 个涂装文件。进入游戏后在对应载具的外观设置中启用。"
        }
    End Function

    Private Function WarThunderInstallSoundFile(FilePath As String,
                                                UseLoopsForGunSound As Boolean) As WarThunderInstallSummary
        Dim Destination As String = WarThunderGetSoundModFolder()
        If Destination = "" Then Throw New DirectoryNotFoundException("请先检测到战争雷霆安装目录。")
        Directory.CreateDirectory(Destination)
        Dim BackupRoot As String = WarThunderCreateBackupRoot("Sound")
        Dim Count As Integer

        Using Archive As ZipArchive = ZipFile.OpenRead(FilePath)
            WarThunderValidateArchive(Archive)
            Dim EligibleFiles As New List(Of KeyValuePair(Of ZipArchiveEntry, String))
            For Each Entry In Archive.Entries
                If Entry.FullName.EndsWithF("/") Then Continue For
                Dim RelativePath As String = WarThunderNormalizeSoundEntry(Entry.FullName)
                If RelativePath = "" OrElse
                   Not SoundExtensions.Contains(Path.GetExtension(RelativePath).ToLowerInvariant()) Then Continue For
                EligibleFiles.Add(New KeyValuePair(Of ZipArchiveEntry, String)(Entry, RelativePath))
            Next
            EligibleFiles = WarThunderStripSingleSoundWrapper(EligibleFiles)
            For Each Item In EligibleFiles
                Dim Entry As ZipArchiveEntry = Item.Key
                Dim RelativePath As String = Item.Value
                Dim TargetPath As String = WarThunderSafeTargetPath(Destination, RelativePath)
                Directory.CreateDirectory(Path.GetDirectoryName(TargetPath))
                If File.Exists(TargetPath) AndAlso BackupRoot <> "" Then
                    WarThunderBackupFile(TargetPath, BackupRoot, Destination)
                End If
                Using Input = Entry.Open()
                    Using Output As New FileStream(TargetPath, FileMode.Create, FileAccess.Write, FileShare.None)
                        Input.CopyTo(Output)
                    End Using
                End Using
                Count += 1
            Next
        End Using

        If Count = 0 Then Throw New InvalidDataException("压缩包中没有找到 FMOD、FSB 或常见音频文件。")
        WarThunderSetSoundModEnabled(WarThunderCurrentInstall.InstallPath, UseLoopsForGunSound)
        WarThunderLog($"已安装 {Count} 个声音模组文件到 {Destination}")
        Return New WarThunderInstallSummary With {
            .Success = True,
            .Kind = WarThunderResourceKind.Sound,
            .FileCount = Count,
            .Destination = Destination,
            .Message = $"已安装 {Count} 个声音文件，并已在 config.blk 中启用声音模组。"
        }
    End Function

    Private Sub WarThunderInspectArchive(
        FilePath As String,
        ByRef SightCount As Integer,
        ByRef CamouflageCount As Integer,
        ByRef SoundCount As Integer)
        Dim HasSightSignal As Boolean
        Dim HasCamouflageSignal As Boolean
        Dim HasCamouflageTexture As Boolean
        Dim HasSightDefinition As Boolean
        Using Archive As ZipArchive = ZipFile.OpenRead(FilePath)
            WarThunderValidateArchive(Archive)
            For Each Entry In Archive.Entries
                Dim EntryPath As String =
                    Entry.FullName.Replace("\", "/")
                Dim LowerPath As String = EntryPath.ToLowerInvariant()
                Dim Extension As String = Path.GetExtension(Entry.FullName).ToLowerInvariant()
                If LowerPath.Contains("userskins/") OrElse
                   LowerPath.Contains("camouflage/") Then
                    HasCamouflageSignal = True
                End If
                If {".dds", ".tga", ".tif", ".tiff"}.Contains(Extension) Then
                    HasCamouflageTexture = True
                End If
                If Extension = ".blk" AndAlso
                   Not LowerPath.Contains("sound/") AndAlso
                   Not Path.GetFileName(Entry.FullName).Equals("config.blk", StringComparison.OrdinalIgnoreCase) Then
                    HasSightDefinition = True
                    Dim BlkText As String = WarThunderReadBlkPreview(Entry)
                    If BlkText.Contains("crosshair") OrElse
                       BlkText.Contains("reticle") OrElse
                       BlkText.Contains("gun_sight") OrElse
                       BlkText.Contains("sighttype") Then
                        HasSightSignal = True
                    End If
                    If BlkText.Contains("camouflage") OrElse
                       BlkText.Contains("skin") OrElse
                       BlkText.Contains(".dds") OrElse
                       BlkText.Contains(".tga") Then
                        HasCamouflageSignal = True
                    End If
                End If
                If SoundExtensions.Contains(Extension) AndAlso
                   {".bank", ".fev", ".fsb", ".wav", ".ogg", ".mp3", ".flac", ".wem"}.Contains(Extension) Then
                    SoundCount += 1
                End If
            Next
        End Using
        If HasCamouflageSignal Then
            CamouflageCount = 1
        ElseIf HasSightDefinition Then
            SightCount = 1
        ElseIf HasCamouflageTexture AndAlso Not HasSightSignal Then
            CamouflageCount = 1
        End If
    End Sub

    Private Function WarThunderReadBlkPreview(Entry As ZipArchiveEntry) As String
        If Entry.Length <= 0 OrElse Entry.Length > 1024 * 1024 Then Return ""
        Try
            Using Reader As New StreamReader(Entry.Open(), Text.Encoding.UTF8, True)
                Return Reader.ReadToEnd().ToLowerInvariant()
            End Using
        Catch
            Return ""
        End Try
    End Function

    Private Sub WarThunderValidateArchive(Archive As ZipArchive)
        If Archive.Entries.Count > MaxArchiveFiles Then
            Throw New InvalidDataException($"压缩包文件数量超过 {MaxArchiveFiles}，已拒绝安装。")
        End If
        Dim TotalSize As Long
        For Each Entry In Archive.Entries
            If Entry.Length > MaxArchiveEntryBytes Then
                Throw New InvalidDataException($"压缩包中的文件过大：{Entry.FullName}")
            End If
            TotalSize += Entry.Length
            If TotalSize > MaxArchiveTotalBytes Then
                Throw New InvalidDataException("解压后的总大小超过 2 GB，已拒绝安装。")
            End If
        Next
    End Sub

    Private Function WarThunderNormalizeSightEntry(PathValue As String) As String
        Dim Value As String = PathValue.Replace("\", "/").TrimStart("/"c)
        Dim Marker As String = "production/usersights/"
        Dim Index As Integer = Value.ToLowerInvariant().IndexOf(Marker)
        If Index >= 0 Then Return Value.Substring(Index + Marker.Length)
        Dim Lower As String = Value.ToLowerInvariant()
        If Lower.StartsWithF("usersights/") Then Return Value.Substring("UserSights/".Length)
        Return Value
    End Function

    Private Function WarThunderNormalizeCamouflageEntry(PathValue As String) As String
        Dim Value As String = PathValue.Replace("\", "/").TrimStart("/"c)
        Dim Lower As String = Value.ToLowerInvariant()
        For Each Marker In {
            "production/userskins/",
            "saves/production/userskins/",
            "userskins/"
        }
            Dim Index As Integer = Lower.IndexOf(Marker)
            If Index >= 0 Then Return Value.Substring(Index + Marker.Length)
        Next
        Return Value
    End Function

    Private Function WarThunderNormalizeSoundEntry(PathValue As String) As String
        Dim Value As String = PathValue.Replace("\", "/").TrimStart("/"c)
        Dim Lower As String = Value.ToLowerInvariant()
        Dim Marker As String = "sound/mod/"
        Dim Index As Integer = Lower.IndexOf(Marker)
        If Index >= 0 Then Return Value.Substring(Index + Marker.Length)
        If Lower.StartsWithF("sound/") Then Value = Value.Substring("sound/".Length) : Lower = Value.ToLowerInvariant()
        If Lower.StartsWithF("mod/") Then Value = Value.Substring("mod/".Length)
        Return Value
    End Function

    Private Function WarThunderStripSingleSoundWrapper(
        Files As List(Of KeyValuePair(Of ZipArchiveEntry, String))) As List(Of KeyValuePair(Of ZipArchiveEntry, String))
        If Files.Count = 0 Then Return Files
        Dim Prefixes As List(Of String) = Files.
            Select(Function(item) item.Value.Split("/"c)(0)).
            Distinct(StringComparer.OrdinalIgnoreCase).
            ToList()
        If Prefixes.Count <> 1 OrElse Files.Any(Function(item) Not item.Value.Contains("/")) Then Return Files
        Dim RootName As String = Prefixes(0)
        If SoundKnownRoots.Contains(RootName, StringComparer.OrdinalIgnoreCase) Then Return Files
        Dim PrefixLength As Integer = RootName.Length + 1
        Return Files.Select(
            Function(item) New KeyValuePair(Of ZipArchiveEntry, String)(
                item.Key, item.Value.Substring(PrefixLength))).ToList()
    End Function

    Private Function WarThunderSafeTargetPath(Root As String, RelativePath As String) As String
        Dim NormalizedRoot As String = Path.GetFullPath(Root).TrimEnd("\"c) & "\"
        Dim TargetPath As String = Path.GetFullPath(Path.Combine(NormalizedRoot, RelativePath.Replace("/", "\")))
        If Not TargetPath.StartsWith(NormalizedRoot, StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidDataException($"压缩包包含越界路径：{RelativePath}")
        End If
        Return TargetPath
    End Function

    Private Sub WarThunderSafeCopy(Source As String, Target As String, BackupRoot As String, Root As String)
        Directory.CreateDirectory(Path.GetDirectoryName(Target))
        If File.Exists(Target) AndAlso BackupRoot <> "" Then WarThunderBackupFile(Target, BackupRoot, Root)
        File.Copy(Source, Target, True)
    End Sub

    Private Function WarThunderCreateBackupRoot(Kind As String) As String
        Try
            Dim Folder As String = Path.Combine(
                Paths.AppDataThenName, "WarThunder", "Backups", Kind,
                Date.Now.ToString("yyyyMMdd_HHmmss"))
            Directory.CreateDirectory(Folder)
            Return Folder
        Catch ex As Exception
            Logger.Warn(ex, "创建战争雷霆资源备份目录失败")
            Return ""
        End Try
    End Function

    Private Sub WarThunderBackupFile(TargetPath As String, BackupRoot As String, Root As String)
        Try
            Dim RelativePath As String = TargetPath.Substring(
                Path.GetFullPath(Root).TrimEnd("\"c).Length).TrimStart("\"c)
            Dim BackupPath As String = Path.Combine(BackupRoot, RelativePath & ".ptlbak")
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath))
            File.Copy(TargetPath, BackupPath, True)
        Catch ex As Exception
            Logger.Warn(ex, $"备份战争雷霆资源文件失败：{TargetPath}")
        End Try
    End Sub

End Module
