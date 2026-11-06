Imports Newtonsoft.Json.Linq

Public Class MyWarThunderResourceItem

    Public Event InstallRequested As Action(Of JObject)
    Public Event OpenPostRequested As Action(Of String)

    Public Property ItemData As JObject
    Public Property PostUrl As String = ""

    Public Sub SetData(Item As JObject)
        If Item Is Nothing Then Throw New ArgumentNullException(NameOf(Item))
        ItemData = Item
        LabTitle.Text = ReadString(Item("title"))
        If String.IsNullOrWhiteSpace(LabTitle.Text) Then
            LabTitle.Text = ReadString(Item("name"))
        End If
        If String.IsNullOrWhiteSpace(LabTitle.Text) Then
            LabTitle.Text = "未命名资源"
        End If
        LabTitle.ToolTip = LabTitle.Text

        Dim Description As String = ReadString(Item("description"))
        LabDescription.Text = If(String.IsNullOrWhiteSpace(Description),
                                 "暂无简介", Description.Replace(vbCrLf, " ").Replace(vbLf, " "))
        LabDescription.ToolTip = Description

        Dim Author = TryCast(Item("author"), JObject)
        Dim AuthorName As String = ReadString(Author?("nickname"))
        If String.IsNullOrWhiteSpace(AuthorName) Then AuthorName = "未知作者"
        Dim Downloads As Long = ReadLong(Item("downloads"))
        Dim Likes As Long = ReadLong(Item("likes"))
        Dim FileSize As Long = ReadLong(Item("size"))
        Dim CreatedAt As String = ReadString(Item("created_at"))
        Dim CreatedText As String = FormatCreatedAt(CreatedAt)
        LabMeta.Text =
            $"{AuthorName}  ·  下载 {Downloads:N0}  ·  点赞 {Likes:N0}  ·  " &
            $"{FormatSize(FileSize)}{If(CreatedText = "", "", "  ·  " & CreatedText)}"

        PostUrl = ReadString(Item("post_url"))
        Dim PreviewUrl As String = ReadString(Item("preview_image"))
        If PreviewUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) Then
            ImgPreview.Source = PreviewUrl
        Else
            ImgPreview.Source = Nothing
        End If

        Dim DownloadUrl As String = ReadString(Item("download_url"))
        BtnInstall.IsEnabled = Not String.IsNullOrWhiteSpace(DownloadUrl)
        BtnPost.IsEnabled = Not String.IsNullOrWhiteSpace(PostUrl)
    End Sub

    Private Sub BtnInstall_Click(sender As Object, e As RoutedEventArgs) Handles BtnInstall.Click
        e.Handled = True
        If ItemData IsNot Nothing Then RaiseEvent InstallRequested(ItemData)
    End Sub

    Private Sub BtnPost_Click(sender As Object, e As RoutedEventArgs) Handles BtnPost.Click
        e.Handled = True
        If Not String.IsNullOrWhiteSpace(PostUrl) Then RaiseEvent OpenPostRequested(PostUrl)
    End Sub

    Private Sub Item_MouseLeftButtonUp(sender As Object, e As MouseButtonEventArgs) _
        Handles Me.PreviewMouseLeftButtonUp
        If IsInsideButton(TryCast(e.OriginalSource, DependencyObject)) Then Return
        If Not String.IsNullOrWhiteSpace(PostUrl) Then RaiseEvent OpenPostRequested(PostUrl)
    End Sub

    Private Sub Item_MouseEnter(sender As Object, e As MouseEventArgs) Handles Me.MouseEnter
        RowBackground.SetResourceReference(Border.BackgroundProperty, "ColorBrush7")
    End Sub

    Private Sub Item_MouseLeave(sender As Object, e As MouseEventArgs) Handles Me.MouseLeave
        RowBackground.SetResourceReference(Border.BackgroundProperty, "ColorBrushSemiTransparent")
    End Sub

    Private Shared Function IsInsideButton(Source As DependencyObject) As Boolean
        Dim Current As DependencyObject = Source
        Do While Current IsNot Nothing
            If TypeOf Current Is Button Then Return True
            Try
                If TypeOf Current Is Media.Visual Then
                    Current = Media.VisualTreeHelper.GetParent(Current)
                ElseIf TypeOf Current Is ContentElement Then
                    Current = ContentOperations.GetParent(CType(Current, ContentElement))
                ElseIf TypeOf Current Is FrameworkContentElement Then
                    Current = CType(Current, FrameworkContentElement).Parent
                Else
                    Current = Nothing
                End If
            Catch
                Current = Nothing
            End Try
        Loop
        Return False
    End Function

    Private Shared Function ReadString(Token As JToken) As String
        If Token Is Nothing OrElse Token.Type = JTokenType.Null Then Return ""
        Return Token.ToString().Trim()
    End Function

    Private Shared Function ReadLong(Token As JToken) As Long
        If Token Is Nothing OrElse Token.Type = JTokenType.Null Then Return 0
        Dim Value As Long
        If Long.TryParse(Token.ToString(), Value) Then Return Math.Max(0, Value)
        Return 0
    End Function

    Private Shared Function FormatSize(Bytes As Long) As String
        If Bytes >= 1024L * 1024 * 1024 Then Return $"{Bytes / 1024 / 1024 / 1024:0.0} GB"
        If Bytes >= 1024L * 1024 Then Return $"{Bytes / 1024 / 1024:0.0} MB"
        If Bytes >= 1024 Then Return $"{Bytes / 1024:0.0} KB"
        Return Bytes & " B"
    End Function

    Private Shared Function FormatCreatedAt(Value As String) As String
        Dim Created As Date
        If Not Date.TryParse(Value, Created) Then Return ""
        Return Created.ToLocalTime().ToString("yyyy-MM-dd")
    End Function

End Class
