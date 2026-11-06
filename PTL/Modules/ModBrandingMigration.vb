Imports Microsoft.Win32

''' <summary>
''' 将旧 PCL WT 品牌的数据迁移到 PTL，不删除旧数据。
''' </summary>
Public Module ModBrandingMigration

    Private Const LegacyRegistryRoot As String = "Software\PCLWT"
    Private Const CurrentRegistryRoot As String = "Software\PTL"

    Public Sub MigrateLegacyBranding()
        Try
            MigrateRegistryTree()
        Catch ex As Exception
            Diagnostics.Debug.WriteLine($"迁移旧 PCL WT 注册表设置失败：{ex}")
        End Try

        MergeDirectory(
            IO.Path.Combine(Paths.Base, "PCLWT"),
            Paths.BaseThenName)
        MergeDirectory(
            IO.Path.Combine(Paths.Base, "PCL"),
            Paths.BaseThenName)
        MergeDirectory(
            IO.Path.Combine(Paths.AppData, "PCLWT"),
            Paths.AppDataThenName)
        MergeDirectory(
            IO.Path.Combine(IO.Path.GetTempPath(), "PCLWT"),
            IO.Path.Combine(IO.Path.GetTempPath(), "PTL"))
    End Sub

    Private Sub MigrateRegistryTree()
        Using Source = Registry.CurrentUser.OpenSubKey(LegacyRegistryRoot, False)
            If Source Is Nothing Then Return
            Using Destination = Registry.CurrentUser.CreateSubKey(CurrentRegistryRoot)
                CopyMissingRegistryValues(Source, Destination)
            End Using
        End Using
    End Sub

    Private Sub CopyMissingRegistryValues(Source As RegistryKey, Destination As RegistryKey)
        For Each ValueName In Source.GetValueNames()
            If Destination.GetValue(ValueName) Is Nothing Then
                Destination.SetValue(
                    ValueName,
                    Source.GetValue(ValueName, Nothing, RegistryValueOptions.DoNotExpandEnvironmentNames),
                    Source.GetValueKind(ValueName))
            End If
        Next

        For Each ChildName In Source.GetSubKeyNames()
            Try
                Using SourceChild = Source.OpenSubKey(ChildName, False)
                    If SourceChild Is Nothing Then Continue For
                    Using DestinationChild = Destination.CreateSubKey(ChildName)
                        CopyMissingRegistryValues(SourceChild, DestinationChild)
                    End Using
                End Using
            Catch ex As Exception
                Diagnostics.Debug.WriteLine($"迁移旧设置子项失败（{ChildName}）：{ex}")
            End Try
        Next
    End Sub

    Private Sub MergeDirectory(SourcePath As String, DestinationPath As String)
        If String.IsNullOrWhiteSpace(SourcePath) OrElse
           String.IsNullOrWhiteSpace(DestinationPath) OrElse
           Not Directory.Exists(SourcePath) Then Return
        Try
            Directory.CreateDirectory(DestinationPath)
            Dim SourceRoot = IO.Path.GetFullPath(SourcePath).TrimEnd("\"c)
            Dim DestinationRoot = IO.Path.GetFullPath(DestinationPath).TrimEnd("\"c)
            For Each SourceDirectory In Directory.EnumerateDirectories(
                SourcePath, "*", SearchOption.AllDirectories)
                Dim RelativePath = IO.Path.GetFullPath(SourceDirectory).
                    Substring(SourceRoot.Length).TrimStart("\"c)
                Directory.CreateDirectory(IO.Path.Combine(DestinationRoot, RelativePath))
            Next
            For Each SourceFile In Directory.EnumerateFiles(
                SourcePath, "*", SearchOption.AllDirectories)
                Dim RelativePath = IO.Path.GetFullPath(SourceFile).
                    Substring(SourceRoot.Length).TrimStart("\"c)
                Dim DestinationFile = IO.Path.Combine(DestinationRoot, RelativePath)
                If IO.File.Exists(DestinationFile) Then Continue For
                Directory.CreateDirectory(IO.Path.GetDirectoryName(DestinationFile))
                IO.File.Copy(SourceFile, DestinationFile, False)
            Next
            Diagnostics.Debug.WriteLine($"已合并旧品牌数据：{SourcePath} → {DestinationPath}")
        Catch ex As Exception
            Diagnostics.Debug.WriteLine($"合并旧品牌数据失败（{SourcePath}）：{ex}")
        End Try
    End Sub

End Module
