using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenLeanPrint.Capture;

/// <summary>
/// One private capture folder per user, inside the machine-wide one.
/// <para>
/// The capture service runs as LocalSystem and writes every job, whoever
/// printed it. It used to write them all into one folder that every user of
/// the machine could read and change - on a shared PC, anyone could open,
/// alter or delete what somebody else had printed. Now each job goes into a
/// folder named after the SID of the user the print queue says printed it, and
/// only that user (plus SYSTEM and administrators) can get into it. The
/// machine-wide folder above them is closed to ordinary users altogether, so
/// nobody can create a folder for someone else in advance and read along.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class UserCaptureFolders
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>
    /// The user a job belongs to, from the name the print queue sent with it
    /// (IPP <c>requesting-user-name</c>, e.g. <c>PC\Martin</c>). Null when it
    /// names nobody this machine knows - such a job has no folder to go to.
    /// </summary>
    public static SecurityIdentifier? Resolve(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;

        try
        {
            var sid = (SecurityIdentifier)new NTAccount(userName.Trim()).Translate(typeof(SecurityIdentifier));
            // A group or a well-known account is not someone who prints.
            return sid.IsAccountSid() ? sid : null;
        }
        catch (Exception)
        {
            return null; // IdentityNotMappedException, or a name that is not a name
        }
    }

    /// <summary>The folder a user's jobs go into.</summary>
    public static string FolderFor(string root, SecurityIdentifier user) => Path.Combine(root, user.Value);

    /// <summary>
    /// Closes the machine-wide folder to ordinary users: they may pass through
    /// it to their own folder, but not list it, write into it or create
    /// anything in it. Replaces whatever was there before, including the
    /// "Users may modify" rule earlier versions set.
    /// </summary>
    /// <returns>True if the rule was applied.</returns>
    public static bool SecureRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        try
        {
            var directory = new DirectoryInfo(root);
            directory.Create();

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddFullControl(security, System);
            AddFullControl(security, Administrators);
            // This folder only, and only enough to reach the folder inside it.
            security.AddAccessRule(new FileSystemAccessRule(Users,
                FileSystemRights.Traverse | FileSystemRights.ReadAttributes | FileSystemRights.Synchronize,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Makes sure <paramref name="user"/>'s folder exists, belongs to SYSTEM or
    /// the administrators, and lets in nobody else but that user; returns it.
    /// Throws if a folder of that name was set up by someone else - a job is
    /// better refused than handed to whoever prepared that folder.
    /// </summary>
    public static string Prepare(string root, SecurityIdentifier user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(user);

        var directory = new DirectoryInfo(FolderFor(root, user));
        directory.Create();

        var owner = directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
        if (owner is not SecurityIdentifier sid || !IsTrustedOwner(sid))
            throw new UnauthorizedAccessException(
                $"The capture folder {directory.FullName} was not created by the capture service (owner {owner}).");

        // Set every time: cheap, and whatever someone changed is undone.
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(security, System);
        AddFullControl(security, Administrators);
        security.AddAccessRule(new FileSystemAccessRule(user,
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);

        return directory.FullName;
    }

    /// <summary>The per-user folders that exist under <paramref name="root"/>.</summary>
    public static IEnumerable<string> All(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(root, "S-1-*");
    }

    /// <summary>
    /// SYSTEM or the administrators - whoever the service runs as creates the
    /// folder. The process's own account is accepted too, which is the same
    /// account for the service and lets the console host and tests work.
    /// </summary>
    private static bool IsTrustedOwner(SecurityIdentifier owner) =>
        owner == System || owner == Administrators || owner == WindowsIdentity.GetCurrent().User;

    private static void AddFullControl(DirectorySecurity security, SecurityIdentifier who) =>
        security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
}
