Public Enum WarThunderComponentKind
    Sights
    Camouflage
    Sound
End Enum

Public Class WarThunderInstalledComponent
    Public Property Kind As WarThunderComponentKind
    Public Property Name As String = ""
    Public Property Path As String = ""
    Public Property FileCount As Integer
    Public Property TotalSize As Long
End Class

''' <summary>
''' 管理战争雷霆已安装的瞄具和声音组件。
''' </summary>
Public Module ModWarThunderComponents

    Public Function WarThunderGetComponentRoot(Kind As WarThunderComponentKind) As String
        Select Case Kind
            Case WarThunderComponentKind.Sights
                Return WarThunderGetUserSightsFolder()
            Case WarThunderComponentKind.Camouflage
                Return WarThunderGetUserSkinsFolder()
            Case Else
                Return WarThunderGetSoundModFolder()
        End Select
    End Function

    Public Function WarThunderComponentKindName(Kind As WarThunderComponentKind) As String
        Select Case Kind
            Case WarThunderComponentKind.Sights
                Return "瞄具"
            Case WarThunderComponentKind.Camouflage
                Return "涂装"
            Case Else
                Return "音效包"
        End Select
    End Function

    Public Function WarThunderEnumerateInstalledComponents(
        Kind As WarThunderComponentKind) As List(Of WarThunderInstalledComponent)
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

    Public Function WarThunderRemoveInstalledComponent(
        Component As WarThunderInstalledComponent,
        ByRef ErrorMessage As String) As Boolean
        ErrorMessage = ""
        Try
            Dim Root As String = Path.GetFullPath(
                WarThunderGetComponentRoot(Component.Kind)).TrimEnd("\"c)
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
            Logger.Info($"已直接删除战争雷霆组件：{Target}")
            Return True
        Catch ex As Exception
            ErrorMessage = ex.GetDisplay(False)
            Logger.Error(ex, $"卸载战争雷霆组件失败：{Component.Path}")
            Return False
        End Try
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
