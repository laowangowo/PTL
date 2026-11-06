Imports System.Collections.Generic

Public Class PageLaunchRight
    Implements IRefreshable

    Private IsWarThunderNewsLoading As Boolean = False

    Private Sub Init() Handles Me.Loaded
        PanBack.ScrollToHome()
        PanMain.Visibility = Visibility.Visible
        CardWtNews.MainChrome.Visibility = Visibility.Collapsed
        LoadWarThunderNews(False)
    End Sub

    Private Sub BtnWtNewsRefresh_Click(sender As Object, e As EventArgs) Handles BtnWtNewsRefresh.Click
        LoadWarThunderNews(True)
    End Sub

    Private Sub LoadWarThunderNews(ForceRefresh As Boolean)
        If IsWarThunderNewsLoading Then Return
        IsWarThunderNewsLoading = True
        BtnWtNewsRefresh.IsEnabled = False
        If PanWtNews.Children.Count = 0 Then
            LabWtNewsStatus.Text = "正在获取官方新闻…"
            LabWtNewsStatus.Visibility = Visibility.Visible
        End If

        WarThunderLoadNewsAsync(ForceRefresh,
        Sub(Items, ErrorMessage)
            IsWarThunderNewsLoading = False
            BtnWtNewsRefresh.IsEnabled = True
            If Items IsNot Nothing AndAlso Items.Count > 0 Then
                BuildWarThunderNews(Items)
                LabWtNewsStatus.Text = If(String.IsNullOrWhiteSpace(ErrorMessage),
                    $"更新于 {Date.Now:HH:mm}",
                    "暂时无法刷新，当前显示缓存新闻。")
                LabWtNewsStatus.Visibility = Visibility.Visible
            Else
                PanWtNews.Children.Clear()
                LabWtNewsStatus.Text = "暂时无法获取新闻，请稍后重试。"
                LabWtNewsStatus.Visibility = Visibility.Visible
            End If
        End Sub)
    End Sub

    Private Sub BuildWarThunderNews(Items As List(Of WarThunderNewsItem))
        PanWtNews.Children.Clear()
        For Each Item As WarThunderNewsItem In Items
            Dim ClickItem As WarThunderNewsItem = Item
            Dim Row As New Border With {
                .Padding = New Thickness(10, 8, 10, 8),
                .Margin = New Thickness(0, 0, 0, 6),
                .CornerRadius = New CornerRadius(4),
                .BorderThickness = New Thickness(0, 0, 0, 1),
                .BorderBrush = TryCast(FindResource("ColorBrushGray1"), Media.Brush),
                .Background = TryCast(FindResource("ColorBrushSemiTransparent"), Media.Brush),
                .Cursor = Cursors.Hand,
                .Effect = Nothing
            }
            Dim Layout As New Grid()
            Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(92)})
            Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Layout.Children.Add(New TextBlock With {
                .Text = ClickItem.DateText,
                .FontSize = 11,
                .Margin = New Thickness(0, 2, 10, 0),
                .VerticalAlignment = VerticalAlignment.Top,
                .TextWrapping = TextWrapping.Wrap,
                .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush)
            })
            Dim TitleBlock As New TextBlock With {
                .Text = ClickItem.Title,
                .FontSize = 12.5,
                .TextWrapping = TextWrapping.Wrap,
                .Foreground = TryCast(FindResource("ColorBrush1"), Media.Brush)
            }
            Grid.SetColumn(TitleBlock, 1)
            Layout.Children.Add(TitleBlock)
            Row.Child = Layout
            AddHandler Row.MouseLeftButtonUp,
                Sub(CurrentSender As Object, MouseArgs As MouseButtonEventArgs)
                    OpenWebsite(ClickItem.Url)
                End Sub
            PanWtNews.Children.Add(Row)
        Next
    End Sub

    Public Sub ForceRefresh() Implements IRefreshable.Refresh
        LoadWarThunderNews(True)
    End Sub

End Class
