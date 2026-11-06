''' <summary>
''' 从用户本机安装的战争雷霆中提取背景音乐，不随源码分发游戏音频。
''' </summary>
Public Module ModWarThunderMusic

    Private ReadOnly ExtractLock As New Object()
    Private IsExtracting As Boolean

    Public Sub WarThunderPrepareBackgroundMusicAsync(Optional ForcePrompt As Boolean = False)
        If Not ForcePrompt AndAlso Not Settings.Get(Of Boolean)("UiMusicWarThunder") Then Return
        SyncLock ExtractLock
            If IsExtracting Then Return
            IsExtracting = True
        End SyncLock

        RunInNewThread(
        Sub()
            Try
                Dim Install = WarThunderCurrentInstall
                If Install Is Nothing Then Install = WarThunderDetectInstallation()
                If Install Is Nothing OrElse Install.InstallPath = "" Then Return

                Dim MusicFolder As String = Path.Combine(Paths.Base, "PTL", "Musics")
                Directory.CreateDirectory(MusicFolder)
                Dim ExistingTracks = Directory.GetFiles(MusicFolder, "War Thunder - *.ogg")
                If ExistingTracks.Length > 0 Then Return

                Dim ExtractorPath As String = Path.Combine(
                    Path.GetDirectoryName(PathExe), "PTL", "FsbExtractor", "WarThunderFsbExtractor.exe")
                If Not File.Exists(ExtractorPath) Then
                    WarThunderLog($"背景音乐提取器不存在：{ExtractorPath}")
                    Return
                End If

                WarThunderLog("正在从本机战争雷霆音频包提取背景音乐…")
                Using Extractor As Process = Process.Start(New ProcessStartInfo With {
                    .FileName = ExtractorPath,
                    .Arguments = $"""{Install.InstallPath}"" ""{MusicFolder}""",
                    .WorkingDirectory = Path.GetDirectoryName(ExtractorPath),
                    .UseShellExecute = False,
                    .CreateNoWindow = True,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True
                })
                    If Extractor Is Nothing Then Throw New InvalidOperationException("背景音乐提取器未能启动。")
                    Dim OutputTask = Extractor.StandardOutput.ReadToEndAsync()
                    Dim ErrorTask = Extractor.StandardError.ReadToEndAsync()
                    If Not Extractor.WaitForExit(120000) Then
                        Try
                            Extractor.Kill()
                        Catch
                        End Try
                        Throw New TimeoutException("背景音乐提取超过 2 分钟。")
                    End If
                    Dim OutputText As String = OutputTask.GetAwaiter().GetResult()
                    Dim ErrorText As String = ErrorTask.GetAwaiter().GetResult()
                    If Extractor.ExitCode <> 0 Then
                        Throw New InvalidOperationException(If(
                            String.IsNullOrWhiteSpace(ErrorText), OutputText, ErrorText).Trim())
                    End If
                    WarThunderLog("战争雷霆背景音乐提取完成。")
                End Using

                RunInUi(
                Sub()
                    MusicRefreshPlay(False)
                    FrmSetupUI?.MusicRefreshUI()
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "提取战争雷霆背景音乐失败")
                WarThunderLog($"背景音乐提取失败：{ex.GetDisplay(False)}")
            Finally
                SyncLock ExtractLock
                    IsExtracting = False
                End SyncLock
            End Try
        End Sub, "War Thunder Background Music", ThreadPriority.BelowNormal)
    End Sub

End Module
