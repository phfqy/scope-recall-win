using System.Runtime.InteropServices;
using System.Text;

namespace ScopeRecallWin.Services;

/// <summary>AI 凭据存取。约束 7：API Key 只存 Windows 凭据管理器，password 输入、不回显、不落日志。</summary>
public interface ICredentialService
{
    Task<string?> GetAsync(string serviceName, string accountName);
    Task<bool> SaveAsync(string serviceName, string accountName, string secret);
    Task<bool> DeleteAsync(string serviceName, string accountName);
}

/// <summary>
/// Windows 凭据管理器实现（advapi32 CredRead / CredWrite / CredDelete）。
/// 设计约束：
///   - 值绝不出现在 runtime-config.json / 日志 / 调用台账；
///   - 配置文件只保留 credential_env 的"环境变量名"，key 值由凭据管理器提供并注入进程环境；
///   - 凭据以 Generic 类型存储，Target 形如 "ScopeRecall:ZHIPU_API_KEY"，用户名为账户占位。
/// </summary>
public sealed class CredentialService : ICredentialService
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    public Task<string?> GetAsync(string serviceName, string accountName)
    {
        var target = Target(serviceName, accountName);
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var handle))
        {
            return Task.FromResult<string?>(null);
        }
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(handle);
            var secret = credential.CredentialBlobSize > 0
                ? Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2)
                : null;
            return Task.FromResult(secret);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public Task<bool> SaveAsync(string serviceName, string accountName, string secret)
    {
        var target = Target(serviceName, accountName);
        var blob = Encoding.Unicode.GetBytes(secret);
        var blobPtr = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlob = blobPtr,
                CredentialBlobSize = blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = accountName,
                Comment = "Scope Recall Win 控制台存储",
            };
            return Task.FromResult(CredWrite(ref credential, 0));
        }
        catch
        {
            return Task.FromResult(false);
        }
        finally
        {
            // 凭据已复制进系统存储，可清空本地缓冲
            for (int i = 0; i < blob.Length; i++)
            {
                Marshal.WriteByte(blobPtr, i, 0);
            }
            Marshal.FreeCoTaskMem(blobPtr);
        }
    }

    public Task<bool> DeleteAsync(string serviceName, string accountName)
        => Task.FromResult(CredDelete(Target(serviceName, accountName), CRED_TYPE_GENERIC, 0));

    private static string Target(string serviceName, string accountName) => $"ScopeRecall:{serviceName}:{accountName}";

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL userCredential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);
}
