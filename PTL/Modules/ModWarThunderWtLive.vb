Imports System.Net
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

''' <summary>
''' 从 WT Live 公开 Feed API 直接获取瞄具和声音模组。
''' </summary>
Public Module ModWarThunderWtLive

    Private Const WtLiveBase As String = "https://live.warthunder.com"
    Private Const FeedEndpoint As String = WtLiveBase & "/api/feed/get_regular/"
    Private Const PageSize As Integer = 25
    Private ReadOnly CacheLifetime As TimeSpan = TimeSpan.FromMinutes(2)
    Private ReadOnly WtLiveCacheLock As New Object()
    Private ReadOnly WtLiveCache As New Dictionary(Of String, WtLiveCacheEntry)

    Private Class WtLiveCacheEntry
        Public Property SavedAt As Date
        Public Property Value As JObject
    End Class

    Public Function WarThunderFetchWtLiveResourceCatalog(
        Kind As String,
        PageIndex As Integer,
        SortValue As String,
        PeriodValue As String,
        SearchTags As String) As JObject
        Dim Content As String = WtLiveNormalizeContent(Kind)
        Dim ExpectedType As String = Content
        Dim PageNumber As Integer = Math.Max(0, Math.Min(10000, PageIndex))
        Dim Sort As String = WtLiveNormalizeSort(SortValue)
        Dim Period As String = WtLiveNormalizePeriod(PeriodValue)
        Dim Tags As String = WtLiveNormalizeSearchTags(SearchTags)
        Dim CacheKey As String =
            $"{Content}|{PageNumber}|{Sort}|{Period}|{Tags}"
        Dim Cached As JObject = WtLiveGetCached(CacheKey)
        If Cached IsNot Nothing Then
            Cached("cached") = True
            Cached("stale") = False
            Return Cached
        End If

        Dim LastError As Exception = Nothing
        For Attempt As Integer = 0 To 1
            Try
                Dim Result As JObject = WtLiveRequestFeed(
                    Content, ExpectedType, PageNumber, Sort, Period, Tags)
                WtLiveStoreCache(CacheKey, Result)
                Result("cached") = False
                Result("stale") = False
                Return Result
            Catch ex As Exception
                LastError = ex
                If Attempt = 0 Then Thread.Sleep(350)
            End Try
        Next

        Dim Stale As JObject = WtLiveGetCached(CacheKey, IgnoreExpiry:=True)
        If Stale IsNot Nothing Then
            Stale("cached") = True
            Stale("stale") = True
            Stale("warning") =
                "WT Live 暂时不可用，正在显示上次缓存：" & LastError?.Message
            Return Stale
        End If
        Throw New WebException(
            "无法连接 WT Live，请检查网络设置后重试。",
            LastError)
    End Function

    Private Function WtLiveRequestFeed(
        Content As String,
        ExpectedType As String,
        PageIndex As Integer,
        SortValue As String,
        PeriodValue As String,
        SearchTags As String) As JObject
        Dim PageSlug As String =
            If(Content = "sight", "sights",
               If(Content = "camouflage", "camouflages", "sounds"))
        Dim Fields As New List(Of KeyValuePair(Of String, String)) From {
            New KeyValuePair(Of String, String)("content", Content),
            New KeyValuePair(Of String, String)("sort", SortValue),
            New KeyValuePair(Of String, String)("page", PageIndex.ToString()),
            New KeyValuePair(Of String, String)("period", PeriodValue),
            New KeyValuePair(Of String, String)("subtype", "all"),
            New KeyValuePair(Of String, String)("searchString", SearchTags),
            New KeyValuePair(Of String, String)("user", "0")
        }

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12
        Using Handler As New HttpClientHandler()
            Handler.UseProxy = True
            Handler.AutomaticDecompression =
                DecompressionMethods.GZip Or DecompressionMethods.Deflate
            Using Client As New HttpClient(Handler)
                Client.Timeout = TimeSpan.FromSeconds(20)
                Client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " &
                    "AppleWebKit/537.36 Chrome/126.0 Safari/537.36 PTL/" & PtlVersion)
                Client.DefaultRequestHeaders.Referrer =
                    New Uri($"{WtLiveBase}/feed/{PageSlug}/")
                Client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "X-Requested-With", "XMLHttpRequest")
                Client.DefaultRequestHeaders.Accept.ParseAdd(
                    "application/json, text/plain, */*")

                Using Form As New FormUrlEncodedContent(Fields)
                    Using Response As HttpResponseMessage = Client.PostAsync(
                        FeedEndpoint, Form).GetAwaiter().GetResult()
                        Dim ResponseText As String =
                            Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        If Not Response.IsSuccessStatusCode Then
                            Throw New WebException(
                                $"WT Live 返回 HTTP {CInt(Response.StatusCode)}。")
                        End If

                        Dim Payload As JObject
                        Try
                            Payload = JObject.Parse(ResponseText)
                        Catch ex As Exception
                            Throw New InvalidDataException(
                                "WT Live 返回的内容不是有效 JSON。", ex)
                        End Try
                        If Not String.Equals(
                            Payload.Value(Of String)("status"),
                            "OK",
                            StringComparison.Ordinal) Then
                            Throw New InvalidDataException(
                                If(String.IsNullOrWhiteSpace(
                                       Payload.Value(Of String)("msg")),
                                   "WT Live 请求失败。",
                                   Payload.Value(Of String)("msg")))
                        End If

                        Dim Data = TryCast(Payload("data"), JObject)
                        Dim RawItems = TryCast(Data?("list"), JArray)
                        If RawItems Is Nothing Then RawItems = New JArray()
                        Dim Seen As New HashSet(Of Long)
                        Dim Items As New JArray()
                        For Each Token In RawItems
                            Try
                                Dim RawItem = TryCast(Token, JObject)
                                Dim Item = WtLiveNormalizeItem(RawItem, ExpectedType)
                                If Item Is Nothing Then Continue For
                                Dim LangGroup As Long =
                                    Item.Value(Of Long?)("lang_group").GetValueOrDefault(0)
                                If LangGroup > 0 AndAlso Not Seen.Add(LangGroup) Then Continue For
                                Items.Add(Item)
                            Catch ex As Exception
                                Logger.Warn(
                                    ex,
                                    "跳过无法解析的 WT Live 列表项：" &
                                    Token?("lang_group")?.ToString())
                            End Try
                        Next

                        Return New JObject From {
                            {"status", "OK"},
                            {"source", "wtlive"},
                            {"kind", PageSlug},
                            {"content", Content},
                            {"page", PageIndex},
                            {"page_size", PageSize},
                            {"sort", SortValue},
                            {"period", PeriodValue},
                            {"search_string", SearchTags},
                            {"page_title", Data?.Value(Of String)("pageTitle")},
                            {"has_more", RawItems.Count >= PageSize},
                            {"fetched_at", Date.UtcNow.ToString("o")},
                            {"items", Items}
                        }
                    End Using
                End Using
            End Using
        End Using
    End Function

    Private Function WtLiveNormalizeItem(Item As JObject, ExpectedType As String) As JObject
        If Item Is Nothing Then Return Nothing
        If Not String.Equals(
            Item.Value(Of String)("type"),
            ExpectedType,
            StringComparison.OrdinalIgnoreCase) Then Return Nothing
        If Item.Value(Of Boolean?)("visible").GetValueOrDefault(True) = False Then Return Nothing

        Dim Description As String = WtLivePlainText(
            Item.Value(Of String)("description")).Trim()
        If Description.Length > 1200 Then Description = Description.Substring(0, 1200)
        Dim FileData = TryCast(Item("file"), JObject)
        Dim FileName As String = WtLiveSafeFileName(
            FileData?.Value(Of String)("name"))
        Dim DownloadUrl As String = WtLiveAbsoluteUrl(
            FileData?.Value(Of String)("link"))
        Dim Images As New JArray()
        Dim PreviewImage As String = ""
        Dim RawImages = TryCast(Item("images"), JArray)
        If RawImages IsNot Nothing Then
            For Each ImageToken In RawImages
                Dim ImageData = TryCast(ImageToken, JObject)
                If ImageData Is Nothing Then Continue For
                Dim ImageUrl As String =
                    WtLiveAbsoluteUrl(ImageData.Value(Of String)("src"))
                If ImageUrl = "" Then
                    Dim Medium = TryCast(ImageData("mq"), JObject)
                    ImageUrl = WtLiveAbsoluteUrl(Medium?.Value(Of String)("src"))
                End If
                If ImageUrl = "" Then Continue For
                If PreviewImage = "" Then PreviewImage = ImageUrl
                Images.Add(New JObject From {
                    {"src", ImageUrl},
                    {"width", ImageData.Value(Of Integer?)("width").GetValueOrDefault(0)},
                    {"height", ImageData.Value(Of Integer?)("height").GetValueOrDefault(0)},
                    {"type", ImageData.Value(Of String)("type")}
                })
            Next
        End If

        Dim Title As String = WtLiveTitle(Item, Description, FileName, ExpectedType)
        Dim Author = TryCast(Item("author"), JObject)
        Dim LangGroup As Long = Item.Value(Of Long?)("lang_group").GetValueOrDefault(0)
        Dim PostId As Long = Item.Value(Of Long?)("id").GetValueOrDefault(0)
        Dim Language As String = If(
            String.IsNullOrWhiteSpace(Item.Value(Of String)("language")),
            "en",
            Item.Value(Of String)("language").Trim())
        Dim CreatedAt As Long =
            Item.Value(Of Long?)("created").GetValueOrDefault(0)
        Dim FileSize As Long =
            FileData?.Value(Of Long?)("size").GetValueOrDefault(0)

        Return New JObject From {
            {"id", If(LangGroup > 0, LangGroup.ToString(), PostId.ToString())},
            {"lang_group", LangGroup},
            {"post_id", PostId},
            {"language", Language},
            {"type", ExpectedType},
            {"kind",
                If(ExpectedType = "sight", "sights",
                   If(ExpectedType = "camouflage", "camouflages", "sounds"))},
            {"source", "wtlive"},
            {"title", Title},
            {"name", Title},
            {"description", Description},
            {"created", CreatedAt},
            {"created_at",
                If(CreatedAt > 0,
                   DateTimeOffset.FromUnixTimeSeconds(CreatedAt).ToString("o"),
                   "")},
            {"author", New JObject From {
                {"id", Author?.Value(Of String)("id")},
                {"nickname", Author?.Value(Of String)("nickname")},
                {"avatar", WtLiveAbsoluteUrl(Author?.Value(Of String)("avatar"))}
            }},
            {"preview_image", PreviewImage},
            {"images", Images},
            {"file", If(FileData Is Nothing,
                JValue.CreateNull(),
                New JObject From {
                    {"id", FileData.Value(Of String)("id")},
                    {"name", FileName},
                    {"mime", FileData.Value(Of String)("type")},
                    {"size", FileSize},
                    {"link", DownloadUrl}
                })},
            {"filename", FileName},
            {"size", FileSize},
            {"download_url", DownloadUrl},
            {"likes", Math.Max(0, Item.Value(Of Integer?)("likes").GetValueOrDefault(0))},
            {"views", Math.Max(0, Item.Value(Of Integer?)("views").GetValueOrDefault(0))},
            {"downloads", Math.Max(0, Item.Value(Of Long?)("downloads").GetValueOrDefault(0))},
            {"comments", Math.Max(0, Item.Value(Of Integer?)("comments").GetValueOrDefault(0))},
            {"post_url",
                If(LangGroup > 0,
                   $"{WtLiveBase}/post/{LangGroup}/{Uri.EscapeDataString(Language)}/",
                   "")},
            {"featured", Item.Value(Of Boolean?)("featured").GetValueOrDefault(False)},
            {"is_pinned", Item.Value(Of Boolean?)("isPinned").GetValueOrDefault(False)}
        }
    End Function

    Private Function WtLiveTitle(
        Item As JObject,
        Description As String,
        FileName As String,
        ExpectedType As String) As String
        If ExpectedType = "camouflage" Then
            Dim SkinName As String =
                Path.GetFileNameWithoutExtension(FileName).Trim()
            If SkinName <> "" Then Return SkinName
        End If
        Dim ExplicitTitle As String = WtLivePlainText(
            Item.Value(Of String)("title"))
        If ExplicitTitle = "" Then ExplicitTitle = WtLivePlainText(Item.Value(Of String)("name"))
        If ExplicitTitle <> "" Then Return ExplicitTitle.Substring(
            0, Math.Min(120, ExplicitTitle.Length))
        Dim FirstLine As String = Description.
            Split({vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries).
            Select(Function(Line) Line.Trim()).
            FirstOrDefault(Function(Line) Line <> "")
        If Not String.IsNullOrWhiteSpace(FirstLine) Then
            Return FirstLine.Substring(0, Math.Min(120, FirstLine.Length))
        End If
        Dim Stem As String = Path.GetFileNameWithoutExtension(FileName).Trim()
        If Stem <> "" Then Return Stem
        Return "WT Live #" & Item.Value(Of String)("lang_group")
    End Function

    Private Function WtLivePlainText(Value As String) As String
        If String.IsNullOrWhiteSpace(Value) Then Return ""
        Dim Text As String = Regex.Replace(
            Value, "<script\b[^>]*>[\s\S]*?</script>",
            " ", RegexOptions.IgnoreCase)
        Text = Regex.Replace(
            Text, "<style\b[^>]*>[\s\S]*?</style>",
            " ", RegexOptions.IgnoreCase)
        Text = Regex.Replace(Text, "<br\s*/?>", vbCrLf, RegexOptions.IgnoreCase)
        Text = Regex.Replace(Text, "</p>", vbCrLf, RegexOptions.IgnoreCase)
        Text = Regex.Replace(Text, "<[^>]+>", " ")
        Text = WebUtility.HtmlDecode(Text)
        Text = Regex.Replace(Text, "[ \t]+", " ")
        Text = Regex.Replace(Text, "(\r?\n){3,}", vbCrLf & vbCrLf)
        Return Text.Trim()
    End Function

    Private Function WtLiveAbsoluteUrl(Value As String) As String
        If String.IsNullOrWhiteSpace(Value) Then Return ""
        Dim Link As New Uri(Value, UriKind.RelativeOrAbsolute)
        If Not Link.IsAbsoluteUri Then Link = New Uri(New Uri(WtLiveBase), Link)
        If Not Link.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) Then
            Return ""
        End If
        If Link.Host <> "live.warthunder.com" AndAlso
           Link.Host <> "cdn-live.warthunder.com" AndAlso
           Not Link.Host.EndsWith(".live.warthunder.com", StringComparison.OrdinalIgnoreCase) Then
            Return ""
        End If
        Return Link.AbsoluteUri
    End Function

    Private Function WtLiveSafeFileName(Value As String) As String
        Dim Name As String = Path.GetFileName(If(Value, "").Trim())
        Name = Regex.Replace(Name, "[\x00-\x1f<>:""/\\|?*]+", "_")
        Name = Name.Trim(" "c, "."c)
        Return If(Name = "", "wtlive-resource.zip", Name)
    End Function

    Private Function WtLiveNormalizeContent(Kind As String) As String
        Select Case If(Kind, "").Trim().ToLowerInvariant()
            Case "sight", "sights"
                Return "sight"
            Case "sound", "sounds"
                Return "sound"
            Case "camouflage", "camouflages"
                Return "camouflage"
            Case Else
                Throw New ArgumentException(
                    "资源分类必须是 sight、camouflage 或 sound。")
        End Select
    End Function

    Private Function WtLiveNormalizeSort(Value As String) As String
        Dim SortValue As String = If(Value, "").Trim().ToLowerInvariant()
        Return If({"created", "rating", "comments", "downloads"}.Contains(SortValue),
                  SortValue, "created")
    End Function

    Private Function WtLiveNormalizePeriod(Value As String) As String
        Dim PeriodValue As String = If(Value, "").Trim()
        Return If({"0", "1", "7", "30", "365"}.Contains(PeriodValue),
                  PeriodValue, "0")
    End Function

    Private Function WtLiveNormalizeSearchTags(Value As String) As String
        Dim Tags As New List(Of String)
        For Each RawTag In If(Value, "").Split(
            New Char() {" "c, ControlChars.Tab, ControlChars.Cr, ControlChars.Lf},
            StringSplitOptions.RemoveEmptyEntries).Take(12)
            Dim Tag = Regex.Replace(RawTag, "[^\p{L}\p{N}_#-]+", "")
            If Tag = "" Then Continue For
            Tags.Add(If(Tag.StartsWith("#"), Tag, "#" & Tag))
        Next
        Return String.Join(" ", Tags.Distinct(StringComparer.OrdinalIgnoreCase))
    End Function

    Private Function WtLiveGetCached(
        Key As String, Optional IgnoreExpiry As Boolean = False) As JObject
        SyncLock WtLiveCacheLock
            Dim Entry As WtLiveCacheEntry = Nothing
            If Not WtLiveCache.TryGetValue(Key, Entry) Then Return Nothing
            If Not IgnoreExpiry AndAlso Date.UtcNow - Entry.SavedAt > CacheLifetime Then
                Return Nothing
            End If
            Return CType(Entry.Value.DeepClone(), JObject)
        End SyncLock
    End Function

    Private Sub WtLiveStoreCache(Key As String, Value As JObject)
        SyncLock WtLiveCacheLock
            If WtLiveCache.Count >= 200 Then
                Dim OldestKey = WtLiveCache.
                    OrderBy(Function(Pair) Pair.Value.SavedAt).
                    Select(Function(Pair) Pair.Key).
                    FirstOrDefault()
                If OldestKey IsNot Nothing Then WtLiveCache.Remove(OldestKey)
            End If
            WtLiveCache(Key) = New WtLiveCacheEntry With {
                .SavedAt = Date.UtcNow,
                .Value = CType(Value.DeepClone(), JObject)
            }
        End SyncLock
    End Sub

End Module
