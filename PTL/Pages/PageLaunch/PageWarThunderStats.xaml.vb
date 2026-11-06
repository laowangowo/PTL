Imports Newtonsoft.Json.Linq
Imports System.Text.RegularExpressions

Public Class PageWarThunderStats
    Implements IRefreshable

    Private IsLoading As Boolean
    Private QueryGeneration As Integer
    Private LastActiveUserId As String = ""

    Private Sub PageRefresh() Handles Me.Loaded, Me.PageEnter
        If Not IsLoaded Then Return
        PanBack.ScrollToHome()
        LoadCurrentPlayer()
        RefreshStats(TextStatsQuery.Text)
    End Sub

    Private Sub LoadCurrentPlayer()
        Dim UserId As String = WarThunderGetActiveUserId()
        Dim UserChanged As Boolean = Not UserId.Equals(LastActiveUserId, StringComparison.Ordinal)
        LastActiveUserId = UserId
        If WarThunderCurrentPlayer Is Nothing OrElse WarThunderCurrentPlayer.UserId <> UserId Then
            WarThunderCurrentPlayer = WarThunderCreatePlayerInfo(UserId)
        End If
        LabStatsNickname.Text = WarThunderCurrentPlayer.DisplayName
        LabStatsUid.Text = If(UserId = "", "UID 尚未识别", "UID " & UserId)
        ImgStatsAvatar.Source = If(
            WarThunderCurrentPlayer.AvatarPath <> "" AndAlso File.Exists(WarThunderCurrentPlayer.AvatarPath),
            WarThunderCurrentPlayer.AvatarPath, Nothing)
        If UserChanged OrElse String.IsNullOrWhiteSpace(TextStatsQuery.Text) Then
            TextStatsQuery.Text = UserId
            ClearStatsDisplay()
        End If
    End Sub

    ''' <summary>
    ''' 账号切换后立即把战绩查询目标同步到新玩家。
    ''' </summary>
    Public Sub SwitchToActiveUser()
        If Not IsLoaded Then Return
        Dim UserId As String = WarThunderGetActiveUserId()
        If UserId = "" Then Return
        LastActiveUserId = UserId
        TextStatsQuery.Text = UserId
        ClearStatsDisplay()
        RefreshStats(UserId, True)
    End Sub

    Private Sub RefreshStats(Optional Query As String = "", Optional Force As Boolean = False)
        If IsLoading AndAlso Not Force Then Return
        Query = If(Query, "").Trim()
        If Query = "" Then Query = TextStatsQuery.Text.Trim()
        If Query = "" Then Query = WarThunderGetActiveUserId()
        If Query = "" Then
            LabStatsStatus.Text = "请输入玩家 UID 或昵称。"
            Return
        End If
        Dim QueryIsUid As Boolean = Regex.IsMatch(Query, "^\d+$")
        QueryGeneration += 1
        Dim Generation As Integer = QueryGeneration
        IsLoading = True
        BtnStatsRefresh.IsEnabled = False
        BtnStatsQuery.IsEnabled = False
        LabStatsStatus.Text = "正在获取最新战绩…"
        RunInNewThread(
        Sub()
            Dim StatsResult As JObject = Nothing
            Dim ResponseUid As String = If(QueryIsUid, Query, "")
            Dim AvatarPath As String = ""
            Dim ErrorMessage As String = ""
            Try
                StatsResult = If(QueryIsUid,
                                 WarThunderFetchPlayerStats(Query, True),
                                 WarThunderFetchPlayerStatsByNickname(Query))
                Dim Player = TryCast(StatsResult("player"), JObject)
                If Player IsNot Nothing Then
                    Dim ResolvedUid As String = GetString(Player, "uid")
                    If ResolvedUid <> "" Then ResponseUid = ResolvedUid
                End If
                If ResponseUid <> "" Then
                    AvatarPath = WarThunderTryDownloadServiceAvatar(
                        ResponseUid, WarThunderPlayerCacheFolder)
                End If
            Catch ex As Exception
                ErrorMessage = ex.GetDisplay(False)
                Logger.Warn(ex, "获取战争雷霆玩家战绩失败")
            End Try
            RunInUi(
            Sub()
                If Generation <> QueryGeneration Then Return
                IsLoading = False
                BtnStatsRefresh.IsEnabled = True
                BtnStatsQuery.IsEnabled = True
                If StatsResult IsNot Nothing Then
                    ApplyStatsResult(StatsResult)
                    If AvatarPath <> "" Then
                        ImgStatsAvatar.Source = AvatarPath
                        If ResponseUid = WarThunderGetActiveUserId() Then
                            WarThunderCurrentPlayer = WarThunderCreatePlayerInfo(ResponseUid)
                            WarThunderRefreshPlayerUI()
                        End If
                    Else
                        Dim CachedAvatar As String = WarThunderGetCachedAvatar(ResponseUid)
                        ImgStatsAvatar.Source = If(
                            CachedAvatar <> "" AndAlso File.Exists(CachedAvatar),
                            CachedAvatar, Nothing)
                    End If
                    ApplyModes(StatsResult)
                Else
                    LabStatsStatus.Text = "暂时无法获取战绩，请稍后重试。"
                    PanModes.Children.Clear()
                    PanModes.Children.Add(New TextBlock With {
                        .Text = "暂时没有获取到模式战绩。",
                        .FontSize = 12,
                        .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush)
                    })
                End If
            End Sub)
        End Sub, "War Thunder Stats Query", ThreadPriority.AboveNormal)
    End Sub

    Private Sub ClearStatsDisplay()
        LabStatsLevel.Text = "—"
        LabStatsClan.Text = "—"
        LabStatsBattles.Text = "—"
        LabStatsVictories.Text = "—"
        LabStatsWinRate.Text = "—"
        LabStatsDeaths.Text = "—"
        LabStatsAirKills.Text = "—"
        LabStatsGroundKills.Text = "—"
        LabStatsNavalKills.Text = "—"
        LabStatsPlayTime.Text = "—"
        LabStatsKills.Text = "—"
        LabStatsKD.Text = "—"
        PanModes.Children.Clear()
    End Sub

    Private Sub ApplyStatsResult(Result As JObject)
        Dim Player = TryCast(Result("player"), JObject)
        Dim Stats = TryCast(Result("stats"), JObject)
        Dim Snapshot = TryCast(Result("snapshot"), JObject)
        If Player IsNot Nothing Then
            Dim Nickname As String = GetString(Player, "nickname")
            If Nickname <> "" Then LabStatsNickname.Text = Nickname
            Dim Uid As String = GetString(Player, "uid")
            If Uid <> "" Then LabStatsUid.Text = "UID " & Uid
            Dim Level As String = GetString(Player, "level")
            LabStatsLevel.Text = If(Level = "", "—", Level)
            LabStatsClan.Text = ValueOrDash(GetString(Player, "clan"))
        End If
        If Stats IsNot Nothing Then
            LabStatsBattles.Text = ValueOrDash(GetString(Stats, "battles"))
            LabStatsVictories.Text = ValueOrDash(GetString(Stats, "victories"))
            LabStatsWinRate.Text = ValueOrDash(GetString(Stats, "win_rate"))
            LabStatsDeaths.Text = ValueOrDash(GetString(Stats, "deaths"))
            LabStatsAirKills.Text = ValueOrDash(GetString(Stats, "air_kills"))
            LabStatsGroundKills.Text = ValueOrDash(GetString(Stats, "ground_kills"))
            LabStatsNavalKills.Text = ValueOrDash(GetString(Stats, "naval_kills"))
            LabStatsPlayTime.Text = FormatPlayTime(Stats)
            LabStatsKills.Text = ValueOrDash(GetString(Stats, "kills"))
            LabStatsKD.Text = ValueOrDash(GetString(Stats, "kd"))
        End If
        If Snapshot IsNot Nothing Then
            Dim IsStale As Boolean = Snapshot.Value(Of Boolean?)("stale").GetValueOrDefault(False)
            Dim IsCacheHit As Boolean = Snapshot.Value(Of Boolean?)("cache_hit").GetValueOrDefault(False)
            Dim Prefix As String = If(IsStale, "当前显示最近一次快照 ",
                                      If(IsCacheHit, "已显示最近一次结果 ", "战绩已更新 "))
            LabStatsStatus.Text = Prefix &
                ValueOrDash(GetString(Snapshot, "captured_at"))
        Else
            LabStatsStatus.Text = "数据已刷新。"
        End If
    End Sub

    Private Sub ApplyModes(Result As JObject)
        PanModes.Children.Clear()
        Dim Modes = TryCast(Result("modes"), JArray)
        If Modes Is Nothing OrElse Modes.Count = 0 Then
            PanModes.Children.Add(New TextBlock With {
                .Text = "暂时没有可展示的模式战绩。",
                .FontSize = 12,
                .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush)
            })
            Return
        End If
        For Each Token As JToken In Modes
            Dim Mode = TryCast(Token, JObject)
            If Mode Is Nothing Then Continue For
            Dim Name As String = ValueOrDash(GetString(Mode, "name"))
            Dim Battles As String = ValueOrDash(GetString(Mode, "battles"))
            Dim WinRate As String = ValueOrDash(GetString(Mode, "win_rate"))
            If WinRate <> "—" AndAlso Not WinRate.EndsWith("%") Then WinRate &= "%"
            Dim Air As String = ValueOrDash(GetString(Mode, "air_kills"))
            Dim Ground As String = ValueOrDash(GetString(Mode, "ground_kills"))
            Dim Naval As String = ValueOrDash(GetString(Mode, "naval_kills"))
            Dim PlayTime As String = FormatPlayTime(Mode)

            Dim Card As New Border With {
                .Margin = New Thickness(0, 0, 0, 9),
                .Padding = New Thickness(14, 11, 14, 11),
                .CornerRadius = New CornerRadius(6),
                .BorderThickness = New Thickness(1),
                .BorderBrush = TryCast(FindResource("ColorBrushGray1"), Media.Brush),
                .Background = TryCast(FindResource("ColorBrushSemiTransparent"), Media.Brush)
            }
            Dim Layout As New Grid()
            Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(90)})
            Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
            Layout.Children.Add(New TextBlock With {
                .Text = Name,
                .FontSize = 15,
                .FontWeight = FontWeights.SemiBold,
                .VerticalAlignment = VerticalAlignment.Center,
                .Foreground = TryCast(FindResource("ColorBrush1"), Media.Brush)
            })
            Dim Middle As New StackPanel With {.Margin = New Thickness(10, 0, 12, 0)}
            Middle.Children.Add(New TextBlock With {
                .Text = $"场次: {Battles}  胜率: {WinRate}",
                .FontSize = 12.5,
                .Foreground = TryCast(FindResource("ColorBrush1"), Media.Brush)
            })
            Middle.Children.Add(New TextBlock With {
                .Text = $"击毁 空 {Air} / 陆 {Ground} / 海 {Naval}",
                .Margin = New Thickness(0, 4, 0, 0),
                .FontSize = 11.5,
                .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush)
            })
            Grid.SetColumn(Middle, 1)
            Layout.Children.Add(Middle)
            Dim TimeText As New TextBlock With {
                .Text = PlayTime,
                .FontSize = 11.5,
                .VerticalAlignment = VerticalAlignment.Center,
                .Foreground = TryCast(FindResource("ColorBrushGray2"), Media.Brush)
            }
            Grid.SetColumn(TimeText, 2)
            Layout.Children.Add(TimeText)
            Card.Child = Layout
            PanModes.Children.Add(Card)
        Next
    End Sub

    Private Shared Function GetString(Obj As JObject, Name As String) As String
        If Obj Is Nothing OrElse Obj(Name) Is Nothing Then Return ""
        Return Obj(Name).ToString()
    End Function

    Private Shared Function FormatPlayTime(Obj As JObject) As String
        If Obj Is Nothing Then Return "—"
        Dim TotalSeconds As Double = 0
        Dim SecondsToken = Obj("play_seconds")
        If SecondsToken IsNot Nothing AndAlso
           Double.TryParse(SecondsToken.ToString(), Globalization.NumberStyles.Float,
                           Globalization.CultureInfo.InvariantCulture, TotalSeconds) Then
            TotalSeconds = Math.Max(0, TotalSeconds)
        Else
            Dim Raw As String = GetString(Obj, "play_time")
            Dim DaysMatch = Regex.Match(Raw, "(\d+)\s*天")
            Dim HoursMatch = Regex.Match(Raw, "(\d+)\s*小时")
            Dim MinutesMatch = Regex.Match(Raw, "(\d+)\s*分钟")
            If DaysMatch.Success Then TotalSeconds += Double.Parse(DaysMatch.Groups(1).Value) * 86400
            If HoursMatch.Success Then TotalSeconds += Double.Parse(HoursMatch.Groups(1).Value) * 3600
            If MinutesMatch.Success Then TotalSeconds += Double.Parse(MinutesMatch.Groups(1).Value) * 60
        End If
        If TotalSeconds <= 0 Then Return ValueOrDash(GetString(Obj, "play_time"))

        If Settings.Get(Of Integer)("UiStatsPlayTimeFormat") = 1 Then
            If TotalSeconds < 3600 Then
                Return $"{Math.Max(1, CInt(Math.Round(TotalSeconds / 60)))} 分钟"
            End If
            Return $"{TotalSeconds / 3600:0.#} 小时"
        End If

        Dim Days As Integer = CInt(Math.Floor(TotalSeconds / 86400))
        Dim Hours As Integer = CInt(Math.Floor((TotalSeconds - Days * 86400) / 3600))
        If Days > 0 Then
            Return $"{Days} 天" & If(Hours > 0, $" {Hours} 小时", "")
        End If
        If Hours > 0 Then Return $"{Hours} 小时"
        Return $"{Math.Max(1, CInt(Math.Floor(TotalSeconds / 60)))} 分钟"
    End Function

    Private Shared Function ValueOrDash(Value As String) As String
        Return If(String.IsNullOrWhiteSpace(Value), "—", Value)
    End Function

    Private Sub BtnStatsRefresh_Click(sender As Object, e As EventArgs) Handles BtnStatsRefresh.Click
        RefreshStats(TextStatsQuery.Text)
    End Sub

    Private Sub BtnStatsQuery_Click(sender As Object, e As EventArgs) Handles BtnStatsQuery.Click
        RefreshStats(TextStatsQuery.Text)
    End Sub

    Private Sub BtnStatsWeb_Click(sender As Object, e As EventArgs) Handles BtnStatsWeb.Click
        Dim Nickname As String = LabStatsNickname.Text
        If String.IsNullOrWhiteSpace(Nickname) Then Return
        OpenWebsite("https://warthunder.com/en/community/userinfo/?nick=" & Uri.EscapeDataString(Nickname))
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        RefreshStats()
    End Sub

End Class
