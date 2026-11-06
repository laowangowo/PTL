Imports System.Runtime.InteropServices

Public Class PtlMemoryResult
    Public Property AvailableBefore As Long
    Public Property AvailableAfter As Long
    Public Property ProcessCount As Integer
    Public Property StandbyPurged As Boolean

    Public ReadOnly Property AvailableIncrease As Long
        Get
            Return Math.Max(0, AvailableAfter - AvailableBefore)
        End Get
    End Property
End Class

''' <summary>
''' PTL 的 Windows 内存整理：回收进程工作集并尝试清理待机内存。
''' </summary>
Public Module ModPtlMemory

    Private Const PROCESS_SET_QUOTA As UInteger = &H100
    Private Const PROCESS_QUERY_INFORMATION As UInteger = &H400
    Private Const TOKEN_ADJUST_PRIVILEGES As UInteger = &H20
    Private Const TOKEN_QUERY As UInteger = &H8
    Private Const SE_PRIVILEGE_ENABLED As UInteger = &H2
    Private Const SystemMemoryListInformation As Integer = 80
    Private Const MemoryPurgeStandbyList As Integer = 4

    <StructLayout(LayoutKind.Sequential)>
    Private Structure MemoryStatusEx
        Public Length As UInteger
        Public MemoryLoad As UInteger
        Public TotalPhysical As ULong
        Public AvailablePhysical As ULong
        Public TotalPageFile As ULong
        Public AvailablePageFile As ULong
        Public TotalVirtual As ULong
        Public AvailableVirtual As ULong
        Public AvailableExtendedVirtual As ULong
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure Luid
        Public LowPart As UInteger
        Public HighPart As Integer
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure LuidAndAttributes
        Public Luid As Luid
        Public Attributes As UInteger
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure TokenPrivileges
        Public PrivilegeCount As UInteger
        Public Privilege As LuidAndAttributes
    End Structure

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function GlobalMemoryStatusEx(ByRef Status As MemoryStatusEx) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function OpenProcess(DesiredAccess As UInteger, InheritHandle As Boolean,
                                 ProcessId As Integer) As IntPtr
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function CloseHandle(Handle As IntPtr) As Boolean
    End Function

    <DllImport("kernel32.dll")>
    Private Function GetCurrentProcess() As IntPtr
    End Function

    <DllImport("psapi.dll", SetLastError:=True)>
    Private Function EmptyWorkingSet(ProcessHandle As IntPtr) As Boolean
    End Function

    <DllImport("advapi32.dll", SetLastError:=True, CharSet:=CharSet.Unicode)>
    Private Function OpenProcessToken(ProcessHandle As IntPtr, DesiredAccess As UInteger,
                                      ByRef TokenHandle As IntPtr) As Boolean
    End Function

    <DllImport("advapi32.dll", SetLastError:=True, CharSet:=CharSet.Unicode)>
    Private Function LookupPrivilegeValue(SystemName As String, Name As String,
                                          ByRef LuidValue As Luid) As Boolean
    End Function

    <DllImport("advapi32.dll", SetLastError:=True)>
    Private Function AdjustTokenPrivileges(TokenHandle As IntPtr, DisableAllPrivileges As Boolean,
                                           ByRef NewState As TokenPrivileges,
                                           BufferLength As UInteger, PreviousState As IntPtr,
                                           ReturnLength As IntPtr) As Boolean
    End Function

    <DllImport("ntdll.dll")>
    Private Function NtSetSystemInformation(SystemInformationClass As Integer,
                                            ByRef SystemInformation As Integer,
                                            SystemInformationLength As Integer) As Integer
    End Function

    Public Function PtlOptimizeMemory() As PtlMemoryResult
        Dim Result As New PtlMemoryResult With {
            .AvailableBefore = PtlGetAvailableMemory()
        }
        EnablePrivilege("SeDebugPrivilege")
        EnablePrivilege("SeProfileSingleProcessPrivilege")
        Try
            GC.Collect()
            GC.WaitForPendingFinalizers()
            GC.Collect()
        Catch
        End Try

        For Each Target As Process In Process.GetProcesses()
            Try
                If Target.Id <= 4 Then Continue For
                Dim Handle As IntPtr = OpenProcess(
                    PROCESS_SET_QUOTA Or PROCESS_QUERY_INFORMATION, False, Target.Id)
                If Handle = IntPtr.Zero Then Continue For
                Try
                    If EmptyWorkingSet(Handle) Then Result.ProcessCount += 1
                Finally
                    CloseHandle(Handle)
                End Try
            Catch ex As Exception
                Logger.Trace($"整理进程内存失败：{Target.ProcessName}，{ex.Message}")
            Finally
                Target.Dispose()
            End Try
        Next

        Try
            Dim PurgeCommand As Integer = MemoryPurgeStandbyList
            Result.StandbyPurged =
                NtSetSystemInformation(
                    SystemMemoryListInformation, PurgeCommand, Marshal.SizeOf(Of Integer)) = 0
        Catch ex As Exception
            Logger.Trace($"清理待机内存失败：{ex.Message}")
        End Try

        Thread.Sleep(250)
        Result.AvailableAfter = PtlGetAvailableMemory()
        Logger.Info(
            $"内存优化完成：" &
            $"可用内存增加 {Result.AvailableIncrease \ 1024 \ 1024} MB")
        Return Result
    End Function

    Private Function PtlGetAvailableMemory() As Long
        Dim Status As New MemoryStatusEx With {
            .Length = CUInt(Marshal.SizeOf(Of MemoryStatusEx))
        }
        If GlobalMemoryStatusEx(Status) Then Return CLng(Status.AvailablePhysical)
        Return 0
    End Function

    Private Sub EnablePrivilege(Name As String)
        Dim Token As IntPtr = IntPtr.Zero
        Try
            If Not OpenProcessToken(
                GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES Or TOKEN_QUERY, Token) Then Return
            Dim LuidValue As New Luid()
            If Not LookupPrivilegeValue(Nothing, Name, LuidValue) Then Return
            Dim State As New TokenPrivileges With {
                .PrivilegeCount = 1,
                .Privilege = New LuidAndAttributes With {
                    .Luid = LuidValue,
                    .Attributes = SE_PRIVILEGE_ENABLED
                }
            }
            AdjustTokenPrivileges(Token, False, State, 0, IntPtr.Zero, IntPtr.Zero)
        Finally
            If Token <> IntPtr.Zero Then CloseHandle(Token)
        End Try
    End Sub

End Module
