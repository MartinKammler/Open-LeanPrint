using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using OpenLeanPrint.Capture;
using Xunit;

namespace OpenLeanPrint.Capture.Tests;

[SupportedOSPlatform("windows")]
public class UserCaptureFoldersTests : IDisposable
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "olp-users-" + Guid.NewGuid().ToString("N"));

    public UserCaptureFoldersTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            // SecureRoot leaves only SYSTEM and administrators in; as its owner
            // we may still let ourselves back in to clean up.
            var security = new DirectorySecurity();
            security.AddAccessRule(new FileSystemAccessRule(Me, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(_root).SetAccessControl(security);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception) { }
    }

    private static List<FileSystemAccessRule> Rules(string folder) =>
        new DirectoryInfo(folder).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

    [Fact]
    public void Resolve_FindsTheUserThePrintQueueNames()
    {
        // The print queue sends "MACHINE\user".
        string name = $"{Environment.UserDomainName}\\{Environment.UserName}";

        Assert.Equal(Me, UserCaptureFolders.Resolve(name));
    }

    [Fact]
    public void Resolve_ReturnsNull_ForNoOneThisMachineKnows()
    {
        Assert.Null(UserCaptureFolders.Resolve(null));
        Assert.Null(UserCaptureFolders.Resolve("  "));
        Assert.Null(UserCaptureFolders.Resolve($"{Environment.MachineName}\\nobody-{Guid.NewGuid():N}"[..40]));
        // A group is not somebody who prints.
        Assert.Null(UserCaptureFolders.Resolve(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)
            .Translate(typeof(NTAccount)).Value));
    }

    [Fact]
    public void AUsersFolder_LetsInThatUser_AndNoOtherUsers()
    {
        string folder = UserCaptureFolders.Prepare(_root, Me);

        Assert.Equal(Path.Combine(_root, Me.Value), folder);
        var security = new DirectoryInfo(folder).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "the folder must not inherit who may read it");

        var rules = Rules(folder);
        Assert.Contains(rules, r => r.IdentityReference == Me && r.AccessControlType == AccessControlType.Allow
                                    && r.FileSystemRights.HasFlag(FileSystemRights.Modify));
        Assert.DoesNotContain(rules, r => r.IdentityReference == Users
                                          || r.IdentityReference == Everyone
                                          || r.IdentityReference == AuthenticatedUsers);

        // What lands in it is just as closed.
        string job = Path.Combine(folder, "job-0001.pdf");
        File.WriteAllBytes(job, new byte[16]);
        var fileRules = new FileInfo(job).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.DoesNotContain(fileRules, r => r.IdentityReference == Users || r.IdentityReference == Everyone);

        // And its owner can still take it away.
        File.Delete(job);
    }

    [Fact]
    public void PreparingAgain_KeepsTheJobs_AndUndoesOpenedRules()
    {
        string folder = UserCaptureFolders.Prepare(_root, Me);
        File.WriteAllBytes(Path.Combine(folder, "job-0001.pdf"), new byte[16]);

        // Someone opened it up meanwhile.
        var opened = new DirectoryInfo(folder).GetAccessControl();
        opened.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(opened);

        UserCaptureFolders.Prepare(_root, Me);

        Assert.True(File.Exists(Path.Combine(folder, "job-0001.pdf")));
        Assert.DoesNotContain(Rules(folder), r => r.IdentityReference == Users);
    }

    [Fact]
    public void TheMachineWideFolder_LetsUsersPassThrough_ButNotLookOrWrite()
    {
        // Earlier versions let every user modify everything in it.
        var old = new DirectoryInfo(_root).GetAccessControl();
        old.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(_root).SetAccessControl(old);

        Assert.True(UserCaptureFolders.SecureRoot(_root));

        var security = new DirectoryInfo(_root).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var forUsers = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.IdentityReference == Users || r.IdentityReference == Everyone
                        || r.IdentityReference == AuthenticatedUsers)
            .ToList();

        var only = Assert.Single(forUsers);
        Assert.Equal(InheritanceFlags.None, only.InheritanceFlags);
        Assert.Equal(FileSystemRights.Traverse | FileSystemRights.ReadAttributes | FileSystemRights.Synchronize,
                     only.FileSystemRights);
    }

    [Fact]
    public void All_ListsTheUsersFolders_AndNothingElse()
    {
        UserCaptureFolders.Prepare(_root, Me);
        Directory.CreateDirectory(Path.Combine(_root, "something-else"));
        File.WriteAllBytes(Path.Combine(_root, "job-0001.pdf"), new byte[16]);

        var all = UserCaptureFolders.All(_root).ToList();

        Assert.Equal(new[] { Path.Combine(_root, Me.Value) }, all);
    }
}
