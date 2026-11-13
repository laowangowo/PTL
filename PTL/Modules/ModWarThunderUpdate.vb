Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions

''' <summary>
''' YUP 清单中的单个文件项。
''' </summary>
Friend NotInheritable Class WarThunderYupFile
    Public Property RelativePath As String = ""
    Public Property Length As Long = 0
    Public Property ModifiedTime As Long = 0
    Public Property Sha1 As Byte() = Nothing
    Public Property Attr As String = ""
    Public Property TorrentIndex As Integer = -1
    Public Property ZipEntries As New List(Of String)

    Public ReadOnly Property IsPatch As Boolean
        Get
            Return Attr.Equals("p", StringComparison.OrdinalIgnoreCase)
        End Get
    End Property
End Class

''' <summary>
''' War Thunder 的 YUP 清单。
''' </summary>
Friend NotInheritable Class WarThunderYupSnapshot
    Public Property Version As String = ""
    Public Property LocalVersion As String = ""
    Public Property RawData As Byte() = Nothing
    Public Property Files As New Dictionary(Of String, WarThunderYupFile)(StringComparer.OrdinalIgnoreCase)

    Public Shared Function TryRead(FilePath As String) As WarThunderYupSnapshot
        If String.IsNullOrWhiteSpace(FilePath) OrElse Not File.Exists(FilePath) Then Return Nothing
        Try
            Dim Data As Byte()
            Using Stream As New FileStream(FilePath, FileMode.Open, FileAccess.Read,
                                            FileShare.ReadWrite Or FileShare.Delete)
                If Stream.Length <= 0 OrElse Stream.Length > 128L * 1024L * 1024L Then Return Nothing
                Data = New Byte(CInt(Stream.Length) - 1) {}
                Dim ReadTotal As Integer = 0
                Do While ReadTotal < Data.Length
                    Dim ReadCount As Integer = Stream.Read(Data, ReadTotal, Data.Length - ReadTotal)
                    If ReadCount <= 0 Then Exit Do
                    ReadTotal += ReadCount
                Loop
                If ReadTotal <> Data.Length Then Return Nothing
            End Using

            Dim RootObject = New WarThunderBencodeReader(Data).ReadDocument()
            Dim Root = TryCast(RootObject, Dictionary(Of String, Object))
            If Root Is Nothing Then Return Nothing
            Dim Info = TryCast(Root.ItemOrDefault("info"), Dictionary(Of String, Object))
            If Info Is Nothing Then Return Nothing
            Dim Files = TryCast(Info.ItemOrDefault("files"), List(Of Object))
            If Files Is Nothing Then Return Nothing

            Dim Result As New WarThunderYupSnapshot
            Result.RawData = Data
            Dim VersionData = TryCast(Info.ItemOrDefault("version"), Byte())
            If VersionData IsNot Nothing Then Result.Version = Encoding.UTF8.GetString(VersionData)
            Dim YupInfo = TryCast(Root.ItemOrDefault("yup"), Dictionary(Of String, Object))
            If YupInfo IsNot Nothing Then
                Dim LocalVersionData = TryCast(YupInfo.ItemOrDefault("version"), Byte())
                If LocalVersionData IsNot Nothing Then
                    Result.LocalVersion = Encoding.UTF8.GetString(LocalVersionData)
                End If
            End If

            For Index As Integer = 0 To Files.Count - 1
                Dim RawEntry = Files(Index)
                Dim Entry = TryCast(RawEntry, Dictionary(Of String, Object))
                If Entry Is Nothing Then Continue For
                Dim LengthObject = Entry.ItemOrDefault("length")
                If LengthObject Is Nothing Then Continue For

                Dim RelativePath As String = WarThunderYupPath(Entry)
                If RelativePath = "" Then Continue For
                Dim ResultEntry As New WarThunderYupFile With {
                    .RelativePath = RelativePath,
                    .Length = Convert.ToInt64(LengthObject, CultureInfo.InvariantCulture),
                    .TorrentIndex = Index
                }
                Dim AttrData = TryCast(Entry.ItemOrDefault("attr"), Byte())
                If AttrData IsNot Nothing Then ResultEntry.Attr = Encoding.UTF8.GetString(AttrData)
                Dim ModifiedTimeObject = Entry.ItemOrDefault("mtime")
                If ModifiedTimeObject IsNot Nothing Then
                    ResultEntry.ModifiedTime = Convert.ToInt64(
                        ModifiedTimeObject, CultureInfo.InvariantCulture)
                End If
                ResultEntry.Sha1 = TryCast(Entry.ItemOrDefault("sha1"), Byte())
                Dim ZipEntries = WarThunderYupStrings(Entry.ItemOrDefault("zipp"))
                If ZipEntries.Count = 0 Then
                    '兼容旧版清单可能使用的字段名。
                    ZipEntries = WarThunderYupStrings(Entry.ItemOrDefault("zip"))
                End If
                ResultEntry.ZipEntries.AddRange(ZipEntries)
                Result.Files(RelativePath) = ResultEntry
            Next
            Return Result
        Catch ex As Exception
            Logger.Warn(ex, $"解析战争雷霆 YUP 清单失败：{FilePath}")
            Return Nothing
        End Try
    End Function

    Private Shared Function WarThunderYupPath(Entry As Dictionary(Of String, Object)) As String
        Dim PathParts = TryCast(Entry.ItemOrDefault("path"), List(Of Object))
        If PathParts Is Nothing OrElse PathParts.Count = 0 Then Return ""
        Dim Builder As New Text.StringBuilder
        For Each RawPart In PathParts
            Dim Part = TryCast(RawPart, Byte())
            If Part Is Nothing Then Continue For
            If Builder.Length > 0 Then Builder.Append("/"c)
            Builder.Append(Encoding.UTF8.GetString(Part))
        Next
        Return Builder.ToString()
    End Function

    Private Shared Function WarThunderYupStrings(Value As Object) As IEnumerable(Of String)
        Dim Result As New List(Of String)
        Dim Values = TryCast(Value, List(Of Object))
        If Values Is Nothing Then Return Result
        For Each RawValue In Values
            Dim Data = TryCast(RawValue, Byte())
            If Data IsNot Nothing Then Result.Add(Encoding.UTF8.GetString(Data))
        Next
        Return Result
    End Function
