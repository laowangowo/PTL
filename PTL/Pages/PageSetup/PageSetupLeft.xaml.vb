Public Class PageSetupLeft

    Private IsPageSwitched As Boolean

    Public PageID As FormMain.PageSubType = FormMain.PageSubType.SetupUI

    Private Sub PageSetupLeft_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PageID = FormMain.PageSubType.SetupUI
        ItemUI.SetChecked(True, False, False)
    End Sub

    Private Sub PageSetupLeft_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        IsPageSwitched = False
    End Sub

    Private Sub PageCheck(sender As MyListItem, e As EventArgs) Handles ItemUI.Check, ItemSystem.Check, ItemAbout.Check
        If sender.Tag IsNot Nothing Then PageChange(CType(Val(sender.Tag), FormMain.PageSubType))
    End Sub

    Public Shared Function GetItemIndex(ID As FormMain.PageSubType) As Integer
        Select Case ID
            Case FormMain.PageSubType.SetupUI
                Return 0
            Case FormMain.PageSubType.SetupSystem
                Return 1
            Case FormMain.PageSubType.SetupAbout
                Return 2
            Case Else
                Return -1
        End Select
    End Function

    Public Function PageGet(Optional ID As FormMain.PageSubType = -1) As MyPageRight
        If ID = -1 Then ID = PageID
        Select Case ID
            Case FormMain.PageSubType.SetupUI
                If FrmSetupUI Is Nothing Then FrmSetupUI = New PageSetupUI
                Return FrmSetupUI
            Case FormMain.PageSubType.SetupSystem
                If FrmSetupSystem Is Nothing Then FrmSetupSystem = New PageSetupSystem
                Return FrmSetupSystem
            Case FormMain.PageSubType.SetupAbout
                If FrmSetupAbout Is Nothing Then FrmSetupAbout = New PageSetupAbout
                Return FrmSetupAbout
            Case Else
                If FrmSetupUI Is Nothing Then FrmSetupUI = New PageSetupUI
                Return FrmSetupUI
        End Select
    End Function

    Public Sub PageChange(ID As FormMain.PageSubType)
        If ID <> FormMain.PageSubType.SetupUI AndAlso
           ID <> FormMain.PageSubType.SetupSystem AndAlso
           ID <> FormMain.PageSubType.SetupAbout Then Return
        If PageID = ID Then
            Return
        End If
        AniControlEnabled += 1
        IsPageSwitched = True
        Try
            PageChangeRun(PageGet(ID))
            PageID = ID
        Catch ex As Exception
            Logger.Error(ex, $"切换设置子页面失败（ID {ID}）")
        Finally
            AniControlEnabled -= 1
        End Try
    End Sub

    Private Shared Sub PageChangeRun(Target As MyPageRight)
        AniStop("FrmMain PageChangeRight")
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
    End Sub

    Public Sub Reset(sender As Object, e As EventArgs)
        Select Case Val(sender.Tag)
            Case FormMain.PageSubType.SetupUI
                If MyMsgBox("是否要初始化 PTL 个性化设置？该操作不可撤销。" & vbCrLf &
                            "背景图片与音乐文件不会被删除。", "初始化确认",, "取消", IsWarn:=True) = 1 Then
                    If FrmSetupUI Is Nothing Then FrmSetupUI = New PageSetupUI
                    FrmSetupUI.Reset()
                    ItemUI.Checked = True
                End If
            Case FormMain.PageSubType.SetupSystem
                If MyMsgBox("是否要初始化启动器设置？该操作不可撤销。", "初始化确认",, "取消", IsWarn:=True) = 1 Then
                    If FrmSetupSystem Is Nothing Then FrmSetupSystem = New PageSetupSystem
                    FrmSetupSystem.Reset()
                    ItemSystem.Checked = True
                End If
        End Select
    End Sub

End Class
