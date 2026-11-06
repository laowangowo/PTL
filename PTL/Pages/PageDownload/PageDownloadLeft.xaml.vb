Public Class PageDownloadLeft
    Implements IRefreshable

    Public PageID As FormMain.PageSubType = FormMain.PageSubType.DownloadSights

    Private Sub PageCheck(sender As FrameworkElement, e As RouteEventArgs) _
        Handles ItemSights.Check, ItemCamouflage.Check, ItemSounds.Check, ItemTools.Check
        If sender.Tag IsNot Nothing Then PageChange(Val(sender.Tag))
    End Sub

    Public Function PageGet(Optional ID As FormMain.PageSubType = -1) As MyPageRight
        If ID = -1 Then ID = PageID
        If ID <> FormMain.PageSubType.DownloadSights AndAlso
           ID <> FormMain.PageSubType.DownloadCamouflage AndAlso
           ID <> FormMain.PageSubType.DownloadSounds AndAlso
           ID <> FormMain.PageSubType.DownloadTools Then
            ID = FormMain.PageSubType.DownloadSights
        End If
        If ID = FormMain.PageSubType.DownloadTools Then
            If FrmWarThunderTools Is Nothing Then FrmWarThunderTools = New PageWarThunderTools
            Return FrmWarThunderTools
        End If
        If FrmWarThunderResources Is Nothing Then FrmWarThunderResources = New PageWarThunderResources
        Select Case ID
            Case FormMain.PageSubType.DownloadCamouflage
                FrmWarThunderResources.SetMode(WarThunderResourceKind.Camouflage)
            Case FormMain.PageSubType.DownloadSounds
                FrmWarThunderResources.SetMode(WarThunderResourceKind.Sound)
            Case Else
                FrmWarThunderResources.SetMode(WarThunderResourceKind.Sights)
        End Select
        Return FrmWarThunderResources
    End Function

    Public Sub PageChange(ID As FormMain.PageSubType)
        If ID <> FormMain.PageSubType.DownloadSights AndAlso
           ID <> FormMain.PageSubType.DownloadCamouflage AndAlso
           ID <> FormMain.PageSubType.DownloadSounds AndAlso
           ID <> FormMain.PageSubType.DownloadTools Then Return
        If PageID = ID Then
            If ID = FormMain.PageSubType.DownloadTools Then
                FrmWarThunderTools?.RefreshPage()
            Else
                FrmWarThunderResources?.RefreshPage()
            End If
            Return
        End If
        AniControlEnabled += 1
        Try
            Dim Target As MyPageRight = PageGet(ID)
            If Target.Parent IsNot Nothing Then Target.SetValue(ContentPresenter.ContentProperty, Nothing)
            FrmMain.PageRight = Target
            CType(FrmMain.PanMainRight.Child, MyPageRight).PageOnExit()
            AniStart({
                AaCode(
                Sub()
                    CType(FrmMain.PanMainRight.Child, MyPageRight).PageOnForceExit()
                    FrmMain.PanMainRight.Child = FrmMain.PageRight
                    FrmMain.PageRight.Opacity = 0
                End Sub, 130),
                AaCode(
                Sub()
                    FrmMain.PageRight.Opacity = 1
                    FrmMain.PageRight.PageOnEnter()
                End Sub, 30, True)
            }, "PageLeft PageChange")
            PageID = ID
        Catch ex As Exception
            Logger.Error(ex, $"切换下载子页面失败（ID {ID}）")
        Finally
            AniControlEnabled -= 1
        End Try
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        If PageID = FormMain.PageSubType.DownloadTools Then
            FrmWarThunderTools?.RefreshPage()
        Else
            FrmWarThunderResources?.RefreshPage()
        End If
    End Sub

End Class
