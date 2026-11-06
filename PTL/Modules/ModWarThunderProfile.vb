Imports System.Text.RegularExpressions

''' <summary>
''' 战争雷霆玩家公开资料。
''' </summary>
Public Class WarThunderPlayerInfo
    Public Property UserId As String = ""
    Public Property Nickname As String = ""
    Public Property AvatarUrl As String = ""
    Public Property AvatarPath As String = ""
    Public Property LastUpdatedUtc As DateTime = DateTime.MinValue

    Public ReadOnly Property DisplayName As String
        Get
            If Not String.IsNullOrWhiteSpace(Nickname) Then Return Nickname
            If Not String.IsNullOrWhiteSpace(UserId) Then Return "玩家 " & UserId
            Return "未识别玩家"
        End Get
    End Property
End Class

''' <summary>
''' 从 WT Live 读取玩家昵称和头像，并缓存到 PTL 独立目录。
''' </summary>
Public Module ModWarThunderProfile

    Private Const ProfileRegistryKey As String = "HKEY_CURRENT_USER\Software\PTL\WarThunderProfile"
    Private Const ProfileRefreshHours As Integer = 6
    Private ReadOnly ProfileLock As New Object()

    Public WarThunderCurrentPlayer As New WarThunderPlayerInfo()
    Public WarThunderPlayerLoading As Boolean = False

    Public ReadOnly Property WarThunderPlayerCacheFolder As String
        Get
            Return Path.Combine(Paths.AppDataThenName, "WarThunder", "Players")
        End Get
    End Property

    Public Function WarThunderGetSavesFolder() As String
        Return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games", "WarThunder", "Saves")
    End Function

    Public Function WarThunderGetActiveUserId() As String
        Dim LastLoginPath As String = Path.Combine(WarThunderGetSavesFolder(), "lastlogin.blk")
        If File.Exists(LastLoginPath) Then
            Try
                Dim Match = Regex.Match(File.ReadAllText(LastLoginPath), "uid:i64\s*=\s*(\d+)")
                If Match.Success Then Return Match.Groups(1).Value
            Catch ex As Exception
                Logger.Warn(ex, "读取战争雷霆当前玩家 UID 失败")
            End Try
        End If

        Dim LastUidPath As String = Path.Combine(WarThunderGetSavesFolder(), "last", "lastUID")
        If File.Exists(LastUidPath) Then
            Try
                Dim Value As String = File.ReadAllText(LastUidPath).Trim(ChrW(0), " "c, vbCr, vbLf, vbTab)
                If Regex.IsMatch(Value, "^\d+$") Then Return Value
            Catch ex As Exception
                Logger.Warn(ex, "读取战争雷霆 lastUID 失败")
            End Try
        End If

        Return RegistryUtils.TryRead(ProfileRegistryKey, "LastUserId", "").ToString()
    End Function

    Public Function WarThunderGetUserFolders() As List(Of String)
        Dim Result As New List(Of String)
        Dim SavesFolder As String = WarThunderGetSavesFolder()
        If Not Directory.Exists(SavesFolder) Then Return Result
        Try
            Result = Directory.GetDirectories(SavesFolder).
                Where(Function(path) Regex.IsMatch(IO.Path.GetFileName(path), "^\d+$")).
                OrderByDescending(Function(path) Directory.GetLastWriteTimeUtc(path)).
                ToList()
        Catch ex As Exception
            Logger.Warn(ex, "扫描战争雷霆玩家存档目录失败")
        End Try
        Return Result
    End Function

    Public Function WarThunderGetCachedNickname(UserId As String) As String
        If String.IsNullOrWhiteSpace(UserId) Then Return ""
        Dim NicknamePath As String = Path.Combine(WarThunderPlayerCacheFolder, UserId & ".nickname")
        If Not File.Exists(NicknamePath) Then Return ""
        Try
            Return File.ReadAllText(NicknamePath).Trim()
        Catch
            Return ""
        End Try
    End Function

    Public Function WarThunderGetCachedAvatar(UserId As String) As String
        If String.IsNullOrWhiteSpace(UserId) Then Return ""
        Directory.CreateDirectory(WarThunderPlayerCacheFolder)
        Dim Candidates = {"jpg", "jpeg", "png", "webp"}.
            Select(Function(Extension) Path.Combine(WarThunderPlayerCacheFolder, $"{UserId}.{Extension}")).
            Where(Function(Candidate) File.Exists(Candidate)).
            OrderByDescending(Function(Candidate) File.GetLastWriteTimeUtc(Candidate)).
            ToList()
        For Each Candidate In Candidates
            Try
                If Not WarThunderAvatarHeaderMatchesExtension(Candidate) Then Continue For
                Using Image = System.Drawing.Image.FromFile(Candidate)
                    If Image.Width > 0 AndAlso Image.Height > 0 Then Return Candidate
                End Using
            Catch
            End Try
        Next
        Return ""
    End Function

    Private Function WarThunderAvatarHeaderMatchesExtension(FilePath As String) As Boolean
        Dim Header(11) As Byte
        Using Stream As New FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            If Stream.Length < 4 Then Return False
            Stream.Read(Header, 0, Header.Length)
        End Using
        Select Case Path.GetExtension(FilePath).ToLowerInvariant()
            Case ".png"
                Return Header(0) = &H89 AndAlso Header(1) = &H50 AndAlso
                       Header(2) = &H4E AndAlso Header(3) = &H47
            Case ".jpg", ".jpeg"
                Return Header(0) = &HFF AndAlso Header(1) = &HD8
            Case ".webp"
                Return Header(0) = &H52 AndAlso Header(1) = &H49 AndAlso
                       Header(2) = &H46 AndAlso Header(3) = &H46 AndAlso
                       Header(8) = &H57 AndAlso Header(9) = &H45 AndAlso
                       Header(10) = &H42 AndAlso Header(11) = &H50
            Case Else
                Return False
        End Select
    End Function

    Public Function WarThunderCreatePlayerInfo(UserId As String) As WarThunderPlayerInfo
        Dim AvatarPath As String = WarThunderGetCachedAvatar(UserId)
        Return New WarThunderPlayerInfo With {
            .UserId = UserId,
            .Nickname = WarThunderGetCachedNickname(UserId),
            .AvatarPath = AvatarPath,
            .LastUpdatedUtc = If(AvatarPath = "", DateTime.MinValue, File.GetLastWriteTimeUtc(AvatarPath))
        }
    End Function

    Public Sub WarThunderRefreshCurrentPlayerAsync(Optional Force As Boolean = False)
        Dim UserId As String = WarThunderGetActiveUserId()
        If UserId = "" Then
            WarThunderCurrentPlayer = New WarThunderPlayerInfo()
            WarThunderRefreshPlayerUI()
            Return
        End If

        SyncLock ProfileLock
            If WarThunderPlayerLoading Then Return
            WarThunderPlayerLoading = True
        End SyncLock

        Dim Cached As WarThunderPlayerInfo = WarThunderCreatePlayerInfo(UserId)
        Cached.UserId = UserId
        WarThunderCurrentPlayer = Cached
        WarThunderRefreshPlayerUI()

        RunInNewThread(
        Sub()
            Try
                If Not Force AndAlso Not WarThunderAvatarNeedsRefresh(UserId) AndAlso
                   Cached.AvatarPath <> "" AndAlso
                   Date.UtcNow - Cached.LastUpdatedUtc < TimeSpan.FromHours(ProfileRefreshHours) Then
                    Return
                End If

                Dim Profile As WarThunderPlayerInfo = WarThunderFetchPlayerInfoV2(UserId)
                SyncLock ProfileLock
                    If WarThunderGetActiveUserId() = UserId Then WarThunderCurrentPlayer = Profile
                End SyncLock
                RegistryUtils.Write(ProfileRegistryKey, "LastUserId", UserId)
                WarThunderLog($"已更新玩家资料：{Profile.DisplayName}（{Profile.UserId}）")
            Catch ex As Exception
                WarThunderLog($"玩家资料更新失败，继续使用缓存：{ex.GetDisplay(False)}")
                Logger.Warn(ex, "获取战争雷霆玩家资料失败")
            Finally
                SyncLock ProfileLock
                    WarThunderPlayerLoading = False
                End SyncLock
                WarThunderRefreshPlayerUI()
            End Try
        End Sub, "War Thunder Player Profile", ThreadPriority.BelowNormal)
    End Sub

    Private Function WarThunderAvatarNeedsRefresh(UserId As String) As Boolean
        Dim SourcePath As String = Path.Combine(WarThunderPlayerCacheFolder, UserId & ".avatar.source")
        If Not File.Exists(SourcePath) Then Return True
        Try
            Dim Source As String = File.ReadAllText(SourcePath)
            Return Source.IndexOf("no_avatar", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                   Source.Equals("generated", StringComparison.OrdinalIgnoreCase)
        Catch
            Return True
        End Try
    End Function

    Private Function WarThunderFetchPlayerInfo(UserId As String) As WarThunderPlayerInfo
        Directory.CreateDirectory(WarThunderPlayerCacheFolder)
        Dim ProfileUrl As String = $"https://live.warthunder.com/user/{UserId}/"
        Using Client As New HttpClient()
            Client.Timeout = TimeSpan.FromSeconds(15)
            Client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PTL/" & PtlVersion)
            Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.6")
            Dim Html As String = Client.GetStringAsync(ProfileUrl).GetAwaiter().GetResult()

            Dim NameMatch As Match = Regex.Match(
                Html, "<title>\s*WT Live\s*//\s*(.*?)\s*</title>",
                RegexOptions.IgnoreCase Or RegexOptions.Singleline)
            If Not NameMatch.Success Then Throw New InvalidDataException("玩家资料页中没有找到昵称。")
            Dim Nickname As String = Net.WebUtility.HtmlDecode(NameMatch.Groups(1).Value).Trim()

            Dim AvatarMatch As Match = Regex.Match(
                Html,
                "<meta\b(?=[^>]*property\s*=\s*[""']og:image[""'])[^>]*content\s*=\s*[""']([^""']+)[""']",
                RegexOptions.IgnoreCase)
            If Not AvatarMatch.Success Then
                AvatarMatch = Regex.Match(
                    Html,
                    "<meta\b(?=[^>]*content\s*=\s*[""']([^""']+)[""'])[^>]*property\s*=\s*[""']og:image[""']",
                    RegexOptions.IgnoreCase)
            End If
            If Not AvatarMatch.Success Then Throw New InvalidDataException("玩家资料页中没有找到头像。")

            Dim AvatarUrl As String = Net.WebUtility.HtmlDecode(AvatarMatch.Groups(1).Value).Trim()
            If AvatarUrl.StartsWithF("//") Then
                AvatarUrl = "https:" & AvatarUrl
            ElseIf AvatarUrl.StartsWithF("/") Then
                AvatarUrl = "https://live.warthunder.com" & AvatarUrl
            End If
            If Not AvatarUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) Then
                Throw New InvalidDataException($"不支持的头像地址：{AvatarUrl}")
            End If

            Dim Extension As String = Path.GetExtension(New Uri(AvatarUrl).AbsolutePath).TrimStart("."c).ToLowerInvariant()
            If Not {"jpg", "jpeg", "png", "webp"}.Contains(Extension) Then Extension = "jpg"
            Dim AvatarPath As String = Path.Combine(WarThunderPlayerCacheFolder, $"{UserId}.{Extension}")
            Dim TempPath As String = AvatarPath & ".tmp"
            Dim Data As Byte() = Client.GetByteArrayAsync(AvatarUrl).GetAwaiter().GetResult()
            If Data.Length = 0 OrElse Data.Length > 8 * 1024 * 1024 Then
                Throw New InvalidDataException("玩家头像为空或超过 8 MB。")
            End If
            File.WriteAllBytes(TempPath, Data)
            If File.Exists(AvatarPath) Then
                File.Replace(TempPath, AvatarPath, Nothing, True)
            Else
                File.Move(TempPath, AvatarPath)
            End If
            For Each OtherExtension In {"jpg", "jpeg", "png", "webp"}.
                Where(Function(item) Not item.Equals(Extension, StringComparison.OrdinalIgnoreCase))
                Dim OtherPath As String = Path.Combine(WarThunderPlayerCacheFolder, $"{UserId}.{OtherExtension}")
                Try
                    If File.Exists(OtherPath) Then File.Delete(OtherPath)
                Catch
                End Try
            Next

            Dim NicknamePath As String = Path.Combine(WarThunderPlayerCacheFolder, UserId & ".nickname")
            File.WriteAllText(NicknamePath, Nickname, New Text.UTF8Encoding(False))
            Return New WarThunderPlayerInfo With {
                .UserId = UserId,
                .Nickname = Nickname,
                .AvatarUrl = AvatarUrl,
                .AvatarPath = AvatarPath,
                .LastUpdatedUtc = Date.UtcNow
            }
        End Using
    End Function

    Private Function WarThunderFetchPlayerInfoV2(UserId As String) As WarThunderPlayerInfo
        Directory.CreateDirectory(WarThunderPlayerCacheFolder)
        Dim ProfileUrl As String = $"https://live.warthunder.com/user/{UserId}/"
        Using Client As New HttpClient()
            Client.Timeout = TimeSpan.FromSeconds(15)
            Client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PTL/" & PtlVersion)
            Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.6")
            Dim Html As String = Client.GetStringAsync(ProfileUrl).GetAwaiter().GetResult()

            Dim NameMatch As Match = Regex.Match(
                Html, "<title>\s*WT Live\s*//\s*(.*?)\s*</title>",
                RegexOptions.IgnoreCase Or RegexOptions.Singleline)
            If Not NameMatch.Success Then Throw New InvalidDataException("玩家资料页中没有找到昵称。")
            Dim Nickname As String = Net.WebUtility.HtmlDecode(NameMatch.Groups(1).Value).Trim()

            Dim AvatarPath As String = WarThunderTryDownloadServiceAvatar(
                UserId, WarThunderPlayerCacheFolder)
            Dim AvatarUrl As String = "service"

            If AvatarPath = "" Then
                Dim AvatarMatch As Match = Regex.Match(
                    Html,
                    "<div\s+class=[""']avatar[^""']*[""'][^>]*>\s*<img\b[^>]*src\s*=\s*[""']([^""']+)[""']",
                    RegexOptions.IgnoreCase Or RegexOptions.Singleline)
                If Not AvatarMatch.Success Then
                    AvatarMatch = Regex.Match(
                        Html,
                        "<meta\b(?=[^>]*property\s*=\s*[""']og:image[""'])[^>]*content\s*=\s*[""']([^""']+)[""']",
                        RegexOptions.IgnoreCase)
                End If
                If AvatarMatch.Success Then
                    AvatarUrl = Net.WebUtility.HtmlDecode(AvatarMatch.Groups(1).Value).Trim()
                    If AvatarUrl.IndexOf("no_avatar", StringComparison.OrdinalIgnoreCase) >= 0 Then
                        AvatarUrl = ""
                    Else
                        If AvatarUrl.StartsWithF("//") Then
                            AvatarUrl = "https:" & AvatarUrl
                        ElseIf AvatarUrl.StartsWithF("/") Then
                            AvatarUrl = "https://live.warthunder.com" & AvatarUrl
                        End If
                        If AvatarUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) Then
                            Dim Extension As String = Path.GetExtension(
                                New Uri(AvatarUrl).AbsolutePath).TrimStart("."c).ToLowerInvariant()
                            If Not {"jpg", "jpeg", "png", "webp"}.Contains(Extension) Then Extension = "jpg"
                            AvatarPath = Path.Combine(
                                WarThunderPlayerCacheFolder, $"{UserId}.{Extension}")
                            Dim Data As Byte() = Client.GetByteArrayAsync(AvatarUrl).GetAwaiter().GetResult()
                            If Data.Length > 0 AndAlso Data.Length <= 8 * 1024 * 1024 Then
                                File.WriteAllBytes(AvatarPath, Data)
                            Else
                                AvatarPath = ""
                            End If
                        Else
                            AvatarUrl = ""
                        End If
                    End If
                Else
                    AvatarUrl = ""
                End If
            End If

            If AvatarPath = "" Then
                AvatarPath = WarThunderGenerateFallbackAvatar(UserId, Nickname)
                AvatarUrl = "generated"
            End If

            If AvatarPath <> "" Then WarThunderRemoveOtherAvatarFiles(UserId, AvatarPath)
            Dim NicknamePath As String = Path.Combine(WarThunderPlayerCacheFolder, UserId & ".nickname")
            File.WriteAllText(NicknamePath, Nickname, New Text.UTF8Encoding(False))
            File.WriteAllText(Path.Combine(WarThunderPlayerCacheFolder, UserId & ".avatar.source"),
                              AvatarUrl, New Text.UTF8Encoding(False))
            Return New WarThunderPlayerInfo With {
                .UserId = UserId,
                .Nickname = Nickname,
                .AvatarUrl = AvatarUrl,
                .AvatarPath = AvatarPath,
                .LastUpdatedUtc = Date.UtcNow
            }
        End Using
    End Function

    Private Sub WarThunderRemoveOtherAvatarFiles(UserId As String, KeepPath As String)
        Dim NormalizedKeepPath As String = Path.GetFullPath(KeepPath)
        For Each Extension In {"jpg", "jpeg", "png", "webp"}
            Dim Candidate As String = Path.Combine(
                WarThunderPlayerCacheFolder, $"{UserId}.{Extension}")
            Try
                If File.Exists(Candidate) AndAlso
                   Not Path.GetFullPath(Candidate).Equals(
                       NormalizedKeepPath, StringComparison.OrdinalIgnoreCase) Then
                    File.Delete(Candidate)
                End If
            Catch ex As Exception
                Logger.Warn(ex, $"清理旧头像缓存失败：{Candidate}")
            End Try
        Next
    End Sub

    Private Function WarThunderGenerateFallbackAvatar(UserId As String, Nickname As String) As String
        Dim TargetPath As String = Path.Combine(WarThunderPlayerCacheFolder, UserId & ".png")
        Dim SeedValue As Long = 17
        For Each Character As Char In UserId & Nickname
            SeedValue = (SeedValue * 31 + AscW(Character)) And &H7FFFFFFFL
        Next
        Dim Seed As Integer = CInt(SeedValue)
        Dim Color1 = System.Drawing.Color.FromArgb(55 + Seed Mod 35, 105 + (Seed >> 3) Mod 55, 150 + (Seed >> 6) Mod 60)
        Dim Color2 = System.Drawing.Color.FromArgb(170 + (Seed >> 2) Mod 60, 95 + (Seed >> 5) Mod 55, 45 + (Seed >> 8) Mod 45)
        Dim Initial As String = If(String.IsNullOrWhiteSpace(Nickname), "WT",
                                   Nickname.Substring(0, Math.Min(2, Nickname.Length)).ToUpperInvariant())
        Using Bitmap As New System.Drawing.Bitmap(160, 160)
            Using Graphics = System.Drawing.Graphics.FromImage(Bitmap)
                Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias
                Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit
                Using Brush As New System.Drawing.Drawing2D.LinearGradientBrush(
                    New System.Drawing.Rectangle(0, 0, 160, 160), Color1, Color2, 45.0F)
                    Graphics.FillRectangle(Brush, 0, 0, 160, 160)
                End Using
                Using Font As New System.Drawing.Font("Microsoft YaHei UI", 43, System.Drawing.FontStyle.Bold,
                                                     System.Drawing.GraphicsUnit.Pixel)
                    Using Format As New System.Drawing.StringFormat()
                        Format.Alignment = System.Drawing.StringAlignment.Center
                        Format.LineAlignment = System.Drawing.StringAlignment.Center
                        Graphics.DrawString(Initial, Font, System.Drawing.Brushes.White,
                                            New System.Drawing.RectangleF(0, 0, 160, 160), Format)
                    End Using
                End Using
            End Using
            Bitmap.Save(TargetPath, System.Drawing.Imaging.ImageFormat.Png)
        End Using
        Return TargetPath
    End Function

    Public Sub WarThunderSetPlayerIdentity(UserId As String, Nickname As String)
        UserId = If(UserId, "").Trim()
        Nickname = If(Nickname, "").Trim()
        If Not Regex.IsMatch(UserId, "^\d+$") Then Throw New ArgumentException("玩家 ID 必须全部为数字。")
        Directory.CreateDirectory(WarThunderPlayerCacheFolder)
        File.WriteAllText(Path.Combine(WarThunderPlayerCacheFolder, UserId & ".nickname"),
                          Nickname, New Text.UTF8Encoding(False))
        RegistryUtils.Write(ProfileRegistryKey, "LastUserId", UserId)
        Dim Profile = WarThunderCreatePlayerInfo(UserId)
        Profile.Nickname = Nickname
        WarThunderCurrentPlayer = Profile
        WarThunderRefreshPlayerUI()
    End Sub

    Public Sub WarThunderRefreshPlayerUI()
        If FrmMain Is Nothing Then Return
        RunInUi(
        Sub()
            Try
                FrmLaunchLeft?.RefreshWarThunderPlayer()
                FrmLaunchSettings?.RefreshPlayerPanel()
                FrmWarThunderUsers?.RefreshUsers()
                FrmSetupUI?.RefreshWarThunderCard()
            Catch ex As Exception
                Logger.Warn(ex, "刷新战争雷霆玩家界面失败")
            End Try
        End Sub)
    End Sub

End Module
