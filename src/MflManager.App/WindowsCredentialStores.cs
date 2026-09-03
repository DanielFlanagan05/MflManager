using MflManager.Application;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Security.Credentials;

namespace MflManager.App;

public sealed class WindowsCredentialLoginStore
{
    private const string Resource = "MflManager";
    private readonly PasswordVault _vault = new();

    public (string Username, string Password)? Load()
    {
        var native = LoadGenericCredential();
        if (native is not null) return native;
        try
        {
            var credential = _vault.FindAllByResource(Resource).FirstOrDefault();
            if (credential is null) return null;
            credential.RetrievePassword();
            return string.IsNullOrWhiteSpace(credential.UserName) || string.IsNullOrWhiteSpace(credential.Password)
                ? null : (credential.UserName, credential.Password);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static (string Username, string Password)? LoadGenericCredential()
    {
        if (!CredRead(Resource, CredentialTypeGeneric, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero) return null;
            var username = Marshal.PtrToStringUni(credential.UserName);
            var passwordBytes = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, passwordBytes, 0, passwordBytes.Length);
            var password = Encoding.Unicode.GetString(passwordBytes);
            return string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) ? null : (username, password);
        }
        finally { CredFree(pointer); }
    }

    private const uint CredentialTypeGeneric = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}

public sealed class WindowsCredentialCookieStore : IMflCookieStore
{
    private const string Resource = "MflManager.MflSession";
    private const string User = "MFL_USER_ID";
    private readonly PasswordVault _vault = new();

    public Task SaveAsync(string cookieValue, CancellationToken cancellationToken)
    {
        ForgetCore();
        _vault.Add(new PasswordCredential(Resource, User, cookieValue));
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(CancellationToken cancellationToken)
    {
        try { var credential = _vault.Retrieve(Resource, User); credential.RetrievePassword(); return Task.FromResult<string?>(credential.Password); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or KeyNotFoundException) { return Task.FromResult<string?>(null); }
    }

    public Task ForgetAsync(CancellationToken cancellationToken) { ForgetCore(); return Task.CompletedTask; }
    private void ForgetCore() { try { _vault.Remove(_vault.Retrieve(Resource, User)); } catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or KeyNotFoundException) { } }
}

public sealed class WindowsCredentialSessionStore : ISecureSessionStore
{
    private const string Resource = "MflManager.Account";
    private readonly PasswordVault _vault = new();
    public Task SaveAsync(string username, string userId, CancellationToken cancellationToken)
    {
        try { foreach (var old in _vault.FindAllByResource(Resource)) _vault.Remove(old); } catch (System.Runtime.InteropServices.COMException) { }
        _vault.Add(new PasswordCredential(Resource, username, userId));
        return Task.CompletedTask;
    }
    public Task<(string Username, string UserId)?> LoadAsync(CancellationToken cancellationToken)
    {
        try { var credential = _vault.FindAllByResource(Resource).FirstOrDefault(); if (credential is null) return Task.FromResult<(string, string)?>(null); credential.RetrievePassword(); return Task.FromResult<(string, string)?>((credential.UserName, credential.Password)); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or KeyNotFoundException) { return Task.FromResult<(string, string)?>(null); }
    }
    public Task ForgetAsync(CancellationToken cancellationToken) { try { foreach (var old in _vault.FindAllByResource(Resource)) _vault.Remove(old); } catch (System.Runtime.InteropServices.COMException) { } return Task.CompletedTask; }
}
