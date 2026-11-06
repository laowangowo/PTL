''' <summary>
''' 将战争雷霆资源下载接入 PTL 任务栏和下载引擎。
''' </summary>
Public Module ModWarThunderDownload

    Public Sub WarThunderRunDownloadTask(Name As String,
                                         DownloadFile As NetFile,
                                         InstallWorker As Func(Of String, WarThunderInstallSummary),
                                         OnFinished As Action(Of LoadState, WarThunderInstallSummary, Exception))
        Dim Result As WarThunderInstallSummary = Nothing
        Dim DownloadLoader As New LoaderDownload("下载 " & Name, {DownloadFile}) With {
            .ProgressWeight = 8,
            .Block = True
        }
        Dim InstallLoader As New LoaderTask(Of Integer, WarThunderInstallSummary)(
            "安装 " & Name,
            Sub(Loader)
                Try
                    If Loader.IsCanceled Then Return
                    Result = InstallWorker(DownloadFile.LocalPath)
                    If Result Is Nothing Then
                        Throw New InvalidOperationException("安装程序没有返回结果。")
                    End If
                    Loader.Output = Result
                    If Not Result.Success Then Throw New InvalidOperationException(Result.Message)
                Catch ex As Exception
                    If Result Is Nothing Then
                        Result = New WarThunderInstallSummary With {
                            .Success = False,
                            .Message = ex.GetDisplay(False)
                        }
                    End If
                    Loader.Output = Result
                    Throw
                End Try
            End Sub) With {
            .ProgressWeight = 2,
            .Block = True
        }

        Dim Task As New LoaderCombo(Of Integer)("下载并安装 " & Name, {DownloadLoader, InstallLoader}) With {
            .OnStateChanged =
            Sub(Loader)
                Select Case Loader.State
                    Case LoadState.Finished, LoadState.Failed, LoadState.Canceled
                        Dim FinalResult = If(Result,
                            New WarThunderInstallSummary With {
                                .Success = False,
                                .Message = If(Loader.Error Is Nothing, "任务已取消。", "下载未完成，请稍后重试。")
                            })
                        If Loader.Error IsNot Nothing Then
                            Logger.Warn(Loader.Error, $"战争雷霆下载任务失败：{Loader.Name}")
                        End If
                        RunInUi(Sub() OnFinished(Loader.State, FinalResult, Loader.Error))
                End Select
            End Sub
        }
        Task.Start(0)
        LoaderTaskbarAdd(Task)
        FrmMain.BtnExtraDownload.ShowRefresh()
        Logger.Info($"战争雷霆任务已加入任务列表：{Task.Name}")
    End Sub

End Module
