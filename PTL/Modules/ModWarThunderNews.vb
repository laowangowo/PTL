Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.RegularExpressions

''' <summary>
''' 战争雷霆官网新闻条目。
''' </summary>
Public Class WarThunderNewsItem
    Public Property DateText As String = ""
    Public Property Title As String = ""
    Public Property Url As String = ""
End Class

''' <summary>
''' 获取与官方启动器相同来源的战争雷霆中文新闻。
''' </summary>
Public Module ModWarThunderNews

    Private Const ChineseNewsUrl As String = "https://warthunder.com/news3-zh.html"
    Private ReadOnly NewsCacheTimeout As TimeSpan = TimeSpan.FromMinutes(10)
    Private ReadOnly NewsLock As New Object()
    Private NewsCache As New List(Of WarThunderNewsItem)
    Private NewsCacheTime As Date = Date.MinValue

    Private ReadOnly BodyRegex As New Regex(
        "<body[^>]*>(?<body>[\s\S]*?)</body>",
        RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
    Private ReadOnly NewsItemRegex As New Regex(
        "<div\s+class\s*=\s*[""']news_item_date[""']\s*>(?<date>[\s\S]*?)</div>" &
        "[\s\S]*?<div\s+class\s*=\s*[""']news_item_text[""']\s*>\s*" &
        "<a\b[^>]*\bhref\s*=\s*[""'](?<url>[^""']+)[""'][^>]*>" &
        "(?<title>[\s\S]*?)</a>",
        RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)

    ''' <summary>
    ''' 在后台加载新闻，完成后在 UI 线程回调。
    ''' </summary>
    Public Sub WarThunderLoadNewsAsync(
        ForceRefresh As Boolean,
        OnComplete As Action(Of List(Of WarThunderNewsItem), String))
        If OnComplete Is Nothing Then Return

        Dim CachedItems As List(Of WarThunderNewsItem) = Nothing
        SyncLock NewsLock
            If Not ForceRefresh AndAlso NewsCache.Count > 0 AndAlso
               Date.UtcNow - NewsCacheTime <= NewsCacheTimeout Then
                CachedItems = WarThunderCopyNews(NewsCache)
            End If
        End SyncLock
        If CachedItems IsNot Nothing Then
            RunInUi(Sub() OnComplete(CachedItems, ""))
            Return
        End If

        RunInNewThread(
        Sub()
            Dim Result As New List(Of WarThunderNewsItem)
            Dim ErrorMessage As String = ""
            Try
                Using Client As New HttpClient()
                    Client.Timeout = TimeSpan.FromSeconds(15)
                    Client.DefaultRequestHeaders.UserAgent.ParseAdd("PTL/" & PtlVersion)
                    Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN")
                    Using Response As HttpResponseMessage =
                        Client.GetAsync(ChineseNewsUrl).GetAwaiter().GetResult()
                        Response.EnsureSuccessStatusCode()
                        Dim HtmlBytes As Byte() =
                            Response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                        Dim Html As String = Encoding.UTF8.GetString(HtmlBytes)
                        Result = WarThunderParseNews(Html)
                    End Using
                    If Result.Count = 0 Then
                        Throw New InvalidDataException("新闻页面中没有找到可显示的新闻。")
                    End If
                    SyncLock NewsLock
                        NewsCache = WarThunderCopyNews(Result)
                        NewsCacheTime = Date.UtcNow
                    End SyncLock
                    Logger.Info($"已获取战争雷霆新闻：{Result.Count} 条")
                End Using
            Catch ex As Exception
                ErrorMessage = ex.GetDisplay(False)
                Logger.Warn(ex, "获取战争雷霆新闻失败")
                SyncLock NewsLock
                    If NewsCache.Count > 0 Then
                        Result = WarThunderCopyNews(NewsCache)
                    End If
                End SyncLock
            End Try
            RunInUi(Sub() OnComplete(Result, ErrorMessage))
        End Sub, "War Thunder News", ThreadPriority.BelowNormal)
    End Sub

    Friend Function WarThunderParseNews(Html As String) As List(Of WarThunderNewsItem)
        Dim Result As New List(Of WarThunderNewsItem)
        If String.IsNullOrWhiteSpace(Html) Then Return Result
        Dim BodyMatch = BodyRegex.Match(Html)
        Dim Body As String = If(BodyMatch.Success, BodyMatch.Groups("body").Value, Html)

        For Each Match As Match In NewsItemRegex.Matches(Body)
            Dim Title As String = WebUtility.HtmlDecode(
                Regex.Replace(Match.Groups("title").Value, "<[^>]+>", " "))
            Title = Regex.Replace(Title, "\s+", " ").Trim()
            Dim Url As String = WebUtility.HtmlDecode(Match.Groups("url").Value.Trim())
            If Title = "" OrElse Not WarThunderIsNewsUrl(Url) Then Continue For
            Result.Add(New WarThunderNewsItem With {
                .DateText = Regex.Replace(WebUtility.HtmlDecode(
                    Match.Groups("date").Value), "\s+", " ").Trim(),
                .Title = Title,
                .Url = Url
            })
            If Result.Count >= 10 Then Exit For
        Next
        Return Result
    End Function

    Private Function WarThunderIsNewsUrl(Url As String) As Boolean
        Dim UriValue As Uri = Nothing
        If Not Uri.TryCreate(Url, UriKind.Absolute, UriValue) Then Return False
        If UriValue.Scheme <> Uri.UriSchemeHttps Then Return False
        Return UriValue.Host.Equals("warthunder.com", StringComparison.OrdinalIgnoreCase) OrElse
               UriValue.Host.EndsWith(".warthunder.com", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Function WarThunderCopyNews(Items As IEnumerable(Of WarThunderNewsItem)) As List(Of WarThunderNewsItem)
        Return Items.Select(Function(Item) New WarThunderNewsItem With {
            .DateText = Item.DateText,
            .Title = Item.Title,
            .Url = Item.Url
        }).ToList()
    End Function

End Module
