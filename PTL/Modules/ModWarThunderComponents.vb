Public Enum WarThunderComponentKind
    Sights
    Camouflage
    Sound
    Text
End Enum

Public Class WarThunderInstalledComponent
    Public Property Kind As WarThunderComponentKind
    Public Property Name As String = ""
    Public Property Path As String = ""
    Public Property FileCount As Integer
    Public Property TotalSize As Long
    Public Property SoundOverlayId As String = ""

    Public ReadOnly Property IsManagedSoundOverlay As Boolean
        Get
            Return Kind = WarThunderComponentKind.Sound AndAlso SoundOverlayId <> ""
        End Get
    End Property
End Class

Friend NotInheritable Class WarThunderSoundOverlaySession
    Public Property State As JObject
    Public Property ComponentId As String = ""
    Public Property InstallPath As String = ""
    Public Property UseLoopsForGunSound As Boolean
    Public Property ConfigChanged As Boolean
    Public Property TotalSize As Long
    Public ReadOnly Property PreparedPaths As New HashSet(Of String)(
        StringComparer.OrdinalIgnoreCase)
End Class

''' <summary>
''' 管理战争雷霆已安装的瞄具、涂装和音效组件。
''' 音效包直接覆盖 sound 目录，并通过安装栈保存和恢复原版文件。
''' </summary>
Public Module ModWarThunderComponents

    Private Const SoundOverlayStateVersion As Integer = 1
    Private Const CamouflageIndexVersion As Integer = 1
    Private ReadOnly CamouflageIndexLock As New Object()

    Public Function WarThunderGetSoundFolder() As String
        If WarThunderCurrentInstall Is Nothing OrElse
           Not WarThunderCurrentInstall.IsValid OrElse
           String.IsNullOrWhiteSpace(WarThunderCurrentInstall.InstallPath) Then
            Return ""
        End If
        Return WarThunderGetSoundFolderForInstall(
            WarThunderCurrentInstall.InstallPath)
    End Function

    Private Function WarThunderGetSoundFolderForInstall(InstallPath As String) As String
        If String.IsNullOrWhiteSpace(InstallPath) Then Return ""
        Return Path.Combine(Path.GetFullPath(InstallPath), "sound")
    End Function

    Private Function WarThunderGetSoundModFolder() As String
        Dim SoundFolder As String = WarThunderGetSoundFolder()
        Return If(SoundFolder = "", "", Path.Combine(SoundFolder, "mod"))
    End Function

    Public Function WarThunderGetComponentRoot(Kind As WarThunderComponentKind) As String
        Select Case Kind
            Case WarThunderComponentKind.Sights
                Return WarThunderGetUserSightsFolder()
            Case WarThunderComponentKind.Camouflage
                Return WarThunderGetUserSkinsFolder()
            Case WarThunderComponentKind.Sound
                Return WarThunderGetSoundModFolder()
            Case WarThunderComponentKind.Text
                If WarThunderCurrentInstall Is Nothing OrElse
                   Not WarThunderCurrentInstall.IsValid Then
                    Return ""
                End If
                Return Path.Combine(
                    WarThunderCurrentInstall.InstallPath, "lang")
            Case Else
                Return ""
        End Select
    End Function

    Public Function WarThunderComponentKindName(Kind As WarThunderComponentKind) As String
        Select Case Kind
            Case WarThunderComponentKind.Sights
                Return "瞄具"
            Case WarThunderComponentKind.Camouflage
                Return "涂装"
            Case WarThunderComponentKind.Sound
                Return "音效包"
            Case WarThunderComponentKind.Text
                Return "文本"
            Case Else
                Return "组件"
        End Select
    End Function

    Public Function WarThunderEnumerateInstalledComponents(
        Kind As WarThunderComponentKind) As List(Of WarThunderInstalledComponent)
        If Kind = WarThunderComponentKind.Camouflage Then
            Return WarThunderEnumerateCamouflageComponents()
        End If
        If Kind = WarThunderComponentKind.Sound Then
            Return WarThunderEnumerateSoundComponents()
        End If
        If Kind = WarThunderComponentKind.Text Then
            Return WarThunderEnumerateTextFiles()
        End If

        Dim Result As New List(Of WarThunderInstalledComponent)
        Dim Root As String = WarThunderGetComponentRoot(Kind)
        If Root = "" OrElse Not Directory.Exists(Root) Then Return Result
        Try
            For Each Folder As String In Directory.GetDirectories(Root).
                Where(Function(FolderPath) Not Path.GetFileName(FolderPath).Equals(
                    ".ptl", StringComparison.OrdinalIgnoreCase))
                Dim FileCount As Integer
                Dim TotalSize As Long
                WarThunderGetComponentStats(Folder, FileCount, TotalSize)
                Result.Add(New WarThunderInstalledComponent With {
                    .Kind = Kind,
                    .Name = Path.GetFileName(Folder),
                    .Path = Folder,
                    .FileCount = FileCount,
                    .TotalSize = TotalSize
                })
            Next
            For Each FilePath As String In Directory.GetFiles(Root).
                Where(Function(CandidatePath) Not Path.GetFileName(CandidatePath).StartsWith("."))
                Dim Info As New FileInfo(FilePath)
                Result.Add(New WarThunderInstalledComponent With {
                    .Kind = Kind,
                    .Name = Info.Name,
                    .Path = Info.FullName,
                    .FileCount = 1,
                    .TotalSize = Info.Length
                })
            Next
        Catch ex As Exception
            Logger.Warn(ex, $"扫描战争雷霆{WarThunderComponentKindName(Kind)}组件失败")
        End Try
        Return Result.OrderBy(Function(item) item.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
    End Function

    Private Function WarThunderEnumerateTextFiles() As List(Of WarThunderInstalledComponent)
        Dim Result As New List(Of WarThunderInstalledComponent)
        Dim Root As String = WarThunderGetComponentRoot(WarThunderComponentKind.Text)
        If Root = "" OrElse Not Directory.Exists(Root) Then Return Result
        Try
            Dim RootPrefix As String =
                Path.GetFullPath(Root).TrimEnd("\"c) & "\"
            Dim Files As List(Of String) = Directory.EnumerateFiles(
                Root, "*", SearchOption.AllDirectories).
                OrderBy(Function(CandidatePath) CandidatePath,
                        StringComparer.CurrentCultureIgnoreCase).
                ToList()
            For Each FilePath As String In Files
                Dim Extension As String = Path.GetExtension(FilePath)
                If Not Extension.Equals(
                    ".csv", StringComparison.OrdinalIgnoreCase) AndAlso
                   Not Extension.Equals(
                       ".blk", StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If
                Dim FullPath As String = Path.GetFullPath(FilePath)
                Dim RelativePath As String =
                    FullPath.Substring(RootPrefix.Length).Replace("\", "/")
                Dim Info As New FileInfo(FullPath)
                Result.Add(New WarThunderInstalledComponent With {
                    .Kind = WarThunderComponentKind.Text,
                    .Name = RelativePath,
                    .Path = FullPath,
                    .FileCount = 1,
                    .TotalSize = Info.Length
                })
            Next
        Catch ex As Exception
            Logger.Warn(ex, "扫描战争雷霆本地化文本失败")
        End Try
        Return Result
    End Function

    Private ReadOnly Property CamouflageIndexPath As String
        Get
            Return Path.Combine(
                Paths.AppDataThenName, "WarThunder", "Camouflage", "index.json")
        End Get
    End Property

    Friend Sub WarThunderRegisterCamouflageFolders(
        Root As String,
        Folders As IEnumerable(Of String),
        ArchivePath As String)
        Try
            SyncLock CamouflageIndexLock
                Dim RootPrefix As String =
                    Path.GetFullPath(Root).TrimEnd("\"c) & "\"
                Dim Index As JObject = WarThunderLoadCamouflageIndex()
                Dim Items As JObject = EnsureJObject(Index, "items")
                For Each Folder As String In Folders
                    Dim FullPath As String = Path.GetFullPath(Folder)
                    If Not FullPath.StartsWith(
                        RootPrefix, StringComparison.OrdinalIgnoreCase) OrElse
                       Not Directory.Exists(FullPath) Then
                        Continue For
                    End If
                    Dim RelativePath As String =
                        FullPath.Substring(RootPrefix.Length).Replace("\", "/")
                    If RelativePath.Contains("/") Then Continue For
                    Dim FileCount As Integer
                    Dim TotalSize As Long
                    WarThunderGetComponentStats(FullPath, FileCount, TotalSize)
                    Items(RelativePath.ToLowerInvariant()) = New JObject From {
                        New JProperty("name", Path.GetFileName(RelativePath)),
                        New JProperty("path", RelativePath),
                        New JProperty("archive_name", Path.GetFileName(ArchivePath)),
                        New JProperty("installed_at", DateTime.UtcNow.ToString("o")),
                        New JProperty("file_count", FileCount),
                        New JProperty("total_size", TotalSize)
                    }
                Next
                WarThunderSaveCamouflageIndex(Index)
            End SyncLock
        Catch ex As Exception
            Logger.Warn(ex, "写入涂装索引失败")
        End Try
    End Sub

    Friend Sub WarThunderRemoveCamouflageIndexEntry(FolderPath As String)
        Try
            SyncLock CamouflageIndexLock
                Dim Root As String = WarThunderGetUserSkinsFolder()
                If Root = "" Then Return
                Dim RootPrefix As String =
                    Path.GetFullPath(Root).TrimEnd("\"c) & "\"
                Dim FullPath As String = Path.GetFullPath(FolderPath)
                If Not FullPath.StartsWith(
                    RootPrefix, StringComparison.OrdinalIgnoreCase) Then Return
                Dim RelativePath As String =
                    FullPath.Substring(RootPrefix.Length).Replace("\", "/")
                Dim Index As JObject = WarThunderLoadCamouflageIndex()
                Dim Items As JObject = EnsureJObject(Index, "items")
                Dim Match As JProperty = Items.Properties().FirstOrDefault(
                    Function(PropertyValue)
                        Return EntryString(
                            PropertyValue.Value, "path").Equals(
                            RelativePath, StringComparison.OrdinalIgnoreCase)
                    End Function)
                If Match Is Nothing Then Return
                Match.Remove()
                WarThunderSaveCamouflageIndex(Index)
            End SyncLock
        Catch ex As Exception
            Logger.Warn(ex, $"移除涂装索引记录失败：{FolderPath}")
        End Try
    End Sub

    Private Function WarThunderEnumerateCamouflageComponents() _
        As List(Of WarThunderInstalledComponent)
        Dim Result As New List(Of WarThunderInstalledComponent)
        Dim Root As String = WarThunderGetUserSkinsFolder()
        If Root = "" OrElse Not Directory.Exists(Root) Then Return Result

        Try
            SyncLock CamouflageIndexLock
                Dim RootPrefix As String =
                    Path.GetFullPath(Root).TrimEnd("\"c) & "\"
                Dim Index As JObject = WarThunderLoadCamouflageIndex()
                Dim Items As JObject = EnsureJObject(Index, "items")
                Dim KnownFolders As New HashSet(Of String)(
                    StringComparer.OrdinalIgnoreCase)
                Dim Changed As Boolean

                For Each PropertyValue In Items.Properties().ToList()
                    Dim RelativePath As String =
                        EntryString(PropertyValue.Value, "path").Replace("\", "/")
                    If RelativePath = "" OrElse RelativePath.Contains("/") Then
                        PropertyValue.Remove()
                        Changed = True
                        Continue For
                    End If
                    Dim FullPath As String =
                        Path.GetFullPath(Path.Combine(Root, RelativePath))
                    If Not FullPath.StartsWith(
                        RootPrefix, StringComparison.OrdinalIgnoreCase) OrElse
                       Not Directory.Exists(FullPath) Then
                        PropertyValue.Remove()
                        Changed = True
                        Continue For
                    End If
                    KnownFolders.Add(RelativePath)
                    Dim FileCount As Integer
                    Dim TotalSize As Long
                    WarThunderGetComponentStats(FullPath, FileCount, TotalSize)
                    Dim Item As JObject = TryCast(PropertyValue.Value, JObject)
                    If Item Is Nothing Then
                        PropertyValue.Remove()
                        Changed = True
                        Continue For
                    End If
                    If EntryInteger(Item, "file_count") <> FileCount OrElse
                       EntryLong(Item, "total_size") <> TotalSize Then
                        Item("file_count") = FileCount
                        Item("total_size") = TotalSize
                        Changed = True
                    End If
                    Result.Add(New WarThunderInstalledComponent With {
                        .Kind = WarThunderComponentKind.Camouflage,
                        .Name = ValueOrDefault(
                            EntryString(Item, "name"), Path.GetFileName(RelativePath)),
                        .Path = FullPath,
                        .FileCount = FileCount,
                        .TotalSize = TotalSize
                    })
                Next

                For Each Folder As String In Directory.GetDirectories(Root)
                    Dim FolderName As String = Path.GetFileName(Folder)
                    If FolderName.StartsWith(".", StringComparison.Ordinal) OrElse
                       KnownFolders.Contains(FolderName) Then
                        Continue For
                    End If
                    Dim FileCount As Integer
                    Dim TotalSize As Long
                    WarThunderGetComponentStats(Folder, FileCount, TotalSize)
                    Items(FolderName.ToLowerInvariant()) = New JObject From {
                        New JProperty("name", FolderName),
                        New JProperty("path", FolderName),
                        New JProperty("archive_name", ""),
                        New JProperty("installed_at", DateTime.UtcNow.ToString("o")),
                        New JProperty("file_count", FileCount),
                        New JProperty("total_size", TotalSize)
                    }
                    KnownFolders.Add(FolderName)
                    Changed = True
                    Result.Add(New WarThunderInstalledComponent With {
                        .Kind = WarThunderComponentKind.Camouflage,
                        .Name = FolderName,
                        .Path = Folder,
                        .FileCount = FileCount,
                        .TotalSize = TotalSize
                    })
                Next
                If Changed Then WarThunderSaveCamouflageIndex(Index)
            End SyncLock
        Catch ex As Exception
            Logger.Warn(ex, "扫描涂装索引失败")
        End Try
        Return Result.OrderBy(
            Function(Item) Item.Name,
            StringComparer.CurrentCultureIgnoreCase).ToList()
    End Function

    Private Function WarThunderLoadCamouflageIndex() As JObject
        If Not File.Exists(CamouflageIndexPath) Then
            Return New JObject From {
                New JProperty("version", CamouflageIndexVersion),
                New JProperty("items", New JObject())
            }
        End If
        Try
            Dim Index As JObject = JObject.Parse(File.ReadAllText(CamouflageIndexPath))
            If EntryInteger(Index, "version") <> CamouflageIndexVersion Then
                Throw New InvalidDataException("涂装索引版本不受支持。")
            End If
            EnsureJObject(Index, "items")
            Return Index
        Catch ex As Exception
            Logger.Warn(ex, "读取涂装索引失败，将重新建立索引")
            Return New JObject From {
                New JProperty("version", CamouflageIndexVersion),
                New JProperty("items", New JObject())
            }
        End Try
    End Function

    Private Sub WarThunderSaveCamouflageIndex(Index As JObject)
        Dim Folder As String = Path.GetDirectoryName(CamouflageIndexPath)
        Directory.CreateDirectory(Folder)
        Dim TempPath As String = CamouflageIndexPath & ".tmp"
        Try
            File.WriteAllText(
                TempPath,
                Index.ToString(Newtonsoft.Json.Formatting.Indented),
                New UTF8Encoding(False))
            If File.Exists(CamouflageIndexPath) Then
                File.Replace(TempPath, CamouflageIndexPath, Nothing, True)
            Else
                File.Move(TempPath, CamouflageIndexPath)
            End If
        Finally
            FileUtils.Delete(TempPath)
        End Try
    End Sub

    Private Function WarThunderEnumerateSoundComponents() As List(Of WarThunderInstalledComponent)
        Dim Result As New List(Of WarThunderInstalledComponent)
        Try
            Dim State As JObject = WarThunderLoadSoundOverlayState()
            Dim Components = TryCast(State("components"), JObject)
            If Components IsNot Nothing Then
                For Each Entry In Components.Properties().
                    OrderByDescending(Function(PropertyValue)
                                          Return EntryString(PropertyValue.Value, "installed_at")
                                      End Function)
                    Dim Item = TryCast(Entry.Value, JObject)
                    If Item Is Nothing Then Continue For
                    Dim StoredInstallPath As String =
                        EntryString(Item, "install_path")
                    Result.Add(New WarThunderInstalledComponent With {
                        .Kind = WarThunderComponentKind.Sound,
                        .Name = ValueOrDefault(
                            EntryString(Item, "name"), "音效包"),
                        .Path = If(StoredInstallPath = "",
                            WarThunderGetSoundFolder(),
                            WarThunderGetSoundFolderForInstall(StoredInstallPath)),
                        .FileCount = Math.Max(0, EntryInteger(Item, "file_count")),
                        .TotalSize = Math.Max(0L, EntryLong(Item, "total_size")),
                        .SoundOverlayId = Entry.Name
                    })
                Next
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读取音效包安装记录失败")
        End Try

        Dim SoundModRoot As String = WarThunderGetSoundModFolder()
        If SoundModRoot <> "" AndAlso Directory.Exists(SoundModRoot) Then
            Try
                For Each Folder As String In Directory.GetDirectories(SoundModRoot).
                    Where(Function(FolderPath) Not Path.GetFileName(FolderPath).Equals(
                        ".ptl", StringComparison.OrdinalIgnoreCase))
                    Dim FileCount As Integer
                    Dim TotalSize As Long
                    WarThunderGetComponentStats(Folder, FileCount, TotalSize)
                    Result.Add(New WarThunderInstalledComponent With {
                        .Kind = WarThunderComponentKind.Sound,
                        .Name = Path.GetFileName(Folder),
                        .Path = Folder,
                        .FileCount = FileCount,
                        .TotalSize = TotalSize
                    })
                Next
                For Each FilePath As String In Directory.GetFiles(SoundModRoot).
                    Where(Function(CandidatePath) Not Path.GetFileName(CandidatePath).StartsWith("."))
                    Dim Info As New FileInfo(FilePath)
                    Result.Add(New WarThunderInstalledComponent With {
                        .Kind = WarThunderComponentKind.Sound,
                        .Name = Info.Name,
                        .Path = Info.FullName,
                        .FileCount = 1,
                        .TotalSize = Info.Length
                    })
                Next
            Catch ex As Exception
                Logger.Warn(ex, "扫描旧版 sound\mod 目录失败")
            End Try
        End If

        Return Result.OrderByDescending(
            Function(item) item.IsManagedSoundOverlay).
            ThenBy(Function(item) item.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
    End Function

    Public Function WarThunderRemoveInstalledComponent(
        Component As WarThunderInstalledComponent,
        ByRef ErrorMessage As String) As Boolean
        ErrorMessage = ""
        Try
            If Component Is Nothing Then Throw New ArgumentNullException(NameOf(Component))
            If Component.IsManagedSoundOverlay Then
                Return WarThunderRemoveSoundOverlayComponent(Component, ErrorMessage)
            End If

            Dim ValidationRoot As String =
                If(Component.Kind = WarThunderComponentKind.Sound,
                   WarThunderGetSoundModFolder(),
                   WarThunderGetComponentRoot(Component.Kind))
            If ValidationRoot = "" Then
                Throw New DirectoryNotFoundException("组件根目录不存在。")
            End If
            Dim Root As String = Path.GetFullPath(ValidationRoot).TrimEnd("\"c)
            Dim Target As String = Path.GetFullPath(Component.Path)
            If Target.Equals(Root, StringComparison.OrdinalIgnoreCase) OrElse
               Not Target.StartsWith(Root & "\", StringComparison.OrdinalIgnoreCase) Then
                Throw New InvalidOperationException("组件路径不在允许的安装目录中。")
            End If
            If Directory.Exists(Target) Then
                DirectoryUtils.Delete(Target, True)
            ElseIf File.Exists(Target) Then
                FileUtils.Delete(Target)
            Else
                Throw New FileNotFoundException("组件已经不存在。", Target)
            End If
            If Component.Kind = WarThunderComponentKind.Camouflage Then
                WarThunderRemoveCamouflageIndexEntry(Target)
            End If
            Logger.Info($"已直接删除战争雷霆组件：{Target}")
            Return True
        Catch ex As Exception
            ErrorMessage = ex.GetDisplay(False)
            Logger.Error(ex, $"卸载战争雷霆组件失败：{Component?.Path}")
            Return False
        End Try
    End Function

    Friend Function WarThunderBeginSoundOverlayInstall(
        InstallPath As String,
        ComponentName As String,
        ArchivePath As String,
        TotalSize As Long,
        UseLoopsForGunSound As Boolean) As WarThunderSoundOverlaySession
        If WarThunderGetGameProcessIds().Any() Then
            Throw New InvalidOperationException("请先关闭战争雷霆，再安装音效包。")
        End If
        If Not Directory.Exists(Path.Combine(InstallPath, "sound")) Then
            Throw New DirectoryNotFoundException("未找到战争雷霆 sound 目录。")
        End If

        Dim State As JObject = WarThunderLoadSoundOverlayState()
        Dim Components As JObject = EnsureJObject(State, "components")
        Dim Files As JObject = EnsureJObject(State, "files")
        Dim ConfigChain As JArray = EnsureJArray(State, "config_chain")
        Dim ComponentId As String =
            DateTime.Now.ToString("yyyyMMddHHmmssfff") & "_" & Guid.NewGuid().ToString("N").Substring(0, 8)
        Dim PreviousUseLoops As Boolean =
            WarThunderReadSoundUseLoops(InstallPath)
        Dim ConfigChanged As Boolean = UseLoopsForGunSound

        Components(ComponentId) = New JObject From {
            New JProperty("id", ComponentId),
            New JProperty("name", SanitizeSoundComponentName(ComponentName)),
            New JProperty("archive_name", Path.GetFileName(ArchivePath)),
            New JProperty("install_path", Path.GetFullPath(InstallPath)),
            New JProperty("installed_at", DateTime.UtcNow.ToString("o")),
            New JProperty("file_count", 0),
            New JProperty("total_size", Math.Max(0L, TotalSize)),
            New JProperty("use_loops_for_gun_sound", UseLoopsForGunSound),
            New JProperty("config_changed", ConfigChanged),
            New JProperty("previous_use_loops", PreviousUseLoops)
        }
        If UseLoopsForGunSound Then
            ConfigChain.Add(New JObject From {
                New JProperty("component_id", ComponentId),
                New JProperty("before_value", PreviousUseLoops)
            })
        End If

        Return New WarThunderSoundOverlaySession With {
            .State = State,
            .ComponentId = ComponentId,
            .InstallPath = Path.GetFullPath(InstallPath),
            .UseLoopsForGunSound = UseLoopsForGunSound,
            .ConfigChanged = ConfigChanged,
            .TotalSize = Math.Max(0L, TotalSize)
        }
    End Function

    Friend Function WarThunderPrepareSoundOverlayFile(
        Session As WarThunderSoundOverlaySession,
        RelativePath As String) As String
        If Session Is Nothing OrElse Session.State Is Nothing Then
            Throw New ArgumentNullException(NameOf(Session))
        End If
        Dim RelativeValue As String = WarThunderNormalizeSoundOverlayRelativePath(
            RelativePath)
        If RelativeValue = "" Then
            Throw New InvalidDataException("音效包包含空的文件路径。")
        End If
        If Not Session.PreparedPaths.Add(RelativeValue) Then
            Throw New InvalidDataException($"音效包包含重复文件：{RelativeValue}")
        End If

        Dim TargetPath As String = WarThunderGetSoundOverlayTargetPath(
            Session.InstallPath, RelativeValue)
        Dim StateKey As String = RelativeValue.ToLowerInvariant()
        Dim BackupRelative As String =
            Session.ComponentId & "/" & StateKey
        Dim BackupPath As String = WarThunderGetSoundOverlayBackupPath(
            BackupRelative)
        Dim ExistedBefore As Boolean = File.Exists(TargetPath)
        If ExistedBefore Then
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath))
            File.Copy(TargetPath, BackupPath, True)
        End If

        Dim Files As JObject = EnsureJObject(Session.State, "files")
        Dim Stack As JArray
        If Files(StateKey) Is Nothing Then
            Stack = New JArray()
            Files(StateKey) = Stack
        Else
            Stack = TryCast(Files(StateKey), JArray)
            If Stack Is Nothing Then
                Throw New InvalidDataException(
                    $"音效备份记录损坏：{RelativeValue}")
            End If
        End If
        Stack.Add(New JObject From {
            New JProperty("component_id", Session.ComponentId),
            New JProperty("relative_path", RelativeValue.Replace("\", "/")),
            New JProperty("before_existed", ExistedBefore),
            New JProperty("backup", If(ExistedBefore, BackupRelative, ""))
        })
        Return TargetPath
    End Function

    Friend Sub WarThunderFinalizeSoundOverlayInstall(
        Session As WarThunderSoundOverlaySession)
        If Session Is Nothing OrElse Session.State Is Nothing Then
            Throw New ArgumentNullException(NameOf(Session))
        End If
        If Session.PreparedPaths.Count = 0 Then
            Throw New InvalidDataException("没有准备任何音效文件。")
        End If
        Dim Components As JObject = EnsureJObject(Session.State, "components")
        Dim Component = TryCast(Components(Session.ComponentId), JObject)
        If Component Is Nothing Then
            Throw New InvalidDataException("音效包安装记录丢失。")
        End If
        Component("file_count") = Session.PreparedPaths.Count
        Component("total_size") = Session.TotalSize
        Component("completed_at") = DateTime.UtcNow.ToString("o")
        If Session.ConfigChanged Then
            WarThunderSetSoundUseLoops(Session.InstallPath, True)
        End If
        WarThunderSaveSoundOverlayState(Session.State)
    End Sub

    Friend Sub WarThunderAbortSoundOverlayInstall(
        Session As WarThunderSoundOverlaySession)
        If Session Is Nothing OrElse Session.State Is Nothing Then Return
        Dim Restored As Boolean = False
        Try
            WarThunderRestoreSoundOverlayEntries(
                Session.State, Session.ComponentId, Session.InstallPath)
            Dim Components As JObject = EnsureJObject(Session.State, "components")
            Components.Remove(Session.ComponentId)
            Restored = True
        Catch ex As Exception
            Logger.Warn(ex, $"回滚音效包安装失败：{Session.ComponentId}")
        End Try
        If Not Restored Then Return
        Try
            Dim Folder As String = Path.Combine(
                WarThunderGetSoundOverlayBackupsRoot(), Session.ComponentId)
            If Directory.Exists(Folder) Then Directory.Delete(Folder, True)
        Catch ex As Exception
            Logger.Warn(ex, $"清理音效包备份失败：{Session.ComponentId}")
        End Try
    End Sub

    Private Function WarThunderRemoveSoundOverlayComponent(
        Component As WarThunderInstalledComponent,
        ByRef ErrorMessage As String) As Boolean
        Try
            If WarThunderGetGameProcessIds().Any() Then
                Throw New InvalidOperationException("请先关闭战争雷霆，再卸载音效包。")
            End If
            Dim State As JObject = WarThunderLoadSoundOverlayState()
            Dim Components As JObject = EnsureJObject(State, "components")
            Dim Entry = TryCast(Components(Component.SoundOverlayId), JObject)
            If Entry Is Nothing Then
                Throw New FileNotFoundException("音效包安装记录不存在。")
            End If
            Dim InstallPath As String = EntryString(Entry, "install_path")
            If InstallPath = "" Then
                Throw New InvalidDataException("音效包安装记录缺少游戏目录。")
            End If
            WarThunderRestoreSoundOverlayEntries(
                State, Component.SoundOverlayId, InstallPath)
            Components.Remove(Component.SoundOverlayId)
            WarThunderSaveSoundOverlayState(State)
            Try
                Dim Folder As String = Path.Combine(
                    WarThunderGetSoundOverlayBackupsRoot(),
                    Component.SoundOverlayId)
                If Directory.Exists(Folder) Then Directory.Delete(Folder, True)
            Catch ex As Exception
                Logger.Warn(ex, $"清理音效包备份目录失败：{Component.SoundOverlayId}")
            End Try
            Logger.Info($"已卸载音效包并恢复原版文件：{Component.Name}")
            Return True
        Catch ex As Exception
            ErrorMessage = ex.GetDisplay(False)
            Logger.Error(ex, $"恢复音效包原版文件失败：{Component?.Name}")
            Return False
        End Try
    End Function

    Private Sub WarThunderRestoreSoundOverlayEntries(
        State As JObject,
        ComponentId As String,
        InstallPath As String)
        Dim Files As JObject = EnsureJObject(State, "files")
        For Each PropertyValue In Files.Properties().ToList()
            Dim Stack = TryCast(PropertyValue.Value, JArray)
            If Stack Is Nothing Then Continue For
            Dim Index As Integer = -1
            For IndexValue As Integer = 0 To Stack.Count - 1
                Dim Item = TryCast(Stack(IndexValue), JObject)
                If Item IsNot Nothing AndAlso
                   EntryString(Item, "component_id") = ComponentId Then
                    Index = IndexValue
                    Exit For
                End If
            Next
            If Index < 0 Then Continue For

            Dim CurrentEntry = CType(Stack(Index), JObject)
            Dim RelativePath As String = EntryString(CurrentEntry, "relative_path")
            If Index = Stack.Count - 1 Then
                WarThunderRestoreSoundOverlayEntry(
                    InstallPath, RelativePath, CurrentEntry)
                Stack.RemoveAt(Index)
            Else
                Dim NextEntry = TryCast(Stack(Index + 1), JObject)
                If NextEntry IsNot Nothing Then
                    Dim NextComponentId As String =
                        EntryString(NextEntry, "component_id")
                    Dim NextBackupRelative As String =
                        NextComponentId & "/" & PropertyValue.Name
                    Dim NextBackupPath As String =
                        WarThunderGetSoundOverlayBackupPath(NextBackupRelative)
                    If EntryBoolean(CurrentEntry, "before_existed") Then
                        Dim CurrentBackupRelative As String =
                            EntryString(CurrentEntry, "backup")
                        Dim CurrentBackupPath As String =
                            WarThunderGetSoundOverlayBackupPath(CurrentBackupRelative)
                        If CurrentBackupPath = "" OrElse Not File.Exists(CurrentBackupPath) Then
                            Throw New FileNotFoundException(
                                $"音效备份文件不存在：{RelativePath}")
                        End If
                        Directory.CreateDirectory(Path.GetDirectoryName(NextBackupPath))
                        File.Copy(CurrentBackupPath, NextBackupPath, True)
                        NextEntry("before_existed") = True
                        NextEntry("backup") = NextBackupRelative
                    Else
                        If File.Exists(NextBackupPath) Then File.Delete(NextBackupPath)
                        NextEntry("before_existed") = False
                        NextEntry("backup") = ""
                    End If
                End If
                WarThunderDeleteSoundOverlayBackupFile(CurrentEntry)
                Stack.RemoveAt(Index)
            End If
            If Stack.Count = 0 Then Files.Remove(PropertyValue.Name)
        Next

        Dim ConfigChain As JArray = EnsureJArray(State, "config_chain")
        Dim ConfigIndex As Integer = -1
        For IndexValue As Integer = 0 To ConfigChain.Count - 1
            Dim Item = TryCast(ConfigChain(IndexValue), JObject)
            If Item IsNot Nothing AndAlso
               EntryString(Item, "component_id") = ComponentId Then
                ConfigIndex = IndexValue
                Exit For
            End If
        Next
        If ConfigIndex >= 0 Then
            Dim CurrentConfig = CType(ConfigChain(ConfigIndex), JObject)
            If ConfigIndex = ConfigChain.Count - 1 Then
                WarThunderSetSoundUseLoops(
                    InstallPath, EntryBoolean(CurrentConfig, "before_value"))
                ConfigChain.RemoveAt(ConfigIndex)
            Else
                Dim NextConfig = TryCast(ConfigChain(ConfigIndex + 1), JObject)
                If NextConfig IsNot Nothing Then
                    NextConfig("before_value") =
                        EntryBoolean(CurrentConfig, "before_value")
                End If
                ConfigChain.RemoveAt(ConfigIndex)
            End If
        End If
    End Sub

    Private Sub WarThunderRestoreSoundOverlayEntry(
        InstallPath As String,
        RelativePath As String,
        Entry As JObject)
        Dim TargetPath As String = WarThunderGetSoundOverlayTargetPath(
            InstallPath, RelativePath)
        If EntryBoolean(Entry, "before_existed") Then
            Dim BackupRelative As String = EntryString(Entry, "backup")
            Dim BackupPath As String =
                WarThunderGetSoundOverlayBackupPath(BackupRelative)
            If BackupPath = "" OrElse Not File.Exists(BackupPath) Then
                Throw New FileNotFoundException(
                    $"音效备份文件不存在：{RelativePath}")
            End If
            Directory.CreateDirectory(Path.GetDirectoryName(TargetPath))
            File.Copy(BackupPath, TargetPath, True)
            Try
                File.SetLastWriteTimeUtc(TargetPath, File.GetLastWriteTimeUtc(BackupPath))
            Catch
            End Try
            WarThunderDeleteSoundOverlayBackupFile(Entry)
        ElseIf File.Exists(TargetPath) Then
            File.Delete(TargetPath)
        End If
        WarThunderDeleteEmptySoundDirectories(InstallPath, Path.GetDirectoryName(TargetPath))
    End Sub

    Private Sub WarThunderDeleteSoundOverlayBackupFile(Entry As JObject)
        Try
            Dim BackupRelative As String = EntryString(Entry, "backup")
            If BackupRelative = "" Then Return
            Dim BackupPath As String =
                WarThunderGetSoundOverlayBackupPath(BackupRelative)
            If File.Exists(BackupPath) Then File.Delete(BackupPath)
        Catch ex As Exception
            Logger.Warn(ex, "删除音效包备份文件失败")
        End Try
    End Sub

    Private Sub WarThunderDeleteEmptySoundDirectories(
        InstallPath As String,
        StartFolder As String)
        Try
            Dim Root As String =
                Path.GetFullPath(WarThunderGetSoundFolderForInstall(InstallPath)).
                TrimEnd("\"c)
            Dim Current As String = Path.GetFullPath(StartFolder).TrimEnd("\"c)
            Do While Current.Length > Root.Length AndAlso
                   Current.StartsWith(Root & "\", StringComparison.OrdinalIgnoreCase) AndAlso
                   Directory.Exists(Current) AndAlso
                   Not Directory.EnumerateFileSystemEntries(Current).Any()
                Directory.Delete(Current)
                Current = Path.GetDirectoryName(Current)
            Loop
        Catch
        End Try
    End Sub

    Private Function WarThunderNormalizeSoundOverlayRelativePath(
        Value As String) As String
        Dim Result As String = If(Value, "").Replace("\", "/").TrimStart("/"c)
        While Result.StartsWith("./", StringComparison.Ordinal)
            Result = Result.Substring(2)
        End While
        Dim Parts As New List(Of String)
        For Each Part In Result.Split("/"c)
            If Part = "" OrElse Part = "." Then Continue For
            If Part = ".." Then
                Throw New InvalidDataException("音效包包含越界路径。")
            End If
            Parts.Add(Part)
        Next
        Return Parts.Join("/")
    End Function

    Private Function WarThunderGetSoundOverlayTargetPath(
        InstallPath As String,
        RelativePath As String) As String
        Dim SoundRoot As String =
            Path.GetFullPath(WarThunderGetSoundFolderForInstall(InstallPath)).
            TrimEnd("\"c) & "\"
        Dim TargetPath As String = Path.GetFullPath(
            Path.Combine(SoundRoot, RelativePath.Replace("/", "\")))
        If Not TargetPath.StartsWith(SoundRoot, StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidDataException($"音效包包含越界路径：{RelativePath}")
        End If
        Return TargetPath
    End Function

    Friend Function WarThunderGetSoundOverlayRoot() As String
        Return Path.Combine(
            Paths.AppDataThenName, "WarThunder", "SoundOverlay")
    End Function

    Private Function WarThunderGetSoundOverlayStatePath() As String
        Return Path.Combine(WarThunderGetSoundOverlayRoot(), "state.json")
    End Function

    Private Function WarThunderGetSoundOverlayBackupsRoot() As String
        Return Path.Combine(WarThunderGetSoundOverlayRoot(), "backups")
    End Function

    Private Function WarThunderGetSoundOverlayBackupPath(
        RelativePath As String) As String
        If String.IsNullOrWhiteSpace(RelativePath) Then Return ""
        Dim Root As String =
            Path.GetFullPath(WarThunderGetSoundOverlayBackupsRoot()).
            TrimEnd("\"c) & "\"
        Dim Result As String = Path.GetFullPath(
            Path.Combine(Root, RelativePath.Replace("/", "\")))
        If Not Result.StartsWith(Root, StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidDataException("音效包备份路径越界。")
        End If
        Return Result
    End Function

    Private Function WarThunderLoadSoundOverlayState() As JObject
        Dim StatePath As String = WarThunderGetSoundOverlayStatePath()
        If Not File.Exists(StatePath) Then
            Return New JObject From {
                New JProperty("version", SoundOverlayStateVersion),
                New JProperty("components", New JObject()),
                New JProperty("files", New JObject()),
                New JProperty("config_chain", New JArray())
            }
        End If
        Try
            Dim State = JObject.Parse(File.ReadAllText(StatePath))
            Dim VersionValue As Integer = EntryInteger(State, "version")
            If VersionValue <> SoundOverlayStateVersion Then
                Throw New InvalidDataException(
                    $"不支持的音效包记录版本：{VersionValue}")
            End If
            EnsureJObject(State, "components")
            EnsureJObject(State, "files")
            EnsureJArray(State, "config_chain")
            Return State
        Catch ex As Exception
            Throw New InvalidDataException(
                $"读取音效包安装记录失败：{StatePath}", ex)
        End Try
    End Function

    Private Sub WarThunderSaveSoundOverlayState(State As JObject)
        If State Is Nothing Then Throw New ArgumentNullException(NameOf(State))
        Dim StatePath As String = WarThunderGetSoundOverlayStatePath()
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath))
        Dim TempPath As String = StatePath & ".tmp"
        Try
            File.WriteAllText(
                TempPath,
                State.ToString(Newtonsoft.Json.Formatting.Indented),
                New UTF8Encoding(False))
            If File.Exists(StatePath) Then
                File.Replace(TempPath, StatePath, Nothing, True)
            Else
                File.Move(TempPath, StatePath)
            End If
        Finally
            FileUtils.Delete(TempPath)
        End Try
    End Sub

    Private Function EnsureJObject(State As JObject, Name As String) As JObject
        Dim Value = TryCast(State(Name), JObject)
        If Value Is Nothing Then
            Value = New JObject()
            State(Name) = Value
        End If
        Return Value
    End Function

    Private Function EnsureJArray(State As JObject, Name As String) As JArray
        Dim Value = TryCast(State(Name), JArray)
        If Value Is Nothing Then
            Value = New JArray()
            State(Name) = Value
        End If
        Return Value
    End Function

    Private Function EntryString(Value As JToken, Name As String) As String
        Dim Item = TryCast(Value, JObject)
        If Item Is Nothing Then Return ""
        Dim Token = Item(Name)
        If Token Is Nothing OrElse Token.Type = JTokenType.Null Then Return ""
        Return If(Token.Value(Of String)(), "").Trim()
    End Function

    Private Function EntryBoolean(Value As JToken, Name As String) As Boolean
        Dim Item = TryCast(Value, JObject)
        If Item Is Nothing OrElse Item(Name) Is Nothing Then Return False
        Try
            Return Item.Value(Of Boolean)(Name)
        Catch
            Return False
        End Try
    End Function

    Private Function EntryInteger(Value As JToken, Name As String) As Integer
        Dim Item = TryCast(Value, JObject)
        If Item Is Nothing OrElse Item(Name) Is Nothing Then Return 0
        Try
            Return Item.Value(Of Integer)(Name)
        Catch
            Return 0
        End Try
    End Function

    Private Function EntryLong(Value As JToken, Name As String) As Long
        Dim Item = TryCast(Value, JObject)
        If Item Is Nothing OrElse Item(Name) Is Nothing Then Return 0
        Try
            Return Item.Value(Of Long)(Name)
        Catch
            Return 0
        End Try
    End Function

    Private Function ValueOrDefault(Value As String, DefaultValue As String) As String
        Return If(String.IsNullOrWhiteSpace(Value), DefaultValue, Value)
    End Function

    Private Function SanitizeSoundComponentName(Value As String) As String
        Dim Name As String = If(Value, "").Trim()
        If Name = "" Then Name = "音效包"
        For Each InvalidCharacter In Path.GetInvalidFileNameChars()
            Name = Name.Replace(InvalidCharacter, "_"c)
        Next
        Return Name.Trim()
    End Function

    Private Sub WarThunderGetComponentStats(PathValue As String,
                                            ByRef FileCount As Integer,
                                            ByRef TotalSize As Long)
        Try
            For Each FilePath As String In Directory.EnumerateFiles(
                PathValue, "*", SearchOption.AllDirectories)
                Try
                    Dim Info As New FileInfo(FilePath)
                    FileCount += 1
                    TotalSize += Info.Length
                Catch
                End Try
            Next
        Catch
        End Try
    End Sub

End Module
