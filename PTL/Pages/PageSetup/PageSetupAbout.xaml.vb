Public Class PageSetupAbout
    Implements IRefreshable

    Private Sub PageRefresh() Handles Me.Loaded, Me.PageEnter
        If Not IsLoaded Then Return
        PanBack.ScrollToHome()
        LabAboutVersion.Text =
            $"Pain Thunder Launcher {VersionDisplay}（Build {VersionCode}）"
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        If IsLoaded Then PanBack.ScrollToHome()
    End Sub

    Private Sub ImgLaowangAvatar_ImageFailed(
        sender As Object, e As ExceptionRoutedEventArgs)
        ImgLaowangAvatar.Visibility = Visibility.Collapsed
        Logger.Warn(e.ErrorException, "加载 laowangowo AVIF 头像失败，已回退到文字头像")
    End Sub

End Class
