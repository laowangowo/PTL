Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions

''' <summary>
''' PTL 机器码与离线激活码。
''' </summary>
Public Module ModPtlActivation

    Private Const ActivationRegistryKey As String = "HKEY_CURRENT_USER\Software\PTL\Activation"
    ' 保留旧算法常量，确保已购买主题和旧激活码继续有效。
    Private Const ActivationSecret As String = "PCLWT-ACTIVATION-V1-7B2D-4E91-A8C3"
    Private Const CodeAlphabet As String = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
    Private ReadOnly MachineCodeRegex As New Regex("^[" & CodeAlphabet & "]{6}$")

    Public Function PtlGetMachineCode() As String
        Try
            Dim MachineGuid As String = RegistryUtils.TryRead(
                "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid", "").ToString().Trim()
            If MachineGuid = "" Then
                MachineGuid = Environment.MachineName & "|" & Environment.SystemDirectory
            End If
            Dim SystemDrive As String = Path.GetPathRoot(Environment.SystemDirectory)
            Dim Fingerprint As String =
                $"PCLWT-MACHINE|{MachineGuid}|{SystemDrive}|" &
                If(Environment.Is64BitOperatingSystem, "64", "32")
            Using Hasher = SHA256.Create()
                Dim Hash As Byte() = Hasher.ComputeHash(Encoding.UTF8.GetBytes(Fingerprint))
                Dim Value As ULong =
                    (CULng(Hash(0)) << 24) Or
                    (CULng(Hash(1)) << 16) Or
                    (CULng(Hash(2)) << 8) Or
                    CULng(Hash(3))
                Dim Number As Integer = CInt(Value Mod &H40000000UL)
                Dim Builder As New StringBuilder(6)
                For Index = 1 To 6
                    Builder.Insert(0, CodeAlphabet(Number Mod CodeAlphabet.Length))
                    Number \= CodeAlphabet.Length
                Next
                Return Builder.ToString()
            End Using
        Catch ex As Exception
            Logger.Warn(ex, "生成 PTL 机器码失败")
            Return "ERROR1"
        End Try
    End Function

    Public Function PtlGenerateActivationCode(MachineCode As String) As String
        Dim NormalizedMachineCode As String = If(MachineCode, "").Trim().ToUpperInvariant()
        If Not MachineCodeRegex.IsMatch(NormalizedMachineCode) Then Return ""
        Using Hmac As New HMACSHA256(Encoding.UTF8.GetBytes(ActivationSecret))
            Dim Hash As Byte() = Hmac.ComputeHash(
                Encoding.UTF8.GetBytes("PCLWT-ACTIVATE|" & NormalizedMachineCode))
            Dim Encoded As String = PtlBase32(Hash, 16)
            Return $"{Encoded.Substring(0, 4)}-{Encoded.Substring(4, 4)}-" &
                   $"{Encoded.Substring(8, 4)}-{Encoded.Substring(12, 4)}"
        End Using
    End Function

    Public Function PtlNormalizeActivationCode(Code As String) As String
        Return Regex.Replace(If(Code, ""), "[^A-Za-z0-9]", "").ToUpperInvariant()
    End Function

    Public Function PtlIsActivated() As Boolean
        Try
            Dim MachineCode As String = PtlGetMachineCode()
            Dim Expected As String = PtlNormalizeActivationCode(
                PtlGenerateActivationCode(MachineCode))
            Dim Saved As String = PtlNormalizeActivationCode(
                RegistryUtils.TryRead(ActivationRegistryKey, "ActivationCode", "").ToString())
            Return Expected <> "" AndAlso Saved = Expected
        Catch
            Return False
        End Try
    End Function

    Public Function PtlActivate(InputCode As String, ByRef ErrorMessage As String) As Boolean
        ErrorMessage = ""
        Dim MachineCode As String = PtlGetMachineCode()
        Dim Expected As String = PtlNormalizeActivationCode(
            PtlGenerateActivationCode(MachineCode))
        Dim Actual As String = PtlNormalizeActivationCode(InputCode)
        If Expected = "" Then
            ErrorMessage = "无法生成当前设备的机器码，请稍后重试。"
            Return False
        End If
        If Actual.Length <> Expected.Length Then
            ErrorMessage = "激活码格式不正确，请检查后重新输入。"
            Return False
        End If
        If Not Actual.Equals(Expected, StringComparison.Ordinal) Then
            ErrorMessage = "激活码与当前设备不匹配。"
            Return False
        End If
        RegistryUtils.Write(ActivationRegistryKey, "MachineCode", MachineCode)
        RegistryUtils.Write(ActivationRegistryKey, "ActivationCode", Expected)
        Logger.Info($"PTL 已激活，机器码：{MachineCode}")
        Return True
    End Function

    Private Function PtlBase32(Data As Byte(), CharacterCount As Integer) As String
        Dim Builder As New StringBuilder()
        Dim Buffer As Integer = 0
        Dim Bits As Integer = 0
        For Each Value As Byte In Data
            Buffer = ((Buffer << 8) Or Value) And &HFFFF
            Bits += 8
            While Bits >= 5
                Bits -= 5
                Builder.Append(CodeAlphabet((Buffer >> Bits) And &H1F))
                If Builder.Length >= CharacterCount Then Return Builder.ToString()
            End While
        Next
        If Bits > 0 AndAlso Builder.Length < CharacterCount Then
            Builder.Append(CodeAlphabet((Buffer << (5 - Bits)) And &H1F))
        End If
        Return Builder.ToString()
    End Function

End Module
