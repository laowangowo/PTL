Imports Microsoft.Win32
Imports Newtonsoft.Json.Linq
Imports System.Text.RegularExpressions

Public Class WarThunderInstalledTool
    Public Property CatalogKey As String = ""
    Public Property Slug As String = ""
    Public Property DisplayName As String = ""
    Public Property Version As String = ""
    Public Property UninstallerPath As String = ""
    Public Property InstallLocation As String = ""

    Public ReadOnly Property CanUninstall As Boolean
        Get
            Return Not String.IsNullOrWhiteSpace(UninstallerPath) AndAlso
                IO.File.Exists(UninstallerPath)
        End Get
    End Property
End Class

''' <summary>
''' 识别通过 Inno Setup 安装的工具，并提供安全、静默的卸载入口。
''' </summary>
Public Module ModWarThunderTools

    Private Class WarThunderToolUninstallEntry
        Public Property DisplayName As String = ""
        Public Property DisplayVersion As String = ""
        Public Property UninstallString As String = ""
        Public Property InstallLocation As String = ""
        Public Property RegistryPath As String = ""
    End Class

    Private ReadOnly InnoUninstallerName As New Regex(
        "^unins\d*\.exe$", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)

    Public Function WarThunderGetToolCatalogKey(Item As JObject) As String
        If Item Is Nothing Then Return ""
        For Each PropertyName In {"id", "slug", "update_url"}
            Dim Value As String = Item.Value(Of String)(PropertyName)
            If Not String.IsNullOrWhiteSpace(Value) Then Return Value.Trim()
        Next
        Return ""
    End Function

    ''' <summary>
    ''' 工具页只显示第三方工具，不把 PTL 自身当作可安装工具。
    ''' </summary>
    Public Function WarThunderFilterToolCatalog(Items As JArray) As JArray
        Dim Result As New JArray()
        If Items Is Nothing Then Return Result
        For Each Token As JToken In Items
            Dim Item = TryCast(Token, JObject)
            If Item Is Nothing OrElse WarThunderIsPtlTool(Item) Then Continue For
            Result.Add(Token.DeepClone())
        Next
        Return Result
    End Function

    Private Function WarThunderIsPtlTool(Item As JObject) As Boolean
        If Item Is Nothing Then Return False
        Dim LauncherKeys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "ptl",
            "ptlexe",
            "ptlsetup",
            "ptlinstaller",
            "ptllauncher",
            "plaincraftlauncherwt",
            "plaincraftlauncher2"
        }
        For Each PropertyName In {"name", "slug", "id", "filename", "download_url", "update_url"}
            Dim Value As String = Item.Value(Of String)(PropertyName)
            If String.IsNullOrWhiteSpace(Value) Then Continue For
            Dim Key As String = Regex.Replace(
                Value.Trim().ToLowerInvariant(), "[^a-z0-9]", "")
            If LauncherKeys.Contains(Key) OrElse
               Key.StartsWith("ptlsetup", StringComparison.OrdinalIgnoreCase) OrElse
               Key.StartsWith("ptlinstaller", StringComparison.OrdinalIgnoreCase) OrElse
               Key.StartsWith("plaincraftlauncher", StringComparison.OrdinalIgnoreCase) Then
                Return True
            End If
            If Regex.IsMatch(
                Value, "(^|[/\\])pcl[-_ ]?wt([/\\]|$)",
                RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant) Then
                Return True
            End If
        Next
        Return False
    End Function

    ''' <summary>
    ''' 一次扫描卸载注册项，再与工具目录匹配，避免每张卡片重复读取注册表。
    ''' </summary>
    Public Function WarThunderResolveInstalledTools(
        Items As JArray) As Dictionary(Of String, WarThunderInstalledTool)
        Dim Result As New Dictionary(Of String, WarThunderInstalledTool)(
            StringComparer.OrdinalIgnoreCase)
        If Items Is Nothing Then Return Result

        Dim Entries As List(Of WarThunderToolUninstallEntry) =
            WarThunderReadToolUninstallEntries()
        For Each Token As JToken In Items
            Dim Item = TryCast(Token, JObject)
            If Item Is Nothing Then Continue For
            Dim Entry As WarThunderToolUninstallEntry =
                WarThunderMatchToolUninstallEntry(Item, Entries)
            If Entry Is Nothing Then Continue For
            Dim Key As String = WarThunderGetToolCatalogKey(Item)
            If Key = "" Then Continue For
            Result(Key) = New WarThunderInstalledTool With {
                .CatalogKey = Key,
                .Slug = Item.Value(Of String)("slug"),
                .DisplayName = If(String.IsNullOrWhiteSpace(Entry.DisplayName),
                    Item.Value(Of String)("name"), Entry.DisplayName),
                .Version = If(String.IsNullOrWhiteSpace(Entry.DisplayVersion),
                    Item.Value(Of String)("version"), Entry.DisplayVersion),
                .UninstallerPath = WarThunderGetInnoUninstallerPath(Entry.UninstallString),
                .InstallLocation = Entry.InstallLocation
            }
        Next
        Return Result
    End Function

    Public Function WarThunderUninstallTool(
        Tool As WarThunderInstalledTool,
        ByRef ErrorMessage As String) As Boolean
        ErrorMessage = ""
        Try
            If Tool Is Nothing Then
                Throw New InvalidOperationException("工具信息无效。")
            End If
            Dim UninstallerPath As String = Tool.UninstallerPath
            If String.IsNullOrWhiteSpace(UninstallerPath) OrElse
               Not IO.File.Exists(UninstallerPath) Then
                Throw New FileNotFoundException("没有找到该工具的卸载程序。", UninstallerPath)
            End If
            If Not InnoUninstallerName.IsMatch(IO.Path.GetFileName(UninstallerPath)) Then
                Throw New InvalidOperationException("该工具没有提供可安全调用的 Inno Setup 卸载程序。")
            End If

            Using Uninstaller As Process = Process.Start(New ProcessStartInfo With {
                .FileName = UninstallerPath,
                .Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
                .UseShellExecute = True,
                .WorkingDirectory = IO.Path.GetDirectoryName(UninstallerPath)
            })
                If Uninstaller Is Nothing Then
                    Throw New InvalidOperationException("卸载程序未能启动。")
                End If
                If Not Uninstaller.WaitForExit(300000) Then
                    Throw New TimeoutException("等待卸载程序结束超时。")
                End If
                If Uninstaller.ExitCode <> 0 Then
                    Throw New InvalidOperationException(
                        $"卸载程序返回退出代码 {Uninstaller.ExitCode}。")
                End If
            End Using
            Logger.Info($"已卸载工具：{Tool.DisplayName} ({Tool.UninstallerPath})")
            Return True
        Catch ex As Exception
            ErrorMessage = ex.GetDisplay(False)
            Logger.Warn(ex, $"卸载工具失败：{If(Tool?.DisplayName, "")}")
            Return False
        End Try
    End Function

    Private Function WarThunderReadToolUninstallEntries() As List(Of WarThunderToolUninstallEntry)
        Dim Result As New List(Of WarThunderToolUninstallEntry)
        WarThunderReadToolUninstallHive(
            Result, Registry.CurrentUser,
            "Software\Microsoft\Windows\CurrentVersion\Uninstall")
        WarThunderReadToolUninstallHive(
            Result, Registry.LocalMachine,
            "Software\Microsoft\Windows\CurrentVersion\Uninstall")
        WarThunderReadToolUninstallHive(
            Result, Registry.LocalMachine,
            "Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
        Return Result
    End Function

    Private Sub WarThunderReadToolUninstallHive(
        Entries As List(Of WarThunderToolUninstallEntry),
        Root As RegistryKey,
        RegistryPath As String)
        Try
            Using UninstallRoot As RegistryKey = Root.OpenSubKey(RegistryPath)
                If UninstallRoot Is Nothing Then Return
                For Each SubKeyName As String In UninstallRoot.GetSubKeyNames()
                    Using EntryKey As RegistryKey = UninstallRoot.OpenSubKey(SubKeyName)
                        If EntryKey Is Nothing Then Continue For
                        Dim DisplayName As String =
                            Convert.ToString(EntryKey.GetValue("DisplayName", ""))
                        If String.IsNullOrWhiteSpace(DisplayName) Then Continue For
                        Dim UninstallString As String =
                            Convert.ToString(EntryKey.GetValue("UninstallString", ""))
                        If String.IsNullOrWhiteSpace(UninstallString) Then Continue For
                        Entries.Add(New WarThunderToolUninstallEntry With {
                            .DisplayName = DisplayName.Trim(),
                            .DisplayVersion = Convert.ToString(
                                EntryKey.GetValue("DisplayVersion", "")).Trim(),
                            .UninstallString = UninstallString.Trim(),
                            .InstallLocation = Convert.ToString(
                                EntryKey.GetValue("InstallLocation", "")).Trim(),
                            .RegistryPath = RegistryPath & "\" & SubKeyName
                        })
                    End Using
                Next
            End Using
        Catch ex As Exception
            Logger.Info($"读取工具卸载注册表失败（{RegistryPath}）：{ex.Message}")
        End Try
    End Sub

    Private Function WarThunderMatchToolUninstallEntry(
        Item As JObject,
        Entries As List(Of WarThunderToolUninstallEntry)) As WarThunderToolUninstallEntry
        Dim ProductName As String = Item.Value(Of String)("name")
        Dim Slug As String = Item.Value(Of String)("slug")
        Dim ProductKey As String = WarThunderNormalizeToolName(ProductName)
        Dim SlugKey As String = WarThunderNormalizeToolName(Slug)

        For Each Entry In Entries
            Dim EntryKey As String = WarThunderNormalizeToolName(Entry.DisplayName)
            If EntryKey = "" Then Continue For
            If ProductKey <> "" AndAlso EntryKey.Equals(
                ProductKey, StringComparison.OrdinalIgnoreCase) Then
                Return Entry
            End If
            If SlugKey <> "" AndAlso EntryKey.Equals(
                SlugKey, StringComparison.OrdinalIgnoreCase) Then
                Return Entry
            End If
        Next

        For Each Entry In Entries
            Dim EntryKey As String = WarThunderNormalizeToolName(Entry.DisplayName)
            If EntryKey = "" Then Continue For
            If ProductKey.Length >= 5 AndAlso EntryKey.StartsWith(
                ProductKey, StringComparison.OrdinalIgnoreCase) Then
                Return Entry
            End If
            If SlugKey.Length >= 6 AndAlso EntryKey.StartsWith(
                SlugKey, StringComparison.OrdinalIgnoreCase) Then
                Return Entry
            End If
        Next
        Return Nothing
    End Function

    Private Function WarThunderNormalizeToolName(Value As String) As String
        If String.IsNullOrWhiteSpace(Value) Then Return ""
        Dim CleanValue As String = Regex.Replace(
            Value.Trim(), "\s*[（(].*$", "", RegexOptions.IgnoreCase)
        CleanValue = Regex.Replace(
            CleanValue, "\s+v?\d+(?:\.\d+){1,3}\s*$", "",
            RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
        Dim Builder As New Text.StringBuilder()
        For Each CharacterValue As Char In CleanValue
            If Char.IsLetterOrDigit(CharacterValue) Then
                Builder.Append(Char.ToLowerInvariant(CharacterValue))
            End If
        Next
        Return Builder.ToString()
    End Function

    Private Function WarThunderGetInnoUninstallerPath(UninstallString As String) As String
        If String.IsNullOrWhiteSpace(UninstallString) Then Return ""
        Dim Value As String = UninstallString.Trim()
        Dim ExecutablePath As String = ""
        If Value.StartsWith("""") Then
            Dim ClosingQuote As Integer = Value.IndexOf(ChrW(34), 1)
            If ClosingQuote <= 1 Then Return ""
            ExecutablePath = Value.Substring(1, ClosingQuote - 1)
        Else
            Dim ExtensionIndex As Integer = Value.IndexOf(
                ".exe", StringComparison.OrdinalIgnoreCase)
            If ExtensionIndex < 0 Then Return ""
            ExecutablePath = Value.Substring(0, ExtensionIndex + 4).Trim()
        End If
        ExecutablePath = Environment.ExpandEnvironmentVariables(
            ExecutablePath.Trim().Trim(""""c))
        If Not InnoUninstallerName.IsMatch(IO.Path.GetFileName(ExecutablePath)) Then
            Return ""
        End If
        Return IO.Path.GetFullPath(ExecutablePath)
    End Function

End Module
