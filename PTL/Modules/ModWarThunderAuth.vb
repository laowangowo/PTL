Imports Microsoft.Win32
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions

''' <summary>
''' 保存和恢复战争雷霆官方客户端按当前 Windows 用户加密的登录数据。
''' 只复制 DPAPI 密文，不解密或记录账号密码。
''' </summary>
Public Module ModWarThunderAuth

    Private Const SourceRegistryPath As String = "Software\Gaijin\WarThunder"
    Private Const SnapshotRegistryRoot As String = "Software\PTL\WarThunderAuth"
    Private Const SnapshotVersion As Integer = 1

    Private ReadOnly BinaryValueNames As String() = {
        "IntAuthShortToken",
        "IntAuthLogin",
        "IntAuthPass"
    }
    Private ReadOnly SnapshotValueNames As String() = {
        "IntAuthShortToken",
        "IntAuthLogin",
        "IntAuthPass",
        "IntAuthSaveBitset",
        "AuthFingerprint",
        "SnapshotVersion",
        "CapturedAtUtc",
        "LastValidatedAtUtc",
        "LastRestoredAtUtc"
    }

    Public Function WarThunderHasAuthSnapshot(UserId As String) As Boolean
        UserId = NormalizeUserId(UserId)
        If UserId = "" Then Return False
        Try
            Using Snapshot As RegistryKey =
                Registry.CurrentUser.OpenSubKey($"{SnapshotRegistryRoot}\{UserId}", False)
                If Snapshot Is Nothing Then Return False
                If Convert.ToInt32(
                    Snapshot.GetValue("SnapshotVersion", 0)) <> SnapshotVersion Then
                    Return False
                End If
                Dim Values As New Dictionary(Of String, Byte())
                For Each ValueName In BinaryValueNames
                    Dim Value = TryCast(Snapshot.GetValue(ValueName), Byte())
                    If Value IsNot Nothing AndAlso Value.Length > 0 Then
                        Values(ValueName) = Value
                    End If
                Next
                Return IsValidAuthSet(Values)
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"检查战争雷霆登录凭据快照失败：UID {UserId}")
            Return False
        End Try
    End Function

    Public Function WarThunderGetAuthSnapshotStatus(UserId As String) As String
        UserId = NormalizeUserId(UserId)
        If UserId = "" OrElse Not WarThunderHasAuthSnapshot(UserId) Then
            Return "未保存登录凭据，需要先在游戏中登录一次"
        End If
        Try
            Using Snapshot As RegistryKey =
                Registry.CurrentUser.OpenSubKey($"{SnapshotRegistryRoot}\{UserId}", False)
                Dim CapturedText As String =
                    Convert.ToString(Snapshot.GetValue("CapturedAtUtc", ""))
                Dim CapturedAt As Date
                If Date.TryParse(CapturedText, CapturedAt) Then
                    Return $"登录凭据已保存： {CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
                End If
            End Using
        Catch
        End Try
        Return "登录凭据已保存"
    End Function

    Public Function WarThunderCaptureCurrentAuthSnapshot(
        UserId As String, ByRef ErrorMessage As String) As Boolean
        ErrorMessage = ""
        UserId = NormalizeUserId(UserId)
        If UserId = "" Then
            ErrorMessage = "无法确定当前玩家的 UID。"
            Return False
        End If
        If Diagnostics.Process.GetProcessesByName("aces").Any() OrElse
           Diagnostics.Process.GetProcessesByName("aces_BE").Any() OrElse
           Diagnostics.Process.GetProcessesByName("beac_wt_mlauncher").Any() Then
            ErrorMessage = "战争雷霆正在运行，请完全退出游戏后再保存登录凭据。"
            Return False
        End If

        Try
            Dim Values As New Dictionary(Of String, Byte())
            Dim SaveBitset As Object = Nothing
            Using Source As RegistryKey =
                Registry.CurrentUser.OpenSubKey(SourceRegistryPath, False)
                If Source Is Nothing Then
                    ErrorMessage = "没有找到 Gaijin 登录数据。"
                    Return False
                End If
                For Each ValueName In BinaryValueNames
                    Dim Value = TryCast(Source.GetValue(ValueName), Byte())
                    If Value IsNot Nothing AndAlso Value.Length > 0 Then
                        Values(ValueName) = CType(Value.Clone(), Byte())
                    End If
                Next
                SaveBitset = Source.GetValue("IntAuthSaveBitset")
            End Using

            If Not IsValidAuthSet(Values) Then
                ErrorMessage = "游戏当前没有保存完整的登录凭据。"
                Return False
            End If

            Dim Fingerprint As String = GetAuthFingerprint(Values)
            Dim NowText As String = Date.UtcNow.ToString("O")
            Using Root As RegistryKey =
                Registry.CurrentUser.CreateSubKey(SnapshotRegistryRoot, True)
                Using Snapshot As RegistryKey = Root.CreateSubKey(UserId, True)
                    Dim OldFingerprint As String =
                        Convert.ToString(Snapshot.GetValue("AuthFingerprint", ""))
                    If OldFingerprint = Fingerprint Then
                        Snapshot.SetValue("LastValidatedAtUtc", NowText,
                                          RegistryValueKind.String)
                        Return True
                    End If

                    For Each ValueName In SnapshotValueNames
                        Snapshot.DeleteValue(ValueName, False)
                    Next
                    For Each Entry In Values
                        Snapshot.SetValue(Entry.Key, Entry.Value, RegistryValueKind.Binary)
                    Next
                    If SaveBitset IsNot Nothing Then
                        Snapshot.SetValue("IntAuthSaveBitset",
                            Convert.ToInt32(SaveBitset), RegistryValueKind.DWord)
                    End If
                    Snapshot.SetValue("AuthFingerprint", Fingerprint,
                                      RegistryValueKind.String)
                    Snapshot.SetValue("SnapshotVersion", SnapshotVersion,
                                      RegistryValueKind.DWord)
                    Snapshot.SetValue("CapturedAtUtc", NowText,
                                      RegistryValueKind.String)
                    Snapshot.SetValue("LastValidatedAtUtc", NowText,
                                      RegistryValueKind.String)
                End Using
            End Using
            Logger.Info($"已保存战争雷霆登录凭据快照：UID {UserId}，字段 {Values.Count} 个")
            Return True
        Catch ex As Exception
            ErrorMessage = ex.GetDisplay(False)
            Logger.Warn(ex, $"保存战争雷霆登录凭据快照失败：UID {UserId}")
            Return False
        End Try
    End Function

    Public Function WarThunderRestoreAuthSnapshot(
        UserId As String, ByRef ErrorMessage As String) As Boolean
        ErrorMessage = ""
        UserId = NormalizeUserId(UserId)
        If UserId = "" Then
            ErrorMessage = "玩家 UID 无效。"
            Return False
        End If

        Try
            Dim Values As New Dictionary(Of String, Byte())
            Dim SaveBitset As Object = Nothing
            Using Snapshot As RegistryKey =
                Registry.CurrentUser.OpenSubKey($"{SnapshotRegistryRoot}\{UserId}", True)
                If Snapshot Is Nothing Then
                    ErrorMessage = "该玩家还没有登录凭据快照。请先登录此账号并退出游戏。"
                    Return False
                End If
                If Convert.ToInt32(
                    Snapshot.GetValue("SnapshotVersion", 0)) <> SnapshotVersion Then
                    ErrorMessage = "该玩家的登录凭据快照版本不受支持。"
                    Return False
                End If
                For Each ValueName In BinaryValueNames
                    Dim Value = TryCast(Snapshot.GetValue(ValueName), Byte())
                    If Value IsNot Nothing AndAlso Value.Length > 0 Then
                        Values(ValueName) = CType(Value.Clone(), Byte())
                    End If
                Next
                SaveBitset = Snapshot.GetValue("IntAuthSaveBitset")
                If Not IsValidAuthSet(Values) Then
                    ErrorMessage = "该玩家的登录凭据快照不完整。"
                    Return False
                End If

                Using Source As RegistryKey =
                    Registry.CurrentUser.OpenSubKey(SourceRegistryPath, True)
                    If Source Is Nothing Then
                        ErrorMessage = "没有找到 Gaijin 登录数据所在位置。"
                        Return False
                    End If
                    For Each ValueName In BinaryValueNames
                        Dim Value As Byte() = Nothing
                        If Values.TryGetValue(ValueName, Value) AndAlso Value.Length > 0 Then
                            Source.SetValue(ValueName, Value, RegistryValueKind.Binary)
                        Else
                            Source.DeleteValue(ValueName, False)
                        End If
                    Next
                    If SaveBitset IsNot Nothing Then
                        Source.SetValue("IntAuthSaveBitset",
                            Convert.ToInt32(SaveBitset), RegistryValueKind.DWord)
                    Else
                        Source.DeleteValue("IntAuthSaveBitset", False)
                    End If
                End Using
                Snapshot.SetValue("LastRestoredAtUtc",
                    Date.UtcNow.ToString("O"), RegistryValueKind.String)
            End Using
            Logger.Info($"已恢复战争雷霆登录凭据快照：UID {UserId}")
            Return True
        Catch ex As Exception
            ErrorMessage = ex.GetDisplay(False)
            Logger.Warn(ex, $"恢复战争雷霆登录凭据快照失败：UID {UserId}")
            Return False
        End Try
    End Function

    Private Function IsValidAuthSet(Values As Dictionary(Of String, Byte())) As Boolean
        Dim HasToken As Boolean =
            Values.ContainsKey("IntAuthShortToken") AndAlso
            Values("IntAuthShortToken").Length > 0
        Dim HasLogin As Boolean =
            Values.ContainsKey("IntAuthLogin") AndAlso
            Values("IntAuthLogin").Length > 0
        Dim HasPassword As Boolean =
            Values.ContainsKey("IntAuthPass") AndAlso
            Values("IntAuthPass").Length > 0
        Return HasToken OrElse (HasLogin AndAlso HasPassword)
    End Function

    Private Function GetAuthFingerprint(Values As Dictionary(Of String, Byte())) As String
        Using Hasher As SHA256 = SHA256.Create()
            Using Stream As New IO.MemoryStream()
                For Each ValueName In BinaryValueNames
                    Dim Value As Byte() = Nothing
                    If Not Values.TryGetValue(ValueName, Value) Then Value = {}
                    Dim LengthBytes As Byte() = BitConverter.GetBytes(Value.Length)
                    Stream.Write(LengthBytes, 0, LengthBytes.Length)
                    Stream.Write(Value, 0, Value.Length)
                Next
                Return Convert.ToBase64String(Hasher.ComputeHash(Stream.ToArray()))
            End Using
        End Using
    End Function

    Private Function NormalizeUserId(UserId As String) As String
        UserId = If(UserId, "").Trim()
        Return If(Regex.IsMatch(UserId, "^\d+$"), UserId, "")
    End Function

End Module
