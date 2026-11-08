Imports Newtonsoft.Json.Linq

''' <summary>
''' PTL 配套服务端的轻量客户端。
''' </summary>
Public Module ModWarThunderService

    Public Const ServiceBaseUrl As String = "https://xn--1kqr09bgu2bzba.top"
    'api.战争雷霆.top 的 Punycode 形式，避免旧版 .NET Framework 的 IDN 兼容差异。
    Public Const WarThunderStatsApiBaseUrl As String = "https://api.xn--1kqr09bgu2bzba.top"

    Public Function WarThunderServiceGet(PathValue As String) As JToken
        Return WarThunderServiceGetUrl(WarThunderResolveServiceUrl(PathValue))
    End Function

    Private Function WarThunderServiceGetUrl(Url As String) As JToken
        Try
            Return WarThunderServiceGetUrlCore(Url, False, 10)
        Catch ex As Exception
            Logger.Info($"服务端直连失败，尝试系统代理：{ex.Message}")
        End Try
        Try
            Return WarThunderServiceGetUrlCore(Url, True, 15)
        Catch ex As Exception
            Throw New WebException("无法连接 PTL 服务端，请检查网络设置后重试。", ex)
        End Try
    End Function

    Private Function WarThunderServiceGetUrlCore(
        Url As String, UseProxy As Boolean, TimeoutSeconds As Integer) As JToken
        Using Handler As New HttpClientHandler()
            Handler.UseProxy = UseProxy
            Handler.AutomaticDecompression =
                Net.DecompressionMethods.GZip Or Net.DecompressionMethods.Deflate
            Using Client As New HttpClient(Handler)
                Client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
                Client.DefaultRequestHeaders.UserAgent.ParseAdd("PTL/" & PtlVersion)
                Using Response As HttpResponseMessage = Client.GetAsync(
                    Url).GetAwaiter().GetResult()
                    Dim Content As String = Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    If Not Response.IsSuccessStatusCode Then
                        Throw New InvalidOperationException(
                            $"服务端返回 HTTP {CInt(Response.StatusCode)}：{Content}")
                    End If
                    Return JToken.Parse(Content)
                End Using
            End Using
        End Using
    End Function

    Public Function WarThunderResolveServiceUrl(PathOrUrl As String) As String
        Dim Value As String = If(PathOrUrl, "").Trim()
        If Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) OrElse
           Value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) Then
            Return Value
        End If
        If Value.StartsWith("//") Then Return "https:" & Value
        Return ServiceBaseUrl & If(Value.StartsWith("/"), "", "/") & Value
    End Function

    Public Function WarThunderResolveStatsApiUrl(PathOrUrl As String) As String
        Dim Value As String = If(PathOrUrl, "").Trim()
        If Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) OrElse
           Value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) Then
            Return Value
        End If
        If Value.StartsWith("//") Then Return "https:" & Value
        Return WarThunderStatsApiBaseUrl & If(Value.StartsWith("/"), "", "/") & Value
    End Function

    Public Function WarThunderFetchPlayerStats(UserId As String,
                                               Optional Refresh As Boolean = True) As JObject
        Dim Suffix As String = If(Refresh, "?refresh=true", "")
        Return CType(WarThunderServiceGetUrl(
            WarThunderResolveStatsApiUrl(
                $"/api/players/{Uri.EscapeDataString(UserId)}/stats{Suffix}")), JObject)
    End Function

    Public Function WarThunderFetchPlayerStatsByNickname(Nickname As String) As JObject
        Return CType(WarThunderServiceGetUrl(
            WarThunderResolveStatsApiUrl(
                $"/api/players/by-nickname/{Uri.EscapeDataString(Nickname.Trim())}/stats")), JObject)
    End Function

    Public Function WarThunderFetchPlayerSnapshots(UserId As String,
                                                   Optional Limit As Integer = 20) As JArray
        Return CType(WarThunderServiceGetUrl(
            WarThunderResolveStatsApiUrl(
                $"/api/players/{Uri.EscapeDataString(UserId)}/snapshots?limit={Limit}")), JArray)
    End Function

    Public Function WarThunderFetchResourceCatalog(Kind As String) As JArray
        Dim Result = WarThunderServiceGet($"/api/resources?kind={Uri.EscapeDataString(Kind)}")
        If TypeOf Result Is JArray Then Return CType(Result, JArray)
        Return CType(CType(Result, JObject)("items"), JArray)
    End Function

    ''' <summary>
    ''' 工具目录中的 download_url 可能是详情页；实际文件地址要从 update_url 的响应中读取。
    ''' </summary>
    Public Function WarThunderResolveToolDownload(Item As JObject,
                                                  ByRef ErrorMessage As String) As JObject
        ErrorMessage = ""
        Try
            Dim UpdateValue As String = Item.Value(Of String)("update_url")
            If String.IsNullOrWhiteSpace(UpdateValue) Then
                Throw New InvalidDataException("工具条目缺少 update_url。")
            End If
            Dim UpdateUrl As String = WarThunderResolveServiceUrl(UpdateValue)
            Dim UpdateResult = CType(WarThunderServiceGetUrl(UpdateUrl), JObject)
            Dim DownloadValue As String = UpdateResult.Value(Of String)("download_url")
            If String.IsNullOrWhiteSpace(DownloadValue) Then
                Throw New InvalidDataException("update_url 响应中缺少 download_url。")
            End If

            Dim DownloadUrl As New Uri(WarThunderResolveServiceUrl(DownloadValue))
            If Not DownloadUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) Then
                Throw New InvalidDataException("工具安装包必须使用 HTTPS 下载地址。")
            End If

            Dim Resolved As JObject = CType(Item.DeepClone(), JObject)
            Resolved("download_url") = DownloadUrl.AbsoluteUri
            For Each PropertyName In {"filename", "size", "sha256"}
                Dim Value = UpdateResult(PropertyName)
                If Value IsNot Nothing AndAlso Value.Type <> JTokenType.Null Then
                    Resolved(PropertyName) = Value.DeepClone()
                End If
            Next
            If String.IsNullOrWhiteSpace(Resolved.Value(Of String)("filename")) Then
                Dim DownloadName As String = Path.GetFileName(DownloadUrl.AbsolutePath)
                If DownloadName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then
                    Resolved("filename") = DownloadName
                Else
                    Resolved("filename") = "ToolSetup.exe"
                End If
            End If
            Logger.Info($"工具下载地址已从 {UpdateUrl} 解析为 {DownloadUrl.AbsoluteUri}")
            Return Resolved
        Catch ex As Exception
            ErrorMessage = ex.GetDisplay(False)
            Logger.Warn(ex, "解析工具 update_url 失败")
            Return Nothing
        End Try
    End Function

    Public Function WarThunderCreateServiceDownloadNetFile(Item As JObject,
                                                           DestinationFolder As String,
                                                           ByRef ErrorMessage As String) As NetFile
        ErrorMessage = ""
        Try
            Dim DownloadValue As String = Item.Value(Of String)("download_url")
            If String.IsNullOrWhiteSpace(DownloadValue) Then
                Throw New InvalidDataException("资源缺少下载地址。")
            End If
            Dim Url As String = WarThunderResolveServiceUrl(DownloadValue)
            Dim FileName As String = Path.GetFileName(Item.Value(Of String)("filename"))
            If String.IsNullOrWhiteSpace(FileName) Then
                FileName = Path.GetFileName(New Uri(Url).AbsolutePath)
            End If
            If String.IsNullOrWhiteSpace(FileName) Then FileName = "PTL_Download.bin"
            Dim ExpectedSize As Long = Item.Value(Of Long?)("size").GetValueOrDefault(0)
            Return WarThunderCreateDownloadNetFile(
                Url, Path.Combine(DestinationFolder, Path.GetFileName(FileName)), ExpectedSize)
        Catch ex As Exception
            ErrorMessage = ex.GetDisplay(False)
            Return Nothing
        End Try
    End Function

    Public Function WarThunderCreateUrlDownloadNetFile(Url As String,
                                                       DestinationFolder As String,
                                                       PreferredExtension As String,
                                                       ByRef ErrorMessage As String) As NetFile
        ErrorMessage = ""
        Try
            Dim NormalizedUrl As String = WarThunderResolveServiceUrl(Url)
            Dim Extension As String = Path.GetExtension(New Uri(NormalizedUrl).AbsolutePath)
            If String.IsNullOrWhiteSpace(Extension) Then Extension = "." & PreferredExtension.TrimStart("."c)
            Dim FileName As String = $"WarThunder_{Guid.NewGuid():N}{Extension}"
            Return WarThunderCreateDownloadNetFile(
                NormalizedUrl, Path.Combine(DestinationFolder, FileName), 0)
        Catch ex As Exception
            ErrorMessage = ex.GetDisplay(False)
            Return Nothing
        End Try
    End Function

    Private Function WarThunderCreateDownloadNetFile(Url As String,
                                                     LocalPath As String,
                                                     ExpectedSize As Long) As NetFile
        Dim Link As New Uri(Url)
        If Link.Scheme <> Uri.UriSchemeHttps AndAlso Link.Scheme <> Uri.UriSchemeHttp Then
            Throw New ArgumentException("只支持 HTTP 或 HTTPS 下载链接。")
        End If
        Directory.CreateDirectory(Path.GetDirectoryName(LocalPath))
        Dim Checker As New FileChecker With {
            .MinSize = 1,
            .ActualSize = If(ExpectedSize > 0, ExpectedSize, -1)
        }
        Return New NetFile({Link.AbsoluteUri}, LocalPath, Checker)
    End Function

    Public Function WarThunderVerifyServiceDownloadHash(Item As JObject,
                                                        FilePath As String,
                                                        ByRef ErrorMessage As String) As Boolean
        ErrorMessage = ""
        Dim ExpectedHash As String = Item.Value(Of String)("sha256")
        If String.IsNullOrWhiteSpace(ExpectedHash) Then Return True
        Try
            Dim ActualHash As String = CryptographyUtils.ComputeFileHash(
                FilePath, CryptographyUtils.HashMethod.Sha256)
            If ActualHash.Equals(ExpectedHash, StringComparison.OrdinalIgnoreCase) Then Return True
            ErrorMessage = "文件校验失败，请重新下载。"
            Return False
        Catch ex As Exception
            ErrorMessage = ex.GetDisplay(False)
            Return False
        End Try
    End Function

    Public Function WarThunderValidateInnoSetupInstaller(FilePath As String) As Boolean
        Try
            Using Stream As New FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)
                If Stream.Length < 1024 Then Return False
                If Stream.ReadByte() <> &H4D OrElse Stream.ReadByte() <> &H5A Then Return False

                Dim BufferLength As Integer = CInt(Math.Min(2L * 1024 * 1024, Stream.Length))
                Dim Buffer(BufferLength - 1) As Byte
                Stream.Position = 0
                Stream.Read(Buffer, 0, Buffer.Length)
                Dim HeadText As String = Text.Encoding.ASCII.GetString(Buffer)
                If HeadText.Contains("Inno Setup") Then Return True

                Stream.Position = Math.Max(0, Stream.Length - BufferLength)
                Stream.Read(Buffer, 0, Buffer.Length)
                Dim TailText As String = Text.Encoding.ASCII.GetString(Buffer)
                Return TailText.Contains("Inno Setup") OrElse TailText.Contains("InnoSetupLdr")
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"校验 Inno Setup 安装器失败：{FilePath}")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 从服务端下载真实游戏头像，没有时返回空字符串。
    ''' </summary>
    Public Function WarThunderTryDownloadServiceAvatar(UserId As String,
                                                       DestinationFolder As String) As String
        Try
            Directory.CreateDirectory(DestinationFolder)
            Using Client As New HttpClient()
                Client.Timeout = TimeSpan.FromSeconds(15)
                Client.DefaultRequestHeaders.UserAgent.ParseAdd("PTL/" & PtlVersion)
                Using Response As HttpResponseMessage = Client.GetAsync(
                    $"{WarThunderStatsApiBaseUrl}/api/players/{Uri.EscapeDataString(UserId)}/avatar",
                    HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()
                    If Response.StatusCode = Net.HttpStatusCode.NotFound Then Return ""
                    Response.EnsureSuccessStatusCode()
                    Dim MediaType As String = If(
                        Response.Content.Headers.ContentType?.MediaType, "").ToLowerInvariant()
                    If MediaType.Contains("avif") Then
                        Return WarThunderTryDownloadAvatarFromStats(UserId, DestinationFolder)
                    End If
                    If Not {"image/png", "image/jpeg", "image/jpg", "image/webp"}.Contains(MediaType) Then
                        Return ""
                    End If
                    Dim Extension As String = If(MediaType.Contains("png"), "png",
                        If(MediaType.Contains("webp"), "webp", "jpg"))
                    Dim TargetPath As String = Path.Combine(DestinationFolder, $"{UserId}.{Extension}")
                    Dim TempPath As String = TargetPath & ".tmp"
                    Using Input = Response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                        Using Output As New FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None)
                            Dim Buffer(81919) As Byte
                            Dim Total As Integer
                            Do
                                Dim ReadCount As Integer = Input.Read(Buffer, 0, Buffer.Length)
                                If ReadCount <= 0 Then Exit Do
                                Total += ReadCount
                                If Total > 8 * 1024 * 1024 Then Throw New InvalidDataException("服务端头像超过 8 MB。")
                                Output.Write(Buffer, 0, ReadCount)
                            Loop
                        End Using
                    End Using
                    If File.Exists(TargetPath) Then
                        File.Replace(TempPath, TargetPath, Nothing, True)
                    Else
                        File.Move(TempPath, TargetPath)
                    End If
                    Return TargetPath
                End Using
            End Using
        Catch ex As Exception
            Logger.Info($"服务端头像不可用或尚未配置：{ex.Message}")
            Return ""
        End Try
    End Function

    Private Function WarThunderTryDownloadAvatarFromStats(
        UserId As String, DestinationFolder As String) As String
        Try
            Dim Stats = CType(WarThunderServiceGetUrl(
                WarThunderResolveStatsApiUrl(
                    $"/api/players/{Uri.EscapeDataString(UserId)}/stats?refresh=false")), JObject)
            Dim Player = TryCast(Stats("player"), JObject)
            If Player Is Nothing Then Return ""
            Using Client As New HttpClient()
                Client.Timeout = TimeSpan.FromSeconds(15)
                Client.DefaultRequestHeaders.UserAgent.ParseAdd("PTL/" & PtlVersion)
                Dim Candidates As New List(Of String) From {
                    Player.Value(Of String)("avatar_mirror_url"),
                    Player.Value(Of String)("avatar_url")
                }
                Dim AvatarIcon As String = Player.Value(Of String)("avatar_icon")
                If Not String.IsNullOrWhiteSpace(AvatarIcon) Then
                    Candidates.Add(
                        "https://raw.githubusercontent.com/gszabi99/War-Thunder-Datamine/master/" &
                        "images.vromfs.bin_u/images/avatars/" &
                        Uri.EscapeDataString(AvatarIcon) & ".png")
                End If
                For Each Candidate In Candidates.Distinct(StringComparer.OrdinalIgnoreCase)
                    If String.IsNullOrWhiteSpace(Candidate) Then Continue For
                    Dim AvatarUrl As String = WarThunderResolveStatsApiUrl(Candidate)
                    If AvatarUrl.IndexOf("/api/players/", StringComparison.OrdinalIgnoreCase) >= 0 Then
                        Continue For
                    End If
                    Try
                        Dim Data As Byte() = Client.GetByteArrayAsync(AvatarUrl).GetAwaiter().GetResult()
                        If Data.Length = 0 OrElse Data.Length > 8 * 1024 * 1024 Then Continue For
                        Dim Extension As String = ""
                        If Data.Length > 8 AndAlso Data(0) = &H89 AndAlso Data(1) = &H50 AndAlso
                           Data(2) = &H4E AndAlso Data(3) = &H47 Then
                            Extension = "png"
                        ElseIf Data.Length > 2 AndAlso Data(0) = &HFF AndAlso Data(1) = &HD8 Then
                            Extension = "jpg"
                        Else
                            Continue For
                        End If
                        Using Image = System.Drawing.Image.FromStream(New MemoryStream(Data))
                            If Image.Width <= 0 OrElse Image.Height <= 0 Then Continue For
                        End Using
                        Dim TargetPath As String = Path.Combine(
                            DestinationFolder, $"{UserId}.{Extension}")
                        Dim TempPath As String = TargetPath & ".tmp"
                        File.WriteAllBytes(TempPath, Data)
                        If File.Exists(TargetPath) Then
                            File.Replace(TempPath, TargetPath, Nothing, True)
                        Else
                            File.Move(TempPath, TargetPath)
                        End If
                        For Each OtherExtension In {"jpg", "jpeg", "png", "webp"}
                            If OtherExtension = Extension Then Continue For
                            Dim OtherPath As String = Path.Combine(
                                DestinationFolder, $"{UserId}.{OtherExtension}")
                            Try
                                If File.Exists(OtherPath) Then File.Delete(OtherPath)
                            Catch
                            End Try
                        Next
                        Return TargetPath
                    Catch ex As Exception
                        Logger.Info($"备用头像源不可用（{AvatarUrl}）：{ex.Message}")
                    End Try
                Next
            End Using
        Catch ex As Exception
            Logger.Info($"获取备用头像失败：{ex.Message}")
        End Try
        Return ""
    End Function

End Module