End Class

''' <summary>
''' 按 YUP 中实际发生变化的 .p 分片估算下载进度。
''' </summary>
Friend NotInheritable Class WarThunderPieceUpdate
    Public ReadOnly Sizes As New Dictionary(Of String, Long)(StringComparer.OrdinalIgnoreCase)

    Public ReadOnly Property TotalBytes As Long
        Get
            Return Sizes.Values.Sum()
        End Get
    End Property

    Public Shared Function Create(OldManifest As WarThunderYupSnapshot,
                                  NewManifest As WarThunderYupSnapshot) As WarThunderPieceUpdate
        Dim Result As New WarThunderPieceUpdate
        If NewManifest Is Nothing Then Return Result
        For Each NewEntry In NewManifest.Files.Values
            If Not NewEntry.RelativePath.StartsWith(".p/", StringComparison.OrdinalIgnoreCase) Then Continue For
            Dim OldEntry As WarThunderYupFile = Nothing
            Dim Changed As Boolean = OldManifest Is Nothing OrElse
                Not OldManifest.Files.TryGetValue(NewEntry.RelativePath, OldEntry) OrElse
                OldEntry.Length <> NewEntry.Length OrElse
                (OldEntry.Sha1 IsNot Nothing AndAlso NewEntry.Sha1 IsNot Nothing AndAlso
                 Not OldEntry.Sha1.SequenceEqual(NewEntry.Sha1))
            If Changed Then Result.Sizes(NewEntry.RelativePath) = NewEntry.Length
        Next
        Return Result
    End Function
End Class

''' <summary>
''' 实时解析官方启动器日志，计算可显示的更新阶段和下载进度。
''' </summary>
Friend NotInheritable Class WarThunderUpdateLogTracker
    Private Shared ReadOnly UpdateFileRegex As New Regex(
        "File to update due to .+?:\s*(?<path>.+?)\s*$", RegexOptions.Compiled)
    Private Shared ReadOnly CompleteFileRegex As New Regex(
        "\bFile (?<path>.+?) complete\s*$", RegexOptions.Compiled)
    Private Shared ReadOnly CachePopRegex As New Regex(
        "\.yup cache pop .+? to (?<path>[A-Za-z]:\\.+?)\s*$", RegexOptions.Compiled)
    Private Shared ReadOnly DownloadStateRegex As New Regex(
        "Downloading state (?<state>\d+)", RegexOptions.Compiled)
    Private Shared ReadOnly DownloadSpeedRegex As New Regex(
        "Stat download_kbs=(?<speed>\d+)", RegexOptions.Compiled)
    Private Shared ReadOnly TotalUpdateFilesRegex As New Regex(
        "Total\s+(?<count>\d+)\s+files?\s+to\s+update", RegexOptions.Compiled)
    Private ReadOnly InstallPath As String
    Private Property TargetFiles As Dictionary(Of String, WarThunderYupFile)
    Friend Property ExpectedPieces As WarThunderPieceUpdate
    Private ReadOnly UpdatePaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly ExpectedUpdatePaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly CompletedPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly PieceMaximums As New Dictionary(Of String, Long)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly SpeedSamples As New Queue(Of Long)
    Private LastPieceBytes As Long = 0
    Private LastPieceSample As Date = Date.MinValue
    Private LastCompletedBytes As Long = 0
    Private LastCompletedSample As Date = Date.MinValue
    Private LastProgress As Double = 0
    Private TotalFinalized As Boolean = False

    Public Property CurrentState As Integer = -1
    Public Property DownloadStarted As Boolean = False
    Public Property DownloadFinished As Boolean = False
    Public Property FilesChecked As Boolean = False
    Public Property LauncherFinished As Boolean = False
    Public Property ObservedUpdateFiles As Integer = 0
    Public Property TotalBytes As Long = 0
    Public Property CompletedBytes As Long = 0
    Public Property PieceBytes As Long = 0
    Public Property SpeedBytes As Long = 0
    Public Property TotalFiles As Integer = 0
    Public Property FilesRemaining As Integer = -1
    Public Property Progress As Double = 0
    Private DeclaredTotalFiles As Integer = 0

    Public Sub New(InstallFolder As String,
                   Target As Dictionary(Of String, WarThunderYupFile),
                   PieceUpdate As WarThunderPieceUpdate)
        InstallPath = InstallFolder.TrimEnd("\"c)
        TargetFiles = If(Target, New Dictionary(Of String, WarThunderYupFile)(StringComparer.OrdinalIgnoreCase))
        ExpectedPieces = If(PieceUpdate, New WarThunderPieceUpdate)
    End Sub

    Friend Sub ReplaceManifest(Target As Dictionary(Of String, WarThunderYupFile),
                               PieceUpdate As WarThunderPieceUpdate)
        TargetFiles = If(Target, New Dictionary(Of String, WarThunderYupFile)(StringComparer.OrdinalIgnoreCase))
        ExpectedPieces = If(PieceUpdate, New WarThunderPieceUpdate)
        PieceMaximums.Clear()
        PieceBytes = 0
        LastPieceBytes = 0
        LastPieceSample = Date.MinValue
        SpeedSamples.Clear()
        SpeedBytes = 0
        If TotalFinalized Then RecalculateCompleted()
    End Sub

    Public Sub ConsumeLine(Line As String)
        If String.IsNullOrWhiteSpace(Line) Then Return

        Dim StateMatch = DownloadStateRegex.Match(Line)
        If StateMatch.Success Then
            Dim NewState As Integer
            If Integer.TryParse(StateMatch.Groups("state").Value, NewState) Then
                If CurrentState <> NewState Then CurrentState = NewState
                If NewState >= 2 Then FinalizeTotal()
                If NewState = 4 Then
                    DownloadStarted = True
                ElseIf NewState = 5 OrElse NewState = 6 Then
                    DownloadFinished = True
                End If
            End If
        End If

        Dim UpdateMatch = UpdateFileRegex.Match(Line)
        If UpdateMatch.Success Then
            AddUpdatePath(UpdateMatch.Groups("path").Value)
        End If

        Dim TotalFilesMatch = TotalUpdateFilesRegex.Match(Line)
        If TotalFilesMatch.Success Then
            Dim Count As Integer
            If Integer.TryParse(TotalFilesMatch.Groups("count").Value, Count) AndAlso Count > 0 Then
                DeclaredTotalFiles = Count
                UpdateFileCounts()
            End If
        End If

        Dim CompleteMatch = CompleteFileRegex.Match(Line)
        If CompleteMatch.Success Then
            MarkComplete(CompleteMatch.Groups("path").Value)
        End If

        Dim PopMatch = CachePopRegex.Match(Line)
        If PopMatch.Success Then
            MarkComplete(PopMatch.Groups("path").Value)
        End If

        Dim SpeedMatch = DownloadSpeedRegex.Match(Line)
        If SpeedMatch.Success Then
            Dim SpeedKbs As Long
            If Long.TryParse(SpeedMatch.Groups("speed").Value, SpeedKbs) Then
                AddSpeedSample(SpeedKbs * 1024L)
            End If
        End If
        If Line.Contains("Stat download_start=1") Then
            DownloadStarted = True
            FinalizeTotal()
        ElseIf Line.Contains("Files check done") OrElse Line.Contains("Fast check done") Then
            FilesChecked = True
        End If
        If Line.Contains("Downloading done") OrElse Line.Contains("Download complete, doing final steps") Then
            DownloadFinished = True
            FilesRemaining = 0
            SpeedSamples.Clear()
            SpeedBytes = 0
        End If
        If Line.Contains("state changed to: finished") Then
            LauncherFinished = True
            FilesRemaining = 0
            SpeedSamples.Clear()
            SpeedBytes = 0
        End If
    End Sub

    Public Sub SamplePieces(Elapsed As TimeSpan)
        If ExpectedPieces Is Nothing OrElse ExpectedPieces.Sizes.Count = 0 Then Return
        Dim Now As Date = Date.UtcNow
        Dim CurrentBytes As Long = 0
        For Each Pair In ExpectedPieces.Sizes
            Dim RelativePath As String = Pair.Key.Replace("/", "\")
            Dim FullPath As String = Path.Combine(InstallPath, RelativePath)
            Dim Observed As Long = 0
            Try
                Dim Info As New FileInfo(FullPath)
                If Info.Exists Then Observed = Math.Min(Info.Length, Pair.Value)
            Catch
                '下载期间文件可能刚好被移动，下一轮再读取即可。
            End Try
            Dim Previous As Long = 0
            PieceMaximums.TryGetValue(Pair.Key, Previous)
            If Observed > Previous Then PieceMaximums(Pair.Key) = Observed
            CurrentBytes += Math.Max(Previous, Observed)
        Next
        PieceBytes = CurrentBytes

        If LastPieceSample <> Date.MinValue AndAlso Now > LastPieceSample Then
            Dim Delta As Long = Math.Max(0, CurrentBytes - LastPieceBytes)
            Dim Seconds As Double = (Now - LastPieceSample).TotalSeconds
            AddSpeedSample(If(Delta > 0 AndAlso Seconds > 0, CLng(Delta / Seconds), 0))
        End If
        LastPieceBytes = CurrentBytes
        LastPieceSample = Now

        If CompletedBytes > LastCompletedBytes AndAlso
           LastCompletedSample <> Date.MinValue AndAlso Now > LastCompletedSample Then
            Dim Delta As Long = CompletedBytes - LastCompletedBytes
            Dim Seconds As Double = (Now - LastCompletedSample).TotalSeconds
            If Delta > 0 AndAlso Seconds > 0 Then
                AddSpeedSample(CLng(Delta / Seconds))
            End If
        End If
        LastCompletedBytes = CompletedBytes
        LastCompletedSample = Now

        Progress = Math.Max(LastProgress, CalculateProgress(Elapsed))
    End Sub

    Public Function GetProgress(Elapsed As TimeSpan) As Double
        Progress = Math.Max(Progress, CalculateProgress(Elapsed))
        LastProgress = Progress
        Return Progress
    End Function

    Public Function GetStatusText() As String
        If LauncherFinished OrElse (CurrentState = 6 AndAlso DownloadFinished) Then Return "正在校验游戏文件"
        If CurrentState = 6 Then Return "正在完成更新"
        If CurrentState = 5 Then Return "正在安装更新"
        If DownloadFinished Then Return "正在安装更新"
        If CurrentState = 4 OrElse DownloadStarted Then
            Return "正在下载更新"
        End If
        If CurrentState = 2 Then Return "正在准备更新"
        If CurrentState = 1 Then Return "正在检查更新文件"
        Return "正在启动更新组件"
    End Function

    Private Sub AddUpdatePath(RawPath As String)
        Dim RelativePath As String = NormalizePath(RawPath)
        If RelativePath = "" OrElse Not UpdatePaths.Add(RelativePath) Then Return
        ExpectedUpdatePaths.Add(RelativePath)
        ObservedUpdateFiles = ExpectedUpdatePaths.Count
        UpdateFileCounts()
        If TotalFinalized Then RecalculateCompleted()
    End Sub

    Private Sub MarkComplete(RawPath As String)
        Dim RelativePath As String = NormalizePath(RawPath)
        If RelativePath = "" OrElse Not CompletedPaths.Add(RelativePath) Then Return
        If Not UpdatePaths.Contains(RelativePath) Then
            UpdatePaths.Add(RelativePath)
        End If
        UpdateFileCounts()
        If TotalFinalized Then RecalculateCompleted()
    End Sub

    Private Sub AddSpeedSample(Value As Long)
        If DownloadFinished Then Value = 0
        SpeedSamples.Enqueue(Math.Max(0, Value))
        While SpeedSamples.Count > 8
            SpeedSamples.Dequeue()
        End While
        SpeedBytes = CLng(SpeedSamples.Average())
    End Sub

    Private Sub UpdateFileCounts()
        TotalFiles = If(DeclaredTotalFiles > 0, DeclaredTotalFiles, ExpectedUpdatePaths.Count)
        Dim Completed As Integer = ExpectedUpdatePaths.Where(
            Function(Path) CompletedPaths.Contains(Path)).Count()
        FilesRemaining = If(TotalFiles > 0, Math.Max(0, TotalFiles - Completed), -1)
    End Sub

    Private Sub FinalizeTotal()
        If TotalFinalized Then Return
        TotalFinalized = True
        RecalculateCompleted()
    End Sub

    Private Sub RecalculateCompleted()
        TotalBytes = 0
        CompletedBytes = 0
        For Each RelativePath In UpdatePaths
            Dim Entry As WarThunderYupFile = Nothing
            If Not TargetFiles.TryGetValue(RelativePath, Entry) Then Continue For
            TotalBytes += Entry.Length
            If CompletedPaths.Contains(RelativePath) Then CompletedBytes += Entry.Length
        Next
    End Sub

    Private Function NormalizePath(RawPath As String) As String
        If String.IsNullOrWhiteSpace(InstallPath) OrElse String.IsNullOrWhiteSpace(RawPath) Then Return ""
        Dim PathText As String = RawPath.Trim().Trim(""""c).Replace("/", "\")
        Dim Root As String = InstallPath.TrimEnd("\"c) & "\"
        Try
            If Path.IsPathRooted(PathText) Then
                PathText = Path.GetFullPath(PathText)
                If PathText.StartsWith(Root, StringComparison.OrdinalIgnoreCase) Then
                    PathText = PathText.Substring(Root.Length)
                Else
                    Return ""
                End If
            End If
        Catch
            Return ""
        End Try
        PathText = PathText.TrimStart("\"c)
        If PathText.StartsWith("warthunder_yupcache\", StringComparison.OrdinalIgnoreCase) Then
            PathText = PathText.Substring("warthunder_yupcache\".Length)
        End If
        Return PathText.Replace("\"c, "/"c)
    End Function

    Private Function CalculateProgress(Elapsed As TimeSpan) As Double
        If LauncherFinished Then Return 1
        Dim Result As Double
        Select Case CurrentState
            Case 0
                Result = 0.02 + Math.Min(0.06, Math.Max(0, Elapsed.TotalSeconds) / 600.0 * 0.06)
            Case 1
                Result = 0.08 + Math.Min(0.07, Math.Max(0, Elapsed.TotalSeconds - 5) / 600.0 * 0.07)
            Case 2
                Result = 0.18
            Case 4
                Dim FileRatio As Double = If(TotalBytes > 0, CompletedBytes / CDbl(TotalBytes), 0)
                Dim PieceRatio As Double = If(ExpectedPieces.TotalBytes > 0,
                                              PieceBytes / CDbl(ExpectedPieces.TotalBytes), 0)
                Dim Ratio As Double = Math.Min(1, Math.Max(0, Math.Max(FileRatio, PieceRatio)))
                If Ratio <= 0 Then
                    Result = 0.25 + Math.Min(0.5, Math.Max(0, Elapsed.TotalSeconds - 20) / 600.0 * 0.5)
                Else
                    Result = 0.25 + Ratio * 0.59
                End If
            Case 5
                Result = 0.87
            Case 6
                Result = 0.97
            Case Else
                Result = 0.02 + Math.Min(0.1, Math.Max(0, Elapsed.TotalSeconds) / 300.0 * 0.1)
        End Select
        If DownloadFinished AndAlso Result < 0.96 Then Result = 0.96
        If LauncherFinished Then Result = 1
        Return Math.Min(1, Math.Max(0, Result))
    End Function
End Class

''' <summary>
''' 最小化的 Bencode 读取器，仅用于读取官方 YUP 清单。
''' </summary>
Friend NotInheritable Class WarThunderBencodeReader
    Private ReadOnly Data As Byte()
    Private Position As Integer = 0

    Public Sub New(Input As Byte())
        Data = Input
    End Sub

    Public Function ReadDocument() As Object
        Dim Result = ReadValue(0)
        If Position <> Data.Length Then
            Throw New InvalidDataException("YUP 清单结尾存在无法识别的数据。")
        End If
        Return Result
    End Function

    Private Function ReadValue(Depth As Integer) As Object
        If Depth > 128 Then Throw New InvalidDataException("YUP 清单嵌套层级过深。")
        If Position >= Data.Length Then Throw New EndOfStreamException("YUP 清单意外结束。")
        Select Case Data(Position)
            Case 105 'i
                Return ReadInteger()
            Case 108 'l
                Return ReadList(Depth + 1)
            Case 100 'd
                Return ReadDictionary(Depth + 1)
            Case Else
                If Data(Position) >= 48 AndAlso Data(Position) <= 57 Then Return ReadByteString()
                Throw New InvalidDataException($"YUP 清单在位置 {Position} 包含无效标记。")
        End Select
    End Function

    Private Function ReadInteger() As Long
        Position += 1
        Dim Start As Integer = Position
        If Position < Data.Length AndAlso Data(Position) = 45 Then Position += 1
        Do While Position < Data.Length AndAlso Data(Position) <> 101
            If Data(Position) < 48 OrElse Data(Position) > 57 Then
                Throw New InvalidDataException("YUP 清单包含无效整数。")
            End If
            Position += 1
        Loop
        If Position >= Data.Length Then Throw New EndOfStreamException("YUP 整数字段不完整。")
        Dim Text As String = Encoding.ASCII.GetString(Data, Start, Position - Start)
        Position += 1
        Return Long.Parse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
    End Function

    Private Function ReadByteString() As Byte()
        Dim Start As Integer = Position
        Do While Position < Data.Length AndAlso Data(Position) <> 58
            If Data(Position) < 48 OrElse Data(Position) > 57 Then
                Throw New InvalidDataException("YUP 清单包含无效字符串长度。")
            End If
            Position += 1
        Loop
        If Position >= Data.Length Then Throw New EndOfStreamException("YUP 字符串长度字段不完整。")
        Dim Length As Integer = Integer.Parse(
            Encoding.ASCII.GetString(Data, Start, Position - Start), CultureInfo.InvariantCulture)
        Position += 1
        If Length < 0 OrElse Position + Length > Data.Length Then
            Throw New EndOfStreamException("YUP 字符串内容不完整。")
        End If
        If Length = 0 Then Return New Byte() {}
        Dim Result As Byte() = New Byte(Length - 1) {}
        Buffer.BlockCopy(Data, Position, Result, 0, Length)
        Position += Length
        Return Result
    End Function

    Private Function ReadList(Depth As Integer) As List(Of Object)
        Position += 1
        Dim Result As New List(Of Object)
        Do While Position < Data.Length AndAlso Data(Position) <> 101
            Result.Add(ReadValue(Depth))
        Loop
        If Position >= Data.Length Then Throw New EndOfStreamException("YUP 列表字段不完整。")
        Position += 1
        Return Result
    End Function

    Private Function ReadDictionary(Depth As Integer) As Dictionary(Of String, Object)
        Position += 1
        Dim Result As New Dictionary(Of String, Object)(StringComparer.Ordinal)
        Do While Position < Data.Length AndAlso Data(Position) <> 101
            Dim KeyData As Byte() = ReadByteString()
            Dim Key As String = Encoding.UTF8.GetString(KeyData)
            Result(Key) = ReadValue(Depth)
        Loop
        If Position >= Data.Length Then Throw New EndOfStreamException("YUP 字典字段不完整。")
        Position += 1
        Return Result
    End Function
End Class

Friend Module WarThunderDictionaryExtensions
    <Runtime.CompilerServices.Extension>
    Public Function ItemOrDefault(Of TKey, TValue)(
        Dictionary As IDictionary(Of TKey, TValue), Key As TKey) As TValue
        If Dictionary Is Nothing Then Return Nothing
        Dim Value As TValue = Nothing
        If Dictionary.TryGetValue(Key, Value) Then Return Value
        Return Nothing
    End Function
End Module
