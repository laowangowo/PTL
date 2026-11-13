Public Class PageWarThunderComponentsLeft
    Implements IRefreshable

    Public Property PageKind As WarThunderComponentKind = WarThunderComponentKind.Sights

    Private Sub PageLoaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        Select Case PageKind
            Case WarThunderComponentKind.Camouflage
                ItemCamouflage.SetChecked(True, False, False)
            Case WarThunderComponentKind.Sound
                ItemSound.SetChecked(True, False, False)
            Case WarThunderComponentKind.Text
                ItemText.SetChecked(True, False, False)
            Case Else
                ItemSights.SetChecked(True, False, False)
        End Select
        FrmWarThunderComponents?.SetMode(PageKind)
    End Sub

    Private Sub PageCheck(sender As FrameworkElement, e As RouteEventArgs) _
        Handles ItemSights.Check, ItemCamouflage.Check, ItemSound.Check,
                ItemText.Check
        PageKind = If(sender Is ItemText,
                      WarThunderComponentKind.Text,
                      If(sender Is ItemSound,
                         WarThunderComponentKind.Sound,
                         If(sender Is ItemCamouflage,
                            WarThunderComponentKind.Camouflage,
                            WarThunderComponentKind.Sights)))
        FrmWarThunderComponents?.SetMode(PageKind)
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        FrmWarThunderComponents?.RefreshPage()
    End Sub

End Class
