Imports System.Security.Cryptography

Public Module ModLauncherUtilities

    Public Sub LauncherOptimizeMemory()
        Dim Result = PtlOptimizeMemory()
        Dim ReleasedText As String =
            StringUtils.FormatByteSize(Result.AvailableIncrease)
        Hint($"内存优化完成，可用内存增加 {ReleasedText}。", HintType.Green)
    End Sub

    Public Sub LauncherOptimizeMemoryInternal()
        PtlOptimizeMemory()
    End Sub

    Public Sub LauncherShowDailyLuck()
        Dim Payload As Byte() = Encoding.UTF8.GetBytes(
            Date.Now.ToString("yyyyMMdd") & "|" & Identify)
        Dim Luck As Integer
        Using Hasher As SHA256 = SHA256.Create()
            Dim Hash = Hasher.ComputeHash(Payload)
            Luck = CInt(BitConverter.ToUInt32(Hash, 0) Mod 101UI)
        End Using
        Hint($"今日人品：{Luck}", If(Luck >= 80, HintType.Green, HintType.Blue))
    End Sub

    Public Sub LauncherClearTemporaryData()
        DirectoryUtils.Delete(Path.Combine(PathTemp, "PTL", "TaskTemp"))
        Hint("临时文件已清理。", HintType.Green)
    End Sub

    Public Sub LauncherDownloadFile(
        Url As String,
        FileName As String,
        Optional Folder As String = Nothing)
        Dim TargetFolder As String =
            If(String.IsNullOrWhiteSpace(Folder),
               Path.Combine(Paths.Base, "Downloads"),
               Folder)
        DirectoryUtils.Create(TargetFolder)
        Dim SafeName As String = Path.GetFileName(FileName)
        If String.IsNullOrWhiteSpace(SafeName) Then SafeName = "download.bin"
        Dim TargetPath As String = Path.Combine(TargetFolder, SafeName)
        RunInNewThread(
            Sub()
                Try
                    NetDownloadByLoader(Url, TargetPath)
                    RunInUi(Sub() Hint($"下载完成：{TargetPath}", HintType.Green))
                Catch ex As Exception
                    Logger.Error(ex, "下载自定义事件文件失败")
                    RunInUi(Sub() Hint("文件下载失败，请查看日志。", HintType.Red))
                End Try
            End Sub, "Launcher Custom Download", ThreadPriority.BelowNormal)
    End Sub

    Public Function LauncherGetRandomHint() As String
        Select Case RandomInteger(0, 3)
            Case 0
                Return "按 Enter 可以直接启动游戏。"
            Case 1
                Return "可以从顶部栏切换玩家并查看战绩。"
            Case 2
                Return "支持把瞄具或语音包拖入窗口安装。"
            Case Else
                Return "启动设置中可以切换正式服与测试服。"
        End Select
    End Function

    Public Function LauncherGetRandomTip() As String
        Return "提示见启动器日志。"
    End Function

End Module
