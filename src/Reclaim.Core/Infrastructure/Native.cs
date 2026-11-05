using System;
using System.Runtime.InteropServices;

namespace Reclaim.Core.Infrastructure
{
    internal static class Native
    {
        public const uint SC_MANAGER_CONNECT = 0x0001;
        public const uint WRITE_DAC = 0x00040000;
        public const uint DACL_SECURITY_INFORMATION = 0x00000004;
        public const uint SDDL_REVISION_1 = 1;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenSCManager(string machineName, string databaseName, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetServiceObjectSecurity(IntPtr service, uint securityInformation, IntPtr securityDescriptor);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
            string stringSecurityDescriptor, uint revision, out IntPtr securityDescriptor, out uint size);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr memory);

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
