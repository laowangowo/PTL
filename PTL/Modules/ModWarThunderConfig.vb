Imports System.Text.RegularExpressions

''' <summary>
''' 战争雷霆 config.blk 中的常用启动画面与声音设置。
''' </summary>
Public Class WarThunderLaunchSettings
    Public Property GraphicsQuality As String = "low"
    Public Property Resolution As String = "auto"
    Public Property WindowMode As String = "fullscreenwindowed"
    Public Property Driver As String = "auto"
    Public Property VSync As Boolean = False
    Public Property SoundEnabled As Boolean = True
    Public Property SpeakerMode As String = "stereo"
    Public Property VREnabled As Boolean = False
    Public Property ServerCircuit As String = "production"
End Class

Public Module ModWarThunderConfig

    Public Function WarThunderLoadLaunchSettings(InstallPath As String) As WarThunderLaunchSettings
        Dim ConfigPath As String = Path.Combine(InstallPath, "config.blk")
        If Not File.Exists(ConfigPath) Then Throw New FileNotFoundException("未找到战争雷霆配置文件。", ConfigPath)
        Dim Text As String = File.ReadAllText(ConfigPath)
        Return New WarThunderLaunchSettings With {
            .GraphicsQuality = WarThunderReadBlkKey(Text, "", "graphicsQuality", "t", "low"),
            .Resolution = WarThunderReadBlkKey(Text, "video", "resolution", "t", "auto"),
            .WindowMode = WarThunderReadBlkKey(Text, "video", "mode", "t", "fullscreenwindowed"),
            .Driver = WarThunderReadBlkKey(Text, "video", "driver", "t", "auto"),
            .VSync = WarThunderReadBlkBool(Text, "video", "vsync", False),
            .SoundEnabled = WarThunderReadBlkBool(Text, "sound", "fmod_sound_enable", True),
            .SpeakerMode = WarThunderReadBlkKey(Text, "sound", "speakerMode", "t", "stereo"),
            .VREnabled = WarThunderReadBlkBool(Text, "gameplay", "enableVR", False),
            .ServerCircuit = WarThunderReadBlkKey(
                Text, "yunetwork", "curCircuit", "t", "production")
        }
    End Function

    ''' <summary>
    ''' 保存常用启动设置，并保留 config.blk 中其余未被本页面管理的配置。
    ''' </summary>
    Public Sub WarThunderSaveLaunchSettings(InstallPath As String, Settings As WarThunderLaunchSettings)
        Dim ConfigPath As String = Path.Combine(InstallPath, "config.blk")
        If Not File.Exists(ConfigPath) Then Throw New FileNotFoundException("未找到战争雷霆配置文件。", ConfigPath)
        Dim Text As String = File.ReadAllText(ConfigPath)
        Text = WarThunderWriteBlkKey(Text, "", "graphicsQuality", "t", Settings.GraphicsQuality, "low")
        Text = WarThunderWriteBlkKey(Text, "video", "resolution", "t", Settings.Resolution, "auto")
        Text = WarThunderWriteBlkKey(Text, "video", "mode", "t", Settings.WindowMode, "fullscreenwindowed")
        Text = WarThunderWriteBlkKey(Text, "video", "driver", "t", Settings.Driver, "auto")
        Text = WarThunderWriteBlkKey(Text, "video", "vsync", "b", Settings.VSync, False)
        Text = WarThunderWriteBlkKey(Text, "sound", "fmod_sound_enable", "b", Settings.SoundEnabled, True)
        Text = WarThunderWriteBlkKey(Text, "sound", "speakerMode", "t", Settings.SpeakerMode, "stereo")
        Text = WarThunderWriteBlkKey(Text, "gameplay", "enableVR", "b", Settings.VREnabled, False)
        Text = WarThunderWriteBlkKey(
            Text, "yunetwork", "curCircuit", "t",
            If(Settings.ServerCircuit.Equals("dev", StringComparison.OrdinalIgnoreCase),
               "dev", "production"), "production")
        WarThunderWriteConfig(ConfigPath, Text)
        Logger.Info($"已保存战争雷霆启动设置：{ConfigPath}")
    End Sub

    ''' <summary>
    ''' 按战争雷霆模组说明启用 sound\mod。
    ''' </summary>
    Public Sub WarThunderSetSoundModEnabled(InstallPath As String, UseLoopsForGunSound As Boolean)
        Dim ConfigPath As String = Path.Combine(InstallPath, "config.blk")
        If Not File.Exists(ConfigPath) Then Throw New FileNotFoundException("未找到战争雷霆配置文件。", ConfigPath)
        Dim Text As String = File.ReadAllText(ConfigPath)
        Text = WarThunderWriteBlkKey(Text, "sound", "enable_mod", "b", True, False)
        If UseLoopsForGunSound Then
            Text = WarThunderWriteBlkKey(Text, "sound", "useLoopsForGunSound", "b", True, False)
        End If
        WarThunderWriteConfig(ConfigPath, Text)
        Logger.Info($"已启用战争雷霆声音模组：{ConfigPath}")
    End Sub

    Private Sub WarThunderWriteConfig(ConfigPath As String, Text As String)
        Dim TempPath As String = ConfigPath & ".ptl.tmp"
        Dim BackupPath As String = ConfigPath & ".ptl.bak"
        File.WriteAllText(TempPath, Text, New UTF8Encoding(False))
        Try
            File.Replace(TempPath, ConfigPath, BackupPath, True)
        Catch ex As Exception
            Logger.Warn(ex, "原子替换战争雷霆 config.blk 失败，改用兼容写入方式")
            File.Copy(ConfigPath, BackupPath, True)
            File.Copy(TempPath, ConfigPath, True)
            File.Delete(TempPath)
        End Try
    End Sub

    Private Function WarThunderReadBlkBool(Text As String, Block As String,
                                           Key As String, DefaultValue As Boolean) As Boolean
        Dim Value As String = WarThunderReadBlkKey(Text, Block, Key, "b", If(DefaultValue, "yes", "no"))
        Return Value.Equals("yes", StringComparison.OrdinalIgnoreCase) OrElse
               Value.Equals("true", StringComparison.OrdinalIgnoreCase) OrElse Value = "1"
    End Function

    Private Function WarThunderReadBlkKey(Text As String, Block As String, Key As String,
                                          TypeCode As String, DefaultValue As String) As String
        Dim Bounds = WarThunderBlkBounds(Text, Block)
        Dim Segment As String = Text
        If Block <> "" Then
            If Bounds.Key < 0 Then Return DefaultValue
            Segment = Text.Substring(Bounds.Key + 1, Bounds.Value - Bounds.Key - 1)
        End If
        Dim Match = Regex.Match(Segment, $"(?im)^[ \t]*{Regex.Escape(Key)}:{TypeCode}[ \t]*=[ \t]*([^\r\n]*)")
        If Not Match.Success Then Return DefaultValue
        Dim Value As String = Match.Groups(1).Value.Trim()
        If Value.StartsWithF("""") AndAlso Value.EndsWithF("""") AndAlso Value.Length >= 2 Then
            Value = Value.Substring(1, Value.Length - 2)
        End If
        Return Value
    End Function

    Private Function WarThunderWriteBlkKey(Text As String, Block As String, Key As String,
                                           TypeCode As String, Value As Object, DefaultValue As Object) As String
        Dim NewLine As String = If(Text.Contains(vbCrLf), vbCrLf, vbLf)
        Dim Formatted As String = WarThunderFormatBlkValue(TypeCode, Value)
        Dim Bounds = WarThunderBlkBounds(Text, Block)
        If Block <> "" AndAlso Bounds.Key < 0 Then
            Return Text.TrimEnd() & NewLine & NewLine & Block & "{" & NewLine &
                   $"  {Key}:{TypeCode}={Formatted}" & NewLine & "}" & NewLine
        End If

        Dim SegmentStart As Integer = If(Block = "", 0, Bounds.Key + 1)
        Dim SegmentLength As Integer = If(Block = "", Text.Length, Bounds.Value - Bounds.Key - 1)
        Dim Segment As String = Text.Substring(SegmentStart, SegmentLength)
        Dim Pattern As String = $"(?im)^[ \t]*({Regex.Escape(Key)}:{TypeCode}[ \t]*=[ \t]*)[^\r\n]*"
        Dim Match = Regex.Match(Segment, Pattern)
        If Match.Success Then
            Segment = Segment.Substring(0, Match.Index) & Match.Groups(1).Value & Formatted &
                      Segment.Substring(Match.Index + Match.Length)
        Else
            Dim Indent As String = If(Block = "", "", "  ")
            Segment = Segment.TrimEnd() & NewLine & $"{Indent}{Key}:{TypeCode}={Formatted}" & NewLine
        End If

        If Block = "" Then Return Segment
        Return Text.Substring(0, SegmentStart) & Segment & Text.Substring(Bounds.Value)
    End Function

    Private Function WarThunderFormatBlkValue(TypeCode As String, Value As Object) As String
        Select Case TypeCode
            Case "b"
                Return If(Convert.ToBoolean(Value), "yes", "no")
            Case "t"
                Return """" & Convert.ToString(Value).Replace("""", "") & """"
            Case "r"
                Return Convert.ToDouble(Value).ToString("0.0######", Globalization.CultureInfo.InvariantCulture)
            Case Else
                Return Convert.ToInt32(Value).ToString(Globalization.CultureInfo.InvariantCulture)
        End Select
    End Function

    Private Function WarThunderBlkBounds(Text As String, Block As String) As KeyValuePair(Of Integer, Integer)
        If String.IsNullOrWhiteSpace(Block) Then Return New KeyValuePair(Of Integer, Integer)(-1, -1)
        Dim Match = Regex.Match(Text, $"(?im)^[ \t]*{Regex.Escape(Block)}[ \t]*\{{")
        If Not Match.Success Then Return New KeyValuePair(Of Integer, Integer)(-1, -1)
        Dim OpenPosition As Integer = Match.Index + Match.Length - 1
        Dim Depth As Integer = 0
        For i = OpenPosition To Text.Length - 1
            Select Case Text(i)
                Case "{"c
                    Depth += 1
                Case "}"c
                    Depth -= 1
                    If Depth = 0 Then Return New KeyValuePair(Of Integer, Integer)(OpenPosition, i)
            End Select
        Next
        Return New KeyValuePair(Of Integer, Integer)(-1, -1)
    End Function

End Module
