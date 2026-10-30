using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AnovaDesk;

/// <summary>
/// Stores the Personal Access Token in Windows Credential Manager so it never
/// lands in a repo file or a plaintext config.
/// </summary>
internal static class TokenStore
{
    private static string Target => AppRuntime.CredentialTarget;
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    public static void Save(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            Delete();
            return;
        }

        var blob = Encoding.Unicode.GetBytes(token);
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target,
                UserName = Environment.UserName,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                Comment = "AnovaDesk personal access token",
            };

            if (!CredWrite(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CredWrite failed");
        }
        finally
        {
            handle.Free();
        }
    }

    public static string? Load()
    {
        if (!CredRead(Target, CRED_TYPE_GENERIC, 0, out var ptr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
                return null;

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static void Delete()
    {
        CredDelete(Target, CRED_TYPE_GENERIC, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);
}
